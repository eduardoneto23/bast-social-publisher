using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Bast.Application;
using Bast.Application.Publishing;
using Bast.Connectors.Meta;
using Bast.Domain;
using Bast.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bast.Tests;

/// <summary>Servidor falso que segue os fluxos documentados da Graph API para Reels do Facebook e do Instagram.</summary>
public sealed class FakeMeta : HttpMessageHandler, IHttpClientFactory
{
    public List<string> Log { get; } = [];
    public string AccountsJson { get; set; } =
        """{"data":[{"id":"page-1","name":"Bast","access_token":"page-token","instagram_business_account":{"id":"ig-1","username":"canal_exemplo"}}]}""";

    // Facebook
    public MemoryStream FacebookReceived { get; } = new();
    public long? FacebookDeclaredSize { get; private set; }
    public int FacebookVideosStarted { get; private set; }
    public int ReelsPublished { get; private set; }
    public string? FacebookDescription { get; private set; }
    public bool FacebookProcessed { get; set; } = true;
    public string? FacebookProcessingError { get; set; }
    public int? DropFacebookUploadAfterBytes { get; set; }
    public byte[]? FacebookThumbnail { get; private set; }
    public string? FacebookThumbnailPreferred { get; private set; }
    public string? FacebookThumbnailError { get; set; }

    // Instagram
    public int ContainersCreated { get; private set; }
    public int MediaPublishCalls { get; private set; }
    public string? InstagramCaption { get; private set; }
    public List<string?> InstagramCoverUrls { get; } = [];
    public string ContainerStatus { get; set; } = "FINISHED";
    public Dictionary<string, byte[]> ContainerUploads { get; } = [];
    public bool InstagramPublished { get; private set; }
    public string? InstagramMediaUrl { get; set; } = "https://cdn.exemplo/reel-1.mp4?oe=ABC";

    public Func<HttpRequestMessage, HttpResponseMessage?>? Intercept { get; set; }

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath;
        Log.Add($"{request.Method} {uri.Host}{path}");

        if (Intercept?.Invoke(request) is { } injected) return injected;

        if (path.EndsWith("/oauth/access_token", StringComparison.Ordinal))
        {
            var form = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(ct));
            return form["fb_exchange_token"] == "token-do-explorador" && form["client_secret"] == "segredo-do-app"
                ? Json("""{"access_token":"user-long","token_type":"bearer","expires_in":5183944}""")
                : Error(190, "Invalid OAuth access token.");
        }

        var token = request.Headers.Authorization?.Parameter;
        if (path.EndsWith("/me/accounts", StringComparison.Ordinal))
        {
            return token == "user-long" ? Json(AccountsJson) : Error(190, "Invalid OAuth access token.");
        }

        if (token != "page-token") return Error(190, "Error validating access token: Session has expired.", 463);

        // ---------- Facebook ----------
        if (path.EndsWith("/page-1/video_reels", StringComparison.Ordinal))
        {
            var form = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(ct));
            if (form["upload_phase"] == "start")
            {
                FacebookVideosStarted++;
                FacebookReceived.SetLength(0);
                return Json("""{"video_id":"vid-1","upload_url":"https://rupload.facebook.com/video-upload/v27.0/vid-1"}""");
            }

            if (form["video_id"] != "vid-1" || form["video_state"] != "PUBLISHED") return Error(100, "Invalid parameter");
            if (ReelsPublished == 0) ReelsPublished++; // um vídeo só gera um Reel
            FacebookDescription = form["description"];
            return Json("""{"success":true}""");
        }

        if (uri.Host == "rupload.facebook.com" && path.EndsWith("/video-upload/v27.0/vid-1", StringComparison.Ordinal))
        {
            var offset = long.Parse(request.Content!.Headers.GetValues("offset").Single());
            FacebookDeclaredSize = long.Parse(request.Content.Headers.GetValues("file_size").Single());
            if (offset != FacebookReceived.Length) return Json("""{"debug_info":{"retriable":false,"type":"OffsetError","message":"offset inesperado"}}""", HttpStatusCode.BadRequest);
            var bytes = await request.Content.ReadAsByteArrayAsync(ct);
            FacebookReceived.Position = FacebookReceived.Length;
            if (DropFacebookUploadAfterBytes is { } keep)
            {
                DropFacebookUploadAfterBytes = null;
                FacebookReceived.Write(bytes, 0, keep);
                throw new HttpRequestException("conexão caiu durante o envio");
            }

            FacebookReceived.Write(bytes);
            return Json("""{"success":true}""");
        }

        if (path.EndsWith("/vid-1/thumbnails", StringComparison.Ordinal))
        {
            if (FacebookThumbnailError is not null) return Error(380, FacebookThumbnailError);
            foreach (var part in (MultipartFormDataContent)request.Content!)
            {
                var name = part.Headers.ContentDisposition?.Name?.Trim('"');
                if (name == "source") FacebookThumbnail = await part.ReadAsByteArrayAsync(ct);
                if (name == "is_preferred") FacebookThumbnailPreferred = await part.ReadAsStringAsync(ct);
            }

            return Json("""{"data":[{"id":"thumb-1","is_preferred":true}]}""");
        }

        if (path.EndsWith("/vid-1", StringComparison.Ordinal))
        {
            var uploaded = FacebookDeclaredSize is not null && FacebookReceived.Length == FacebookDeclaredSize;
            var published = ReelsPublished > 0 && FacebookProcessed && FacebookProcessingError is null;
            var processing = FacebookProcessingError is not null
                ? "{\"status\":\"error\",\"error\":{\"message\":\"" + FacebookProcessingError + "\"}}"
                : $$"""{"status":"{{(FacebookProcessed && uploaded ? "complete" : "in_progress")}}"}""";
            var publishing = ReelsPublished == 0
                ? """{"status":"not_started"}"""
                : $$"""{"status":"{{(published ? "complete" : "in_progress")}}","publish_status":"{{(published ? "published" : "draft")}}"}""";
            return Json($$"""
                {"status":{"video_status":"{{(published ? "ready" : uploaded ? "upload_complete" : "uploading")}}",
                 "uploading_phase":{"status":"{{(uploaded ? "complete" : "in_progress")}}","bytes_transfered":{{FacebookReceived.Length}}},
                 "processing_phase":{{processing}},"publishing_phase":{{publishing}}},
                 "permalink_url":"/reel/987654/","id":"vid-1"}
                """);
        }

        // ---------- Instagram ----------
        if (path.EndsWith("/ig-1/media", StringComparison.Ordinal))
        {
            var form = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(ct));
            if (form["media_type"] != "REELS" || form["upload_type"] != "resumable") return Error(100, "Invalid parameter");
            InstagramCaption = form["caption"];
            InstagramCoverUrls.Add(form["cover_url"]);
            var id = $"c{++ContainersCreated}";
            return Json($$"""{"id":"{{id}}","uri":"https://rupload.facebook.com/ig-api-upload/v27.0/{{id}}"}""");
        }

        if (uri.Host == "rupload.facebook.com" && path.Contains("/ig-api-upload/", StringComparison.Ordinal))
        {
            ContainerUploads[path[(path.LastIndexOf('/') + 1)..]] = await request.Content!.ReadAsByteArrayAsync(ct);
            return Json("""{"success":true,"message":"Upload successful."}""");
        }

        if (path.EndsWith("/ig-1/media_publish", StringComparison.Ordinal))
        {
            MediaPublishCalls++;
            InstagramPublished = true;
            return Json("""{"id":"media-1"}""");
        }

        if (path.EndsWith("/media-1", StringComparison.Ordinal))
        {
            // media_url some quando o Reel tem música protegida (documentação do IG Media).
            var mediaUrl = InstagramMediaUrl is null ? "" : $",\"media_url\":\"{InstagramMediaUrl}\"";
            return Json($$"""{"permalink":"https://www.instagram.com/reel/ABC123/","id":"media-1","media_type":"VIDEO"{{mediaUrl}}}""");
        }

        var last = path[(path.LastIndexOf('/') + 1)..];
        if (last.StartsWith('c'))
        {
            var status = InstagramPublished ? "PUBLISHED" : ContainerStatus;
            return Json($$"""{"status_code":"{{status}}","status":"{{(status == "ERROR" ? "2207026: formato de vídeo não suportado" : status)}}","id":"{{last}}"}""");
        }

        return Error(100, "Unsupported request");
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Error(int code, string message, int? subcode = null) =>
        Json("{\"error\":{\"message\":\"" + message + "\",\"type\":\"OAuthException\",\"code\":" + code + (subcode is null ? "" : ",\"error_subcode\":" + subcode) + ",\"fbtrace_id\":\"x\"}}",
            HttpStatusCode.BadRequest);
}

public sealed class MetaHarness : IDisposable
{
    public MetaHarness(bool connected = true)
    {
        Secrets = new DpapiSecretStore(Env.Workspace);
        Accounts = new AccountService(Env.DbFactory, Env.Time);
        Auth = new MetaAuth(Meta, Secrets, Accounts, NullLogger<MetaAuth>.Instance);
        Auth.SaveAppCredentials("app-123", "segredo-do-app");
        if (connected)
        {
            var result = Auth.ConnectAsync("token-do-explorador", null, default).GetAwaiter().GetResult();
            Assert.True(result.Connected, result.Message);
            Meta.Log.Clear();
        }

        Facebook = new FacebookConnector(Meta, Auth);
        Instagram = new InstagramConnector(Meta, Auth, Options.Create(new InstagramOptions()), Files);
        VideoPath = Path.Combine(Env.Root, "video.mp4");
        FileBytes = RandomNumberGenerator.GetBytes(300_000);
        File.WriteAllBytes(VideoPath, FileBytes);
    }

    public const string Caption = "GTA 6 VAI TER POSTER!\n\nDescrição 🎮\n#GTA6 #Bast";

    public TestEnvironment Env { get; } = new();
    public FakeMeta Meta { get; } = new();
    public DpapiSecretStore Secrets { get; }
    public AccountService Accounts { get; }
    public MetaAuth Auth { get; }
    public FacebookConnector Facebook { get; }
    public InstagramConnector Instagram { get; }
    public string VideoPath { get; }
    public byte[] FileBytes { get; }
    public List<string> SavedIds { get; } = [];
    public FakeFileShare Files { get; } = new();
    public Guid PublicationId { get; } = Guid.NewGuid();

    public PublishRequest Request(string? thumbnailPath = null) => new(
        PublicationId, "chave-1", VideoPath, thumbnailPath, Caption, "GTA 6 VAI TER POSTER!",
        SavedIds.LastOrDefault(),
        id => { SavedIds.Add(id); return Task.CompletedTask; });

    public int Count(string fragment) => Meta.Log.Count(l => l.Contains(fragment, StringComparison.Ordinal));

    public void Dispose() => Env.Dispose();
}

public class MetaAuthTests
{
    [Fact]
    public async Task Explorer_token_connects_page_and_instagram_and_only_encrypted_tokens_are_stored()
    {
        using var h = new MetaHarness(connected: false);

        var result = await h.Auth.ConnectAsync("token-do-explorador", null, default);

        Assert.True(result.Connected);
        Assert.True(h.Facebook.IsAvailable);
        Assert.True(h.Instagram.IsAvailable);
        var accounts = (await h.Accounts.GetAllAsync(default)).ToDictionary(a => a.Platform);
        Assert.Equal((AccountStatus.Connected, "Bast"), (accounts[Platform.Facebook].Status, accounts[Platform.Facebook].DisplayName));
        Assert.Equal((AccountStatus.Connected, "@canal_exemplo"), (accounts[Platform.Instagram].Status, accounts[Platform.Instagram].DisplayName));

        foreach (var file in Directory.GetFiles(Path.Combine(h.Env.Workspace.SystemPath, "secrets")))
        {
            var raw = Encoding.Latin1.GetString(File.ReadAllBytes(file));
            Assert.DoesNotContain("page-token", raw);
            Assert.DoesNotContain("segredo-do-app", raw);
        }

        // Um novo processo (reinício do serviço) recupera a conexão do armazenamento seguro.
        var restarted = new MetaAuth(h.Meta, h.Secrets, h.Accounts, NullLogger<MetaAuth>.Instance);
        Assert.Equal("page-token", restarted.Connection!.PageToken);
    }

    [Fact]
    public async Task Tokens_and_secrets_never_travel_in_the_address()
    {
        using var h = new MetaHarness(connected: false);
        var urls = new List<string>();
        h.Meta.Intercept = r => { urls.Add(r.RequestUri!.ToString()); return null; };

        await h.Auth.ConnectAsync("token-do-explorador", null, default);
        await h.Facebook.PublishAsync(h.Request(), default);

        Assert.NotEmpty(urls);
        Assert.DoesNotContain(urls, u => u.Contains("token", StringComparison.OrdinalIgnoreCase) && u.Contains('=') && u.Contains("access_token=", StringComparison.Ordinal));
        Assert.DoesNotContain(urls, u => u.Contains("segredo-do-app") || u.Contains("page-token") || u.Contains("token-do-explorador") || u.Contains("user-long"));
    }

    [Fact]
    public async Task Several_pages_require_an_explicit_choice()
    {
        using var h = new MetaHarness(connected: false);
        h.Meta.AccountsJson = """{"data":[{"id":"page-1","name":"Bast","access_token":"page-token","instagram_business_account":{"id":"ig-1","username":"canal_exemplo"}},{"id":"page-2","name":"Outra","access_token":"outro-token"}]}""";

        var ask = await h.Auth.ConnectAsync("token-do-explorador", null, default);
        Assert.False(ask.Connected);
        Assert.Equal(["Bast", "Outra"], ask.Pages.Select(p => p.Name).ToArray());
        Assert.False(h.Facebook.IsAvailable);

        var chosen = await h.Auth.ConnectAsync("token-do-explorador", "page-2", default);
        Assert.True(chosen.Connected);
        Assert.True(h.Facebook.IsAvailable);
        // Página sem Instagram profissional vinculado: só o Facebook fica disponível.
        Assert.False(h.Instagram.IsAvailable);
        var instagram = (await h.Accounts.GetAllAsync(default)).Single(a => a.Platform == Platform.Instagram);
        Assert.Contains("não tem uma conta profissional do Instagram", instagram.LastError);
    }

    [Fact]
    public async Task Invalid_token_is_refused_with_the_reason()
    {
        using var h = new MetaHarness(connected: false);

        var result = await h.Auth.ConnectAsync("token-errado", null, default);

        Assert.False(result.Connected);
        Assert.Contains("Invalid OAuth access token", result.Message);
        Assert.False(h.Facebook.IsAvailable);
    }
}

public class FacebookConnectorTests
{
    [Fact]
    public async Task Reel_is_uploaded_finished_and_confirmed_by_the_platform()
    {
        using var h = new MetaHarness();

        var sent = await h.Facebook.PublishAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.Processing, sent.Kind);
        Assert.Equal(["video-uploading:vid-1", "video-publishing:vid-1", "video:vid-1"], h.SavedIds);
        Assert.Equal(h.FileBytes, h.Meta.FacebookReceived.ToArray());
        Assert.Equal(MetaHarness.Caption, h.Meta.FacebookDescription);

        var confirmed = await h.Facebook.CheckAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.Published, confirmed.Kind);
        Assert.Equal("vid-1", confirmed.ExternalId);
        Assert.Equal("https://www.facebook.com/reel/987654/", confirmed.Url);
        Assert.Equal(1, h.Meta.ReelsPublished);
    }

    [Fact]
    public async Task Still_processing_is_not_reported_as_published()
    {
        using var h = new MetaHarness();
        h.Meta.FacebookProcessed = false;
        await h.Facebook.PublishAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.Processing, (await h.Facebook.CheckAsync(h.Request(), default)).Kind);

        h.Meta.FacebookProcessed = true;
        Assert.Equal(PublishOutcomeKind.Published, (await h.Facebook.CheckAsync(h.Request(), default)).Kind);
    }

    [Fact]
    public async Task Connection_lost_mid_upload_resumes_from_the_bytes_the_platform_kept()
    {
        using var h = new MetaHarness();
        h.Meta.DropFacebookUploadAfterBytes = 100_000;

        var first = await h.Facebook.PublishAsync(h.Request(), default);
        Assert.Equal(PublishOutcomeKind.Offline, first.Kind);
        Assert.Equal(["video-uploading:vid-1"], h.SavedIds);

        var second = await h.Facebook.PublishAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.Processing, second.Kind);
        Assert.Equal(h.FileBytes, h.Meta.FacebookReceived.ToArray());
        Assert.Equal(1, h.Meta.FacebookVideosStarted);
        Assert.Equal(1, h.Meta.ReelsPublished);
    }

    [Fact]
    public async Task Lost_finish_request_is_repeated_safely_and_never_uploads_again()
    {
        using var h = new MetaHarness();
        var posts = 0;
        // O segundo POST em video_reels é a finalização.
        h.Meta.Intercept = r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/video_reels") && ++posts == 2
            ? throw new HttpRequestException("caiu antes de finalizar")
            : null;

        var first = await h.Facebook.PublishAsync(h.Request(), default);
        Assert.Equal(PublishOutcomeKind.Offline, first.Kind);
        Assert.Equal("video-publishing:vid-1", h.SavedIds.Last());
        Assert.Equal(0, h.Meta.ReelsPublished);

        h.Meta.Intercept = null;
        h.Meta.Log.Clear();
        var second = await h.Facebook.PublishAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.Processing, second.Kind);
        Assert.Equal(1, h.Meta.ReelsPublished);
        Assert.Equal(0, h.Count("rupload.facebook.com"));
        Assert.Equal(1, h.Meta.FacebookVideosStarted);
    }

    [Fact]
    public async Task Publication_already_sent_is_only_checked()
    {
        using var h = new MetaHarness();
        await h.Facebook.PublishAsync(h.Request(), default);
        h.Meta.Log.Clear();

        var again = await h.Facebook.PublishAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.Published, again.Kind);
        Assert.Equal(0, h.Count("rupload.facebook.com"));
        Assert.Equal(0, h.Count("/video_reels"));
        Assert.Equal(1, h.Meta.ReelsPublished);
    }

    [Fact]
    public async Task Processing_error_is_a_permanent_failure_with_the_platform_reason()
    {
        using var h = new MetaHarness();
        h.Meta.FacebookProcessingError = "Reels duration must be between 3 and 90 seconds";
        await h.Facebook.PublishAsync(h.Request(), default);

        var outcome = await h.Facebook.CheckAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.Permanent, outcome.Kind);
        Assert.Contains("between 3 and 90 seconds", outcome.Error);
    }

    [Fact]
    public async Task Expired_token_stops_facebook_and_instagram_and_flags_both_accounts()
    {
        using var h = new MetaHarness();
        h.Meta.Intercept = r => r.RequestUri!.AbsolutePath.EndsWith("/video_reels")
            ? FakeMeta.Error(190, "Error validating access token: Session has expired.", 463)
            : null;

        var outcome = await h.Facebook.PublishAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.AuthRequired, outcome.Kind);
        Assert.False(h.Facebook.IsAvailable);
        Assert.False(h.Instagram.IsAvailable);
        Assert.All(await h.Accounts.GetAllAsync(default), a => Assert.Equal(AccountStatus.NeedsReauth, a.Status));
    }

    [Fact]
    public async Task Rate_limit_waits_instead_of_failing()
    {
        using var h = new MetaHarness();
        h.Meta.Intercept = r => r.RequestUri!.AbsolutePath.EndsWith("/video_reels")
            ? FakeMeta.Error(613, "Calls to this api have exceeded the rate limit.")
            : null;

        var outcome = await h.Facebook.PublishAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.Transient, outcome.Kind);
        Assert.Equal(TimeSpan.FromHours(1), outcome.RetryAfter);
        Assert.True(h.Facebook.IsAvailable);
    }
}

public class InstagramConnectorTests
{
    [Fact]
    public async Task Reel_is_uploaded_processed_published_once_and_gets_its_link()
    {
        using var h = new MetaHarness();
        h.Meta.ContainerStatus = "IN_PROGRESS";

        var sent = await h.Instagram.PublishAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.Processing, sent.Kind);
        Assert.Equal(["container-uploading:c1", "container:c1"], h.SavedIds);
        Assert.Equal(h.FileBytes, h.Meta.ContainerUploads["c1"]);
        Assert.Equal(MetaHarness.Caption, h.Meta.InstagramCaption);
        Assert.Equal(0, h.Meta.MediaPublishCalls);

        // Enquanto o Instagram processa, nada é publicado.
        Assert.Equal(PublishOutcomeKind.Processing, (await h.Instagram.CheckAsync(h.Request(), default)).Kind);
        Assert.Equal(0, h.Meta.MediaPublishCalls);

        h.Meta.ContainerStatus = "FINISHED";
        var published = await h.Instagram.CheckAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.Published, published.Kind);
        Assert.Equal("media-1", published.ExternalId);
        Assert.Equal("https://www.instagram.com/reel/ABC123/", published.Url);
        Assert.Equal(1, h.Meta.MediaPublishCalls);
        Assert.Equal("media-1", h.SavedIds.Last());
    }

    [Fact]
    public async Task Interrupted_upload_is_redone_in_a_new_container_and_nothing_is_published()
    {
        using var h = new MetaHarness();
        h.Meta.Intercept = r => r.RequestUri!.AbsolutePath.Contains("/ig-api-upload/") ? throw new HttpRequestException("conexão caiu") : null;

        var first = await h.Instagram.PublishAsync(h.Request(), default);
        Assert.Equal(PublishOutcomeKind.Offline, first.Kind);
        Assert.Equal("container-uploading:c1", h.SavedIds.Last());
        Assert.Equal(PublishOutcomeKind.NotFound, (await h.Instagram.CheckAsync(h.Request(), default)).Kind);

        h.Meta.Intercept = null;
        var second = await h.Instagram.PublishAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.Processing, second.Kind);
        Assert.Equal("container:c2", h.SavedIds.Last());
        Assert.Equal(h.FileBytes, h.Meta.ContainerUploads["c2"]);
        Assert.Equal(0, h.Meta.MediaPublishCalls);
    }

    [Fact]
    public async Task Lost_publish_response_is_never_repeated_only_confirmed()
    {
        using var h = new MetaHarness();
        await h.Instagram.PublishAsync(h.Request(), default);
        // O pedido chega à Meta, mas a resposta se perde.
        var delivered = 0;
        h.Meta.Intercept = r =>
        {
            if (!r.RequestUri!.AbsolutePath.EndsWith("/media_publish")) return null;
            delivered++;
            throw new HttpRequestException("resposta perdida");
        };

        var lost = await h.Instagram.CheckAsync(h.Request(), default);
        Assert.Equal(PublishOutcomeKind.Unknown, lost.Kind);
        Assert.Equal("container-publishing:c1", h.SavedIds.Last());

        // O contêiner ainda aparece como FINISHED por um tempo: o pedido não é refeito.
        h.Meta.Intercept = r => r.RequestUri!.AbsolutePath.EndsWith("/media_publish") ? throw new InvalidOperationException("não deveria pedir de novo") : null;
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(PublishOutcomeKind.Unknown, (await h.Instagram.CheckAsync(h.Request(), default)).Kind);
        }

        Assert.Equal(1, delivered);

        // A Meta passa a informar PUBLISHED: confirmado sem novo pedido.
        h.Meta.ContainerStatus = "PUBLISHED";
        Assert.Equal(PublishOutcomeKind.Published, (await h.Instagram.CheckAsync(h.Request(), default)).Kind);
        Assert.Equal(1, delivered);
    }

    [Fact]
    public async Task Publish_refused_by_the_platform_can_be_requested_again_later()
    {
        using var h = new MetaHarness();
        await h.Instagram.PublishAsync(h.Request(), default);
        h.Meta.Intercept = r => r.RequestUri!.AbsolutePath.EndsWith("/media_publish") ? FakeMeta.Error(2, "An unexpected error has occurred. Please retry your request later.") : null;

        var refused = await h.Instagram.CheckAsync(h.Request(), default);
        Assert.Equal(PublishOutcomeKind.Transient, refused.Kind);
        Assert.Equal("container:c1", h.SavedIds.Last());

        h.Meta.Intercept = null;
        var published = await h.Instagram.CheckAsync(h.Request(), default);
        Assert.Equal(PublishOutcomeKind.Published, published.Kind);
        Assert.Equal(1, h.Meta.MediaPublishCalls);
    }

    [Fact]
    public async Task Rejected_video_is_a_permanent_failure_with_the_platform_reason()
    {
        using var h = new MetaHarness();
        await h.Instagram.PublishAsync(h.Request(), default);
        h.Meta.ContainerStatus = "ERROR";

        var outcome = await h.Instagram.CheckAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.Permanent, outcome.Kind);
        Assert.Contains("2207026", outcome.Error);
        Assert.Equal(0, h.Meta.MediaPublishCalls);
    }

    [Fact]
    public async Task Expired_container_is_discarded_and_the_upload_redone()
    {
        using var h = new MetaHarness();
        await h.Instagram.PublishAsync(h.Request(), default);
        h.Meta.ContainerStatus = "EXPIRED";

        var expired = await h.Instagram.CheckAsync(h.Request(), default);
        Assert.Equal(PublishOutcomeKind.NotFound, expired.Kind);
        Assert.Equal("", h.SavedIds.Last());

        h.Meta.ContainerStatus = "FINISHED";
        var redo = await h.Instagram.PublishAsync(h.Request(), default);
        Assert.Equal(PublishOutcomeKind.Processing, redo.Kind);
        Assert.Equal("container:c2", h.SavedIds.Last());
    }

    [Fact]
    public async Task Page_without_instagram_makes_only_instagram_unavailable()
    {
        using var h = new MetaHarness(connected: false);
        h.Meta.AccountsJson = """{"data":[{"id":"page-1","name":"Bast","access_token":"page-token"}]}""";
        await h.Auth.ConnectAsync("token-do-explorador", null, default);

        var registry = new ConnectorRegistry([h.Facebook, h.Instagram], new BastOptions());

        Assert.Equal([Platform.Facebook], registry.Available.ToArray());
    }
}

public class MetaThroughEngineTests
{
    [Fact]
    public async Task Engine_drives_both_connectors_to_published_with_one_post_each()
    {
        using var h = new MetaHarness();
        var registry = new ConnectorRegistry([h.Facebook, h.Instagram], new BastOptions());
        var scheduling = new SchedulingService(h.Env.DbFactory, registry, h.Env.Time, NullLogger<SchedulingService>.Instance);
        var engine = new PublishingEngine(h.Env.DbFactory, registry, scheduling,
            new FolderSync(h.Env.DbFactory, h.Env.Workspace, h.Env.Time, NullLogger<FolderSync>.Instance),
            h.Env.Workspace, h.Env.Time, NullLogger<PublishingEngine>.Instance);
        await h.Env.Service.ProcessFolderAsync(h.Env.CreateFolder("Video 1"), default);

        await engine.RunCycleAsync(default);
        h.Env.Time.Advance(TimeSpan.FromMinutes(65)); // 10:05 em São Paulo: horário das 10:00 dentro da tolerância
        for (var i = 0; i < 4; i++)
        {
            await engine.RunCycleAsync(default);
            h.Env.Time.Advance(TimeSpan.FromSeconds(61));
        }

        await using var db = h.Env.DbFactory.CreateDbContext();
        var video = Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .Include(db.Videos, v => v.Publications).Single();
        Assert.Equal(VideoState.Published, video.State);
        Assert.Equal(WorkspaceFolder.Sent, video.CurrentFolder);
        var facebook = video.Publications.Single(p => p.Platform == Platform.Facebook);
        var instagram = video.Publications.Single(p => p.Platform == Platform.Instagram);
        Assert.Equal((PublicationState.Published, "vid-1", 1), (facebook.State, facebook.ExternalId, facebook.AttemptCount));
        Assert.Equal((PublicationState.Published, "media-1", 1), (instagram.State, instagram.ExternalId, instagram.AttemptCount));
        Assert.Equal("https://www.instagram.com/reel/ABC123/", instagram.ExternalUrl);
        Assert.Equal(1, h.Meta.ReelsPublished);
        Assert.Equal(1, h.Meta.MediaPublishCalls);
    }
}

public class ReelCoverTests
{
    private static string Cover(MetaHarness h)
    {
        var path = Path.Combine(h.Env.Root, "thumbnail.jpg");
        File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);
        return path;
    }

    [Fact]
    public async Task Instagram_gets_the_cover_by_a_temporary_link_closed_after_processing()
    {
        using var h = new MetaHarness();
        h.Meta.ContainerStatus = "IN_PROGRESS";
        var cover = Cover(h);

        await h.Instagram.PublishAsync(h.Request(cover), default);

        var url = Assert.Single(h.Meta.InstagramCoverUrls);
        Assert.StartsWith("https://exemplo.trycloudflare.com/", url);
        Assert.Contains(h.PublicationId, h.Files.Open);

        // Ainda processando: o link continua aberto. Terminou: fecha e publica.
        await h.Instagram.CheckAsync(h.Request(cover), default);
        Assert.Contains(h.PublicationId, h.Files.Open);
        h.Meta.ContainerStatus = "FINISHED";
        Assert.Equal(PublishOutcomeKind.Published, (await h.Instagram.CheckAsync(h.Request(cover), default)).Kind);
        Assert.Empty(h.Files.Open);
    }

    [Fact]
    public async Task Without_an_image_or_a_link_the_reel_goes_with_the_automatic_cover()
    {
        using var h = new MetaHarness();
        await h.Instagram.PublishAsync(h.Request(), default);
        Assert.Null(Assert.Single(h.Meta.InstagramCoverUrls));

        using var h2 = new MetaHarness();
        h2.Files.Next = PublicFileShareResult.NotInstalled("cloudflared não encontrado");
        var sent = await h2.Instagram.PublishAsync(h2.Request(Cover(h2)), default);
        Assert.Equal(PublishOutcomeKind.Processing, sent.Kind);
        Assert.Null(Assert.Single(h2.Meta.InstagramCoverUrls));
    }

    [Fact]
    public async Task Processing_error_with_the_cover_is_retried_once_without_it()
    {
        using var h = new MetaHarness();
        h.Meta.ContainerStatus = "ERROR";
        var cover = Cover(h);

        await h.Instagram.PublishAsync(h.Request(cover), default);
        var failed = await h.Instagram.CheckAsync(h.Request(cover), default);
        Assert.Equal(PublishOutcomeKind.Transient, failed.Kind);
        Assert.Contains("sem a capa", failed.Error);
        Assert.Equal("", h.SavedIds.Last());

        await h.Instagram.PublishAsync(h.Request(cover), default);
        Assert.Equal(2, h.Meta.InstagramCoverUrls.Count);
        Assert.Null(h.Meta.InstagramCoverUrls[1]);
        Assert.Equal(PublishOutcomeKind.Permanent, (await h.Instagram.CheckAsync(h.Request(cover), default)).Kind);
    }

    [Fact]
    public async Task Facebook_sets_the_cover_as_preferred_thumbnail_after_publishing()
    {
        using var h = new MetaHarness();
        var cover = Cover(h);

        await h.Facebook.PublishAsync(h.Request(cover), default);
        var published = await h.Facebook.CheckAsync(h.Request(cover), default);

        Assert.Equal(PublishOutcomeKind.Published, published.Kind);
        Assert.Null(published.Warning);
        Assert.Equal(File.ReadAllBytes(cover), h.Meta.FacebookThumbnail);
        Assert.Equal("true", h.Meta.FacebookThumbnailPreferred);
    }

    [Fact]
    public async Task Facebook_refused_cover_is_only_a_warning()
    {
        using var h = new MetaHarness();
        h.Meta.FacebookThumbnailError = "The thumbnail file you selected is in a format that we don't support.";
        var cover = Cover(h);

        await h.Facebook.PublishAsync(h.Request(cover), default);
        var published = await h.Facebook.CheckAsync(h.Request(cover), default);

        Assert.Equal(PublishOutcomeKind.Published, published.Kind);
        Assert.Contains("capa não foi aplicada no Facebook", published.Warning);
        Assert.Contains("format", published.Warning);
    }
}