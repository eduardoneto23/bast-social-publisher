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

public sealed record TelegramButton(string Text, string CallbackData);

public sealed record TelegramSent(ChannelSendStatus Status, long? MessageId, string? Error, TimeSpan? RetryAfter = null);

/// <param name="FromLinkedChat">Verdadeiro só quando o toque veio da conversa privada vinculada.</param>
public sealed record TelegramCallback(string Id, string Data, long? MessageId, bool FromLinkedChat);

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
    /// <summary>Para envio de arquivos e espera longa por botões: prazo maior que o do cliente de avisos.</summary>
    public const string UploadClientName = "telegram-upload";
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

    /// <summary>Limite de envio de arquivos por bot na Bot API em nuvem (multipart/form-data).</summary>
    public const long MaxUploadBytes = 50L * 1024 * 1024;

    /// <summary>Envia um arquivo como documento, preservando o original. Devolve o id da mensagem.</summary>
    public async Task<TelegramSent> SendDocumentAsync(string path, string fileName, string caption, CancellationToken ct)
    {
        var settings = Current;
        if (settings?.ChatId is null) return new(ChannelSendStatus.Rejected, null, "O Telegram não está conectado.");
        if (caption.Length > 1024) caption = caption[..1023] + "…";

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(settings.ChatId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), "chat_id");
            form.Add(new StringContent(caption), "caption");
            form.Add(new StringContent("true"), "disable_content_type_detection");
            var file = new StreamContent(stream);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "document", fileName);
            var (result, error) = await CallAsync(settings.Token, "sendDocument", form, ct, UploadClientName);
            return ToSent(result, error);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return new(ChannelSendStatus.Retry, null, "Sem conexão com o Telegram.");
        }
    }

    /// <summary>Mensagem com botões de resposta (callback). Devolve o id da mensagem.</summary>
    public async Task<TelegramSent> SendWithButtonsAsync(string text, IReadOnlyList<TelegramButton> buttons, CancellationToken ct)
    {
        var settings = Current;
        if (settings?.ChatId is null) return new(ChannelSendStatus.Rejected, null, "O Telegram não está conectado.");
        if (text.Length > MaxTextLength) text = text[..(MaxTextLength - 1)] + "…";

        try
        {
            var (result, error) = await CallAsync(settings.Token, "sendMessage", new
            {
                chat_id = settings.ChatId,
                text,
                link_preview_options = new { is_disabled = true },
                reply_markup = new { inline_keyboard = new[] { buttons.Select(b => new { text = b.Text, callback_data = b.CallbackData }).ToArray() } },
            }, ct);
            return ToSent(result, error);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return new(ChannelSendStatus.Retry, null, "Sem conexão com o Telegram.");
        }
    }

    private long _updateOffset;

    /// <summary>
    /// Botões tocados na conversa vinculada (getUpdates com espera longa). Toques de outras conversas são
    /// descartados. Cada chamada confirma as atualizações anteriores ao Telegram.
    /// </summary>
    public async Task<IReadOnlyList<TelegramCallback>> ReadCallbacksAsync(TimeSpan wait, CancellationToken ct)
    {
        var settings = Current;
        if (settings?.ChatId is null) return [];

        var (updates, error) = await CallAsync(settings.Token, "getUpdates",
            new { offset = _updateOffset, limit = 100, timeout = (int)wait.TotalSeconds, allowed_updates = new[] { "callback_query" } }, ct, UploadClientName);
        if (error is not null || updates.ValueKind != JsonValueKind.Array) return [];

        var found = new List<TelegramCallback>();
        foreach (var update in updates.EnumerateArray())
        {
            if (update.TryGetProperty("update_id", out var uid) && uid.TryGetInt64(out var updateId)) _updateOffset = Math.Max(_updateOffset, updateId + 1);
            if (!update.TryGetProperty("callback_query", out var query)) continue;
            var id = query.TryGetProperty("id", out var qid) ? qid.GetString() : null;
            var data = query.TryGetProperty("data", out var d) ? d.GetString() : null;
            var fromId = query.TryGetProperty("from", out var from) && from.TryGetProperty("id", out var fid) && fid.TryGetInt64(out var f) ? f : (long?)null;
            long? chatId = null, messageId = null;
            if (query.TryGetProperty("message", out var message))
            {
                if (message.TryGetProperty("chat", out var chat) && chat.TryGetProperty("id", out var cid) && cid.TryGetInt64(out var c)) chatId = c;
                if (message.TryGetProperty("message_id", out var mid) && mid.TryGetInt64(out var m)) messageId = m;
            }

            if (id is null || data is null) continue;
            var fromLinkedChat = chatId == settings.ChatId && fromId == settings.ChatId;
            found.Add(new TelegramCallback(id, data, messageId, fromLinkedChat));
        }

        return found;
    }

    /// <summary>Responde ao toque no botão (obrigatório: sem isso o Telegram mostra carregando).</summary>
    public async Task AnswerCallbackAsync(string callbackId, string text, CancellationToken ct)
    {
        var settings = Current;
        if (settings is null) return;
        await CallAsync(settings.Token, "answerCallbackQuery", new { callback_query_id = callbackId, text = text.Length > 200 ? text[..200] : text }, ct);
    }

    /// <summary>Troca o texto de uma mensagem e remove os botões dela.</summary>
    public async Task EditTextAsync(long messageId, string text, CancellationToken ct)
    {
        var settings = Current;
        if (settings?.ChatId is null) return;
        await CallAsync(settings.Token, "editMessageText", new { chat_id = settings.ChatId, message_id = messageId, text }, ct);
    }

    private static TelegramSent ToSent(JsonElement result, TelegramError? error)
    {
        if (error is null)
        {
            return new(ChannelSendStatus.Sent,
                result.ValueKind == JsonValueKind.Object && result.TryGetProperty("message_id", out var id) && id.TryGetInt64(out var m) ? m : null, null);
        }

        if (error.Code == 429) return new(ChannelSendStatus.Retry, null, $"Telegram: limite de envio atingido ({error.Description}).", error.RetryAfter);
        if (error.Code >= 500) return new(ChannelSendStatus.Retry, null, $"Telegram indisponível ({error.Description}).");
        return new(ChannelSendStatus.Rejected, null, $"O Telegram recusou a mensagem ({error.Code}: {error.Description}).");
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

    private async Task<(JsonElement Result, TelegramError? Error)> CallAsync(string token, string method, object? body, CancellationToken ct, string client = HttpClientName)
    {
        var http = httpFactory.CreateClient(client);
        var url = $"{ApiBase}/bot{token}/{method}";
        using var response = body switch
        {
            null => await http.GetAsync(url, ct),
            HttpContent content => await http.PostAsync(url, content, ct),
            _ => await http.PostAsJsonAsync(url, body, ct),
        };
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
        services.AddHttpClient(TelegramBot.UploadClientName, client => client.Timeout = TimeSpan.FromMinutes(10)).RemoveAllLoggers();
        services.AddSingleton<TelegramBot>();
        services.AddSingleton<INotificationChannel>(sp => sp.GetRequiredService<TelegramBot>());
        services.AddSingleton<NotificationDispatcher>();

        // TikTok assistido: entregue pelo mesmo bot. Ver TikTokAssistedConnector.
        services.AddSingleton<Bast.Application.Publishing.ManualPublishing>();
        services.AddSingleton<TikTokAssistedConnector>();
        services.AddSingleton<Bast.Application.Publishing.IPlatformConnector>(sp => sp.GetRequiredService<TikTokAssistedConnector>());
        services.AddSingleton<TelegramButtonsHandler>();
        return services;
    }
}
