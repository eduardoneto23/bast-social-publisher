using System.Text.Json.Nodes;
using Bast.Application;
using Bast.Application.Radar;
using Bast.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bast.Tests;

public sealed class RadarHarness : IDisposable
{
    public RadarHarness()
    {
        Options = new BastOptions { RootFolder = Env.Root, StudioFolder = Path.Combine(Env.Root, "ESTUDIO_DE_IDEIAS") };
        Radar = new RadarService(Env.DbFactory, Options, Env.Time);
        Studio = new StudioService(Env.DbFactory, Options, Env.Analyzer, Env.Time, NullLogger<StudioService>.Instance);
        Radar.EnsureDefaultsAsync(default).GetAwaiter().GetResult();
    }

    public TestEnvironment Env { get; } = new();
    public BastOptions Options { get; }
    public RadarService Radar { get; }
    public StudioService Studio { get; }
    public const string Tz = "America/Sao_Paulo";

    public static RadarSuggestionInput Suggestion(string format, int rank, string tag = "GTA 6", bool withSource = true, bool withPlan = true) => new(
        tag, format, rank, $"Ideia {format} {rank}", "Curiosidade", rank % 2 == 0 ? "Brasil" : "Internacional · EUA", "Alta",
        withSource ? [new RadarSourceInput($"https://www.youtube.com/watch?v=abc{rank}", "Vídeo fonte", "YouTube", "Canal", "EUA", 120000, 8000, 900, DateTime.UtcNow.AddHours(-10), 12000, null)] : [],
        new JsonObject { ["o_que_acontece"] = "algo", ["por_que_agora"] = "está crescendo rápido" },
        withPlan
            ? new JsonObject { ["ideia"] = "Ideia central", ["gancho"] = "Existe uma criatura escondida", ["roteiro"] = new JsonArray("cena 1", "cena 2"), ["gravar"] = new JsonArray("mapa", "caminho") }
            : new JsonObject { ["ideia"] = "Só a ideia" });

    public List<Notification> Notifications()
    {
        using var db = Env.DbFactory.CreateDbContext();
        return db.Notifications.AsNoTracking().OrderBy(n => n.CreatedAtUtc).ThenBy(n => n.DedupKey).ToList();
    }

    public void Dispose() => Env.Dispose();
}

public class RadarTests
{
    [Fact]
    public async Task Gta_vi_tag_recognizes_its_synonyms_and_blocks_duplicates()
    {
        using var h = new RadarHarness();
        using var db = h.Env.DbFactory.CreateDbContext();
        var tags = db.RadarTags.ToList();
        var gta = Assert.Single(tags);
        Assert.Equal("GTA VI", gta.Name);
        Assert.True(gta.Active);
        foreach (var name in new[] { "GTA VI", "gta 6", "Grand Theft Auto VI", "GTA6" }) Assert.Same(gta, RadarService.Resolve(tags, name));
        Assert.True(gta.Matches("Como achar o Pé Grande no GTA 6"));
        Assert.False(gta.Matches("Dead by Daylight novo assassino"));

        var (clash, problems) = await h.Radar.SaveTagAsync(null, new RadarTagInput("GTA 6", null, null, null, true), default);
        Assert.Null(clash);
        Assert.Contains("GTA VI", problems.Single());

        var (dbd, _) = await h.Radar.SaveTagAsync(null, new RadarTagInput("DBD", "Dead by Daylight", null, "mobile", false), default);
        Assert.NotNull(dbd);
        Assert.False(dbd!.Active);
    }

    [Fact]
    public async Task Daily_research_is_due_after_the_time_once_a_day_or_when_asked()
    {
        using var h = new RadarHarness();
        // 12:00 UTC = 09:00 em São Paulo: passou das 08:00 e não houve pesquisa hoje.
        var due = await h.Radar.DueAsync(RadarHarness.Tz, default);
        Assert.True(due.Due);
        Assert.Single(due.Tags);

        var run = await h.Radar.BeginRunAsync("agendada", default);
        Assert.False((await h.Radar.DueAsync(RadarHarness.Tz, default)).Due); // em andamento

        await h.Radar.SubmitRunAsync(run.Id, new RadarRunInput([RadarHarness.Suggestion("Horizontal", 1)], "resumo", null), default);
        var after = await h.Radar.DueAsync(RadarHarness.Tz, default);
        Assert.False(after.Due);
        Assert.Contains("já foi feita", after.Reason);

        h.Env.Time.Advance(TimeSpan.FromMinutes(5));
        await h.Radar.RequestManualAsync(default);
        Assert.True((await h.Radar.DueAsync(RadarHarness.Tz, default)).Due);

        // A tarefa atende o pedido manual.
        h.Env.Time.Advance(TimeSpan.FromMinutes(1));
        var manual = await h.Radar.BeginRunAsync("manual", default);
        await h.Radar.SubmitRunAsync(manual.Id, new RadarRunInput([RadarHarness.Suggestion("Vertical", 1)], null, null), default);
        Assert.False((await h.Radar.DueAsync(RadarHarness.Tz, default)).Due);

        await h.Radar.UpdateSettingsAsync("10:30", 12, default);
        h.Env.Time.Advance(TimeSpan.FromDays(1)); // dia seguinte, 09:05: antes do novo horário
        var early = await h.Radar.DueAsync(RadarHarness.Tz, default);
        Assert.False(early.Due);
        Assert.Contains("10:30", early.Reason);
    }

    [Fact]
    public async Task Run_keeps_five_per_format_with_sources_and_sends_a_split_telegram_summary()
    {
        using var h = new RadarHarness();
        var run = await h.Radar.BeginRunAsync("agendada", default);
        var input = Enumerable.Range(1, 6).Select(i => RadarHarness.Suggestion("Horizontal", i))
            .Concat(Enumerable.Range(1, 5).Select(i => RadarHarness.Suggestion("Vertical", i)))
            .Append(RadarHarness.Suggestion("Vertical", 9, withSource: false))
            .Append(RadarHarness.Suggestion("Vertical", 10, tag: "Call of Duty"))
            .Append(RadarHarness.Suggestion("Vertical", 11, withPlan: false))
            .ToList();

        var (saved, rejected) = await h.Radar.SubmitRunAsync(run.Id, new RadarRunInput(input, "Semana de descobertas no GTA 6.", null), default);

        Assert.Equal(RadarRunStatus.Completed, saved!.Status);
        Assert.Equal(4, rejected.Count);
        Assert.Contains(rejected, r => r.Contains("sem fonte verificável"));
        Assert.Contains(rejected, r => r.Contains("Call of Duty"));
        Assert.Contains(rejected, r => r.Contains("plano sem"));
        Assert.Contains(rejected, r => r.Contains("acima de 5"));

        using (var db = h.Env.DbFactory.CreateDbContext())
        {
            var s = db.RadarSuggestions.ToList();
            Assert.Equal(5, s.Count(x => x.Format == RadarFormat.Horizontal));
            Assert.Equal(5, s.Count(x => x.Format == RadarFormat.Vertical));
            Assert.All(s, x => Assert.Equal("GTA VI", x.TagName));
            Assert.All(s, x => Assert.Contains("https://", x.SourcesJson));
        }

        var messages = h.Notifications().Where(n => n.Kind == NotificationKind.RadarDaily).ToList();
        Assert.Equal(3, messages.Count);
        Assert.Contains("Radar de Conteúdo", messages[0].Text);
        Assert.Contains(messages, m => m.Text.Contains("HORIZONTAIS") && m.Text.Contains("H5. Ideia Horizontal 5") && m.Text.Contains("#radar/"));
        Assert.Contains(messages, m => m.Text.Contains("VERTICAIS") && m.Text.Contains("V1."));
        Assert.All(messages, m => Assert.True(m.Text.Length <= 4096));

        // A mesma pesquisa não aceita outro envio.
        var (again, problems) = await h.Radar.SubmitRunAsync(run.Id, new RadarRunInput(input, null, null), default);
        Assert.Null(again);
        Assert.Contains("encerrada", problems.Single());
    }

    [Fact]
    public async Task Few_real_opportunities_are_reported_and_failures_never_reuse_old_suggestions()
    {
        using var h = new RadarHarness();
        var run = await h.Radar.BeginRunAsync("manual", default);
        var (partial, _) = await h.Radar.SubmitRunAsync(run.Id, new RadarRunInput(
            [RadarHarness.Suggestion("Horizontal", 1), RadarHarness.Suggestion("Horizontal", 2)], null, "Pouca novidade hoje."), default);
        Assert.Equal(RadarRunStatus.Partial, partial!.Status);
        Assert.Contains("GTA VI horizontais (YouTube): 2 de 5", partial.Shortfall);
        Assert.Contains("GTA VI verticais (Shorts/Reels/TikTok): 0 de 5", partial.Shortfall);

        var failed = await h.Radar.BeginRunAsync("agendada", default);
        await h.Radar.FailRunAsync(failed.Id, "A pesquisa web não respondeu.", default);
        var warning = h.Notifications().Single(n => n.DedupKey == $"radar:{failed.Id}:falha");
        Assert.Contains("falhou", warning.Text);
        Assert.DoesNotContain("Ideia Horizontal", warning.Text);
        using var db = h.Env.DbFactory.CreateDbContext();
        Assert.Empty(db.RadarSuggestions.Where(s => s.RunId == failed.Id));
    }

    [Fact]
    public async Task Starting_production_creates_a_folder_identified_by_code_and_detects_the_finished_recording()
    {
        using var h = new RadarHarness();
        var run = await h.Radar.BeginRunAsync("agendada", default);
        await h.Radar.SubmitRunAsync(run.Id, new RadarRunInput([RadarHarness.Suggestion("Horizontal", 1), RadarHarness.Suggestion("Vertical", 1)], null, null), default);
        Guid[] ids;
        using (var db = h.Env.DbFactory.CreateDbContext()) ids = db.RadarSuggestions.OrderBy(s => s.Format).Select(s => s.Id).ToArray();

        var (p1, _) = await h.Studio.StartAsync(ids[0], RadarHarness.Tz, default);
        var (p2, _) = await h.Studio.StartAsync(ids[1], RadarHarness.Tz, default);
        Assert.Equal("BAST-20261114-001", p1!.Code);
        Assert.Equal("BAST-20261114-002", p2!.Code);
        Assert.StartsWith(Path.Combine(h.Options.StudioFolder, "00 - GRAVACOES_PENDENTES"), p1.Folder);
        Assert.Contains("BAST_20261114_GTA_VI_IDEIA_HORIZONTAL_1_001", p1.Folder);
        Assert.True(File.Exists(Path.Combine(p1.Folder, StudioService.ScriptFile)));
        Assert.Contains("Existe uma criatura escondida", File.ReadAllText(Path.Combine(p1.Folder, StudioService.ReadableScriptFile)));
        Assert.All(StudioService.Stages, s => Assert.True(Directory.Exists(Path.Combine(h.Options.StudioFolder, s))));
        var (same, message) = await h.Studio.StartAsync(ids[0], RadarHarness.Tz, default);
        Assert.Equal(p1.Id, same!.Id);
        Assert.Contains("já está em produção", message);

        // O usuário renomeia a pasta: a associação é pelo código do roteiro.json.
        var renamed = Path.Combine(Path.GetDirectoryName(p1.Folder)!, "minha gravacao do pe grande");
        Directory.Move(p1.Folder, renamed);
        var video = Path.Combine(renamed, "cena 1.mp4");
        File.WriteAllText(video, "video");
        File.SetLastWriteTimeUtc(video, h.Env.Time.GetUtcNow().UtcDateTime.AddMinutes(-5));

        await h.Studio.ScanAsync(default);
        var waiting = Production(h, p1.Id);
        Assert.Equal(StudioStatus.RecordingDetected, waiting.Status);
        Assert.Contains("terminou de gravar", waiting.StatusDetail);
        Assert.Equal(renamed, waiting.Folder);

        File.WriteAllText(Path.Combine(renamed, StudioService.DoneMarker), "");
        Assert.Equal(1, await h.Studio.ScanAsync(default));
        var ready = Production(h, p1.Id);
        Assert.Equal(StudioStatus.AnalyzingFiles, ready.Status);
        Assert.StartsWith(Path.Combine(h.Options.StudioFolder, "01 - EM_ANALISE"), ready.Folder);
        Assert.True(File.Exists(Path.Combine(ready.Folder, "cena 1.mp4"))); // nada apagado
        Assert.Contains("cena 1.mp4", ready.FilesJson);
        Assert.Equal(StudioStatus.AwaitingRecording, Production(h, p2.Id).Status);
    }

    [Fact]
    public async Task Studio_video_is_never_released_without_individual_approval()
    {
        using var h = new RadarHarness();
        var run = await h.Radar.BeginRunAsync("agendada", default);
        await h.Radar.SubmitRunAsync(run.Id, new RadarRunInput([RadarHarness.Suggestion("Vertical", 1)], null, null), default);
        Guid id;
        using (var db = h.Env.DbFactory.CreateDbContext()) id = db.RadarSuggestions.Single().Id;
        var (p, _) = await h.Studio.StartAsync(id, RadarHarness.Tz, default);

        var (_, refused) = await h.Studio.ReleaseAsync(p!.Id, default);
        Assert.Contains("Só um vídeo aprovado", refused);
        Assert.Null(Production(h, p.Id).ReleasedAtUtc);

        using (var db = h.Env.DbFactory.CreateDbContext())
        {
            db.StudioProductions.Single().Status = StudioStatus.Approved;
            db.SaveChanges();
        }

        await h.Studio.ReleaseAsync(p.Id, default);
        var released = Production(h, p.Id);
        Assert.Equal(StudioStatus.ReleasedForPublishing, released.Status);
        Assert.NotNull(released.ReleasedAtUtc);
    }

    private static StudioProduction Production(RadarHarness h, Guid id)
    {
        using var db = h.Env.DbFactory.CreateDbContext();
        return db.StudioProductions.AsNoTracking().Single(p => p.Id == id);
    }
}
