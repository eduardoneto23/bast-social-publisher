using System.Net.Http.Headers;
using System.Text.Json;
using Bast.Application;
using Bast.Application.Publishing;
using Bast.Domain;
using Microsoft.Extensions.Logging;

namespace Bast.Connectors.Meta;

/// <summary>Chamadas à Graph API da Meta e leitura das respostas de erro.</summary>
internal static class Graph
{
    public const string Version = "v27.0";
    public const string Api = "https://graph.facebook.com/" + Version;
    public const string Upload = "https://rupload.facebook.com";

    /// <param name="Retriable">Preenchido só nas respostas do servidor de upload (debug_info.retriable).</param>
    public sealed record Error(int Code, int? Subcode, string Message, bool? Retriable = null)
    {
        /// <summary>Token expirado, revogado ou inválido.</summary>
        public bool IsAuth => Code is 190 or 102;

        /// <summary>Permissão negada ou ausente.</summary>
        public bool IsPermission => Code == 10 || Code is >= 200 and < 300;

        public bool IsRateLimit => Code is 4 or 17 or 32 or 341 or 613;

        /// <summary>Problema temporário do lado da Meta.</summary>
        public bool IsTemporary => Code is 1 or 2;
    }

    public static async Task<(JsonElement Body, Error? Error)> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonElement body = default;
        try
        {
            using var json = JsonDocument.Parse(text);
            body = json.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Resposta que não é JSON: tratada abaixo pelo código HTTP.
        }

        if (body.ValueKind == JsonValueKind.Object)
        {
            if (body.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                return (body, new Error(
                    error.TryGetProperty("code", out var code) && code.TryGetInt32(out var c) ? c : (int)response.StatusCode,
                    error.TryGetProperty("error_subcode", out var sub) && sub.TryGetInt32(out var s) ? s : null,
                    Text(error, "error_user_msg") ?? Text(error, "message") ?? "erro sem descrição"));
            }

            if (body.TryGetProperty("debug_info", out var debug) && debug.ValueKind == JsonValueKind.Object)
            {
                return (body, new Error(0, null, Text(debug, "message") ?? Text(debug, "type") ?? "falha no upload",
                    debug.TryGetProperty("retriable", out var r) && r.ValueKind is JsonValueKind.True or JsonValueKind.False ? r.GetBoolean() : null));
            }
        }

        return response.IsSuccessStatusCode
            ? (body, null)
            : (body, new Error((int)response.StatusCode, null, $"resposta HTTP {(int)response.StatusCode}"));
    }

    public static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Separa "prefixo:valor" do identificador gravado na publicação.</summary>
    public static (string Stage, string Id) Split(string? externalId)
    {
        if (string.IsNullOrEmpty(externalId)) return ("", "");
        var colon = externalId.IndexOf(':');
        return colon < 0 ? ("", externalId) : (externalId[..colon], externalId[(colon + 1)..]);
    }

    /// <summary>Falha de rede ou tempo esgotado (não um cancelamento pedido pelo serviço).</summary>
    public static bool IsConnectivity(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested);
}

public sealed record MetaPage(string Id, string Name, bool HasInstagram, string? InstagramUsername);

public sealed record MetaConnection(string PageId, string PageName, string PageToken, string? InstagramUserId, string? InstagramUsername);

public sealed record MetaConnectResult(bool Connected, IReadOnlyList<MetaPage> Pages, string Message);

/// <summary>
/// Credenciais da Meta para Facebook (Página) e Instagram (conta profissional vinculada à Página).
/// O usuário gera um token de acesso no Explorador da Graph API; o sistema o troca por um de longa
/// duração e obtém o token da Página, que não tem data de expiração. Todos ficam no armazenamento
/// seguro e são enviados à Meta apenas no cabeçalho Authorization, nunca em endereços nem em logs.
/// </summary>
public sealed class MetaAuth(
    IHttpClientFactory httpFactory,
    ISecretStore secrets,
    AccountService accounts,
    ILogger<MetaAuth> logger)
{
    public const string HttpClientName = "meta";
    internal const string AppKey = "meta.app";
    internal const string ConnectionKey = "meta.connection";

    private sealed record AppCredentials(string AppId, string AppSecret);

    private MetaConnection? _connection;
    private bool _loaded;
    private bool _revoked;

    public bool HasAppCredentials => secrets.Exists(AppKey);

    public MetaConnection? Connection
    {
        get
        {
            if (_revoked) return null;
            if (!_loaded)
            {
                _connection = secrets.Get(ConnectionKey) is { } raw ? JsonSerializer.Deserialize<MetaConnection>(raw) : null;
                _loaded = true;
            }

            return _connection;
        }
    }

    public void SaveAppCredentials(string appId, string appSecret) =>
        secrets.Set(AppKey, JsonSerializer.Serialize(new AppCredentials(appId.Trim(), appSecret.Trim())));

    /// <summary>
    /// Conecta a partir de um token de usuário gerado no Explorador da Graph API.
    /// Se a conta administra mais de uma Página e nenhuma foi indicada, devolve a lista para escolha.
    /// </summary>
    public async Task<MetaConnectResult> ConnectAsync(string userToken, string? pageId, CancellationToken ct)
    {
        var app = secrets.Get(AppKey) is { } raw ? JsonSerializer.Deserialize<AppCredentials>(raw) : null;
        if (app is null) return new(false, [], "Informe antes o ID e a chave secreta do aplicativo da Meta.");

        var http = httpFactory.CreateClient(HttpClientName);

        // Troca por token de longa duração. Os segredos vão no corpo do pedido, não no endereço.
        using var exchange = await http.PostAsync($"{Graph.Api}/oauth/access_token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "fb_exchange_token",
            ["client_id"] = app.AppId,
            ["client_secret"] = app.AppSecret,
            ["fb_exchange_token"] = userToken.Trim(),
        }), ct);
        var (exchangeBody, exchangeError) = await Graph.ReadAsync(exchange, ct);
        if (exchangeError is not null)
        {
            // Este pedido leva o token e a chave secreta do aplicativo: a Meta diz qual dos dois recusou.
            return new(false, [], exchangeError.Message.Contains("client secret", StringComparison.OrdinalIgnoreCase)
                ? $"A Meta recusou a chave secreta do aplicativo (ela tem 32 caracteres; confira com scripts\\check-meta-secret.ps1): {exchangeError.Message}"
                : $"A Meta recusou o token informado: {exchangeError.Message}");
        }
        var longLived = Graph.Text(exchangeBody, "access_token");
        if (string.IsNullOrEmpty(longLived)) return new(false, [], "A Meta não devolveu um token de longa duração.");

        // Páginas administradas, com o token de cada uma e o Instagram profissional vinculado.
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{Graph.Api}/me/accounts?fields=id,name,access_token,instagram_business_account{{id,username}}&limit=100");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", longLived);
        using var response = await http.SendAsync(request, ct);
        var (body, error) = await Graph.ReadAsync(response, ct);
        if (error is not null) return new(false, [], $"Não foi possível listar as Páginas: {error.Message}");

        var found = new List<MetaConnection>();
        if (body.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                var token = Graph.Text(item, "access_token");
                var id = Graph.Text(item, "id");
                if (token is null || id is null) continue;
                item.TryGetProperty("instagram_business_account", out var instagram);
                found.Add(new MetaConnection(id, Graph.Text(item, "name") ?? id, token, Graph.Text(instagram, "id"), Graph.Text(instagram, "username")));
            }
        }

        var pages = found.Select(p => new MetaPage(p.PageId, p.PageName, p.InstagramUserId is not null, p.InstagramUsername)).ToList();
        if (found.Count == 0)
        {
            return new(false, [], "O token não dá acesso a nenhuma Página do Facebook. Confira as permissões pages_show_list, pages_read_engagement e pages_manage_posts.");
        }

        var chosen = pageId is null ? (found.Count == 1 ? found[0] : null) : found.FirstOrDefault(p => p.PageId == pageId);
        if (chosen is null)
        {
            return new(false, pages, pageId is null
                ? "A conta administra mais de uma Página. Indique qual usar."
                : "A Página indicada não está entre as que o token acessa.");
        }

        secrets.Set(ConnectionKey, JsonSerializer.Serialize(chosen));
        _connection = chosen;
        _loaded = true;
        _revoked = false;

        await accounts.SetConnectedAsync(Platform.Facebook, chosen.PageId, chosen.PageName, ct);
        if (chosen.InstagramUserId is not null)
        {
            await accounts.SetConnectedAsync(Platform.Instagram, chosen.InstagramUserId, "@" + (chosen.InstagramUsername ?? chosen.InstagramUserId), ct);
        }
        else
        {
            await accounts.SetNeedsReauthAsync(Platform.Instagram,
                $"A Página \"{chosen.PageName}\" não tem uma conta profissional do Instagram vinculada.", ct);
        }

        logger.LogInformation("Meta conectada: Página {Page}; Instagram {Instagram}.", chosen.PageName, chosen.InstagramUsername ?? "não vinculado");
        return new(true, pages, chosen.InstagramUserId is null
            ? $"Facebook conectado à Página \"{chosen.PageName}\". Nenhum Instagram profissional está vinculado a ela."
            : $"Facebook conectado à Página \"{chosen.PageName}\" e Instagram conectado a @{chosen.InstagramUsername}.");
    }

    /// <summary>A Meta recusou o token ou a permissão: interrompe as duas integrações até a reconexão.</summary>
    public async Task MarkNeedsReauthAsync(string reason, CancellationToken ct)
    {
        if (_revoked) return;
        _revoked = true;
        await accounts.SetNeedsReauthAsync(Platform.Facebook, reason, ct);
        await accounts.SetNeedsReauthAsync(Platform.Instagram, reason, ct);
        logger.LogError("Meta: acesso recusado. Integrações do Facebook e do Instagram interrompidas até a reconexão.");
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        secrets.Delete(ConnectionKey);
        _connection = null;
        _loaded = true;
        await accounts.SetDisconnectedAsync(Platform.Facebook, ct);
        await accounts.SetDisconnectedAsync(Platform.Instagram, ct);
    }
}

/// <summary>Base dos conectores da Meta: envio autenticado e tradução dos erros da Graph API.</summary>
public abstract class MetaConnectorBase(IHttpClientFactory httpFactory, MetaAuth auth)
{
    private static readonly TimeSpan RateLimitWait = TimeSpan.FromHours(1);

    protected MetaAuth Auth => auth;

    private protected async Task<(System.Text.Json.JsonElement Body, Graph.Error? Error, int Status)> SendAsync(
        HttpMethod method, string url, string token, HttpContent? content, CancellationToken ct, string scheme = "Bearer")
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue(scheme, token);
        using var response = await httpFactory.CreateClient(MetaAuth.HttpClientName).SendAsync(request, ct);
        var (body, error) = await Graph.ReadAsync(response, ct);
        return (body, error, (int)response.StatusCode);
    }

    private protected static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    /// <summary>Traduz um erro da Meta em falha temporária, definitiva ou de credencial.</summary>
    private protected async Task<PublishOutcome> FailAsync(Graph.Error error, int status, string what, CancellationToken ct)
    {
        var detail = $"{what}: {error.Message}" + (error.Code > 0 ? $" (código {error.Code}{(error.Subcode is null ? "" : "/" + error.Subcode)})" : "");

        if (error.IsAuth || error.IsPermission)
        {
            await auth.MarkNeedsReauthAsync(detail, ct);
            return PublishOutcome.AuthRequired(detail + " Conecte a conta da Meta de novo.");
        }

        if (error.IsRateLimit) return PublishOutcome.Transient(detail, RateLimitWait);
        if (error.IsTemporary || status >= 500 || error.Retriable == true) return PublishOutcome.Transient(detail);
        return PublishOutcome.Permanent(detail);
    }
}
