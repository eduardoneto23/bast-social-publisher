using Bast.Application.Publishing;
using Bast.Domain;
using Bast.Infrastructure;
using Bast.Notifications.Telegram;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bast.Tests;

public class TikTokAssistedTests
{
    private sealed class Harness : IDisposable
    {
        public Harness(bool linked = true)
        {
            Secrets = new DpapiSecretStore(Env.Workspace);
            Bot = new TelegramBot(Telegram, Secrets, NullLogger<TelegramBot>.Instance);
            if (linked)
            {
                Telegram.Updates = "[" + FakeTelegram.Update(555, "private", "/start 4821", "dono_exemplo") + "]";
                Assert.True(Bot.SaveTokenAsync(FakeTelegram.Token, default).GetAwaiter().GetResult().Ok);
                Assert.True(Bot.LinkAsync("4821", default).GetAwaiter().GetResult().Ok);
                Telegram.Calls.Clear();
                Telegram.Messages.Clear();
                Telegram.Updates = "[]";
            }

            Connector = new TikTokAssistedConnector(Bot);
            var folder = Path.Combine(Env.Root, "Video 7");
            Directory.CreateDirectory(folder);
            VideoPath = Path.Combine(folder, "video.mp4");
            File.WriteAllBytes(VideoPath, FileBytes);
        }

        public TestEnvironment Env { get; } = new();
        public FakeTelegram Telegram { get; } = new();
        public DpapiSecretStore Secrets { get; }
        public TelegramBot Bot { get; }
        public TikTokAssistedConnector Connector { get; }
        public byte[] FileBytes { get; } = [0, 1, 2, 3, 250, 251, 252];
        public string VideoPath { get; }
        public Guid PublicationId { get; } = Guid.NewGuid();
        public List<string> SavedIds { get; } = [];

        public PublishRequest Request() => new(PublicationId, "chave", VideoPath, null, "Legenda do TikTok 🎵\n#Bast", "Legenda do TikTok",
            SavedIds.LastOrDefault(), id => { SavedIds.Add(id); return Task.CompletedTask; });

        public void Dispose() => Env.Dispose();
    }

    [Fact]
    public async Task Sends_the_original_file_and_the_caption_with_buttons_once()
    {
        using var h = new Harness();

        var outcome = await h.Connector.PublishAsync(h.Request(), default);

        Assert.Equal(PublishOutcomeKind.Processing, outcome.Kind);
        Assert.Equal("manual-sent:10", h.SavedIds.Last());
        var document = Assert.Single(h.Telegram.Documents);
        Assert.Equal("555", document.ChatId);
        Assert.Equal(h.FileBytes, document.Bytes);
        Assert.Equal("Video 7.mp4", document.FileName);
        Assert.Contains("Video 7", document.Caption);

        var message = Assert.Single(h.Telegram.Messages);
        Assert.EndsWith("Legenda do TikTok 🎵\n#Bast", message.GetProperty("text").GetString());
        var buttons = message.GetProperty("reply_markup").GetProperty("inline_keyboard")[0];
        Assert.Equal("tt:ok:" + h.PublicationId.ToString("N"), buttons[0].GetProperty("callback_data").GetString());
        Assert.Equal("tt:no:" + h.PublicationId.ToString("N"), buttons[1].GetProperty("callback_data").GetString());
        Assert.All(buttons.EnumerateArray(), b => Assert.True(System.Text.Encoding.UTF8.GetByteCount(b.GetProperty("callback_data").GetString()!) <= 64));

        // Uma nova tentativa nunca reenvia o vídeo.
        var again = await h.Connector.PublishAsync(h.Request(), default);
        Assert.Equal(PublishOutcomeKind.Processing, again.Kind);
        Assert.Single(h.Telegram.Documents);
        Assert.Single(h.Telegram.Messages);
    }

    [Fact]
    public async Task Waits_for_the_answer_and_never_overwrites_it()
    {
        using var h = new Harness();
        await h.Connector.PublishAsync(h.Request(), default);

        var waiting = await h.Connector.CheckAsync(h.Request(), default);
        Assert.Equal(PublishOutcomeKind.Processing, waiting.Kind);
        Assert.Null(waiting.ExternalId);

        h.SavedIds.Add(ManualPublishing.Done);
        Assert.Equal(PublishOutcomeKind.Published, (await h.Connector.CheckAsync(h.Request(), default)).Kind);

        h.SavedIds.Add(ManualPublishing.Skipped);
        Assert.Equal(PublishOutcomeKind.Skipped, (await h.Connector.CheckAsync(h.Request(), default)).Kind);
    }

    [Fact]
    public async Task File_over_the_telegram_limit_is_not_sent_and_the_message_says_where_it_is()
    {
        using var h = new Harness();
        using (var file = new FileStream(h.VideoPath, FileMode.Open)) file.SetLength(TelegramBot.MaxUploadBytes + 1);

        await h.Connector.PublishAsync(h.Request(), default);

        Assert.Empty(h.Telegram.Documents);
        var text = Assert.Single(h.Telegram.Messages).GetProperty("text").GetString();
        Assert.Contains("03 - PUBLICANDO\\Video 7", text);
        Assert.Contains("50 MB", text);
    }

    [Fact]
    public async Task Telegram_failures_wait_without_spending_attempts()
    {
        using var h = new Harness();
        h.Telegram.DocumentOverride = () => FakeTelegram.Error(429, "Too Many Requests: retry after 5", 5);
        Assert.Equal(PublishOutcomeKind.Offline, (await h.Connector.PublishAsync(h.Request(), default)).Kind);

        h.Telegram.DocumentOverride = () => FakeTelegram.Error(403, "Forbidden: bot was blocked by the user");
        Assert.Equal(PublishOutcomeKind.AuthRequired, (await h.Connector.PublishAsync(h.Request(), default)).Kind);
        Assert.Empty(h.SavedIds);
    }

    [Fact]
    public void Not_available_without_telegram()
    {
        using var h = new Harness(linked: false);
        Assert.False(h.Connector.IsAvailable);
    }
}

public class ManualPublishingThroughEngineTests
{
    private static async Task<EngineHarness> TikTokScheduledAsync()
    {
        var h = new EngineHarness(Platform.TikTok, Platform.YouTube);
        await h.Scheduling.GetSettingsAsync(default);
        using (var db = h.Env.DbFactory.CreateDbContext())
        {
            var settings = db.ScheduleSettings.Single();
            settings.PlatformsMask = ScheduleSettings.MaskOf(Platform.TikTok, Platform.YouTube);
            db.SaveChanges();
        }

        var tiktok = h.Connectors[Platform.TikTok];
        tiktok.OnPublish = _ => PublishOutcome.Processing("manual-sent:10", TimeSpan.FromMinutes(1));
        tiktok.OnCheck = r => r.ExternalId switch
        {
            ManualPublishing.Done => PublishOutcome.Published("tiktok-manual", null),
            ManualPublishing.Skipped => PublishOutcome.Skipped("Você marcou que não vai publicar."),
            _ => PublishOutcome.StillWaiting(TimeSpan.FromMinutes(1)),
        };

        await h.ImportAsync("Video 1");
        await h.CycleAsync();
        h.SetTime(EngineHarness.Local(14, 10));
        await h.CycleAsync();
        return h;
    }

    [Fact]
    public async Task Answer_published_completes_the_video()
    {
        using var h = await TikTokScheduledAsync();
        var publication = h.Publication("Video 1", Platform.TikTok);
        Assert.Equal(PublicationState.Processing, publication.State);

        // Enquanto espera, as consultas não mexem no identificador.
        h.Env.Time.Advance(TimeSpan.FromMinutes(2));
        await h.CycleAsync();
        Assert.Equal("manual-sent:10", h.Publication("Video 1", Platform.TikTok).ExternalId);

        var answer = await new ManualPublishing(h.Env.DbFactory).AnswerAsync(publication.Id, published: true, default);
        Assert.True(answer.Ok, answer.Message);
        await h.CycleAsync();

        Assert.Equal(PublicationState.Published, h.Publication("Video 1", Platform.TikTok).State);
        Assert.Equal(VideoState.Published, h.Video("Video 1").State);
        Assert.Single(h.Connectors[Platform.TikTok].PublishCalls);
    }

    [Fact]
    public async Task Answer_skipped_cancels_only_tiktok()
    {
        using var h = await TikTokScheduledAsync();
        var publication = h.Publication("Video 1", Platform.TikTok);

        await new ManualPublishing(h.Env.DbFactory).AnswerAsync(publication.Id, published: false, default);
        await h.CycleAsync();

        Assert.Equal(PublicationState.Cancelled, h.Publication("Video 1", Platform.TikTok).State);
        Assert.Equal(PublicationState.Published, h.Publication("Video 1", Platform.YouTube).State);
        Assert.Equal(VideoState.Published, h.Video("Video 1").State);
    }

    [Fact]
    public async Task Queue_cancel_does_not_touch_a_publication_waiting_for_the_user()
    {
        using var h = await TikTokScheduledAsync();

        var result = await h.Commands.CancelVideoAsync(h.Video("Video 1").Id, default);

        Assert.Equal((0, 1), result);
        Assert.Equal(PublicationState.Processing, h.Publication("Video 1", Platform.TikTok).State);
    }

    [Fact]
    public async Task Buttons_from_the_linked_chat_answer_and_strangers_are_ignored()
    {
        using var h = await TikTokScheduledAsync();
        var publication = h.Publication("Video 1", Platform.TikTok);
        var telegram = new FakeTelegram { Updates = "[" + FakeTelegram.Update(555, "private", "/start 4821", "dono_exemplo") + "]" };
        var bot = new TelegramBot(telegram, new DpapiSecretStore(h.Env.Workspace), NullLogger<TelegramBot>.Instance);
        await bot.SaveTokenAsync(FakeTelegram.Token, default);
        await bot.LinkAsync("4821", default);
        var handler = new TelegramButtonsHandler(bot, new ManualPublishing(h.Env.DbFactory), NullLogger<TelegramButtonsHandler>.Instance);
        var data = "tt:ok:" + publication.Id.ToString("N");

        telegram.Updates = "[" + FakeTelegram.Callback(900, chatId: 111, fromId: 111, data) + "]";
        await handler.RunOnceAsync(TimeSpan.Zero, default);
        Assert.Equal("manual-sent:10", h.Publication("Video 1", Platform.TikTok).ExternalId);

        telegram.Updates = "[" + FakeTelegram.Callback(901, chatId: 555, fromId: 555, data) + "]";
        await handler.RunOnceAsync(TimeSpan.Zero, default);
        Assert.Equal(ManualPublishing.Done, h.Publication("Video 1", Platform.TikTok).ExternalId);
        Assert.Contains(telegram.Replies, r => r.Method == "editMessageText");
        Assert.Equal(2, telegram.Replies.Count(r => r.Method == "answerCallbackQuery"));
    }
}
