using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Bast.Application;
using Bast.Application.Publishing;
using Bast.Connectors.YouTube;
using Bast.Domain;
using Bast.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bast.Tests;

/// <summary>Servidor falso que segue o protocolo de upload retomável e o OAuth do Google.</summary>
public sealed class FakeGoogle : HttpMessageHandler, IHttpClientFactory
{
    public const string SessionUri = "https://upload.exemplo/sessao-1";

    public List<string> Log { get; } = [];
    public MemoryStream Received { get; } = new();
    public long? DeclaredTotal { get; private set; }
    public string? Metadata { get; private set; }
    public int VideosCreated { get; private set; }
    public string UploadStatus { get; set; } = "processed";
    public string? StatusExtra { get; set; }
    public bool RefreshTokenRevoked { get; set; }
    public string? ExpectedCodeChallenge { get; set; }
    public bool SessionExists { get; set; }

    /// <summary>Permite injetar uma falha: devolve uma resposta, lança uma exceção ou devolve nulo para seguir o fluxo normal.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Intercept { get; set; }

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        var range = request.Content?.Headers.ContentRange;
        Log.Add($"{request.Method} {url.Split('?')[0]}{(range is null ? "" : $" [{range}]")}");

        if (Intercept?.Invoke(request) is { } injected) return injected;

        if (url.StartsWith("https://oauth2.googleapis.com/token", StringComparison.Ordinal))
        {
            var form = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(ct));
            if (form["grant_type"] == "refresh_token")
            {
                return RefreshTokenRevoked
                    ? Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"Token has been expired or revoked."}""")
                    : Json(HttpStatusCode.OK, """{"access_token":"access-1","expires_in":3600,"token_type":"Bearer"}""");
            }

            var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"] ?? "")))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return form["code"] == "codigo-ok" && challenge == ExpectedCodeChallenge
                ? Json(HttpStatusCode.OK, """{"access_token":"access-1","expires_in":3600,"refresh_token":"refresh-secreto-123","token_type":"Bearer"}""")
                : Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        }

        if (url.Contains("/youtube/v3/channels", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, """{"items":[{"id":"UC123","snippet":{"title":"Canal Bast"}}]}""");
        }

        if (request.Headers.Authorization?.Parameter != "access-1") return Json(HttpStatusCode.Unauthorized, "{}");

        if (url.Contains("/upload/youtube/v3/videos", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
        {
            Metadata = await request.Content!.ReadAsStringAsync(ct);
            DeclaredTotal = long.Parse(request.Headers.GetValues("X-Upload-Content-Length").Single());
            SessionExists = true;
            Received.SetLength(0);
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Location = new Uri(SessionUri);
            return response;
        }

        if (url == SessionUri && request.Method == HttpMethod.Put)
        {
            if (!SessionExists) return Json(HttpStatusCode.NotFound, "{}");
            if (range!.HasRange)
            {
                // Blocos fora de sequência são rejeitados, como no serviço real.
                if (range.From != Received.Length) return Json(HttpStatusCode.BadRequest, """{"error":{"message":"bloco fora de sequência"}}""");
                var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
                Received.Position = Received.Length;
                Received.Write(bytes);
            }

            if (Received.Length == DeclaredTotal)
            {
                if (VideosCreated == 0) VideosCreated++;
                return Json(HttpStatusCode.Created, """{"id":"video-abc"}""");
            }

            var incomplete = new HttpResponseMessage((HttpStatusCode)308);
            if (Received.Length > 0) incomplete.Headers.TryAddWithoutValidation("Range", $"bytes=0-{Received.Length - 1}");
            return incomplete;
        }

        if (url.Contains("/youtube/v3/videos?part=status", StringComparison.Ordinal))
        {
            return UploadStatus == "ausente"
                ? Json(HttpStatusCode.OK, """{"items":[]}""")
                : Json(HttpStatusCode.OK, "{\"items\":[{\"id\":\"video-abc\",\"status\":{\"uploadStatus\":\"" + UploadStatus + "\"" + StatusExtra + "}}]}");
        }

        return Json(HttpStatusCode.NotFound, "{}");
    }

    public static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

public sealed class YouTubeHarness : IDisposable
{
    public YouTubeHarness(bool connected = true, int fileBytes = 2_500_000)
    {
        Secrets = new DpapiSecretStore(Env.Workspace);
        Accounts = new AccountService(Env.DbFactory, Env.Time);
        Secrets.Set(GoogleAuth.ClientKey, """{"ClientId":"cliente.apps.googleusercontent.com","ClientSecret":"segredo-do-app"}""");
        if (connected) Secrets.Set(GoogleAuth.TokensKey, """{"RefreshToken":"refresh-secreto-123"}""");

        Auth = new GoogleAuth(Google, Secrets, Accounts, Env.Time, NullLogger<GoogleAuth>.Instance);
        Connector = new YouTubeConnector(Google, Auth, Options.Create(new YouTubeOptions { ChunkSizeMb = 1 }), Env.Time, NullLogger<YouTubeConnector>.Instance);

        VideoPath = Path.Combine(Env.Root, "video.mp4");
        FileBytes = RandomNumberGenerator.GetBytes(fileBytes);
        File.WriteAllBytes(VideoPath, FileBytes);
    }

    public TestEnvironment Env { get; } = new();
    public FakeGoogle Google { get; } = new();
    public DpapiSecretStore Secrets { get; }
    public AccountService Accounts { get; }
    public GoogleAuth Auth { get; }
    public YouTubeConnector Connector { get; }
    public string VideoPath { get; }
    public byte[] FileBytes { get; }
    public List<string> SavedIds { get; } = [];

    public PublishRequest Request(string? externalId = null) => new(
        Guid.NewGuid(), "chave-1", VideoPath, null,
        "GTA 6 VAI TER POSTER!\n\nDescrição 🎮\n#GTA6 #Bast", "GTA 6 VAI TER POSTER!",
        externalId ?? SavedIds.LastOrDefault(),
        id => { SavedIds.Add(id); return Task.CompletedTask; });

    public void Dispose() => Env.Dispose();
}

public class YouTubeConnectorTests
{
    [Fact]
    public async Task Thumbnail_is_sent_and_a_refusal_becomes_a_warning_without_failing_the_video()
    {
        using var h = new YouTubeHarness();
        var thumbnail = Path.Combine(h.Env.Root, "thumbnail.jpg");
        File.WriteAllBytes(thumbnail, [0xFF, 0xD8, 0xFF, 0xE0]);
        byte[]? sent = null;
        var status = HttpStatusCode.OK;
        h.Google.Intercept = r =>
        {
            if (!r.RequestUri!.ToString().Contains("/youtube/v3/thumbnails/set?videoId=video-abc", StringComparison.Ordinal)) return null;
            sent = r.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            Assert.Equal("image/jpeg", r.Content.Headers.ContentType!.MediaType);
            return status == HttpStatusCode.OK
                ? FakeGoogle.Json(HttpStatusCode.OK, """{"items":[{"default":{"url":"https://i.ytimg.com/x.jpg"}}]}""")
                : FakeGoogle.Json(status, """{"error":{"code":403,"message":"The authenticated user doesn't have permissions to upload and set custom video thumbnails.","errors":[{"reason":"forbidden"}]}}""");
        };
        PublishRequest WithThumbnail() => h.Request() with { ThumbnailPath = thumbnail };

        var ok = await h.Connector.PublishAsync(WithThumbnail(), default);
        Assert.Equal(PublishOutcomeKind.Processing, ok.Kind);
        Assert.Null(ok.Warning);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, sent);

        using var h2 = new YouTubeHarness();
        File.WriteAllBytes(Path.Combine(h2.Env.Root, "thumbnail.jpg"), [0xFF, 0xD8]);
        h2.Google.Intercept = r => r.RequestUri!.ToString().Contains("/thumbnails/set", StringComparison.Ordinal)
            ? FakeGoogle.Json(HttpStatusCode.Forbidden, """{"error":{"code":403,"message":"The authenticated user doesn't have permissions to upload and set custom video thumbnails.","errors":[{"reason":"forbidden"}]}}""")
            : null;
        var refused = await h2.Connector.PublishAsync(h2.Request() with { ThumbnailPath = Path.Combine(h2.Env.Root, "thumbnail.jpg") }, default);

        Assert.Equal(PublishOutcomeKind.Processing, refused.Kind);
        Assert.Equal("video-abc", refused.ExternalId);
        Assert.Contains("thumbnail não foi aplicada", refused.Warning);
        Assert.Contains("youtube.com/verify", refused.Warning);
    }
    [Fact]
    public async Task Uploads_in_chunks_saves_the_session_first_and_waits_for_confirmation()
    {
        using var h = new YouTubeHarness();

        var outcome = await h.Connector.PublishAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.Processing, outcome.Kind);
        Assert.Equal("video-abc", outcome.ExternalId);
        Assert.Equal(h.FileBytes, h.Google.Received.ToArray());
        Assert.Equal(["session:" + FakeGoogle.SessionUri, "video-abc"], h.SavedIds);
        Assert.Equal(
        [
            "POST https://oauth2.googleapis.com/token",
            "POST https://www.googleapis.com/upload/youtube/v3/videos",
            $"PUT {FakeGoogle.SessionUri} [bytes 0-1048575/2500000]",
            $"PUT {FakeGoogle.SessionUri} [bytes 1048576-2097151/2500000]",
            $"PUT {FakeGoogle.SessionUri} [bytes 2097152-2499999/2500000]",
        ], h.Google.Log);
    }

    [Fact]
    public async Task Sends_the_untouched_caption_first_line_as_title_and_explicit_privacy()
    {
        using var h = new YouTubeHarness();

        await h.Connector.PublishAsync(h.Request(), default);

        using var json = JsonDocument.Parse(h.Google.Metadata!);
        var snippet = json.RootElement.GetProperty("snippet");
        Assert.Equal("GTA 6 VAI TER POSTER!", snippet.GetProperty("title").GetString());
        Assert.Equal("GTA 6 VAI TER POSTER!\n\nDescrição 🎮\n#GTA6 #Bast", snippet.GetProperty("description").GetString());
        Assert.Equal("public", json.RootElement.GetProperty("status").GetProperty("privacyStatus").GetString());
        Assert.False(json.RootElement.GetProperty("status").GetProperty("selfDeclaredMadeForKids").GetBoolean());
    }

    [Theory]
    [InlineData("processed", null, PublishOutcomeKind.Published)]
    [InlineData("uploaded", null, PublishOutcomeKind.Processing)]
    [InlineData("rejected", ",\"rejectionReason\":\"duplicate\"", PublishOutcomeKind.Permanent)]
    [InlineData("failed", ",\"failureReason\":\"invalidFile\"", PublishOutcomeKind.Permanent)]
    [InlineData("ausente", null, PublishOutcomeKind.Permanent)]
    public async Task Final_state_comes_from_the_platform_not_from_the_upload_response(string status, string? extra, PublishOutcomeKind expected)
    {
        using var h = new YouTubeHarness();
        await h.Connector.PublishAsync(h.Request(), default);
        h.Google.UploadStatus = status;
        h.Google.StatusExtra = extra;

        var outcome = await h.Connector.CheckAsync(h.Request(), default);

        Assert.Equal(expected, outcome.Kind);
        if (expected == PublishOutcomeKind.Published) Assert.Equal("https://www.youtube.com/watch?v=video-abc", outcome.Url);
        if (status == "rejected") Assert.Contains("duplicate", outcome.Error);
        if (status == "failed") Assert.Contains("invalidFile", outcome.Error);
    }

    [Fact]
    public async Task Connection_lost_mid_upload_is_resumed_from_where_it_stopped_creating_one_video()
    {
        using var h = new YouTubeHarness();
        var puts = 0;
        h.Google.Intercept = r => r.Method == HttpMethod.Put && ++puts == 2 ? throw new HttpRequestException("conexão caiu") : null;

        var first = await h.Connector.PublishAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.Offline, first.Kind);
        Assert.Equal(["session:" + FakeGoogle.SessionUri], h.SavedIds);
        Assert.Equal(1_048_576, h.Google.Received.Length);

        h.Google.Intercept = null;
        h.Google.Log.Clear();
        var second = await h.Connector.PublishAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.Processing, second.Kind);
        Assert.Equal(h.FileBytes, h.Google.Received.ToArray());
        Assert.Equal(1, h.Google.VideosCreated);
        // Retomada: pergunta o que já chegou e continua do byte seguinte, sem abrir sessão nova.
        Assert.Equal(
        [
            $"PUT {FakeGoogle.SessionUri} [bytes */2500000]",
            $"PUT {FakeGoogle.SessionUri} [bytes 1048576-2097151/2500000]",
            $"PUT {FakeGoogle.SessionUri} [bytes 2097152-2499999/2500000]",
        ], h.Google.Log);
    }

    [Fact]
    public async Task Lost_final_response_is_recovered_from_the_session_without_sending_again()
    {
        using var h = new YouTubeHarness();
        await h.Connector.PublishAsync(h.Request(), default);
        h.SavedIds.RemoveAt(1); // a queda aconteceu antes de gravar o id do vídeo
        h.Google.Log.Clear();

        var check = await h.Connector.CheckAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.Processing, check.Kind);
        Assert.Equal("video-abc", check.ExternalId);
        Assert.Equal("video-abc", h.SavedIds.Last());
        Assert.Equal([$"PUT {FakeGoogle.SessionUri} [bytes */2500000]"], h.Google.Log);
        Assert.Equal(1, h.Google.VideosCreated);
    }

    [Fact]
    public async Task Publication_that_already_has_a_video_is_never_uploaded_again()
    {
        using var h = new YouTubeHarness();
        h.Google.UploadStatus = "processed";

        var outcome = await h.Connector.PublishAsync(h.Request("video-abc"), default);

        Assert.Equal(PublishOutcomeKind.Published, outcome.Kind);
        Assert.DoesNotContain(h.Google.Log, l => l.StartsWith("PUT", StringComparison.Ordinal) || l.Contains("/upload/"));
    }

    [Fact]
    public async Task Incomplete_session_reported_by_check_allows_a_safe_retry()
    {
        using var h = new YouTubeHarness();
        var puts = 0;
        h.Google.Intercept = r => r.Method == HttpMethod.Put && ++puts == 2 ? throw new HttpRequestException("caiu") : null;
        await h.Connector.PublishAsync(h.Request(), default);
        h.Google.Intercept = null;

        var check = await h.Connector.CheckAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.NotFound, check.Kind);
        Assert.Equal(0, h.Google.VideosCreated);
    }

    [Fact]
    public async Task Expired_session_starts_a_new_one()
    {
        using var h = new YouTubeHarness();
        h.SavedIds.Add("session:" + FakeGoogle.SessionUri);
        h.Google.SessionExists = false;

        var outcome = await h.Connector.PublishAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.Processing, outcome.Kind);
        Assert.Contains("POST https://www.googleapis.com/upload/youtube/v3/videos", h.Google.Log);
        Assert.Equal(h.FileBytes, h.Google.Received.ToArray());
    }

    [Fact]
    public async Task Server_error_on_a_chunk_asks_what_was_received_and_continues()
    {
        using var h = new YouTubeHarness();
        var puts = 0;
        h.Google.Intercept = r => r.Method == HttpMethod.Put && ++puts == 2 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null;

        var outcome = await h.Connector.PublishAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.Processing, outcome.Kind);
        Assert.Equal(h.FileBytes, h.Google.Received.ToArray());
        Assert.Contains($"PUT {FakeGoogle.SessionUri} [bytes */2500000]", h.Google.Log);
    }

    [Fact]
    public async Task Daily_quota_exhausted_waits_for_the_reset_instead_of_failing()
    {
        using var h = new YouTubeHarness();
        h.Google.Intercept = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.Contains("/upload/")
            ? FakeGoogle.Json(HttpStatusCode.Forbidden, """{"error":{"code":403,"message":"The request cannot be completed because you have exceeded your quota.","errors":[{"reason":"quotaExceeded"}]}}""")
            : null;

        var outcome = await h.Connector.PublishAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.Transient, outcome.Kind);
        Assert.Contains("quotaExceeded", outcome.Error);
        Assert.InRange(outcome.RetryAfter!.Value, TimeSpan.FromMinutes(10), TimeSpan.FromHours(25));
        Assert.Empty(h.SavedIds);
    }

    [Fact]
    public async Task Rejected_metadata_is_a_permanent_failure_with_the_platform_reason()
    {
        using var h = new YouTubeHarness();
        h.Google.Intercept = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.Contains("/upload/")
            ? FakeGoogle.Json(HttpStatusCode.BadRequest, """{"error":{"code":400,"message":"The request metadata specifies an invalid video title.","errors":[{"reason":"invalidTitle"}]}}""")
            : null;

        var outcome = await h.Connector.PublishAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.Permanent, outcome.Kind);
        Assert.Contains("invalidTitle", outcome.Error);
    }

    [Fact]
    public async Task Revoked_permission_stops_the_integration_and_flags_the_account()
    {
        using var h = new YouTubeHarness();
        h.Google.RefreshTokenRevoked = true;
        Assert.True(h.Connector.IsAvailable);

        var outcome = await h.Connector.PublishAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.AuthRequired, outcome.Kind);
        Assert.False(h.Connector.IsAvailable);
        var account = Assert.Single(await h.Accounts.GetAllAsync(default));
        Assert.Equal(AccountStatus.NeedsReauth, account.Status);
        Assert.Contains("invalid_grant", account.LastError);
        Assert.DoesNotContain(h.Google.Log, l => l.Contains("/upload/"));
    }

    [Fact]
    public async Task Access_token_is_renewed_once_and_reused()
    {
        using var h = new YouTubeHarness();

        await h.Connector.PublishAsync(h.Request(), default);
        await h.Connector.CheckAsync(h.Request(), default);

        Assert.Single(h.Google.Log, l => l.Contains("oauth2.googleapis.com/token"));
    }

    [Fact]
    public void Not_connected_account_makes_the_connector_unavailable()
    {
        using var h = new YouTubeHarness(connected: false);
        Assert.False(h.Connector.IsAvailable);
        var registry = new ConnectorRegistry([h.Connector], new BastOptions());
        Assert.Empty(registry.Available);
        Assert.Null(registry.Get(Platform.YouTube));
    }
}

public class GoogleAuthFlowTests
{
    [Fact]
    public async Task Browser_authorization_with_pkce_connects_the_channel_and_stores_only_encrypted_tokens()
    {
        using var h = new YouTubeHarness(connected: false);

        var url = new Uri(h.Auth.BeginAuthorization());
        var query = HttpUtility.ParseQueryString(url.Query);
        Assert.Equal("accounts.google.com", url.Host);
        Assert.Equal("cliente.apps.googleusercontent.com", query["client_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Contains("youtube.upload", query["scope"]);
        Assert.StartsWith("http://127.0.0.1:", query["redirect_uri"]);
        h.Google.ExpectedCodeChallenge = query["code_challenge"];

        using var browser = new HttpClient();
        // Um pedido com state errado não conclui nem cancela a conexão.
        var wrong = await browser.GetAsync($"{query["redirect_uri"]}/?code=codigo-ok&state=errado");
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.False(h.Auth.IsConnected);

        var page = await browser.GetStringAsync($"{query["redirect_uri"]}/?code=codigo-ok&state={query["state"]}");

        Assert.Contains("Canal Bast", page);
        Assert.True(h.Auth.IsConnected);
        Assert.True(h.Connector.IsAvailable);
        var account = Assert.Single(await h.Accounts.GetAllAsync(default));
        Assert.Equal((AccountStatus.Connected, "UC123", "Canal Bast"), (account.Status, account.ExternalAccountId, account.DisplayName));

        // O refresh token não aparece em texto puro no disco nem no banco.
        var secretFile = Directory.GetFiles(Path.Combine(h.Env.Workspace.SystemPath, "secrets"), "youtube.tokens.bin").Single();
        Assert.DoesNotContain("refresh-secreto-123", Encoding.Latin1.GetString(File.ReadAllBytes(secretFile)));
        Assert.Contains("refresh-secreto-123", h.Secrets.Get(GoogleAuth.TokensKey));
    }

    [Fact]
    public async Task Denied_consent_leaves_the_account_disconnected()
    {
        using var h = new YouTubeHarness(connected: false);
        var query = HttpUtility.ParseQueryString(new Uri(h.Auth.BeginAuthorization()).Query);

        using var browser = new HttpClient();
        await browser.GetStringAsync($"{query["redirect_uri"]}/?error=access_denied&state={query["state"]}");

        Assert.False(h.Auth.IsConnected);
        Assert.Contains("access_denied", h.Auth.FlowStatus);
    }

    [Fact]
    public async Task Disconnect_removes_the_stored_token()
    {
        using var h = new YouTubeHarness();

        await h.Auth.DisconnectAsync(default);

        Assert.False(h.Auth.IsConnected);
        Assert.False(h.Secrets.Exists(GoogleAuth.TokensKey));
        Assert.Equal(AccountStatus.Disconnected, Assert.Single(await h.Accounts.GetAllAsync(default)).Status);
    }
}

public class WaitingOutcomesTests
{
    [Fact]
    public async Task Internet_outage_and_disconnected_account_wait_without_consuming_attempts()
    {
        using var h = new EngineHarness(Platform.YouTube);
        await h.ImportAsync("Video 1");
        await h.CycleAsync();
        var youtube = h.Connectors[Platform.YouTube];
        youtube.OnPublish = _ => PublishOutcome.Offline("sem internet");
        h.SetTime(EngineHarness.Local(14, 10));

        // Vinte ciclos sem internet: mais que o limite de 5 tentativas.
        for (var i = 0; i < 20; i++)
        {
            await h.CycleAsync();
            h.Env.Time.Advance(TimeSpan.FromMinutes(3));
        }

        var waiting = h.Publication("Video 1", Platform.YouTube);
        Assert.Equal(PublicationState.RetryWait, waiting.State);
        Assert.Equal(0, waiting.AttemptCount);
        Assert.Equal("sem internet", waiting.LastError);

        youtube.OnPublish = _ => PublishOutcome.AuthRequired("conecte de novo");
        await h.CycleAsync();
        Assert.Equal(PublicationState.RetryWait, h.Publication("Video 1", Platform.YouTube).State);

        youtube.OnPublish = r => PublishOutcome.Published("yt-1", null);
        h.Env.Time.Advance(TimeSpan.FromMinutes(6));
        await h.CycleAsync();
        Assert.Equal(PublicationState.Published, h.Publication("Video 1", Platform.YouTube).State);
        Assert.Equal(1, h.Publication("Video 1", Platform.YouTube).AttemptCount);
    }
}
