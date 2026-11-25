using Bast.Application;
using Bast.Application.Import;
using Bast.Application.Production;
using Bast.Application.Publishing;
using Bast.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bast.Tests;

public sealed class FakeDisk : IDiskSpace
{
    public long? Free { get; set; } = 600 * ProductionSpace.Gb;
    public long? FreeBytes(string path) => Free;
}

/// <summary>Etapa de teste que só termina quando o teste manda (para testar parada, pausa e retomada).</summary>
public sealed class BlockingStep(string name) : IProductionStep
{
    public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Runs { get; private set; }

    public string Name => name;
    public string Title => "Etapa de teste";
    public ProductionStatus Status => ProductionStatus.Analyzing;

    public async Task<string> RunAsync(StepContext context, CancellationToken ct)
    {
        Runs++;
        File.WriteAllText(Path.Combine(context.WorkFolder, ProductionFolders.Temp, "parcial.part"), "pela metade");
        Started.TrySetResult();
        await Release.Task.WaitAsync(ct);
        return "ok";
    }

    public void Reset()
    {
        Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed class ProductionHarness : IDisposable
{
    public ProductionHarness(BlockingStep? extra = null)
    {
        Options = new BastOptions
        {
            RootFolder = Env.Root,
            ProductionWorkFolder = Path.Combine(Env.Root, "trabalho"),
            ProductionStableSeconds = 120,
            ProductionMinDurationSeconds = 300,
        };
        Env.Analyzer.Handler = _ => VideoAnalysisResult.Ok(new VideoAnalysis(1100, 1920, 1080, "hevc", "aac", 60, 33_000_000, "mov,mp4", 4));
        var steps = new List<IProductionStep> { new IntegrityStep(Env.Analyzer) };
        if (extra is not null) steps.Add(extra);
        Pipeline = new ProductionPipeline(steps);
        Scanner = new RecordingScanner(Env.DbFactory, Env.Workspace, Options, Env.Analyzer, Pipeline, Env.Time, NullLogger<RecordingScanner>.Instance);
        Runner = new ProductionRunner(Env.DbFactory, Pipeline, Options, Disk, Env.Time, NullLogger<ProductionRunner>.Instance);
        Commands = new ProductionCommands(Env.DbFactory, Runner, Env.Time);
    }

    public TestEnvironment Env { get; } = new();
    public BastOptions Options { get; }
    public FakeDisk Disk { get; } = new();
    public ProductionPipeline Pipeline { get; }
    public RecordingScanner Scanner { get; }
    public ProductionRunner Runner { get; }
    public ProductionCommands Commands { get; }

    public string Recordings => Env.Workspace.PathOf(WorkspaceFolder.Recordings);

    public string Record(string name, string content = "gravacao-do-obs")
    {
        var path = Path.Combine(Recordings, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Varre, espera a gravação estabilizar e varre de novo.</summary>
    public async Task ScanUntilStableAsync()
    {
        await Scanner.ScanAsync(default);
        Env.Time.Advance(TimeSpan.FromSeconds(Options.ProductionStableSeconds + 1));
        await Scanner.ScanAsync(default);
    }

    public ProductionJob Job(string name)
    {
        using var db = Env.DbFactory.CreateDbContext();
        return db.ProductionJobs.AsNoTracking().Include(j => j.Steps).Single(j => j.SourceFileName == name);
    }

    public List<ProductionLog> Logs(Guid id)
    {
        using var db = Env.DbFactory.CreateDbContext();
        return db.ProductionLogs.AsNoTracking().Where(l => l.JobId == id).OrderBy(l => l.Id).ToList();
    }

    public void Dispose() => Env.Dispose();
}

public class ProductionTests
{
    [Fact]
    public async Task Only_finished_full_recordings_become_jobs_and_replays_are_ignored()
    {
        using var h = new ProductionHarness();
        h.Record("2026-11-25 20-00-00.mp4");
        h.Record("Replay 2026-11-25 20-10-00.mp4");
        h.Record("Screenshot 2026-11-25.png");
        h.Record("anotacoes.txt");

        await h.Scanner.ScanAsync(default);
        Assert.Equal("Estabilizando", h.Scanner.LastScan.Single(r => r.FileName.StartsWith("2026")).State);
        Assert.Equal("Ignorado", h.Scanner.LastScan.Single(r => r.FileName.StartsWith("Replay")).State);
        Assert.Equal("Ignorado", h.Scanner.LastScan.Single(r => r.FileName.StartsWith("Screenshot")).State);
        Assert.Equal("Ignorado", h.Scanner.LastScan.Single(r => r.FileName == "anotacoes.txt").State);

        h.Env.Time.Advance(TimeSpan.FromSeconds(121));
        Assert.Equal(1, await h.Scanner.ScanAsync(default));
        Assert.Equal(0, await h.Scanner.ScanAsync(default)); // a mesma gravação nunca gera dois jobs

        var job = h.Job("2026-11-25 20-00-00.mp4");
        Assert.Equal(ProductionStatus.Queued, job.Status);
        Assert.False(job.Authorized);
        Assert.Equal(4, job.AudioTracks);
        Assert.Equal(h.Pipeline.Steps.Count, job.Steps.Count);
        using var db = h.Env.DbFactory.CreateDbContext();
        Assert.Single(db.ProductionJobs);
        Assert.Contains(db.Notifications, n => n.Kind == NotificationKind.ProductionAlert);
    }

    [Fact]
    public async Task Recording_still_open_by_obs_or_short_is_not_a_job()
    {
        using var h = new ProductionHarness();
        var recording = h.Record("2026-11-25 21-00-00.mp4");
        h.Record("2026-11-25 21-30-00.mp4");
        h.Env.Analyzer.Handler = path => VideoAnalysisResult.Ok(new VideoAnalysis(path.Contains("21-30") ? 40 : 1100, 1920, 1080, "hevc", "aac", 60, null, "mp4", 4));

        // O OBS grava com o arquivo aberto para escrita.
        using (new FileStream(recording, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            await h.ScanUntilStableAsync();
            Assert.Equal("Gravando", h.Scanner.LastScan.Single(r => r.FileName.StartsWith("2026-11-25 21-00")).State);
        }

        var shortOne = h.Scanner.LastScan.Single(r => r.FileName.StartsWith("2026-11-25 21-30"));
        Assert.Equal("Ignorado", shortOne.State);
        Assert.Contains("teste", shortOne.Detail);

        await h.Scanner.ScanAsync(default);
        using var db = h.Env.DbFactory.CreateDbContext();
        Assert.Single(db.ProductionJobs);
    }

    [Fact]
    public async Task Nothing_runs_without_authorization_and_the_original_is_never_touched()
    {
        using var h = new ProductionHarness();
        var original = h.Record("2026-11-25 22-00-00.mp4", "bytes originais");
        await h.ScanUntilStableAsync();

        Assert.False(await h.Runner.RunCycleAsync(default));
        Assert.Null(h.Job("2026-11-25 22-00-00.mp4").WorkFolder);

        var id = h.Job("2026-11-25 22-00-00.mp4").Id;
        Assert.Equal(CommandResult.Done, (await h.Commands.AuthorizeAsync(id, default)).Result);
        Assert.True(await h.Runner.RunCycleAsync(default));
        Assert.False(await h.Runner.RunCycleAsync(default)); // próxima etapa ainda não implementada

        var job = h.Job("2026-11-25 22-00-00.mp4");
        Assert.Equal(ProductionStepState.Done, job.Steps.Single(s => s.Name == IntegrityStep.StepName).State);
        Assert.Contains("fase 3", job.CurrentStep);
        Assert.True(File.Exists(Path.Combine(job.WorkFolder!, "job.json")));
        Assert.All(ProductionFolders.All, sub => Assert.True(Directory.Exists(Path.Combine(job.WorkFolder!, sub))));
        Assert.StartsWith(h.Options.ProductionWorkFolder, job.WorkFolder);

        // O original continua no mesmo lugar, com o mesmo conteúdo.
        Assert.Equal("bytes originais", File.ReadAllText(original));
        Assert.Single(Directory.GetFiles(h.Recordings));
    }

    [Fact]
    public async Task Low_disk_space_waits_with_the_reason_and_continues_when_there_is_space()
    {
        using var h = new ProductionHarness();
        h.Record("2026-11-26 10-00-00.mp4");
        await h.ScanUntilStableAsync();
        var id = h.Job("2026-11-26 10-00-00.mp4").Id;
        await h.Commands.AuthorizeAsync(id, default);

        h.Disk.Free = 52 * ProductionSpace.Gb; // reserva de 50 GB + ~5 GB do job não cabem
        Assert.False(await h.Runner.RunCycleAsync(default));
        var job = h.Job("2026-11-26 10-00-00.mp4");
        Assert.Contains("Espaço insuficiente", job.BlockedReason);
        Assert.Null(job.WorkFolder);
        Assert.Equal(ProductionStepState.Pending, job.Steps.Single(s => s.Name == IntegrityStep.StepName).State);

        h.Disk.Free = 300 * ProductionSpace.Gb;
        Assert.True(await h.Runner.RunCycleAsync(default));
        job = h.Job("2026-11-26 10-00-00.mp4");
        Assert.Null(job.BlockedReason);
        Assert.NotNull(job.WorkFolder);
    }

    [Fact]
    public async Task Emergency_stop_interrupts_the_step_keeps_files_and_resumes_after_release()
    {
        var blocking = new BlockingStep("analise-midia");
        using var h = new ProductionHarness(blocking);
        h.Record("2026-11-26 11-00-00.mp4");
        await h.ScanUntilStableAsync();
        var id = h.Job("2026-11-26 11-00-00.mp4").Id;
        await h.Commands.AuthorizeAsync(id, default);
        Assert.True(await h.Runner.RunCycleAsync(default)); // integridade

        var cycle = h.Runner.RunCycleAsync(default);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(id, h.Runner.RunningJobId);
        await h.Commands.EmergencyStopAsync("teste", default);
        Assert.True(await cycle.WaitAsync(TimeSpan.FromSeconds(10)));

        var job = h.Job("2026-11-26 11-00-00.mp4");
        var step = job.Steps.Single(s => s.Name == "analise-midia");
        Assert.Equal(ProductionStepState.Pending, step.State);
        Assert.Equal(ProductionStepState.Done, job.Steps.Single(s => s.Name == IntegrityStep.StepName).State);
        Assert.True(File.Exists(Path.Combine(job.WorkFolder!, "job.json")));
        Assert.Contains(h.Logs(id), l => l.Message.Contains("parada de emergência", StringComparison.OrdinalIgnoreCase));

        // Parado: nenhuma etapa começa.
        blocking.Reset();
        Assert.False(await h.Runner.RunCycleAsync(default));
        Assert.Equal(1, blocking.Runs);

        await h.Commands.ReleaseStopAsync(default);
        blocking.Release.TrySetResult();
        Assert.True(await h.Runner.RunCycleAsync(default));
        Assert.Equal(ProductionStepState.Done, h.Job("2026-11-26 11-00-00.mp4").Steps.Single(s => s.Name == "analise-midia").State);
        Assert.Equal(2, blocking.Runs);
    }

    [Fact]
    public async Task Pause_and_cancel_stop_the_job_and_restart_recovers_interrupted_steps()
    {
        var blocking = new BlockingStep("analise-midia");
        using var h = new ProductionHarness(blocking);
        h.Record("2026-11-26 12-00-00.mp4");
        await h.ScanUntilStableAsync();
        var id = h.Job("2026-11-26 12-00-00.mp4").Id;
        await h.Commands.AuthorizeAsync(id, default);
        await h.Runner.RunCycleAsync(default);

        var cycle = h.Runner.RunCycleAsync(default);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await h.Commands.PauseAsync(id, default);
        await cycle.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(h.Job("2026-11-26 12-00-00.mp4").Paused);
        Assert.False(await h.Runner.RunCycleAsync(default));

        // Reinício no meio de uma etapa: ela volta a pendente; as concluídas continuam concluídas.
        using (var db = h.Env.DbFactory.CreateDbContext())
        {
            var step = db.ProductionSteps.Single(s => s.JobId == id && s.Name == "analise-midia");
            step.State = ProductionStepState.Running;
            db.SaveChanges();
        }

        var restarted = new ProductionRunner(h.Env.DbFactory, h.Pipeline, h.Options, h.Disk, h.Env.Time, NullLogger<ProductionRunner>.Instance);
        await restarted.RecoverAsync(default);
        var job = h.Job("2026-11-26 12-00-00.mp4");
        Assert.Equal(ProductionStepState.Pending, job.Steps.Single(s => s.Name == "analise-midia").State);
        Assert.Equal(ProductionStepState.Done, job.Steps.Single(s => s.Name == IntegrityStep.StepName).State);

        Assert.Equal(CommandResult.Done, (await h.Commands.CancelAsync(id, default)).Result);
        await h.Commands.ResumeAsync(id, default);
        Assert.Equal(ProductionStatus.Cancelled, h.Job("2026-11-26 12-00-00.mp4").Status);
        Assert.False(await h.Runner.RunCycleAsync(default));
        Assert.Single(Directory.GetFiles(h.Recordings));
    }

    [Fact]
    public async Task Original_changed_after_receiving_fails_without_retrying()
    {
        using var h = new ProductionHarness();
        var original = h.Record("2026-11-26 13-00-00.mp4");
        await h.ScanUntilStableAsync();
        var id = h.Job("2026-11-26 13-00-00.mp4").Id;
        await h.Commands.AuthorizeAsync(id, default);

        File.AppendAllText(original, "mais bytes");
        await h.Runner.RunCycleAsync(default);

        var job = h.Job("2026-11-26 13-00-00.mp4");
        Assert.Equal(ProductionStatus.Failed, job.Status);
        Assert.Contains("mudou", job.LastError);
        Assert.Equal(1, job.Steps.Single(s => s.Name == IntegrityStep.StepName).Attempts);
    }

    [Fact]
    public void Panel_only_serves_deliverables_inside_the_job_folder()
    {
        using var env = new TestEnvironment();
        var work = Path.Combine(env.Root, "trabalho", "job");
        foreach (var sub in ProductionFolders.All) Directory.CreateDirectory(Path.Combine(work, sub));
        File.WriteAllText(Path.Combine(work, ProductionFolders.Vertical, "corte.mp4"), "v");
        Directory.CreateDirectory(Path.Combine(work, ProductionFolders.Thumbnails, "anterior"));
        File.WriteAllText(Path.Combine(work, ProductionFolders.Thumbnails, "anterior", "v0.png"), "p");
        Directory.CreateDirectory(Path.Combine(work, ProductionFolders.Thumbnails, "trabalho"));
        File.WriteAllText(Path.Combine(work, ProductionFolders.Thumbnails, "trabalho", "recorte.png"), "p");
        File.WriteAllText(Path.Combine(work, ProductionFolders.Analysis, "mic.wav"), "a");
        File.WriteAllText(Path.Combine(work, ProductionFolders.Vertical, "render.part.mp4"), "parcial");
        File.WriteAllText(Path.Combine(env.Root, "segredo.json"), "{}");

        var listed = ProductionFiles.Deliverables(work).Select(f => Path.GetRelativePath(work, f.FullName).Replace('\\', '/')).ToList();
        Assert.Equal(["3 - verticais/corte.mp4", "4 - thumbnails/anterior/v0.png"], listed);

        Assert.NotNull(ProductionFiles.Resolve(work, "3 - verticais/corte.mp4"));
        Assert.Null(ProductionFiles.Resolve(work, "../../segredo.json"));
        Assert.Null(ProductionFiles.Resolve(work, "1 - analise/mic.wav"));
        Assert.Null(ProductionFiles.Resolve(work, Path.Combine(env.Root, "segredo.json")));
        Assert.Null(ProductionFiles.Resolve(work, "3 - verticais/../../../segredo.json"));
    }

    [Fact]
    public async Task Auto_publish_of_approved_content_starts_off_and_can_be_turned_on()
    {
        using var h = new EngineHarness();
        var settings = await h.Scheduling.GetSettingsAsync(default);
        Assert.False(settings.AutoPublishApproved);

        Assert.Empty(await h.Commands.UpdateSettingsAsync(new ScheduleSettingsInput("10:00", "23:59", 240, 10, Enum.GetValues<DayOfWeek>(),
            "America/Sao_Paulo", [Platform.YouTube], MissedSlotPolicy.NextAllowedSlot, 30, 5, AutoPublishApproved: true), default));
        Assert.True((await h.Scheduling.GetSettingsAsync(default)).AutoPublishApproved);

        // Quem não manda o campo (scripts antigos) mantém o que está salvo.
        Assert.Empty(await h.Commands.UpdateSettingsAsync(new ScheduleSettingsInput("10:00", "23:59", 240, 10, Enum.GetValues<DayOfWeek>(),
            "America/Sao_Paulo", [Platform.YouTube], MissedSlotPolicy.NextAllowedSlot, 30, 5), default));
        Assert.True((await h.Scheduling.GetSettingsAsync(default)).AutoPublishApproved);
    }
}
