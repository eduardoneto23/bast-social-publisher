using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bast.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace Bast.Notifications.Telegram;

public sealed record TelegramStatus(bool HasToken, bool Connected, string? Bot, string? Chat);

public sealed record TelegramResult(bool Ok, string Message);

/// <summary>
/// Avisos por um bot do Telegram do próprio usuário (Bot API oficial).
///
/// O token do bot fica no armazenamento seguro. A Bot API exige o token no endereço do pedido,
/// então o cliente HTTP deste módulo não registra pedidos em log e nenhuma mensagem de erro
/// daqui inclui o endereço.
///
/// A conversa de destino é vinculada por um código: o usuário envia o código ao bot e o sistema
/// só aceita a conversa privada que o enviou. Assim, um estranho que encontre o bot não recebe os avisos.
/// </summary>
public sealed partial class TelegramBot(IHttpClientFactory httpFactory, ISecretStore secrets, ILogger<TelegramBot> logger) : INotificationChannel
{
    public const string HttpClientName = "telegram";
    internal const string SecretKey = "telegram.bot";
    internal const string ApiBase = "https://api.telegram.org";
    private const int MaxTextLength = 4096;

    internal sealed record Settings(string Token, string? BotUsername, long? ChatId, string? ChatName);

    private Settings? _settings;
    private bool _loaded;

    [GeneratedRegex(@"^\d{3,20}:[A-Za-z0-9_-]{20,100}$")]
    private static partial Regex TokenFormat();

    private Settings? Current
    {
        get
        {
            if (!_loaded)
            {
                _settings = secrets.Get(SecretKey) is { } raw ? JsonSerializer.Deserialize<Settings>(raw) : null;
                _loaded = true;
            }

            return _settings;
        }
    }

    public bool IsConfigured => Current?.ChatId is not null;

    public TelegramStatus Status => new(Current is not null, IsConfigured, Current?.BotUsername, Current?.ChatName);

    /// <summary>Confere o token com o Telegram (getMe) e o guarda. A conversa ainda precisa ser vinculada.</summary>
    public async Task<TelegramResult> SaveTokenAsync(string token, CancellationToken ct)
    {
        token = token.Trim();
        if (!TokenFormat().IsMatch(token))
        {
            return new(false, "O token não tem o formato dos tokens do BotFather (números, dois-pontos e uma sequência de letras e números).");
        }

        var (response, error) = await CallAsync(token, "getMe", null, ct);
        if (error is not null) return new(false, $"O Telegram recusou o token: {error.Description}");

        var username = response.TryGetProperty("username", out var u) ? u.GetString() : null;
        Save(new Settings(token, username, null, null));
        logger.LogInformation("Telegram: token do bot @{Bot} guardado.", username);
        return new(true, $"Token aceito. Bot: @{username}.");
    }

    /// <summary>Procura, entre as mensagens recebidas pelo bot, a conversa privada que enviou o código.</summary>
    public async Task<TelegramResult> LinkAsync(string code, CancellationToken ct)
    {
        var settings = Current;
        if (settings is null) return new(false, "Informe antes o token do bot.");
        code = code.Trim();
        if (code.Length < 4) return new(false, "Código de vínculo muito curto.");

        var (updates, error) = await CallAsync(settings.Token, "getUpdates", new { limit = 100, timeout = 0, allowed_updates = new[] { "message" } }, ct);
        if (error is not null) return new(false, $"Não foi possível ler as mensagens do bot: {error.Description}");

        long? chatId = null;
        string? chatName = null;
        if (updates.ValueKind == JsonValueKind.Array)
        {
            foreach (var update in updates.EnumerateArray())
            {
                if (!update.TryGetProperty("message", out var message) || !message.TryGetProperty("chat", out var chat)) continue;
                if (!(chat.TryGetProperty("type", out var type) && type.GetString() == "private")) continue;
                if (!(message.TryGetProperty("text", out var text) && (text.GetString() ?? "").Contains(code, StringComparison.Ordinal))) continue;
                if (!chat.TryGetProperty("id", out var id) || !id.TryGetInt64(out var value)) continue;

                chatId = value;
                chatName = chat.TryGetProperty("username", out var user) && user.GetString() is { Length: > 0 } name ? "@" + name
                    : chat.TryGetProperty("first_name", out var first) ? first.GetString()
                    : null;
            }
        }

        if (chatId is null)
        {
            return new(false, $"Nenhuma mensagem com o código foi encontrada. Abra a conversa com @{settings.BotUsername} no Telegram, envie o código e tente de novo.");
        }

        var linked = settings with { ChatId = chatId, ChatName = chatName };
        var hello = await SendToAsync(linked, "🔔 BAST SOCIAL PUBLISHER conectado. Os avisos de publicação, falha e reconexão chegam por aqui.", ct);
        if (hello.Status != ChannelSendStatus.Sent) return new(false, $"A conversa foi encontrada, mas a mensagem de confirmação falhou: {hello.Error}");

        Save(linked);
        logger.LogInformation("Telegram: avisos vinculados a uma conversa privada.");
        return new(true, $"Avisos conectados à conversa de {chatName ?? "um usuário"}.");
    }

    public Task<ChannelSendResult> SendAsync(string text, CancellationToken ct)
    {
        var settings = Current;
        return settings?.ChatId is null
            ? Task.FromResult(new ChannelSendResult(ChannelSendStatus.Rejected, "O Telegram não está conectado."))
            : SendToAsync(settings, text, ct);
    }

    public void Disconnect()
    {
        secrets.Delete(SecretKey);
        _settings = null;
        _loaded = true;
    }

    private void Save(Settings settings)
    {
        secrets.Set(SecretKey, JsonSerializer.Serialize(settings));
        _settings = settings;
        _loaded = true;
    }

    private async Task<ChannelSendResult> SendToAsync(Settings settings, string text, CancellationToken ct)
    {
        if (text.Length > MaxTextLength) text = text[..(MaxTextLength - 1)] + "…";

        TelegramError? error;
        try
        {
            (_, error) = await CallAsync(settings.Token, "sendMessage",
                new { chat_id = settings.ChatId, text, link_preview_options = new { is_disabled = true } }, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // Sem a mensagem da exceção: ela pode citar o endereço do pedido, que contém o token.
            return new(ChannelSendStatus.Retry, "Sem conexão com o Telegram.");
        }

        if (error is null) return ChannelSendResult.Sent;
        if (error.Code == 429) return new(ChannelSendStatus.Retry, $"Telegram: limite de envio atingido ({error.Description}).", error.RetryAfter);
        if (error.Code >= 500) return new(ChannelSendStatus.Retry, $"Telegram indisponível ({error.Description}).");
        return new(ChannelSendStatus.Rejected, $"O Telegram recusou o aviso ({error.Code}: {error.Description}). Confira se o bot não foi bloqueado e conecte de novo.");
    }

    private sealed record TelegramError(int Code, string Description, TimeSpan? RetryAfter);

    private async Task<(JsonElement Result, TelegramError? Error)> CallAsync(string token, string method, object? body, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        var url = $"{ApiBase}/bot{token}/{method}";
        using var response = body is null ? await http.GetAsync(url, ct) : await http.PostAsJsonAsync(url, body, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);

        JsonElement root = default;
        try
        {
            using var json = JsonDocument.Parse(raw);
            root = json.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Resposta que não é JSON: tratada abaixo pelo código HTTP.
        }

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
        {
            return (root.TryGetProperty("result", out var result) ? result : default, null);
        }

        var code = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error_code", out var c) && c.TryGetInt32(out var parsed) ? parsed : (int)response.StatusCode;
        var description = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
        if (description.Length == 0) description = $"resposta HTTP {(int)response.StatusCode}";
        TimeSpan? retryAfter = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("parameters", out var p)
                               && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("retry_after", out var r) && r.TryGetInt32(out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
        return (default, new TelegramError(code, description, retryAfter));
    }
}

public static class TelegramServiceCollectionExtensions
{
    public static IServiceCollection AddTelegramNotifications(this IServiceCollection services)
    {
        // Sem log de pedidos HTTP: o endereço da Bot API contém o token do bot.
        services.AddHttpClient(TelegramBot.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30)).RemoveAllLoggers();
        services.AddSingleton<TelegramBot>();
        services.AddSingleton<INotificationChannel>(sp => sp.GetRequiredService<TelegramBot>());
        services.AddSingleton<NotificationDispatcher>();
        return services;
    }
}
