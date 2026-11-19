using System.Security.Cryptography;
using System.Text.Json;
using Bast.Application;
using Bast.Application.Publishing;
using Bast.Domain;
using Microsoft.Extensions.Logging;

namespace Bast.Connectors.Meta;

/// <summary>Endereços da API do Threads (documentação oficial, v1.0).</summary>
internal static class ThreadsApi
{
    public const string Host = "https://graph.threads.net";
    public const string Api = Host + "/v1.0";
    public const string Authorize = "https://threads.com/oauth/authorize";
    public const string Scopes = "threads_basic,threads_content_publish";
}

public sealed record ThreadsConnection(string UserId, string Username, string Token, DateTime IssuedAtUtc, DateTime ExpiresAtUtc);

public sealed record ThreadsResult(bool Ok, string Message, string? AuthorizeUrl = null);

/// <summary>
/// Conta do Threads. O login é o OAuth oficial: o usuário autoriza no navegador, o Threads volta para o
/// endereço de retorno cadastrado no app (uma página https do próprio usuário) com um código, e o usuário
/// cola esse endereço no script. O código é trocado por um token de 60 dias, renovado automaticamente.
///
/// O token do Threads só é aceito como parâmetro access_token (a documentação não cita o cabeçalho
/// Authorization). Por isso o cliente HTTP não registra pedidos e nenhuma mensagem de erro cita endereços.
/// </summary>
public sealed class ThreadsAuth(
    IHttpClientFactory httpFactory,
    ISecretStore secrets,
    AccountService accounts,
    TimeProvider time,
    ILogger<ThreadsAuth> logger)
{
    public const string HttpClientName = "threads";
    internal const string AppKey = "threads.app";
    internal const string ConnectionKey = "threads.connection";

    /// <summary>Renova quando faltam menos de 50 dias (a cada ~10 dias). A Meta exige token com mais de 24 h.</summary>
    private static readonly TimeSpan RenewWhenLeft = TimeSpan.FromDays(50);
    private static readonly TimeSpan MinimumAge = TimeSpan.FromHours(24);

    private sealed record AppCredentials(string AppId, string AppSecret, string RedirectUri);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ThreadsConnection? _connection;
    private bool _loaded;
    private bool _revoked;
    private string? _pendingState;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public bool HasAppCredentials => secrets.Exists(AppKey);

    public string? RedirectUri => LoadApp()?.RedirectUri;

    public ThreadsConnection? Connection
    {
        get
        {
            if (_revoked) return null;
            if (!_loaded)
            {
                _connection = secrets.Get(ConnectionKey) is { } raw ? JsonSerializer.Deserialize<ThreadsConnection>(raw) : null;
                _loaded = true;
            }

            return _connection;
        }
    }

    /// <returns>Problema encontrado, ou nulo quando as credenciais foram guardadas.</returns>
    public string? SaveAppCredentials(string appId, string appSecret, string redirectUri)
    {
        if (!Uri.TryCreate(redirectUri.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return "O endereço de retorno precisa ser um endereço https completo, igual ao cadastrado no app da Meta.";
        }

        secrets.Set(AppKey, JsonSerializer.Serialize(new AppCredentials(appId.Trim(), appSecret.Trim(), uri.ToString())));
        return null;
    }

    /// <summary>Endereço de autorização a abrir no navegador. O "state" aleatório é conferido na volta.</summary>
    public ThreadsResult Start()
    {
        var app = LoadApp();
        if (app is null) return new(false, "Informe antes o ID, a chave secreta do app do Threads e o endereço de retorno.");

        _pendingState = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var url = $"{ThreadsApi.Authorize}?client_id={Uri.EscapeDataString(app.AppId)}&redirect_uri={Uri.EscapeDataString(app.RedirectUri)}" +
                  $"&scope={ThreadsApi.Scopes}&response_type=code&state={_pendingState}";
        return new(true, "Abra o endereço no navegador e autorize.", url);
    }

    /// <summary>Conclui o login com o endereço para onde o Threads redirecionou (ou só o código).</summary>
    public async Task<ThreadsResult> ConnectAsync(string redirectedUrlOrCode, CancellationToken ct)
    {
        var app = LoadApp();
        if (app is null) return new(false, "Informe antes o ID, a chave secreta do app do Threads e o endereço de retorno.");

        var (code, problem) = ParseCode(redirectedUrlOrCode);
        if (problem is not null) return new(false, problem);

        var http = httpFactory.CreateClient(HttpClientName);

        // 1. Código -> token de curta duração (1 h). O código vale 1 h e só pode ser usado uma vez.
        using var exchange = await http.PostAsync($"{ThreadsApi.Host}/oauth/access_token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = app.AppId,
            ["client_secret"] = app.AppSecret,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = app.RedirectUri,
            ["code"] = code!,
        }), ct);
        var (exchangeBody, exchangeError) = await Graph.ReadAsync(exchange, ct);
        if (exchangeError is not null)
        {
            // A Meta repete a chave recusada na mensagem ("Invalid client_secret: ..."): nunca devolver esse trecho.
            return new(false, exchangeError.Message.Contains("client_secret", StringComparison.OrdinalIgnoreCase)
                ? "A Meta recusou a chave secreta do app do Threads (ela tem 32 caracteres; clique em \"Mostrar\" no painel antes de copiar). Rode o script de novo e informe as credenciais."
                : $"O Threads recusou o código de autorização: {exchangeError.Message}");
        }
        var shortLived = Graph.Text(exchangeBody, "access_token");
        if (string.IsNullOrEmpty(shortLived)) return new(false, "O Threads não devolveu um token de acesso.");

        // 2. Curta -> longa duração (60 dias).
        using var longResponse = await http.GetAsync(
            $"{ThreadsApi.Host}/access_token?grant_type=th_exchange_token&client_secret={Uri.EscapeDataString(app.AppSecret)}&access_token={Uri.EscapeDataString(shortLived)}", ct);
        var (longBody, longError) = await Graph.ReadAsync(longResponse, ct);
        if (longError is not null)
        {
            return new(false, longError.Message.Contains("client_secret", StringComparison.OrdinalIgnoreCase)
                ? "A Meta recusou a chave secreta do app do Threads. Rode o script de novo e informe as credenciais."
                : $"O Threads não trocou o token por um de longa duração: {longError.Message}");
        }
        var longLived = Graph.Text(longBody, "access_token");
        if (string.IsNullOrEmpty(longLived)) return new(false, "O Threads não devolveu o token de longa duração.");
        var expiresIn = longBody.TryGetProperty("expires_in", out var e) && e.TryGetInt64(out var seconds) ? seconds : 60L * 24 * 3600;

        // 3. Quem é a conta.
        using var me = await http.GetAsync($"{ThreadsApi.Api}/me?fields=id,username&access_token={Uri.EscapeDataString(longLived)}", ct);
        var (meBody, meError) = await Graph.ReadAsync(me, ct);
        if (meError is not null) return new(false, $"Não foi possível ler a conta do Threads: {meError.Message}");
        var userId = Graph.Text(meBody, "id");
        if (userId is null) return new(false, "O Threads não informou o id da conta.");
        var username = Graph.Text(meBody, "username") ?? userId;

        var now = Now;
        var connection = new ThreadsConnection(userId, username, longLived, now, now.AddSeconds(expiresIn));
        Save(connection);
        _pendingState = null;
        await accounts.SetConnectedAsync(Platform.Threads, userId, "@" + username, ct);
        logger.LogInformation("Threads conectado: @{Username}.", username);
        return new(true, $"Threads conectado a @{username}.");
    }

    /// <summary>Token válido para usar agora, renovado antes de chegar perto do vencimento.</summary>
    public async Task<string?> GetTokenAsync(CancellationToken ct)
    {
        await RenewIfNeededAsync(ct);
        return Connection?.Token;
    }

    /// <summary>Renova o token de 60 dias. Chamado antes de publicar e periodicamente pelo serviço.</summary>
    public async Task RenewIfNeededAsync(CancellationToken ct)
    {
        var current = Connection;
        if (current is null) return;

        var now = Now;
        if (now >= current.ExpiresAtUtc)
        {
            await MarkNeedsReauthAsync("O token do Threads venceu. Conecte a conta de novo.", ct);
            return;
        }

        if (now - current.IssuedAtUtc < MinimumAge || current.ExpiresAtUtc - now > RenewWhenLeft) return;

        await _gate.WaitAsync(ct);
        try
        {
            current = Connection;
            if (current is null || current.ExpiresAtUtc - Now > RenewWhenLeft) return;

            using var response = await httpFactory.CreateClient(HttpClientName).GetAsync(
                $"{ThreadsApi.Host}/refresh_access_token?grant_type=th_refresh_token&access_token={Uri.EscapeDataString(current.Token)}", ct);
            var (body, error) = await Graph.ReadAsync(response, ct);
            if (error is not null)
            {
                if (error.IsAuth || error.IsPermission) await MarkNeedsReauthAsync($"O Threads recusou renovar o token: {error.Message}", ct);
                else logger.LogWarning("Threads: renovação do token adiada ({Code}).", error.Code);
                return;
            }

            var token = Graph.Text(body, "access_token");
            if (string.IsNullOrEmpty(token)) return;
            var expiresIn = body.TryGetProperty("expires_in", out var e) && e.TryGetInt64(out var seconds) ? seconds : 60L * 24 * 3600;
            var now2 = Now;
            Save(current with { Token = token, IssuedAtUtc = now2, ExpiresAtUtc = now2.AddSeconds(expiresIn) });
            logger.LogInformation("Threads: token renovado até {Expires:yyyy-MM-dd}.", now2.AddSeconds(expiresIn));
        }
        catch (Exception ex) when (Graph.IsConnectivity(ex, ct))
        {
            // Sem internet agora: o token atual continua valendo e a renovação é tentada de novo depois.
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkNeedsReauthAsync(string reason, CancellationToken ct)
    {
        if (_revoked) return;
        _revoked = true;
        await accounts.SetNeedsReauthAsync(Platform.Threads, reason, ct);
        logger.LogError("Threads: acesso recusado. Publicações no Threads interrompidas até a reconexão.");
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        secrets.Delete(ConnectionKey);
        _connection = null;
        _loaded = true;
        await accounts.SetDisconnectedAsync(Platform.Threads, ct);
    }

    private (string? Code, string? Problem) ParseCode(string input)
    {
        var text = input.Trim();
        if (text.Length == 0) return (null, "Cole o endereço para onde o Threads levou o navegador.");

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            // Só o código, copiado à mão. A Meta costuma acrescentar "#_" no fim.
            return (text.EndsWith("#_", StringComparison.Ordinal) ? text[..^2] : text, null);
        }

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        if (query["error"] is { } error)
        {
            return (null, $"O Threads não autorizou: {query["error_description"] ?? query["error_reason"] ?? error}.");
        }

        var code = query["code"];
        if (string.IsNullOrEmpty(code)) return (null, "O endereço colado não tem o código de autorização (parâmetro code).");

        var state = query["state"];
        if (_pendingState is null || state != _pendingState)
        {
            return (null, "Este endereço não corresponde à autorização iniciada agora. Rode o script de novo e use o endereço que ele abrir.");
        }

        return (code, null);
    }

    private AppCredentials? LoadApp() =>
        secrets.Get(AppKey) is { } raw ? JsonSerializer.Deserialize<AppCredentials>(raw) : null;

    private void Save(ThreadsConnection connection)
    {
        secrets.Set(ConnectionKey, JsonSerializer.Serialize(connection));
        _connection = connection;
        _loaded = true;
        _revoked = false;
    }
}

/// <summary>
/// Vídeos no Threads. A API do Threads não recebe o arquivo: ela baixa o vídeo de um endereço público.
/// O sistema abre um link temporário para o arquivo original (<see cref="IPublicFileShare"/>), que é
/// fechado assim que o Threads termina de processar o contêiner.
///
/// O arquivo do Reel do Instagram não serve: testado em 17/11/2026, o Threads o recusa com
/// error_message UNKNOWN, enquanto o arquivo original pelo link temporário é aceito.
///
/// Etapas: criar o contêiner (nada fica visível), aguardar o processamento, publicar uma única vez.
/// O estado "container-publishing" é gravado antes da publicação; se a resposta se perder, o sistema só
/// consulta o contêiner, sem pedir de novo.
/// </summary>
public sealed class ThreadsConnector(IHttpClientFactory httpFactory, ThreadsAuth threads, IPublicFileShare files) : IPlatformConnector
{
    internal const string Ready = "container";
    internal const string Publishing = "container-publishing";

    // Contêiner de vídeo costuma ficar pronto em menos de 1 minuto; consultar a cada 30 s fecha o link mais cedo.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RateLimitWait = TimeSpan.FromHours(1);

    public Platform Platform => Platform.Threads;
    public bool IsSimulation => false;
    public bool IsAvailable => threads.Connection is not null;

    public async Task<PublishOutcome> PublishAsync(PublishRequest request, CancellationToken ct)
    {
        var connection = threads.Connection;
        if (connection is null) return PublishOutcome.AuthRequired("A conta do Threads não está conectada.");

        try
        {
            var (stage, containerId) = Graph.Split(request.ExternalId);
            if (stage is Ready or Publishing) return await CheckContainerAsync(connection, request, stage, containerId, ct);
            if (stage == "" && containerId != "") return PublishOutcome.Published(containerId, null); // já publicado

            var token = await threads.GetTokenAsync(ct);
            if (token is null) return PublishOutcome.AuthRequired("A conta do Threads precisa ser conectada de novo.");

            var share = await files.ShareAsync(request.PublicationId, request.VideoPath, ct);
            if (share.Url is null)
            {
                return share.Missing ? PublishOutcome.Permanent(share.Problem!) : PublishOutcome.Transient(share.Problem!);
            }

            var create = await PostAsync($"{ThreadsApi.Api}/{connection.UserId}/threads", token, ct,
                ("media_type", "VIDEO"), ("video_url", share.Url), ("text", request.Text));
            if (create.Error is not null)
            {
                await files.StopAsync(request.PublicationId);
                return await FailAsync(create.Error, create.Status, "O Threads não criou o post", ct);
            }

            containerId = Graph.Text(create.Body, "id") ?? throw new JsonException("Resposta do Threads sem id do contêiner.");
            await request.SaveExternalIdAsync($"{Ready}:{containerId}");
            // A documentação recomenda esperar cerca de 30 s antes de publicar e consultar uma vez por minuto.
            return PublishOutcome.Processing($"{Ready}:{containerId}", PollInterval);
        }
        catch (Exception ex) when (Graph.IsConnectivity(ex, ct))
        {
            return PublishOutcome.Offline("Sem conexão com o Threads. O envio será refeito.");
        }
    }

    public async Task<PublishOutcome> CheckAsync(PublishRequest request, CancellationToken ct)
    {
        var connection = threads.Connection;
        if (connection is null) return PublishOutcome.Unknown("A conta do Threads não está conectada.");

        var (stage, containerId) = Graph.Split(request.ExternalId);
        if (stage is not (Ready or Publishing)) return PublishOutcome.NotFound("O post do Threads não chegou a ser criado; será refeito.");

        try
        {
            return await CheckContainerAsync(connection, request, stage, containerId, ct);
        }
        catch (Exception ex) when (Graph.IsConnectivity(ex, ct))
        {
            return PublishOutcome.Unknown("Sem conexão com o Threads.");
        }
    }

    private async Task<PublishOutcome> CheckContainerAsync(ThreadsConnection connection, PublishRequest request, string stage, string containerId, CancellationToken ct)
    {
        var token = await threads.GetTokenAsync(ct);
        if (token is null) return PublishOutcome.Unknown("A conta do Threads precisa ser conectada de novo para confirmar o post.");

        var response = await GetAsync($"{ThreadsApi.Api}/{containerId}?fields=status,error_message", token, ct);
        if (response.Error is not null)
        {
            if (response.Error.IsAuth || response.Error.IsPermission) await threads.MarkNeedsReauthAsync(response.Error.Message, ct);
            return PublishOutcome.Unknown($"Não foi possível consultar o post no Threads: {response.Error.Message}");
        }

        var status = Graph.Text(response.Body, "status");

        // Fora de IN_PROGRESS o Threads já terminou de buscar o arquivo: o link temporário pode fechar.
        if (status != "IN_PROGRESS") await files.StopAsync(request.PublicationId);

        switch (status)
        {
            case "PUBLISHED":
                return PublishOutcome.Published(containerId, null);

            case "ERROR":
                var reason = Graph.Text(response.Body, "error_message") ?? "motivo não informado";
                if (reason == "FAILED_DOWNLOADING_VIDEO")
                {
                    // O link temporário pode ter caído (reinício do serviço, rede): um contêiner novo abre um link novo.
                    await request.SaveExternalIdAsync("");
                    return PublishOutcome.Transient("O Threads não conseguiu baixar o vídeo; será tentado de novo com um link novo.");
                }

                return PublishOutcome.Permanent($"O Threads não aceitou o vídeo: {reason}.");

            case "EXPIRED":
                await request.SaveExternalIdAsync("");
                return PublishOutcome.NotFound("O post expirou no Threads sem ser publicado; o envio será refeito.");

            case "IN_PROGRESS":
                return PublishOutcome.Processing($"{stage}:{containerId}", PollInterval);

            case "FINISHED" when stage == Publishing:
                return PublishOutcome.Unknown("A publicação foi pedida ao Threads, mas ainda não foi confirmada. Confira o perfil; o sistema continua consultando.");

            case "FINISHED":
                return await PublishContainerAsync(connection, request, containerId, token, ct);

            default:
                return PublishOutcome.Unknown("O Threads informou um estado desconhecido para o post.");
        }
    }

    private async Task<PublishOutcome> PublishContainerAsync(ThreadsConnection connection, PublishRequest request, string containerId, string token, CancellationToken ct)
    {
        await request.SaveExternalIdAsync($"{Publishing}:{containerId}");

        var publish = await PostAsync($"{ThreadsApi.Api}/{connection.UserId}/threads_publish", token, ct, ("creation_id", containerId));
        if (publish.Error is not null)
        {
            // O Threads respondeu com erro: nada foi publicado, então o pedido pode ser repetido depois.
            await request.SaveExternalIdAsync($"{Ready}:{containerId}");
            return await FailAsync(publish.Error, publish.Status, "O Threads não publicou o post", ct);
        }

        var mediaId = Graph.Text(publish.Body, "id") ?? containerId;
        await request.SaveExternalIdAsync(mediaId);

        string? permalink = null;
        try
        {
            var media = await GetAsync($"{ThreadsApi.Api}/{mediaId}?fields=permalink", token, ct);
            if (media.Error is null) permalink = Graph.Text(media.Body, "permalink");
        }
        catch (Exception ex) when (Graph.IsConnectivity(ex, ct))
        {
            // O post já está publicado; só o link ficou sem ser lido.
        }

        return PublishOutcome.Published(mediaId, permalink);
    }

    private async Task<PublishOutcome> FailAsync(Graph.Error error, int status, string what, CancellationToken ct)
    {
        var detail = $"{what}: {error.Message}" + (error.Code > 0 ? $" (código {error.Code})" : "");
        if (error.IsAuth || error.IsPermission || status == 401)
        {
            await threads.MarkNeedsReauthAsync(detail, ct);
            return PublishOutcome.AuthRequired(detail + " Conecte a conta do Threads de novo.");
        }

        if (error.IsRateLimit) return PublishOutcome.Transient(detail, RateLimitWait);
        if (error.IsTemporary || status >= 500) return PublishOutcome.Transient(detail);
        return PublishOutcome.Permanent(detail);
    }

    private async Task<(JsonElement Body, Graph.Error? Error, int Status)> PostAsync(string url, string token, CancellationToken ct, params (string Key, string Value)[] fields)
    {
        var form = fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)).Append(new("access_token", token));
        using var response = await httpFactory.CreateClient(ThreadsAuth.HttpClientName).PostAsync(url, new FormUrlEncodedContent(form), ct);
        var (body, error) = await Graph.ReadAsync(response, ct);
        return (body, error, (int)response.StatusCode);
    }

    private async Task<(JsonElement Body, Graph.Error? Error, int Status)> GetAsync(string url, string token, CancellationToken ct)
    {
        using var response = await httpFactory.CreateClient(ThreadsAuth.HttpClientName).GetAsync($"{url}&access_token={Uri.EscapeDataString(token)}", ct);
        var (body, error) = await Graph.ReadAsync(response, ct);
        return (body, error, (int)response.StatusCode);
    }
}
