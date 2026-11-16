using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Bast.Application;
using Bast.Domain;
using Microsoft.Extensions.Logging;

namespace Bast.Connectors.YouTube;

/// <summary>
/// OAuth 2.0 do Google para aplicativo instalado: código de autorização com PKCE e redirecionamento
/// para um endereço de loopback (127.0.0.1) em porta livre, como recomenda a documentação oficial.
/// O refresh token fica no armazenamento seguro; o access token só existe em memória. Nenhum dos dois vai para logs.
/// </summary>
public sealed class GoogleAuth(
    IHttpClientFactory httpFactory,
    ISecretStore secrets,
    AccountService accounts,
    TimeProvider time,
    ILogger<GoogleAuth> logger)
{
    public const string HttpClientName = "google";
    internal const string ClientKey = "youtube.client";
    internal const string TokensKey = "youtube.tokens";

    private const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
    private const string ChannelsEndpoint = "https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true";

    // youtube.upload envia vídeos e thumbnails; youtube.readonly consulta o estado final do vídeo e o canal.
    private const string Scopes = "https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly";

    private static readonly TimeSpan FlowTimeout = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpires;
    private CancellationTokenSource? _flow;

    private sealed record ClientCredentials(string ClientId, string ClientSecret);

    private sealed record StoredTokens(string RefreshToken);

    public bool HasClientCredentials => secrets.Exists(ClientKey);

    /// <summary>Verdadeiro quando existe um refresh token guardado e ele não foi recusado pelo Google.</summary>
    public bool IsConnected { get; private set; } = secrets.Exists(TokensKey);

    /// <summary>Situação do último pedido de conexão, para exibição ao usuário.</summary>
    public string FlowStatus { get; private set; } = "Nenhuma conexão em andamento.";

    public void SaveClientCredentials(string clientId, string clientSecret)
    {
        secrets.Set(ClientKey, JsonSerializer.Serialize(new ClientCredentials(clientId.Trim(), clientSecret.Trim())));
    }

    /// <summary>Inicia a conexão e devolve o endereço que o usuário deve abrir no navegador deste computador.</summary>
    public string BeginAuthorization()
    {
        var client = LoadClient() ?? throw new InvalidOperationException("Informe antes o Client ID e o Client Secret do Google Cloud.");

        _flow?.Cancel();
        var flow = new CancellationTokenSource(FlowTimeout);
        _flow = flow;

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirectUri = $"http://127.0.0.1:{port}";

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));

        var query = HttpUtility.ParseQueryString("");
        query["client_id"] = client.ClientId;
        query["redirect_uri"] = redirectUri;
        query["response_type"] = "code";
        query["scope"] = Scopes;
        query["code_challenge"] = challenge;
        query["code_challenge_method"] = "S256";
        query["state"] = state;
        query["prompt"] = "consent";

        FlowStatus = "Aguardando a autorização no navegador.";
        _ = Task.Run(() => CompleteAsync(listener, client, redirectUri, verifier, state, flow.Token));
        return $"{AuthorizationEndpoint}?{query}";
    }

    private async Task CompleteAsync(TcpListener listener, ClientCredentials client, string redirectUri, string verifier, string state, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                using var connection = await listener.AcceptTcpClientAsync(ct);
                await using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(ct) ?? "";
                var parts = requestLine.Split(' ');
                var target = parts.Length >= 2 ? parts[1] : "/";
                var query = HttpUtility.ParseQueryString(target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "");

                if (query["state"] != state)
                {
                    // Pedido que não pertence a esta conexão (favicon, outra aba): ignora e continua esperando.
                    await RespondAsync(stream, 400, "Pedido não reconhecido.", ct);
                    continue;
                }

                if (query["error"] is { } error)
                {
                    FlowStatus = $"Autorização negada pelo Google: {error}.";
                    await RespondAsync(stream, 200, "A autorização não foi concedida. Você pode fechar esta aba.", ct);
                    return;
                }

                try
                {
                    var channel = await ExchangeAsync(client, redirectUri, verifier, query["code"] ?? "", ct);
                    FlowStatus = $"Conectado ao canal \"{channel}\".";
                    await RespondAsync(stream, 200, $"BAST SOCIAL PUBLISHER conectado ao canal \"{WebUtility.HtmlEncode(channel)}\". Você pode fechar esta aba.", ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
                {
                    FlowStatus = $"Falha ao concluir a conexão: {ex.Message}";
                    await RespondAsync(stream, 200, "Não foi possível concluir a conexão: " + WebUtility.HtmlEncode(ex.Message), ct);
                }

                return;
            }
        }
        catch (OperationCanceledException)
        {
            if (FlowStatus.StartsWith("Aguardando", StringComparison.Ordinal)) FlowStatus = "A autorização não foi concluída a tempo.";
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            FlowStatus = $"Falha ao receber a resposta do navegador: {ex.Message}";
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task<string> ExchangeAsync(ClientCredentials client, string redirectUri, string verifier, string code, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        using var response = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = client.ClientId,
            ["client_secret"] = client.ClientSecret,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
        }), ct);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"O Google recusou o código de autorização ({OAuthError(json)}).");
        }

        var root = json.RootElement;
        if (!root.TryGetProperty("refresh_token", out var refresh) || string.IsNullOrEmpty(refresh.GetString()))
        {
            throw new InvalidOperationException("O Google não devolveu um refresh token.");
        }

        var accessToken = root.GetProperty("access_token").GetString()!;
        var expires = time.GetUtcNow().AddSeconds(root.GetProperty("expires_in").GetInt32());

        // Confere o canal antes de guardar qualquer coisa: conta sem canal não pode publicar.
        using var channelRequest = new HttpRequestMessage(HttpMethod.Get, ChannelsEndpoint);
        channelRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var channelResponse = await http.SendAsync(channelRequest, ct);
        using var channelJson = JsonDocument.Parse(await channelResponse.Content.ReadAsStringAsync(ct));
        if (!channelResponse.IsSuccessStatusCode
            || !channelJson.RootElement.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("A conta autorizada não tem um canal do YouTube acessível.");
        }

        var channelId = items[0].GetProperty("id").GetString()!;
        var channelTitle = items[0].GetProperty("snippet").GetProperty("title").GetString() ?? channelId;

        secrets.Set(TokensKey, JsonSerializer.Serialize(new StoredTokens(refresh.GetString()!)));
        _accessToken = accessToken;
        _accessTokenExpires = expires;
        IsConnected = true;
        await accounts.SetConnectedAsync(Platform.YouTube, channelId, channelTitle, ct);
        logger.LogInformation("YouTube conectado ao canal {Channel}.", channelTitle);
        return channelTitle;
    }

    /// <summary>Access token válido, renovado automaticamente quando necessário.</summary>
    /// <exception cref="AuthRequiredException">O Google recusou o refresh token: é preciso conectar de novo.</exception>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_accessToken is not null && time.GetUtcNow() < _accessTokenExpires.AddMinutes(-2)) return _accessToken;

            var client = LoadClient();
            var stored = secrets.Get(TokensKey) is { } raw ? JsonSerializer.Deserialize<StoredTokens>(raw) : null;
            if (client is null || stored is null || !IsConnected)
            {
                throw new AuthRequiredException("A conta do YouTube não está conectada.");
            }

            var http = httpFactory.CreateClient(HttpClientName);
            using var response = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = client.ClientId,
                ["client_secret"] = client.ClientSecret,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = stored.RefreshToken,
            }), ct);

            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                using var errorJson = JsonDocument.Parse(body);
                var error = OAuthError(errorJson);
                IsConnected = false;
                _accessToken = null;
                var message = $"O Google recusou a renovação do acesso ({error}). Conecte a conta do YouTube de novo.";
                await accounts.SetNeedsReauthAsync(Platform.YouTube, message, ct);
                logger.LogError("YouTube: renovação do acesso recusada ({Error}). Integração interrompida até a reconexão.", error);
                throw new AuthRequiredException(message);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Servidor de autenticação do Google respondeu {(int)response.StatusCode}.");
            }

            using var json = JsonDocument.Parse(body);
            _accessToken = json.RootElement.GetProperty("access_token").GetString()!;
            _accessTokenExpires = time.GetUtcNow().AddSeconds(json.RootElement.GetProperty("expires_in").GetInt32());
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>Descarta o access token em memória (a API respondeu 401); o próximo uso renova.</summary>
    public void InvalidateAccessToken() => _accessToken = null;

    public async Task DisconnectAsync(CancellationToken ct)
    {
        var stored = secrets.Get(TokensKey) is { } raw ? JsonSerializer.Deserialize<StoredTokens>(raw) : null;
        secrets.Delete(TokensKey);
        IsConnected = false;
        _accessToken = null;
        await accounts.SetDisconnectedAsync(Platform.YouTube, ct);

        if (stored is null) return;
        try
        {
            // Revogação no Google é a melhor tentativa: o token local já foi apagado.
            var http = httpFactory.CreateClient(HttpClientName);
            using var response = await http.PostAsync(RevokeEndpoint,
                new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = stored.RefreshToken }), ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Não foi possível revogar o acesso no Google ({Reason}); o token local foi apagado.", ex.Message);
        }
    }

    private ClientCredentials? LoadClient() =>
        secrets.Get(ClientKey) is { } raw ? JsonSerializer.Deserialize<ClientCredentials>(raw) : null;

    private static string OAuthError(JsonDocument json)
    {
        var root = json.RootElement;
        var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : "erro desconhecido";
        var description = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
        return description is null ? error! : $"{error}: {description}";
    }

    private static async Task RespondAsync(Stream stream, int status, string message, CancellationToken ct)
    {
        var html = $"<!doctype html><html lang=\"pt-BR\"><meta charset=\"utf-8\"><title>BAST SOCIAL PUBLISHER</title>" +
                   $"<body style=\"font-family:sans-serif;margin:3rem\"><h2>BAST SOCIAL PUBLISHER</h2><p>{message}</p></body></html>";
        var body = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Bad Request")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
