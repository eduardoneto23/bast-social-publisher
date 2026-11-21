using Bast.Application;
using Bast.Application.Publishing;
using Bast.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bast.Tests;

/// <summary>Conector de teste: registra cada chamada e responde conforme programado.</summary>
public sealed class ScriptedConnector(Platform platform) : IPlatformConnector
{
    public Platform Platform => platform;
    public bool IsSimulation => false;
    public bool IsAvailable => true;
    public List<string> PublishCalls { get; } = [];
    public List<string> CheckCalls { get; } = [];
    public Func<PublishRequest, PublishOutcome> OnPublish { get; set; } = r => PublishOutcome.Published("ext-" + r.IdempotencyKey, "https://exemplo/" + r.IdempotencyKey);
    public Func<PublishRequest, PublishOutcome> OnCheck { get; set; } = _ => PublishOutcome.NotFound();

    public Task<PublishOutcome> PublishAsync(PublishRequest request, CancellationToken ct)
    {
        PublishCalls.Add(request.IdempotencyKey);
        return Task.FromResult(OnPublish(request));
    }

    public Task<PublishOutcome> CheckAsync(PublishRequest request, CancellationToken ct)
    {
        CheckCalls.Add(request.IdempotencyKey);
        return Task.FromResult(OnCheck(request));
    }
}

public sealed class TestRegistry(IEnumerable<IPlatformConnector> connectors) : IConnectorRegistry
{
    private readonly Dictionary<Platform, IPlatformConnector> _map = connectors.ToDictionary(c => c.Platform);
    public IPlatformConnector? Get(Platform platform) => _map.GetValueOrDefault(platform);
    public IReadOnlyCollection<Platform> Available => _map.Keys;
    public bool SimulationMode => false;
}

public sealed class EngineHarness : IDisposable
{
    /// <summary>Agenda no modo de intervalo (10:00 + 4 em 4 h), a base da maioria dos testes do motor.</summary>
    public EngineHarness(params Platform[] connected) : this(ScheduleMode.Automatic, connected)
    {
    }

    public EngineHarness(ScheduleMode mode, params Platform[] connected)
    {
        if (connected.Length == 0) connected = [Platform.Instagram, Platform.Facebook, Platform.Threads, Platform.YouTube];
        Connectors = connected.ToDictionary(p => p, p => new ScriptedConnector(p));
        Engine = NewEngine();
        // 14/11/2026 12:00 UTC = 09:00 em São Paulo; horários do intervalo: 10:00, 14:00, 18:00, 22:00.
        using var db = Env.DbFactory.CreateDbContext();
        db.ScheduleSettings.Add(new ScheduleSettings { Mode = mode, UpdatedAtUtc = Env.Time.GetUtcNow().UtcDateTime });
        db.SaveChanges();
    }

    public TestEnvironment Env { get; } = new();
    public Dictionary<Platform, ScriptedConnector> Connectors { get; }
    public PublishingEngine Engine { get; private set; }
    public SchedulingService Scheduling { get; private set; } = null!;
    public QueueCommands Commands { get; private set; } = null!;

    /// <summary>Simula um novo processo após reinício, sobre o mesmo banco e as mesmas pastas.</summary>
    public PublishingEngine NewEngine()
    {
        var registry = new TestRegistry(Connectors.Values);
        Scheduling = new SchedulingService(Env.DbFactory, registry, Env.Time, NullLogger<SchedulingService>.Instance);
        Commands = new QueueCommands(Env.DbFactory, Scheduling, Env.Time);
        var folders = new FolderSync(Env.DbFactory, Env.Workspace, Env.Time, NullLogger<FolderSync>.Instance);
        Engine = new PublishingEngine(Env.DbFactory, registry, Scheduling, folders, Env.Workspace, Env.Time, NullLogger<PublishingEngine>.Instance);
        return Engine;
    }

    public static DateTime Local(int day, int hour, int minute = 0) => new DateTime(2026, 11, day, 0, 0, 0, DateTimeKind.Utc).AddHours(hour + 3).AddMinutes(minute);

    public void SetTime(DateTime utc) => Env.Time.Advance(utc - Env.Time.GetUtcNow().UtcDateTime);

    public async Task ImportAsync(params string[] names)
    {
        foreach (var name in names)
        {
            var outcome = await Env.Service.ProcessFolderAsync(Env.CreateFolder(name, videoContent: "conteudo-" + name), default);
            Assert.Equal(Bast.Application.Import.ImportOutcome.Imported, outcome);
        }
    }

    public Task CycleAsync() => Engine.RunCycleAsync(default);

    public List<Video> Videos()
    {
        using var db = Env.DbFactory.CreateDbContext();
        return db.Videos.AsNoTracking().Include(v => v.Publications).ToList()
            .OrderBy(v => v.FolderName, NaturalStringComparer.Instance).ToList();
    }

    public Video Video(string name) => Videos().Single(v => v.FolderName == name);

    public Publication Publication(string name, Platform platform) => Video(name).Publications.Single(p => p.Platform == platform);

    public int TotalPublishCalls => Connectors.Values.Sum(c => c.PublishCalls.Count);

    public void Dispose() => Env.Dispose();
}

public class SchedulingTests
{
    [Fact]
    public async Task Videos_get_consecutive_slots_in_numeric_order_and_one_publication_per_platform()
    {
        using var h = new EngineHarness();
        await h.ImportAsync("Video 10", "Video 2", "Video 001", "Video 3", "Video 4");

        await h.CycleAsync();

        var videos = h.Videos();
        Assert.Equal(
            [EngineHarness.Local(14, 10), EngineHarness.Local(14, 14), EngineHarness.Local(14, 18), EngineHarness.Local(14, 22), EngineHarness.Local(15, 10)],
            videos.Select(v => v.ScheduledAtUtc!.Value).ToArray());
        Assert.All(videos, v =>
        {
            Assert.Equal(4, v.Publications.Count);
            Assert.All(v.Publications, p =>
            {
                Assert.Equal(PublicationState.Scheduled, p.State);
                Assert.Equal(v.ScheduledAtUtc, p.ScheduledAtUtc);
            });
        });
        Assert.Equal(0, h.TotalPublishCalls);
    }

    [Fact]
    public async Task Restart_keeps_the_same_slots_and_creates_nothing_twice()
    {
        using var h = new EngineHarness();
        await h.ImportAsync("Video 1", "Video 2");
        await h.CycleAsync();
        var before = h.Videos().Select(v => (v.Id, v.ScheduledAtUtc, v.Publications.Count)).ToList();

        h.NewEngine();
        await h.Engine.RecoverAsync(default);
        await h.CycleAsync();
        await h.CycleAsync();

        Assert.Equal(before, h.Videos().Select(v => (v.Id, v.ScheduledAtUtc, v.Publications.Count)).ToList());
    }

    [Fact]
    public async Task New_video_goes_after_the_ones_already_scheduled()
    {
        using var h = new EngineHarness();
        await h.ImportAsync("Video 5", "Video 6");
        await h.CycleAsync();

        await h.ImportAsync("Video 1");
        await h.CycleAsync();

        Assert.Equal(EngineHarness.Local(14, 18), h.Video("Video 1").ScheduledAtUtc);
        Assert.Equal(EngineHarness.Local(14, 10), h.Video("Video 5").ScheduledAtUtc);
    }

    [Fact]
    public async Task Incompatible_caption_is_flagged_before_publication_only_for_that_platform()
    {
        using var h = new EngineHarness();
        var folder = h.Env.CreateFolder("Video 1", caption: "Título\n" + new string('a', 600));
        await h.Env.Service.ProcessFolderAsync(folder, default);

        await h.CycleAsync();

        var threads = h.Publication("Video 1", Platform.Threads);
        Assert.Equal(PublicationState.Failed, threads.State);
        Assert.Contains("limite é 500", threads.LastError);
        Assert.All(h.Video("Video 1").Publications.Where(p => p.Platform != Platform.Threads),
            p => Assert.Equal(PublicationState.Scheduled, p.State));
        Assert.Equal(0, h.TotalPublishCalls);
    }

    [Fact]
    public async Task Without_connected_networks_videos_wait_unscheduled()
    {
        using var h = new EngineHarness();
        h.Connectors.Clear();
        h.NewEngine();
        await h.ImportAsync("Video 1");

        await h.CycleAsync();

        var video = h.Video("Video 1");
        Assert.Equal(VideoState.Ready, video.State);
        Assert.Null(video.ScheduledAtUtc);
        Assert.Empty(video.Publications);
    }

    [Fact]
    public async Task Disabled_platform_gets_no_publication()
    {
        using var h = new EngineHarness(Platform.TikTok, Platform.YouTube);
        await h.ImportAsync("Video 1");

        await h.CycleAsync();

        // TikTok vem desabilitado por padrão nas configurações.
        Assert.Equal([Platform.YouTube], h.Video("Video 1").Publications.Select(p => p.Platform).ToArray());
    }
}

public class PublishingEngineTests
{
    private static async Task<EngineHarness> ScheduledAsync(params string[] names)
    {
        var h = new EngineHarness();
        await h.ImportAsync(names.Length == 0 ? ["Video 1"] : names);
        await h.CycleAsync();
        return h;
    }

    [Fact]
    public async Task Nothing_is_published_before_the_scheduled_time()
    {
        using var h = await ScheduledAsync();
        h.SetTime(EngineHarness.Local(14, 9, 59));

        await h.CycleAsync();

        Assert.Equal(0, h.TotalPublishCalls);
    }

    [Fact]
    public async Task At_the_scheduled_time_every_platform_is_published_once_and_folder_goes_to_sent()
    {
        using var h = await ScheduledAsync();
        h.SetTime(EngineHarness.Local(14, 10));

        await h.CycleAsync();
        await h.CycleAsync();
        await h.CycleAsync();

        var video = h.Video("Video 1");
        Assert.Equal(VideoState.Published, video.State);
        Assert.Equal(WorkspaceFolder.Sent, video.CurrentFolder);
        Assert.True(File.Exists(Path.Combine(h.Env.Workspace.ToAbsolute(video.RelativePath), "video.mp4")));
        Assert.StartsWith(h.Env.Workspace.PathOf(WorkspaceFolder.Sent), h.Env.Workspace.ToAbsolute(video.RelativePath));
        Assert.All(video.Publications, p =>
        {
            Assert.Equal(PublicationState.Published, p.State);
            Assert.Equal(1, p.AttemptCount);
            Assert.StartsWith("ext-", p.ExternalId);
            Assert.StartsWith("https://exemplo/", p.ExternalUrl);
            Assert.False(p.Simulated);
        });
        Assert.All(h.Connectors.Values, c => Assert.Single(c.PublishCalls));
    }

    [Fact]
    public async Task Connector_receives_the_untouched_caption_and_the_first_line_as_title()
    {
        using var h = await ScheduledAsync();
        PublishRequest? seen = null;
        var fileExisted = false;
        h.Connectors[Platform.YouTube].OnPublish = r => { seen = r; fileExisted = File.Exists(r.VideoPath); return PublishOutcome.Published("id", null); };
        h.SetTime(EngineHarness.Local(14, 10));

        await h.CycleAsync();

        Assert.Equal("TÍTULO\nDescrição 🎮\n#Bast", seen!.Text);
        Assert.Equal("TÍTULO", seen.Title);
        Assert.True(fileExisted);
    }

    [Fact]
    public async Task Partial_failure_keeps_confirmed_results_and_retries_only_the_failed_platform()
    {
        using var h = await ScheduledAsync();
        var threads = h.Connectors[Platform.Threads];
        threads.OnPublish = _ => PublishOutcome.Transient("Erro 503 da plataforma");
        h.SetTime(EngineHarness.Local(14, 10));

        await h.CycleAsync();

        Assert.Equal(PublicationState.RetryWait, h.Publication("Video 1", Platform.Threads).State);
        Assert.Equal("Erro 503 da plataforma", h.Publication("Video 1", Platform.Threads).LastError);
        Assert.Equal(VideoState.Publishing, h.Video("Video 1").State);
        Assert.Equal(WorkspaceFolder.Publishing, h.Video("Video 1").CurrentFolder);
        Assert.All(h.Video("Video 1").Publications.Where(p => p.Platform != Platform.Threads),
            p => Assert.Equal(PublicationState.Published, p.State));

        // Antes do prazo de espera (1 minuto) nada é repetido.
        h.Env.Time.Advance(TimeSpan.FromSeconds(30));
        await h.CycleAsync();
        Assert.Single(threads.PublishCalls);

        threads.OnPublish = r => PublishOutcome.Published("ok", null);
        h.Env.Time.Advance(TimeSpan.FromSeconds(31));
        await h.CycleAsync();
        await h.CycleAsync();

        Assert.Equal(2, threads.PublishCalls.Count);
        Assert.All(h.Connectors.Values.Where(c => c.Platform != Platform.Threads), c => Assert.Single(c.PublishCalls));
        Assert.Equal(VideoState.Published, h.Video("Video 1").State);
        Assert.Equal(2, h.Publication("Video 1", Platform.Threads).AttemptCount);
    }

    [Fact]
    public async Task Retries_use_exponential_backoff_and_stop_at_the_limit()
    {
        using var h = await ScheduledAsync();
        var threads = h.Connectors[Platform.Threads];
        threads.OnPublish = _ => PublishOutcome.Transient("fora do ar");
        h.SetTime(EngineHarness.Local(14, 10));

        var waits = new List<TimeSpan>();
        for (var i = 0; i < 20 && h.Publication("Video 1", Platform.Threads).State != PublicationState.Failed; i++)
        {
            await h.CycleAsync();
            var p = h.Publication("Video 1", Platform.Threads);
            if (p.State != PublicationState.RetryWait) continue;
            waits.Add(p.NextRetryAtUtc!.Value - h.Env.Time.GetUtcNow().UtcDateTime);
            h.SetTime(p.NextRetryAtUtc.Value);
        }

        Assert.Equal([1, 2, 4, 8], waits.Select(w => (int)w.TotalMinutes).ToArray());
        var failed = h.Publication("Video 1", Platform.Threads);
        Assert.Equal(PublicationState.Failed, failed.State);
        Assert.Equal(5, threads.PublishCalls.Count);
        Assert.Contains("Limite de 5 tentativas", failed.LastError);

        await h.CycleAsync();
        var video = h.Video("Video 1");
        Assert.Equal(VideoState.Error, video.State);
        Assert.Equal(WorkspaceFolder.Errors, video.CurrentFolder);
        Assert.Equal(3, video.Publications.Count(p => p.State == PublicationState.Published));

        // Falha definitiva não continua repetindo.
        h.Env.Time.Advance(TimeSpan.FromDays(2));
        await h.CycleAsync();
        Assert.Equal(5, threads.PublishCalls.Count);
    }

    [Fact]
    public async Task Rate_limit_wait_informed_by_the_platform_is_respected()
    {
        using var h = await ScheduledAsync();
        var youtube = h.Connectors[Platform.YouTube];
        youtube.OnPublish = _ => PublishOutcome.Transient("Limite de requisições", TimeSpan.FromMinutes(45));
        h.SetTime(EngineHarness.Local(14, 10));
        await h.CycleAsync();

        h.Env.Time.Advance(TimeSpan.FromMinutes(44));
        await h.CycleAsync();
        Assert.Single(youtube.PublishCalls);

        h.Env.Time.Advance(TimeSpan.FromMinutes(1));
        await h.CycleAsync();
        Assert.Equal(2, youtube.PublishCalls.Count);
    }

    [Fact]
    public async Task Rejected_video_fails_at_once_without_retry()
    {
        using var h = await ScheduledAsync();
        h.Connectors[Platform.Instagram].OnPublish = _ => PublishOutcome.Permanent("Vídeo rejeitado: formato não suportado");
        h.SetTime(EngineHarness.Local(14, 10));

        await h.CycleAsync();
        h.Env.Time.Advance(TimeSpan.FromHours(3));
        await h.CycleAsync();

        Assert.Single(h.Connectors[Platform.Instagram].PublishCalls);
        Assert.Equal(PublicationState.Failed, h.Publication("Video 1", Platform.Instagram).State);
        Assert.Equal(VideoState.Error, h.Video("Video 1").State);
    }

    [Fact]
    public async Task Timeout_is_unknown_and_is_reconciled_instead_of_published_again()
    {
        using var h = await ScheduledAsync();
        var facebook = h.Connectors[Platform.Facebook];
        facebook.OnPublish = _ => throw new TimeoutException("tempo esgotado");
        facebook.OnCheck = _ => PublishOutcome.Unknown("plataforma indisponível");
        h.SetTime(EngineHarness.Local(14, 10));

        await h.CycleAsync();
        Assert.Equal(PublicationState.Unknown, h.Publication("Video 1", Platform.Facebook).State);

        // Enquanto a plataforma não confirma nada, a publicação não é repetida.
        for (var i = 0; i < 5; i++)
        {
            h.Env.Time.Advance(TimeSpan.FromMinutes(2));
            await h.CycleAsync();
        }

        Assert.Single(facebook.PublishCalls);
        Assert.Equal(PublicationState.Unknown, h.Publication("Video 1", Platform.Facebook).State);
        Assert.True(facebook.CheckCalls.Count >= 5);

        // A consulta encontra a publicação: confirmada sem novo envio.
        facebook.OnCheck = _ => PublishOutcome.Published("fb-123", "https://facebook.exemplo/123");
        h.Env.Time.Advance(TimeSpan.FromMinutes(2));
        await h.CycleAsync();
        await h.CycleAsync();

        Assert.Single(facebook.PublishCalls);
        var p = h.Publication("Video 1", Platform.Facebook);
        Assert.Equal(PublicationState.Published, p.State);
        Assert.Equal("fb-123", p.ExternalId);
        Assert.Equal(VideoState.Published, h.Video("Video 1").State);
    }

    [Fact]
    public async Task Unknown_is_retried_only_after_the_platform_says_it_has_nothing()
    {
        using var h = await ScheduledAsync();
        var facebook = h.Connectors[Platform.Facebook];
        facebook.OnPublish = _ => PublishOutcome.Unknown("conexão caiu durante o envio");
        h.SetTime(EngineHarness.Local(14, 10));
        await h.CycleAsync();

        facebook.OnCheck = _ => PublishOutcome.NotFound();
        facebook.OnPublish = r => PublishOutcome.Published("fb-9", null);
        h.Env.Time.Advance(TimeSpan.FromMinutes(2));
        await h.CycleAsync();
        Assert.Equal(PublicationState.RetryWait, h.Publication("Video 1", Platform.Facebook).State);
        Assert.Single(facebook.PublishCalls);

        h.Env.Time.Advance(TimeSpan.FromMinutes(2));
        await h.CycleAsync();
        Assert.Equal(PublicationState.Published, h.Publication("Video 1", Platform.Facebook).State);
        Assert.Equal(2, facebook.PublishCalls.Count);
    }

    [Fact]
    public async Task Power_loss_during_upload_becomes_unknown_on_restart_and_is_not_resent_blindly()
    {
        using var h = await ScheduledAsync();
        var youtube = h.Connectors[Platform.YouTube];
        // Simula a queda: o processo morre dentro da chamada à plataforma.
        youtube.OnPublish = _ => throw new OperationCanceledException();
        h.SetTime(EngineHarness.Local(14, 10));
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Engine.RunCycleAsync(cts.Token));
        }

        // A queda acima aconteceu antes de qualquer envio; força o caso real gravando UPLOADING no banco.
        await using (var db = h.Env.DbFactory.CreateDbContext())
        {
            var p = db.Publications.Single(x => x.Platform == Platform.YouTube);
            p.TransitionTo(PublicationState.Uploading, h.Env.Time.GetUtcNow().UtcDateTime);
            p.AttemptCount = 1;
            p.ExternalId = "sessao-de-upload-1";
            await db.SaveChangesAsync();
        }

        h.NewEngine();
        youtube.OnPublish = r => PublishOutcome.Published("novo", null);
        string? checkedWith = null;
        youtube.OnCheck = r => { checkedWith = r.ExternalId; return PublishOutcome.Published("yt-existente", "https://youtu.exemplo/1"); };
        await h.Engine.RecoverAsync(default);
        Assert.Equal(PublicationState.Unknown, h.Publication("Video 1", Platform.YouTube).State);

        await h.CycleAsync();

        Assert.Empty(youtube.PublishCalls);
        Assert.Equal("sessao-de-upload-1", checkedWith);
        var result = h.Publication("Video 1", Platform.YouTube);
        Assert.Equal(PublicationState.Published, result.State);
        Assert.Equal("yt-existente", result.ExternalId);
    }

    [Fact]
    public async Task External_id_saved_mid_upload_survives_a_crash()
    {
        using var h = await ScheduledAsync();
        h.Connectors[Platform.Instagram].OnPublish = r =>
        {
            r.SaveExternalIdAsync("container-77").GetAwaiter().GetResult();
            throw new IOException("conexão perdida");
        };
        h.SetTime(EngineHarness.Local(14, 10));

        await h.CycleAsync();

        var p = h.Publication("Video 1", Platform.Instagram);
        Assert.Equal(PublicationState.Unknown, p.State);
        Assert.Equal("container-77", p.ExternalId);
    }

    [Fact]
    public async Task After_two_days_offline_backlog_is_spread_over_next_slots_never_all_at_once()
    {
        using var h = await ScheduledAsync("Video 1", "Video 2", "Video 3", "Video 4", "Video 5");

        // Computador desligado por dois dias; volta às 11:00 do dia 16.
        h.SetTime(EngineHarness.Local(16, 11));
        h.NewEngine();
        await h.Engine.RecoverAsync(default);
        await h.CycleAsync();

        Assert.Equal(0, h.TotalPublishCalls);
        Assert.Equal(
            [EngineHarness.Local(16, 14), EngineHarness.Local(16, 18), EngineHarness.Local(16, 22), EngineHarness.Local(17, 10), EngineHarness.Local(17, 14)],
            h.Videos().Select(v => v.ScheduledAtUtc!.Value).ToArray());

        h.SetTime(EngineHarness.Local(16, 14));
        await h.CycleAsync();
        await h.CycleAsync();

        Assert.Equal(4, h.TotalPublishCalls);
        Assert.Equal(VideoState.Published, h.Video("Video 1").State);
        Assert.All(h.Videos().Skip(1), v => Assert.Equal(VideoState.Ready, v.State));
    }

    [Fact]
    public async Task Next_cycle_policy_waits_for_the_next_day()
    {
        using var h = await ScheduledAsync("Video 1", "Video 2");
        await h.Commands.UpdateSettingsAsync(new ScheduleSettingsInput("10:00", "23:59", 240, 10, Enum.GetValues<DayOfWeek>(),
            "America/Sao_Paulo", [Platform.YouTube], MissedSlotPolicy.NextCycle, 30, 5), default);

        h.SetTime(EngineHarness.Local(16, 11));
        await h.CycleAsync();

        Assert.Equal([EngineHarness.Local(17, 10), EngineHarness.Local(17, 14)], h.Videos().Select(v => v.ScheduledAtUtc!.Value).ToArray());
    }

    [Fact]
    public async Task Short_delay_within_tolerance_still_publishes_at_the_original_slot()
    {
        using var h = await ScheduledAsync("Video 1", "Video 2");
        h.SetTime(EngineHarness.Local(14, 10, 20));

        await h.CycleAsync();

        Assert.Equal(4, h.TotalPublishCalls);
        Assert.Equal(EngineHarness.Local(14, 14), h.Video("Video 2").ScheduledAtUtc);
    }

    [Fact]
    public async Task Two_videos_late_within_tolerance_are_never_published_together()
    {
        using var h = new EngineHarness();
        await h.Commands.UpdateSettingsAsync(new ScheduleSettingsInput("10:00", "23:59", 10, 100, Enum.GetValues<DayOfWeek>(),
            "America/Sao_Paulo", [Platform.YouTube], MissedSlotPolicy.NextAllowedSlot, 30, 5), default);
        await h.ImportAsync("Video 1", "Video 2", "Video 3");
        await h.CycleAsync();

        // Serviço parado por 25 minutos: três horários (10:00, 10:10, 10:20) venceram dentro da tolerância.
        h.SetTime(EngineHarness.Local(14, 10, 25));
        await h.CycleAsync();

        Assert.Equal(1, h.TotalPublishCalls);
        Assert.Equal(PublicationState.Published, h.Publication("Video 1", Platform.YouTube).State);
        Assert.Equal(EngineHarness.Local(14, 10, 30), h.Video("Video 2").ScheduledAtUtc);
        Assert.Equal(EngineHarness.Local(14, 10, 40), h.Video("Video 3").ScheduledAtUtc);
    }

    [Fact]
    public async Task Paused_queue_publishes_nothing_and_resumes_without_dumping_the_backlog()
    {
        using var h = await ScheduledAsync("Video 1", "Video 2", "Video 3");
        await h.Commands.SetPausedAsync(true, default);

        h.SetTime(EngineHarness.Local(14, 23));
        await h.CycleAsync();
        Assert.Equal(0, h.TotalPublishCalls);

        await h.Commands.SetPausedAsync(false, default);
        await h.CycleAsync();
        Assert.Equal(0, h.TotalPublishCalls);
        Assert.Equal(EngineHarness.Local(15, 10), h.Video("Video 1").ScheduledAtUtc);

        h.SetTime(EngineHarness.Local(15, 10));
        await h.CycleAsync();
        Assert.Equal(4, h.TotalPublishCalls);
    }

    [Fact]
    public async Task Publish_now_sends_only_that_video_and_keeps_the_others_on_schedule()
    {
        using var h = await ScheduledAsync("Video 1", "Video 2");
        var second = h.Video("Video 2");
        var firstSlot = h.Video("Video 1").ScheduledAtUtc;

        Assert.Equal(4, await h.Commands.PublishNowAsync(second.Id, default));
        await h.CycleAsync();
        await h.CycleAsync();

        Assert.Equal(4, h.TotalPublishCalls);
        Assert.Equal(VideoState.Published, h.Video("Video 2").State);
        Assert.Equal(VideoState.Ready, h.Video("Video 1").State);
        Assert.Equal(firstSlot, h.Video("Video 1").ScheduledAtUtc);

        // Pedir de novo não reenvia o que já saiu.
        Assert.Equal(0, await h.Commands.PublishNowAsync(second.Id, default));
        await h.CycleAsync();
        Assert.Equal(4, h.TotalPublishCalls);
        Assert.Null(await h.Commands.PublishNowAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Cancelled_video_is_never_published_and_goes_to_archived()
    {
        using var h = await ScheduledAsync("Video 1", "Video 2");
        var result = await h.Commands.CancelVideoAsync(h.Video("Video 1").Id, default);
        Assert.Equal((4, 0), result);

        h.SetTime(EngineHarness.Local(14, 14));
        await h.CycleAsync();
        await h.CycleAsync();

        Assert.DoesNotContain(h.Connectors.Values.SelectMany(c => c.PublishCalls), key => key.StartsWith(h.Video("Video 1").Id.ToString("N")));
        Assert.Equal(VideoState.Archived, h.Video("Video 1").State);
        Assert.Equal(WorkspaceFolder.Archived, h.Video("Video 1").CurrentFolder);
        Assert.Equal(VideoState.Published, h.Video("Video 2").State);
    }

    [Fact]
    public async Task Upload_with_unknown_result_cannot_be_cancelled()
    {
        using var h = await ScheduledAsync();
        h.Connectors[Platform.Facebook].OnPublish = _ => PublishOutcome.Unknown("sem resposta");
        h.Connectors[Platform.Facebook].OnCheck = _ => PublishOutcome.Unknown("sem resposta");
        h.SetTime(EngineHarness.Local(14, 10));
        await h.CycleAsync();

        var result = await h.Commands.CancelVideoAsync(h.Video("Video 1").Id, default);

        Assert.Equal((0, 1), result);
        Assert.Equal(PublicationState.Unknown, h.Publication("Video 1", Platform.Facebook).State);
    }

    [Fact]
    public async Task Asynchronous_processing_is_polled_until_the_platform_confirms()
    {
        using var h = await ScheduledAsync();
        var instagram = h.Connectors[Platform.Instagram];
        instagram.OnPublish = _ => PublishOutcome.Processing("container-1");
        instagram.OnCheck = _ => PublishOutcome.Processing("container-1");
        h.SetTime(EngineHarness.Local(14, 10));

        await h.CycleAsync();
        Assert.Equal(PublicationState.Processing, h.Publication("Video 1", Platform.Instagram).State);
        Assert.Equal(VideoState.Publishing, h.Video("Video 1").State);

        instagram.OnCheck = _ => PublishOutcome.Published("media-1", "https://instagram.exemplo/1");
        h.Env.Time.Advance(TimeSpan.FromSeconds(31));
        await h.CycleAsync();
        await h.CycleAsync();

        Assert.Single(instagram.PublishCalls);
        Assert.Equal(PublicationState.Published, h.Publication("Video 1", Platform.Instagram).State);
        Assert.Equal(VideoState.Published, h.Video("Video 1").State);
    }

    [Fact]
    public async Task Locked_folder_does_not_block_publication_and_is_moved_later()
    {
        using var h = await ScheduledAsync();
        var video = h.Video("Video 1");
        var file = Path.Combine(h.Env.Workspace.ToAbsolute(video.RelativePath), "legenda.txt");
        h.SetTime(EngineHarness.Local(14, 10));

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await h.CycleAsync();
            await h.CycleAsync();
            Assert.Equal(VideoState.Published, h.Video("Video 1").State);
            Assert.Equal(WorkspaceFolder.Scheduled, h.Video("Video 1").CurrentFolder);
        }

        await h.CycleAsync();
        Assert.Equal(WorkspaceFolder.Sent, h.Video("Video 1").CurrentFolder);
        Assert.Equal(4, h.TotalPublishCalls);
    }

    [Fact]
    public async Task Simulation_mode_runs_the_whole_flow_and_marks_publications_as_simulated()
    {
        using var env = new TestEnvironment();
        var registry = new ConnectorRegistry([], new BastOptions { SimulationMode = true });
        var scheduling = new SchedulingService(env.DbFactory, registry, env.Time, NullLogger<SchedulingService>.Instance);
        var engine = new PublishingEngine(env.DbFactory, registry, scheduling,
            new FolderSync(env.DbFactory, env.Workspace, env.Time, NullLogger<FolderSync>.Instance),
            env.Workspace, env.Time, NullLogger<PublishingEngine>.Instance);
        await env.Service.ProcessFolderAsync(env.CreateFolder("Video 1"), default);

        await engine.RunCycleAsync(default);
        env.Time.Advance(TimeSpan.FromMinutes(65));
        await engine.RunCycleAsync(default);
        await engine.RunCycleAsync(default);

        await using var db = env.DbFactory.CreateDbContext();
        var video = db.Videos.Include(v => v.Publications).Single();
        Assert.Equal(VideoState.Published, video.State);
        Assert.All(video.Publications, p =>
        {
            Assert.True(p.Simulated);
            Assert.Null(p.ExternalUrl);
            Assert.StartsWith("simulacao-", p.ExternalId);
        });
    }
}
