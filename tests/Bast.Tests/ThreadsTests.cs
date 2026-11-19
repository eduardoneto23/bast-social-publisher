using System.Net;
using System.Text;
using System.Web;
using Bast.Application;
using Bast.Application.Publishing;
using Bast.Connectors.Meta;
using Bast.Domain;
using Bast.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bast.Tests;

/// <summary>
/// Servidor falso que segue o fluxo documentado da API do Threads (graph.threads.net). Pedidos para
/// outros endereços (Graph API do Facebook/Instagram) vão para o falso da Meta.
/// </summary>
public sealed class FakeThreads : HttpMessageHandler, IHttpClientFactory
{
    public const string RedirectUri = "https://exemplo.github.io/site/";

    private readonly HttpMessageInvoker _meta;

    public FakeThreads() => _meta = new HttpMessageInvoker(Meta, disposeHandler: false);

    public FakeMeta Meta { get; } = new();
    public List<string> Urls { get; } = [];
    public string LongToken { get; private set; } = "th-long";
    public int Refreshes { get; private set; }
    public int ContainersCreated { get; private set; }
    public int PublishCalls { get; private set; }
    public string? LastVideoUrl { get; private set; }
    public string? LastText { get; private set; }
    public string ContainerStatus { get; set; } = "FINISHED";
    public string? ContainerError { get; set; }
    public bool Published { get; private set; }
    public Func<HttpRequestMessage, HttpResponseMessage?>? Intercept { get; set; }

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        if (uri.Host != "graph.threads.net") return await _meta.SendAsync(request, ct);

        Urls.Add(uri.ToString());
        if (Intercept?.Invoke(request) is { } injected) return injected;

        var path = uri.AbsolutePath;
        var query = HttpUtility.ParseQueryString(uri.Query);
        var form = request.Content is null ? new System.Collections.Specialized.NameValueCollection()
            : HttpUtility.ParseQueryString(await request.Content.ReadAsStringAsync(ct));

        if (path == "/oauth/access_token")
        {
            return form["code"] == "codigo-bom" && form["client_secret"] == "segredo-threads" && form["client_id"] == "th-app"
                   && form["redirect_uri"] == RedirectUri && form["grant_type"] == "authorization_code"
                ? FakeMeta.Json("""{"access_token":"th-short","user_id":"th-user"}""")
                : FakeMeta.Json("""{"error_type":"OAuthException","code":400,"error_message":"Matching code was not found or was already used"}""", HttpStatusCode.BadRequest);
        }

        if (path == "/access_token")
        {
            return query["grant_type"] == "th_exchange_token" && query["client_secret"] == "segredo-threads" && query["access_token"] == "th-short"
                ? FakeMeta.Json("""{"access_token":"th-long","token_type":"bearer","expires_in":5183944}""")
                : FakeMeta.Error(190, "Invalid OAuth access token.");
        }

        if (path == "/refresh_access_token")
        {
            if (query["grant_type"] != "th_refresh_token" || query["access_token"] != LongToken) return FakeMeta.Error(190, "Invalid OAuth access token.");
            Refreshes++;
            LongToken = $"th-long-{Refreshes + 1}";
            return FakeMeta.Json($$"""{"access_token":"{{LongToken}}","token_type":"bearer","expires_in":5183944}""");
        }

        var token = query["access_token"] ?? form["access_token"];
        if (token != LongToken) return FakeMeta.Error(190, "Error validating access token.");

        if (path == "/v1.0/me") return FakeMeta.Json("""{"id":"th-user","username":"bast_threads"}""");

        if (path == "/v1.0/th-user/threads")
        {
            if (form["media_type"] != "VIDEO" || string.IsNullOrEmpty(form["video_url"])) return FakeMeta.Error(100, "Invalid parameter");
            LastVideoUrl = form["video_url"];
            LastText = form["text"];
            return FakeMeta.Json($$"""{"id":"tc{{++ContainersCreated}}"}""");
        }

        if (path == "/v1.0/th-user/threads_publish")
        {
            PublishCalls++;
            Published = true;
            return FakeMeta.Json("""{"id":"th-media-1"}""");
        }

        if (path == "/v1.0/th-media-1")
        {
            return FakeMeta.Json("""{"permalink":"https://www.threads.com/@bast_threads/post/XYZ","id":"th-media-1"}""");
        }

        if (path.StartsWith("/v1.0/tc", StringComparison.Ordinal))
        {
            var status = Published ? "PUBLISHED" : ContainerStatus;
            var error = ContainerError is null ? "" : $",\"error_message\":\"{ContainerError}\"";
            return FakeMeta.Json($$"""{"status":"{{status}}","id":"{{path[6..]}}"{{error}}}""");
        }

        return FakeMeta.Error(100, "Unsupported request");
    }
}

public sealed class ThreadsHarness : IDisposable
{
    public ThreadsHarness(bool connected = true)
    {
        Secrets = new DpapiSecretStore(Env.Workspace);
        Accounts = new AccountService(Env.DbFactory, Env.Time);
        Auth = new ThreadsAuth(Fake, Secrets, Accounts, Env.Time, NullLogger<ThreadsAuth>.Instance);
        Assert.Null(Auth.SaveAppCredentials("th-app", "segredo-threads", FakeThreads.RedirectUri));
        Connector = new ThreadsConnector(Fake, Auth, Files);

        if (connected)
        {
            var start = Auth.Start();
            var state = HttpUtility.ParseQueryString(new Uri(start.AuthorizeUrl!).Query)["state"];
            var result = Auth.ConnectAsync($"{FakeThreads.RedirectUri}?code=codigo-bom&state={state}#_", default).GetAwaiter().GetResult();
            Assert.True(result.Ok, result.Message);
            Fake.Urls.Clear();
        }

        VideoPath = Path.Combine(Env.Root, "video.mp4");
        File.WriteAllBytes(VideoPath, [1, 2, 3]);
    }

    public const string Caption = "GTA 6 VAI TER POSTER!\n\nDescrição 🎮\n#GTA6";

    public TestEnvironment Env { get; } = new();
    public FakeThreads Fake { get; } = new();
    public DpapiSecretStore Secrets { get; }
    public AccountService Accounts { get; }
    public FakeFileShare Files { get; } = new();
    public ThreadsAuth Auth { get; }
    public ThreadsConnector Connector { get; }
    public string VideoPath { get; }
    public List<string> SavedIds { get; } = [];

    public Guid PublicationId { get; } = Guid.NewGuid();

    public PublishRequest Request() => new(
        PublicationId, "chave-1", VideoPath, null, Caption, "GTA 6 VAI TER POSTER!",
        SavedIds.LastOrDefault(),
        id => { SavedIds.Add(id); return Task.CompletedTask; });

    public async Task<AccountStatus> ThreadsStatusAsync() =>
        (await Accounts.GetAllAsync(default)).Single(a => a.Platform == Platform.Threads).Status;

    public void Dispose() => Env.Dispose();
}

public class ThreadsAuthTests
{
    [Fact]
    public async Task Login_with_the_returned_address_connects_and_stores_only_encrypted_tokens()
    {
        using var h = new ThreadsHarness(connected: false);

        var start = h.Auth.Start();
        Assert.True(start.Ok);
        var url = new Uri(start.AuthorizeUrl!);
        Assert.Equal("threads.com", url.Host);
        var query = HttpUtility.ParseQueryString(url.Query);
        Assert.Equal("th-app", query["client_id"]);
        Assert.Equal(FakeThreads.RedirectUri, query["redirect_uri"]);
        Assert.Equal("threads_basic,threads_content_publish", query["scope"]);
        Assert.Equal("code", query["response_type"]);

        var result = await h.Auth.ConnectAsync($"{FakeThreads.RedirectUri}?code=codigo-bom&state={query["state"]}#_", default);

        Assert.True(result.Ok, result.Message);
        Assert.Contains("@bast_threads", result.Message);
        Assert.True(h.Connector.IsAvailable);
        Assert.Equal(AccountStatus.Connected, await h.ThreadsStatusAsync());
        Assert.Equal(h.Env.Time.GetUtcNow().UtcDateTime.AddSeconds(5183944), h.Auth.Connection!.ExpiresAtUtc);

        foreach (var file in Directory.GetFiles(Path.Combine(h.Env.Workspace.SystemPath, "secrets")))
        {
            var raw = Encoding.Latin1.GetString(File.ReadAllBytes(file));
            Assert.DoesNotContain("th-long", raw);
            Assert.DoesNotContain("segredo-threads", raw);
        }

        // Reinício do serviço: a conexão volta do armazenamento seguro.
        var restarted = new ThreadsAuth(h.Fake, h.Secrets, h.Accounts, h.Env.Time, NullLogger<ThreadsAuth>.Instance);
        Assert.Equal("th-long", restarted.Connection!.Token);
    }

    [Fact]
    public async Task Address_from_another_authorization_is_refused_without_calling_the_Threads()
    {
        using var h = new ThreadsHarness(connected: false);
        h.Auth.Start();

        var result = await h.Auth.ConnectAsync($"{FakeThreads.RedirectUri}?code=codigo-bom&state=outro", default);

        Assert.False(result.Ok);
        Assert.Contains("não corresponde", result.Message);
        Assert.Empty(h.Fake.Urls);
        Assert.False(h.Connector.IsAvailable);
    }

    [Fact]
    public async Task Denied_or_used_code_is_reported_with_the_reason()
    {
        using var h = new ThreadsHarness(connected: false);
        var state = HttpUtility.ParseQueryString(new Uri(h.Auth.Start().AuthorizeUrl!).Query)["state"];

        var denied = await h.Auth.ConnectAsync($"{FakeThreads.RedirectUri}?error=access_denied&error_description=Permissions+error&state={state}", default);
        Assert.False(denied.Ok);
        Assert.Contains("Permissions error", denied.Message);

        var used = await h.Auth.ConnectAsync($"{FakeThreads.RedirectUri}?code=codigo-velho&state={state}", default);
        Assert.False(used.Ok);
        Assert.Contains("Matching code was not found", used.Message);
    }

    [Fact]
    public void Redirect_address_must_be_https()
    {
        using var h = new ThreadsHarness(connected: false);
        Assert.NotNull(h.Auth.SaveAppCredentials("th-app", "segredo-threads", "http://localhost:5080/"));
        Assert.NotNull(h.Auth.SaveAppCredentials("th-app", "segredo-threads", "exemplo.github.io"));
    }

    [Fact]
    public async Task Token_is_renewed_before_it_gets_close_to_expiring_and_not_before_24_hours()
    {
        using var h = new ThreadsHarness();

        h.Env.Time.Advance(TimeSpan.FromHours(12));
        Assert.Equal("th-long", await h.Auth.GetTokenAsync(default));
        Assert.Equal(0, h.Fake.Refreshes);

        h.Env.Time.Advance(TimeSpan.FromDays(11));
        Assert.Equal("th-long-2", await h.Auth.GetTokenAsync(default));
        Assert.Equal(1, h.Fake.Refreshes);
        Assert.Equal("th-long-2", await h.Auth.GetTokenAsync(default));
        Assert.Equal(1, h.Fake.Refreshes);

        // O token renovado também fica guardado para depois de um reinício.
        var restarted = new ThreadsAuth(h.Fake, h.Secrets, h.Accounts, h.Env.Time, NullLogger<ThreadsAuth>.Instance);
        Assert.Equal("th-long-2", restarted.Connection!.Token);
    }

    [Fact]
    public async Task Expired_token_asks_for_a_new_connection()
    {
        using var h = new ThreadsHarness();
        h.Env.Time.Advance(TimeSpan.FromDays(61));

        Assert.Null(await h.Auth.GetTokenAsync(default));
        Assert.False(h.Connector.IsAvailable);
        Assert.Equal(AccountStatus.NeedsReauth, await h.ThreadsStatusAsync());
    }
}

/// <summary>Link temporário falso: registra o que foi aberto e fechado.</summary>
public sealed class FakeFileShare : IPublicFileShare
{
    public List<Guid> Opened { get; } = [];
    public List<Guid> Stopped { get; } = [];
    public HashSet<Guid> Open { get; } = [];
    public PublicFileShareResult? Next { get; set; }

    public Task<PublicFileShareResult> ShareAsync(Guid key, string filePath, CancellationToken ct)
    {
        if (Next is { } forced) return Task.FromResult(forced);
        Opened.Add(key);
        Open.Add(key);
        return Task.FromResult(PublicFileShareResult.Opened($"https://exemplo.trycloudflare.com/{key:N}{Path.GetExtension(filePath)}"));
    }

    public Task StopAsync(Guid key)
    {
        if (Open.Remove(key)) Stopped.Add(key);
        return Task.CompletedTask;
    }
}

public class ThreadsConnectorTests
{
    [Fact]
    public async Task Publishes_the_original_file_by_a_temporary_link_once_and_closes_it()
    {
        using var h = new ThreadsHarness();
        h.Fake.ContainerStatus = "IN_PROGRESS";

        var first = await h.Connector.PublishAsync(h.Request(), default);
        Assert.Equal(PublishOutcomeKind.Processing, first.Kind);
        Assert.Equal("container:tc1", h.SavedIds.Last());
        Assert.Equal($"https://exemplo.trycloudflare.com/{h.PublicationId:N}.mp4", h.Fake.LastVideoUrl);
        Assert.Equal(ThreadsHarness.Caption, h.Fake.LastText);

        // Enquanto o Threads processa, o link continua aberto.
        Assert.Equal(PublishOutcomeKind.Processing, (await h.Connector.CheckAsync(h.Request(), default)).Kind);
        Assert.Contains(h.PublicationId, h.Files.Open);

        h.Fake.ContainerStatus = "FINISHED";
        var done = await h.Connector.CheckAsync(h.Request(), default);
        Assert.Equal(PublishOutcomeKind.Published, done.Kind);
        Assert.Equal("th-media-1", done.ExternalId);
        Assert.Equal("https://www.threads.com/@bast_threads/post/XYZ", done.Url);
        Assert.Equal(1, h.Fake.PublishCalls);
        Assert.Empty(h.Files.Open);

        // Já publicado: nada é enviado de novo e nenhum link é aberto.
        var again = await h.Connector.PublishAsync(h.Request(), default);
        Assert.Equal(PublishOutcomeKind.Published, again.Kind);
        Assert.Equal(1, h.Fake.ContainersCreated);
        Assert.Equal(1, h.Fake.PublishCalls);
        Assert.Single(h.Files.Opened);
    }

    [Fact]
    public async Task Without_cloudflared_the_reason_is_final_and_the_Threads_is_not_called()
    {
        using var h = new ThreadsHarness();
        h.Files.Next = PublicFileShareResult.NotInstalled("O cloudflared não foi encontrado.");

        var outcome = await h.Connector.PublishAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.Permanent, outcome.Kind);
        Assert.Contains("cloudflared", outcome.Error);
        Assert.Equal(0, h.Fake.ContainersCreated);
    }

    [Fact]
    public async Task Tunnel_that_does_not_open_is_tried_again_later()
    {
        using var h = new ThreadsHarness();
        h.Files.Next = PublicFileShareResult.Failed("O túnel do Cloudflare não abriu.");

        var outcome = await h.Connector.PublishAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.Transient, outcome.Kind);
        Assert.Equal(0, h.Fake.ContainersCreated);
        Assert.Empty(h.SavedIds);
    }

    [Fact]
    public async Task Refused_container_closes_the_link()
    {
        using var h = new ThreadsHarness();
        h.Fake.Intercept = r => r.RequestUri!.AbsolutePath.EndsWith("/threads", StringComparison.Ordinal)
            ? FakeMeta.Error(100, "Invalid parameter")
            : null;

        var outcome = await h.Connector.PublishAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.Permanent, outcome.Kind);
        Assert.Empty(h.Files.Open);
    }

    [Fact]
    public async Task Lost_publish_response_is_never_repeated()
    {
        using var h = new ThreadsHarness();
        h.SavedIds.Add("container-publishing:tc1");

        var outcome = await h.Connector.CheckAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.Unknown, outcome.Kind);
        Assert.Equal(0, h.Fake.PublishCalls);
    }

    [Fact]
    public async Task Failed_download_starts_over_with_a_fresh_link_other_errors_are_final()
    {
        using var h = new ThreadsHarness();
        await h.Connector.PublishAsync(h.Request(), default);
        h.Fake.ContainerStatus = "ERROR";
        h.Fake.ContainerError = "FAILED_DOWNLOADING_VIDEO";

        var download = await h.Connector.CheckAsync(h.Request(), default);
        Assert.Equal(PublishOutcomeKind.Transient, download.Kind);
        Assert.Equal("", h.SavedIds.Last());
        Assert.Empty(h.Files.Open);

        await h.Connector.PublishAsync(h.Request(), default);
        Assert.Equal(2, h.Fake.ContainersCreated);
        Assert.Equal(2, h.Files.Opened.Count);
        h.Fake.ContainerError = "UNKNOWN";
        var invalid = await h.Connector.CheckAsync(h.Request(), default);
        Assert.Equal(PublishOutcomeKind.Permanent, invalid.Kind);
        Assert.Contains("UNKNOWN", invalid.Error);
        Assert.Empty(h.Files.Open);
    }

    [Fact]
    public async Task Refused_token_stops_threads_and_waits_for_reconnection()
    {
        using var h = new ThreadsHarness();
        h.Fake.Intercept = r => r.RequestUri!.AbsolutePath.EndsWith("/threads", StringComparison.Ordinal)
            ? FakeMeta.Error(190, "Error validating access token: Session has been invalidated.")
            : null;

        var outcome = await h.Connector.PublishAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.AuthRequired, outcome.Kind);
        Assert.False(h.Connector.IsAvailable);
        Assert.Equal(AccountStatus.NeedsReauth, await h.ThreadsStatusAsync());
    }

    [Fact]
    public async Task Network_failure_never_exposes_the_token()
    {
        using var h = new ThreadsHarness();
        h.Fake.Intercept = r => throw new HttpRequestException($"falha ao acessar {r.RequestUri}");

        var outcome = await h.Connector.PublishAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.Offline, outcome.Kind);
        Assert.DoesNotContain("th-long", outcome.Error);
    }
}

public class TemporaryLinkTests
{
    [Fact]
    public async Task Local_server_serves_only_the_file_on_its_secret_path()
    {
        using var env = new TestEnvironment();
        var path = Path.Combine(env.Root, "video.mp4");
        byte[] bytes = [9, 8, 7, 6, 5];
        File.WriteAllBytes(path, bytes);
        using var server = SingleFileServer.Start(path);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };

        Assert.EndsWith(".mp4", server.PathAndName);
        Assert.True(server.PathAndName.Length > 40);

        using (var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, server.PathAndName)))
        {
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            Assert.Equal(5, head.Content.Headers.ContentLength);
            Assert.Equal(0, server.Downloads);
        }

        using (var get = await http.GetAsync(server.PathAndName))
        {
            Assert.Equal("video/mp4", get.Content.Headers.ContentType!.MediaType);
            Assert.Equal(bytes, await get.Content.ReadAsByteArrayAsync());
        }

        // A contagem sobe logo depois do último byte; o cliente pode terminar de ler um instante antes.
        for (var i = 0; i < 100 && server.Downloads == 0; i++) await Task.Delay(20);
        Assert.Equal(1, server.Downloads);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/video.mp4")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync(server.PathAndName, new StringContent("x"))).StatusCode);
    }

    [Fact]
    public async Task Missing_cloudflared_is_reported_without_starting_anything()
    {
        using var env = new TestEnvironment();
        var share = new CloudflaredFileShare(new BastOptions { RootFolder = env.Root }, env.Workspace, NullLogger<CloudflaredFileShare>.Instance);

        var result = await share.ShareAsync(Guid.NewGuid(), Path.Combine(env.Root, "video.mp4"), default);

        Assert.Null(result.Url);
        Assert.True(result.Missing);
        Assert.Contains("cloudflared", result.Problem);
    }
}
public class ThreadsThroughEngineTests
{
    private static async Task<EngineHarness> ScheduledAsync()
    {
        var h = new EngineHarness();
        await h.ImportAsync("Video 1");
        await h.CycleAsync();
        return h;
    }

    [Fact]
    public async Task Waiting_for_another_network_does_not_spend_attempts()
    {
        using var h = await ScheduledAsync();
        var threads = h.Connectors[Platform.Threads];
        threads.OnPublish = _ => PublishOutcome.Waiting("Aguardando o Instagram.", TimeSpan.FromMinutes(1));
        h.SetTime(EngineHarness.Local(14, 10));

        for (var i = 0; i < 8; i++)
        {
            await h.CycleAsync();
            h.Env.Time.Advance(TimeSpan.FromMinutes(1));
        }

        var publication = h.Publication("Video 1", Platform.Threads);
        Assert.Equal(PublicationState.RetryWait, publication.State);
        Assert.Equal(0, publication.AttemptCount);
        Assert.True(threads.PublishCalls.Count >= 4);
    }

    [Fact]
    public async Task Connector_sees_the_other_networks_of_the_same_video()
    {
        using var h = await ScheduledAsync();
        IReadOnlyList<SiblingPublication>? seen = null;
        h.Connectors[Platform.Threads].OnPublish = r => { seen = r.Siblings; return PublishOutcome.Published("t1", null); };
        h.SetTime(EngineHarness.Local(14, 10));

        await h.CycleAsync();

        Assert.NotNull(seen);
        Assert.DoesNotContain(seen!, s => s.Platform == Platform.Threads);
        Assert.Contains(seen!, s => s.Platform == Platform.Instagram);
    }
}
