using System.Net;
using System.Text;
using System.Text.Json;
using Bast.Application;
using Bast.Application.Publishing;
using Bast.Domain;
using Bast.Infrastructure;
using Bast.Notifications.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bast.Tests;

public sealed class FakeChannel : INotificationChannel
{
    public bool IsConfigured { get; set; } = true;
    public List<string> Sent { get; } = [];
    public Func<string, ChannelSendResult> OnSend { get; set; } = _ => ChannelSendResult.Sent;

    public Task<ChannelSendResult> SendAsync(string text, CancellationToken ct)
    {
        var result = OnSend(text);
        if (result.Status == ChannelSendStatus.Sent) Sent.Add(text);
        return Task.FromResult(result);
    }
}

internal static class NotificationTestExtensions
{
    public static List<Notification> Notifications(this TestEnvironment env)
    {
        using var db = env.DbFactory.CreateDbContext();
        return db.Notifications.AsNoTracking().ToList().OrderBy(n => n.CreatedAtUtc).ToList();
    }

    public static void AddNotification(this TestEnvironment env, string text, string? key = null)
    {
        using var db = env.DbFactory.CreateDbContext();
        NotificationOutbox.AddAsync(db, NotificationKind.Test, key, text, env.Time.GetUtcNow().UtcDateTime, default).GetAwaiter().GetResult();
        db.SaveChanges();
        env.Time.Advance(TimeSpan.FromSeconds(1));
    }

    public static NotificationDispatcher Dispatcher(this TestEnvironment env, INotificationChannel channel) =>
        new(env.DbFactory, channel, env.Time, NullLogger<NotificationDispatcher>.Instance) { Spacing = TimeSpan.Zero };
}

public class NotificationOutboxTests
{
    private static async Task<EngineHarness> ScheduledAsync()
    {
        var h = new EngineHarness();
        await h.ImportAsync("Video 1");
        await h.CycleAsync();
        return h;
    }

    [Fact]
    public async Task Published_video_gets_one_summary_with_the_links_and_never_a_second_one()
    {
        using var h = await ScheduledAsync();
        h.SetTime(EngineHarness.Local(14, 10));

        for (var i = 0; i < 5; i++) await h.CycleAsync();
        h.NewEngine();
        await h.CycleAsync();

        var notification = Assert.Single(h.Env.Notifications());
        Assert.Equal(NotificationKind.VideoFinished, notification.Kind);
        Assert.StartsWith("✅ Publicado: Video 1", notification.Text);
        Assert.Contains("YouTube: publicado", notification.Text);
        Assert.Contains("https://exemplo/", notification.Text);
        Assert.Null(notification.SentAtUtc);
    }

    [Fact]
    public async Task Partial_failure_is_reported_with_the_platform_reason()
    {
        using var h = await ScheduledAsync();
        h.Connectors[Platform.Threads].OnPublish = _ => PublishOutcome.Permanent("Vídeo recusado pela plataforma");
        h.SetTime(EngineHarness.Local(14, 10));

        await h.CycleAsync();
        await h.CycleAsync();

        var notification = Assert.Single(h.Env.Notifications());
        Assert.StartsWith("⚠️ Publicado com falhas: Video 1", notification.Text);
        Assert.Contains("Threads: falhou - Vídeo recusado pela plataforma", notification.Text);
        Assert.Contains("Facebook: publicado", notification.Text);
    }

    [Fact]
    public async Task Nothing_is_reported_while_a_platform_is_still_retrying()
    {
        using var h = await ScheduledAsync();
        h.Connectors[Platform.Threads].OnPublish = _ => PublishOutcome.Transient("Erro 503");
        h.SetTime(EngineHarness.Local(14, 10));

        await h.CycleAsync();
        await h.CycleAsync();

        Assert.Empty(h.Env.Notifications());
    }

    [Fact]
    public async Task Unconfirmed_upload_is_reported_once_even_after_many_checks()
    {
        using var h = await ScheduledAsync();
        var youtube = h.Connectors[Platform.YouTube];
        youtube.OnPublish = _ => PublishOutcome.Unknown("Tempo esgotado");
        youtube.OnCheck = _ => PublishOutcome.Unknown("Ainda sem resposta");
        h.SetTime(EngineHarness.Local(14, 10));

        for (var i = 0; i < 4; i++)
        {
            await h.CycleAsync();
            h.Env.Time.Advance(TimeSpan.FromMinutes(2));
        }

        var notification = Assert.Single(h.Env.Notifications());
        Assert.Equal(NotificationKind.PublicationUnknown, notification.Kind);
        Assert.Contains("Video 1 em YouTube", notification.Text);
        Assert.Contains("Tempo esgotado", notification.Text);
    }

    [Fact]
    public async Task Cancelled_video_reports_nothing()
    {
        using var h = await ScheduledAsync();
        await h.Commands.CancelVideoAsync(h.Video("Video 1").Id, default);

        await h.CycleAsync();
        await h.CycleAsync();

        Assert.Empty(h.Env.Notifications());
    }

    [Fact]
    public async Task Account_needing_reconnection_is_reported_only_when_the_status_changes()
    {
        using var env = new TestEnvironment();
        var accounts = new AccountService(env.DbFactory, env.Time);

        await accounts.SetNeedsReauthAsync(Platform.YouTube, "Token revogado", default);
        await accounts.SetNeedsReauthAsync(Platform.YouTube, "Token revogado", default);
        await accounts.SetNeedsReauthAsync(Platform.YouTube, "Token revogado outra vez", default);

        var first = Assert.Single(env.Notifications());
        Assert.Equal(NotificationKind.AccountNeedsReauth, first.Kind);
        Assert.Contains("YouTube", first.Text);
        Assert.Contains("Token revogado", first.Text);

        await accounts.SetConnectedAsync(Platform.YouTube, "canal", "Canal", default);
        await accounts.SetNeedsReauthAsync(Platform.YouTube, "Senha trocada", default);

        Assert.Equal(2, env.Notifications().Count);
    }

    [Fact]
    public async Task Folder_problem_is_reported_once_however_many_times_it_is_seen()
    {
        using var env = new TestEnvironment();
        var folder = env.CreateFolder("Sem legenda", caption: null);

        await env.Service.ProcessFolderAsync(folder, default);
        await env.Service.ProcessFolderAsync(folder, default);
        await env.Service.ProcessFolderAsync(folder, default);

        var notification = Assert.Single(env.Notifications());
        Assert.Equal(NotificationKind.ImportIssue, notification.Kind);
        Assert.Contains("Sem legenda", notification.Text);
    }
}

public class NotificationDispatcherTests
{
    [Fact]
    public async Task Pending_notifications_are_sent_in_order_and_only_once()
    {
        using var env = new TestEnvironment();
        var channel = new FakeChannel();
        var dispatcher = env.Dispatcher(channel);
        env.AddNotification("primeiro");
        env.AddNotification("segundo");
        env.AddNotification("terceiro");

        await dispatcher.RunCycleAsync(default);
        await dispatcher.RunCycleAsync(default);

        Assert.Equal(["primeiro", "segundo", "terceiro"], channel.Sent);
        Assert.All(env.Notifications(), n => Assert.NotNull(n.SentAtUtc));
    }

    [Fact]
    public async Task Same_fact_is_never_queued_twice()
    {
        using var env = new TestEnvironment();
        env.AddNotification("resumo", "video:1:finished");
        env.AddNotification("resumo repetido", "video:1:finished");

        Assert.Single(env.Notifications());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Without_a_channel_notifications_are_discarded_and_not_dumped_when_one_is_connected_later()
    {
        using var env = new TestEnvironment();
        var channel = new FakeChannel { IsConfigured = false };
        var dispatcher = env.Dispatcher(channel);
        env.AddNotification("antigo");

        await dispatcher.RunCycleAsync(default);
        channel.IsConfigured = true;
        env.AddNotification("novo");
        await dispatcher.RunCycleAsync(default);

        Assert.Equal(["novo"], channel.Sent);
        Assert.NotNull(env.Notifications().Single(n => n.Text == "antigo").DiscardedAtUtc);
    }

    [Fact]
    public async Task Temporary_failure_waits_with_backoff_and_keeps_the_order()
    {
        using var env = new TestEnvironment();
        var channel = new FakeChannel { OnSend = _ => new ChannelSendResult(ChannelSendStatus.Retry, "sem rede") };
        var dispatcher = env.Dispatcher(channel);
        env.AddNotification("primeiro");
        env.AddNotification("segundo");

        await dispatcher.RunCycleAsync(default);
        var calls = 0;
        channel.OnSend = _ => { calls++; return ChannelSendResult.Sent; };

        // Antes do prazo (1 minuto) nada é tentado, nem o aviso seguinte.
        env.Time.Advance(TimeSpan.FromSeconds(30));
        await dispatcher.RunCycleAsync(default);
        Assert.Equal(0, calls);
        Assert.Equal("sem rede", env.Notifications()[0].LastError);

        env.Time.Advance(TimeSpan.FromSeconds(31));
        await dispatcher.RunCycleAsync(default);

        Assert.Equal(["primeiro", "segundo"], channel.Sent);
        Assert.Equal(2, env.Notifications()[0].Attempts);
    }

    [Fact]
    public async Task Wait_time_informed_by_the_channel_is_respected()
    {
        using var env = new TestEnvironment();
        var channel = new FakeChannel { OnSend = _ => new ChannelSendResult(ChannelSendStatus.Retry, "limite", TimeSpan.FromMinutes(10)) };
        var dispatcher = env.Dispatcher(channel);
        env.AddNotification("aviso");

        await dispatcher.RunCycleAsync(default);
        channel.OnSend = _ => ChannelSendResult.Sent;
        env.Time.Advance(TimeSpan.FromMinutes(9));
        await dispatcher.RunCycleAsync(default);
        Assert.Empty(channel.Sent);

        env.Time.Advance(TimeSpan.FromMinutes(2));
        await dispatcher.RunCycleAsync(default);
        Assert.Single(channel.Sent);
    }

    [Fact]
    public async Task Notification_that_cannot_be_delivered_in_a_day_is_discarded()
    {
        using var env = new TestEnvironment();
        var channel = new FakeChannel { OnSend = _ => new ChannelSendResult(ChannelSendStatus.Rejected, "bot bloqueado") };
        var dispatcher = env.Dispatcher(channel);
        env.AddNotification("velho");

        await dispatcher.RunCycleAsync(default);
        env.Time.Advance(TimeSpan.FromHours(25));
        channel.OnSend = _ => ChannelSendResult.Sent;
        await dispatcher.RunCycleAsync(default);

        Assert.Empty(channel.Sent);
        Assert.NotNull(env.Notifications().Single().DiscardedAtUtc);
    }

    [Fact]
    public async Task Channel_exception_does_not_lose_the_notification()
    {
        using var env = new TestEnvironment();
        var channel = new FakeChannel { OnSend = _ => throw new InvalidOperationException("falhou") };
        var dispatcher = env.Dispatcher(channel);
        env.AddNotification("aviso");

        await dispatcher.RunCycleAsync(default);
        channel.OnSend = _ => ChannelSendResult.Sent;
        env.Time.Advance(TimeSpan.FromMinutes(2));
        await dispatcher.RunCycleAsync(default);

        Assert.Equal(["aviso"], channel.Sent);
    }
}

/// <summary>Servidor falso que segue a Bot API do Telegram (getMe, getUpdates, sendMessage).</summary>
public sealed class FakeTelegram : HttpMessageHandler, IHttpClientFactory
{
    public const string Token = "123456789:AAExemploDeTokenDoBotFather_abcdefghijk";

    public List<string> Calls { get; } = [];
    public List<JsonElement> Messages { get; } = [];
    public string Updates { get; set; } = "[]";
    public Func<HttpResponseMessage>? SendOverride { get; set; }
    public bool Offline { get; set; }

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    public static string Update(long chatId, string type, string text, string? username = null, string? firstName = null) =>
        JsonSerializer.Serialize(new
        {
            update_id = chatId,
            message = new { message_id = 1, text, chat = new { id = chatId, type, username, first_name = firstName } },
        });

    public static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Error(int code, string description, int? retryAfter = null) =>
        Json(JsonSerializer.Serialize(new { ok = false, error_code = code, description, parameters = retryAfter is null ? null : new { retry_after = retryAfter } }), (HttpStatusCode)code);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Offline) throw new HttpRequestException($"Falha ao conectar em {request.RequestUri}");

        var path = request.RequestUri!.AbsolutePath;
        var prefix = $"/bot{Token}/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return Error(401, "Unauthorized");
        var method = path[prefix.Length..];
        Calls.Add(method);

        switch (method)
        {
            case "getMe":
                return Json("""{"ok":true,"result":{"id":123456789,"is_bot":true,"first_name":"Avisos","username":"avisos_exemplo_bot"}}""");
            case "getUpdates":
                return Json($$"""{"ok":true,"result":{{Updates}}}""");
            case "sendMessage":
                using (var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)))
                {
                    Messages.Add(json.RootElement.Clone());
                }

                return SendOverride?.Invoke() ?? Json("""{"ok":true,"result":{"message_id":10}}""");
            default:
                return Error(404, "Not Found");
        }
    }
}

public class TelegramBotTests
{
    private sealed class Harness : IDisposable
    {
        public Harness(bool linked = false)
        {
            Secrets = new DpapiSecretStore(Env.Workspace);
            Bot = NewBot();
            if (linked)
            {
                Telegram.Updates = "[" + FakeTelegram.Update(555, "private", "/start 4821", "dono_exemplo") + "]";
                Assert.True(Bot.SaveTokenAsync(FakeTelegram.Token, default).GetAwaiter().GetResult().Ok);
                Assert.True(Bot.LinkAsync("4821", default).GetAwaiter().GetResult().Ok);
                Telegram.Calls.Clear();
                Telegram.Messages.Clear();
            }
        }

        public TestEnvironment Env { get; } = new();
        public FakeTelegram Telegram { get; } = new();
        public DpapiSecretStore Secrets { get; }
        public TelegramBot Bot { get; }

        /// <summary>Simula um novo processo após reinício, lendo as credenciais guardadas.</summary>
        public TelegramBot NewBot() => new(Telegram, Secrets, NullLogger<TelegramBot>.Instance);

        public void Dispose() => Env.Dispose();
    }

    [Fact]
    public async Task Token_is_checked_with_telegram_and_stored_only_encrypted()
    {
        using var h = new Harness();

        var result = await h.Bot.SaveTokenAsync("  " + FakeTelegram.Token + "  ", default);

        Assert.True(result.Ok, result.Message);
        Assert.Contains("@avisos_exemplo_bot", result.Message);
        Assert.Equal(new TelegramStatus(true, false, "avisos_exemplo_bot", null), h.Bot.Status);
        Assert.False(h.Bot.IsConfigured);

        var files = Directory.GetFiles(h.Env.Root, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".db", StringComparison.Ordinal)).ToList();
        Assert.Contains(files, f => Path.GetFileName(f).StartsWith("telegram.bot", StringComparison.Ordinal));
        Assert.All(files, f => Assert.DoesNotContain("AAExemploDeToken", Encoding.Latin1.GetString(ReadShared(f))));
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    [Fact]
    public async Task Malformed_token_is_refused_without_calling_telegram()
    {
        using var h = new Harness();

        var result = await h.Bot.SaveTokenAsync("token/../../outra-coisa", default);

        Assert.False(result.Ok);
        Assert.Empty(h.Telegram.Calls);
        Assert.False(h.Bot.Status.HasToken);
    }

    [Fact]
    public async Task Token_refused_by_telegram_is_not_stored()
    {
        using var h = new Harness();

        var result = await h.Bot.SaveTokenAsync("987654321:AAOutroTokenQueOTelegramNaoConhece_xyz", default);

        Assert.False(result.Ok);
        Assert.Contains("Unauthorized", result.Message);
        Assert.False(h.Bot.Status.HasToken);
    }

    [Fact]
    public async Task Only_the_private_chat_that_sent_the_code_is_linked()
    {
        using var h = new Harness();
        await h.Bot.SaveTokenAsync(FakeTelegram.Token, default);
        h.Telegram.Updates = "[" + string.Join(",",
            FakeTelegram.Update(111, "private", "oi, o que esse bot faz?", "estranho"),
            FakeTelegram.Update(-222, "group", "/start 4821", null),
            FakeTelegram.Update(555, "private", "/start 4821", "dono_exemplo")) + "]";

        var result = await h.Bot.LinkAsync("4821", default);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(new TelegramStatus(true, true, "avisos_exemplo_bot", "@dono_exemplo"), h.Bot.Status);
        var hello = Assert.Single(h.Telegram.Messages);
        Assert.Equal(555, hello.GetProperty("chat_id").GetInt64());

        // Depois de reiniciar, a conexão continua valendo.
        Assert.True(h.NewBot().IsConfigured);
    }

    [Fact]
    public async Task Without_the_code_nobody_is_linked()
    {
        using var h = new Harness();
        await h.Bot.SaveTokenAsync(FakeTelegram.Token, default);
        h.Telegram.Updates = "[" + FakeTelegram.Update(111, "private", "/start", "estranho") + "]";

        var result = await h.Bot.LinkAsync("4821", default);

        Assert.False(result.Ok);
        Assert.False(h.Bot.IsConfigured);
        Assert.Empty(h.Telegram.Messages);
    }

    [Fact]
    public async Task Notification_goes_to_the_linked_chat_as_plain_text_without_link_preview()
    {
        using var h = new Harness(linked: true);

        var result = await h.Bot.SendAsync("✅ Publicado: Video 1\nhttps://exemplo/1", default);

        Assert.Equal(ChannelSendStatus.Sent, result.Status);
        var message = Assert.Single(h.Telegram.Messages);
        Assert.Equal(555, message.GetProperty("chat_id").GetInt64());
        Assert.Equal("✅ Publicado: Video 1\nhttps://exemplo/1", message.GetProperty("text").GetString());
        Assert.True(message.GetProperty("link_preview_options").GetProperty("is_disabled").GetBoolean());
        Assert.False(message.TryGetProperty("parse_mode", out _));
    }

    [Fact]
    public async Task Text_longer_than_the_telegram_limit_is_cut()
    {
        using var h = new Harness(linked: true);

        await h.Bot.SendAsync(new string('a', 5000), default);

        Assert.Equal(4096, Assert.Single(h.Telegram.Messages).GetProperty("text").GetString()!.Length);
    }

    [Fact]
    public async Task Flood_limit_is_a_retry_with_the_wait_informed_by_telegram()
    {
        using var h = new Harness(linked: true);
        h.Telegram.SendOverride = () => FakeTelegram.Error(429, "Too Many Requests: retry after 37", 37);

        var result = await h.Bot.SendAsync("aviso", default);

        Assert.Equal(ChannelSendStatus.Retry, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(37), result.RetryAfter);
    }

    [Fact]
    public async Task Blocked_bot_is_a_rejection_that_asks_for_reconnection()
    {
        using var h = new Harness(linked: true);
        h.Telegram.SendOverride = () => FakeTelegram.Error(403, "Forbidden: bot was blocked by the user");

        var result = await h.Bot.SendAsync("aviso", default);

        Assert.Equal(ChannelSendStatus.Rejected, result.Status);
        Assert.Contains("bot was blocked", result.Error);
    }

    [Fact]
    public async Task Network_failure_is_a_retry_and_the_error_never_carries_the_token()
    {
        using var h = new Harness(linked: true);
        h.Telegram.Offline = true;

        var result = await h.Bot.SendAsync("aviso", default);

        Assert.Equal(ChannelSendStatus.Retry, result.Status);
        Assert.DoesNotContain("AAExemploDeToken", result.Error);
        Assert.DoesNotContain("bot123", result.Error);
    }

    [Fact]
    public async Task Disconnect_erases_the_token_and_stops_the_channel()
    {
        using var h = new Harness(linked: true);

        h.Bot.Disconnect();

        Assert.Equal(new TelegramStatus(false, false, null, null), h.Bot.Status);
        Assert.False(h.NewBot().IsConfigured);
        Assert.Equal(ChannelSendStatus.Rejected, (await h.Bot.SendAsync("aviso", default)).Status);
        Assert.Empty(h.Telegram.Messages);
    }

    [Fact]
    public async Task Dispatcher_delivers_the_engine_summary_through_the_bot()
    {
        using var h = new Harness(linked: true);
        var dispatcher = h.Env.Dispatcher(h.Bot);
        h.Env.AddNotification("✅ Publicado: Video 1");

        await dispatcher.RunCycleAsync(default);

        Assert.Equal("✅ Publicado: Video 1", Assert.Single(h.Telegram.Messages).GetProperty("text").GetString());
        Assert.NotNull(h.Env.Notifications().Single().SentAtUtc);
    }
}
