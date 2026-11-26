using Bast.Application.Persistence;
using Bast.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Bast.Application.Production;

/// <summary>
/// Executa os jobs autorizados, uma etapa por vez e um job por vez. Regras:
/// sem autorização do usuário nada roda; a parada de emergência impede etapas novas e interrompe a atual;
/// etapa concluída nunca é refeita numa retomada; uma etapa interrompida volta a "pendente" e recomeça do zero
/// (o resultado parcial nunca vira entregável); falta de espaço no disco de trabalho só faz o job esperar.
/// </summary>
public sealed class ProductionRunner(
    IDbContextFactory<BastDbContext> factory,
    ProductionPipeline pipeline,
    BastOptions options,
    IDiskSpace disk,
    TimeProvider time,
    ILogger<ProductionRunner> logger)
{
    public const int MaxStepAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(5);

    /// <summary>Grava o progresso e o registro acumulado de uma etapa em andamento.</summary>
    private async Task FlushAsync(Guid jobId, string stepName, int progress,
        System.Collections.Concurrent.ConcurrentQueue<(ProductionLogLevel Level, string Message, string? Program, string? File, DateTime At)> logs)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(CancellationToken.None);
            var job = await db.ProductionJobs.FirstOrDefaultAsync(j => j.Id == jobId, CancellationToken.None);
            if (job is null) return;
            job.Progress = progress;
            job.UpdatedAtUtc = Now;
            while (logs.TryDequeue(out var entry)) ProductionJournal.Add(db, jobId, entry.At, entry.Level, stepName, entry.Message, entry.Program, entry.File);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            logger.LogWarning("Produção: não foi possível gravar o progresso ({Type}); tenta no próximo intervalo.", ex.GetType().Name);
        }
    }

    private readonly object _gate = new();
    private CancellationTokenSource? _running;
    private Guid? _runningJob;
    private string? _interruptReason;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>Job com etapa em execução agora (para o painel).</summary>
    public Guid? RunningJobId { get { lock (_gate) return _runningJob; } }

    /// <summary>Interrompe a etapa em andamento (de um job, ou de qualquer um). Ela volta a pendente.</summary>
    public void Interrupt(Guid? jobId, string reason)
    {
        lock (_gate)
        {
            if (_running is null || (jobId is not null && _runningJob != jobId)) return;
            _interruptReason = reason;
            _running.Cancel();
        }
    }

    /// <summary>Depois de um reinício: etapas que estavam rodando voltam a pendente para recomeçar do zero.</summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var running = await db.ProductionSteps.Where(s => s.State == ProductionStepState.Running).ToListAsync(ct);
        var now = Now;
        foreach (var step in running)
        {
            step.State = ProductionStepState.Pending;
            step.Error = "Interrompida por reinício do serviço ou do computador; recomeça do zero.";
            ProductionJournal.Add(db, step.JobId, now, ProductionLogLevel.Warning, step.Name,
                "Etapa interrompida por reinício: o resultado parcial foi descartado e ela recomeça do zero. As etapas já concluídas são mantidas.");
        }

        if (running.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Produção: {Count} etapa(s) interrompida(s) por reinício voltam para a fila.", running.Count);
        }
    }

    public static readonly ProductionStatus[] Idle =
    [
        ProductionStatus.Published, ProductionStatus.Cancelled, ProductionStatus.Failed, ProductionStatus.NeedsHumanReview,
        ProductionStatus.WaitingApproval, ProductionStatus.Approved, ProductionStatus.ReadyToPublish, ProductionStatus.Publishing,
    ];

    /// <returns>Verdadeiro quando uma etapa rodou neste ciclo.</returns>
    public async Task<bool> RunCycleAsync(CancellationToken ct)
    {
        ProductionJob job;
        ProductionStep step;
        IProductionStep implementation;
        string workFolder;

        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var control = await db.ProductionControl.FirstOrDefaultAsync(c => c.Id == 1, ct);
            if (control?.Stopped == true) return false;

            var candidates = await db.ProductionJobs.Include(j => j.Steps)
                .Where(j => j.Authorized && !j.Paused && !Idle.Contains(j.Status))
                .OrderBy(j => j.AuthorizedAtUtc).ThenBy(j => j.ReceivedAtUtc)
                .ToListAsync(ct);
            var now = Now;

            ProductionJob? chosen = null;
            ProductionStep? next = null;
            IProductionStep? impl = null;
            foreach (var candidate in candidates)
            {
                if (pipeline.EnsureSteps(db, candidate)) await db.SaveChangesAsync(ct);

                var pending = candidate.Steps.OrderBy(s => s.Order)
                    .FirstOrDefault(s => s.State is not (ProductionStepState.Done or ProductionStepState.Skipped));
                if (pending is null) continue;

                var definition = pipeline.Steps.First(s => s.Name == pending.Name);
                if (definition.Step is null)
                {
                    // Etapa prevista e ainda não implementada: o job espera aqui, sem simular nada.
                    var waiting = $"Aguardando: {definition.Title} (chega na fase {definition.Phase} da implementação)";
                    if (candidate.CurrentStep != waiting)
                    {
                        candidate.CurrentStep = waiting;
                        candidate.Status = definition.Status;
                        candidate.Progress = 0;
                        candidate.UpdatedAtUtc = now;
                        ProductionJournal.Add(db, candidate.Id, now, ProductionLogLevel.Info, pending.Name, waiting);
                        await db.SaveChangesAsync(ct);
                    }

                    continue;
                }

                if (pending.State == ProductionStepState.Pending && pending.Error is not null && pending.FinishedAtUtc is { } failedAt
                    && now - failedAt < RetryDelay)
                {
                    continue;
                }

                chosen = candidate;
                next = pending;
                impl = definition.Step;
                break;
            }

            if (chosen is null || next is null || impl is null) return false;

            var folder = await PrepareWorkFolderAsync(db, chosen, now, ct);
            if (folder is null) return false;

            next.State = ProductionStepState.Running;
            next.Attempts++;
            next.StartedAtUtc = now;
            next.FinishedAtUtc = null;
            chosen.Status = impl.Status;
            chosen.CurrentStep = impl.Title;
            chosen.Progress = 0;
            chosen.Attempts++;
            chosen.UpdatedAtUtc = now;
            ProductionJournal.Add(db, chosen.Id, now, ProductionLogLevel.Info, next.Name, $"Início: {impl.Title} (tentativa {next.Attempts}).");
            await db.SaveChangesAsync(ct);

            job = chosen;
            step = next;
            implementation = impl;
            workFolder = folder;
        }

        var logs = new System.Collections.Concurrent.ConcurrentQueue<(ProductionLogLevel Level, string Message, string? Program, string? File, DateTime At)>();
        var context = new StepContext(job, workFolder, (level, message, program, file) => logs.Enqueue((level, message, program, file, Now)));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate)
        {
            _running = cts;
            _runningJob = job.Id;
            _interruptReason = null;
        }

        string? output = null;
        Exception? failure = null;
        var interrupted = false;
        try
        {
            // Etapas longas: progresso e registro vão para o painel a cada poucos segundos, não só no fim.
            var work = implementation.RunAsync(context, cts.Token);
            while (await Task.WhenAny(work, Task.Delay(ProgressInterval, ct)) != work)
            {
                await FlushAsync(job.Id, step.Name, context.Progress, logs);
            }

            output = await work;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            interrupted = true;
        }
        catch (OperationCanceledException)
        {
            // Serviço parando: a etapa fica "rodando" e o RecoverAsync a devolve para a fila no próximo início.
            throw;
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            lock (_gate)
            {
                _running = null;
                _runningJob = null;
            }
        }

        await using (var db = await factory.CreateDbContextAsync(CancellationToken.None))
        {
            var saved = await db.ProductionJobs.Include(j => j.Steps).FirstAsync(j => j.Id == job.Id, CancellationToken.None);
            var savedStep = saved.Steps.First(s => s.Id == step.Id);
            var now = Now;
            while (logs.TryDequeue(out var entry)) ProductionJournal.Add(db, saved.Id, entry.At, entry.Level, step.Name, entry.Message, entry.Program, entry.File);

            savedStep.FinishedAtUtc = now;
            saved.UpdatedAtUtc = now;
            if (interrupted)
            {
                savedStep.State = ProductionStepState.Pending;
                savedStep.Attempts = Math.Max(0, savedStep.Attempts - 1);
                savedStep.Error = null;
                saved.Progress = 0;
                var reason = _interruptReason ?? "Interrompida.";
                saved.CurrentStep = $"{implementation.Title} interrompida: {reason}";
                ProductionJournal.Add(db, saved.Id, now, ProductionLogLevel.Warning, step.Name,
                    $"Etapa interrompida ({reason}). O resultado parcial foi descartado; ela recomeça do zero ao retomar.");
            }
            else if (failure is null)
            {
                savedStep.State = ProductionStepState.Done;
                savedStep.Output = output;
                savedStep.Error = null;
                saved.Progress = 100;
                saved.LastError = null;
                ProductionJournal.Add(db, saved.Id, now, ProductionLogLevel.Info, step.Name, $"Concluída: {output}");
            }
            else
            {
                var message = failure is ProductionStepException known ? known.Message : $"{failure.GetType().Name}: {failure.Message}";
                var retry = failure is not ProductionStepException { Retry: false } && savedStep.Attempts < MaxStepAttempts;
                savedStep.Error = message;
                saved.LastError = $"{implementation.Title}: {message}";
                if (retry)
                {
                    savedStep.State = ProductionStepState.Pending;
                    saved.CurrentStep = $"{implementation.Title}: falhou, nova tentativa em {RetryDelay.TotalMinutes:0} min";
                    ProductionJournal.Add(db, saved.Id, now, ProductionLogLevel.Warning, step.Name,
                        $"Falha na tentativa {savedStep.Attempts} de {MaxStepAttempts}: {message}");
                }
                else
                {
                    savedStep.State = ProductionStepState.Failed;
                    saved.Status = ProductionStatus.Failed;
                    saved.CurrentStep = $"{implementation.Title}: falhou";
                    saved.CompletedAtUtc = now;
                    ProductionJournal.Add(db, saved.Id, now, ProductionLogLevel.Error, step.Name, $"Falha definitiva: {message}");
                    await NotificationOutbox.AddAsync(db, NotificationKind.ProductionAlert, $"production:{saved.Id}:failed:{step.Name}",
                        $"Produção de {saved.SourceFileName} parou em \"{implementation.Title}\": {message}", now, CancellationToken.None);
                }

                logger.LogWarning("Produção: {Job} falhou em {Step}: {Error}", saved.SourceFileName, step.Name, message);
            }

            await db.SaveChangesAsync(CancellationToken.None);
        }

        return true;
    }

    /// <summary>
    /// Pasta de trabalho do job: criada só com espaço suficiente no disco de trabalho. Sem espaço, o job fica
    /// esperando (com o motivo no painel e um aviso), e nada é apagado para abrir espaço.
    /// </summary>
    private async Task<string?> PrepareWorkFolderAsync(BastDbContext db, ProductionJob job, DateTime now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.ProductionWorkFolder))
        {
            await BlockAsync(db, job, now, "O disco de trabalho da produção não está configurado (Bast:ProductionWorkFolder).", "config", ct);
            return null;
        }

        var free = disk.FreeBytes(options.ProductionWorkFolder);
        if (free is null)
        {
            await BlockAsync(db, job, now, $"O disco de trabalho {Path.GetPathRoot(options.ProductionWorkFolder)} não está disponível.", "disk", ct);
            return null;
        }

        var reserve = options.ProductionReserveGb * ProductionSpace.Gb;
        var needed = job.WorkFolder is null ? ProductionSpace.EstimateBytes(job.SizeBytes) : 0;
        if (free.Value - needed < reserve)
        {
            await BlockAsync(db, job, now,
                $"Espaço insuficiente em {Path.GetPathRoot(options.ProductionWorkFolder)}: {free.Value / (double)ProductionSpace.Gb:0.0} GB livres; " +
                $"o job precisa de ~{needed / (double)ProductionSpace.Gb:0.0} GB e {options.ProductionReserveGb} GB ficam sempre livres. " +
                "Libere espaço; o job continua sozinho.", "space", ct);
            return null;
        }

        if (job.BlockedReason is not null)
        {
            ProductionJournal.Add(db, job.Id, now, ProductionLogLevel.Info, "espaco", "Bloqueio resolvido; o job continua.");
            job.BlockedReason = null;
        }

        if (job.WorkFolder is null)
        {
            var name = $"{Path.GetFileNameWithoutExtension(job.SourceFileName)} - {job.Id.ToString("N")[..8]}";
            job.WorkFolder = Path.Combine(options.ProductionWorkFolder, Sanitize(name));
            ProductionJournal.Add(db, job.Id, now, ProductionLogLevel.Info, "espaco",
                $"Pasta de trabalho criada em {job.WorkFolder} ({free.Value / (double)ProductionSpace.Gb:0.0} GB livres).");
        }

        foreach (var sub in ProductionFolders.All) Directory.CreateDirectory(Path.Combine(job.WorkFolder, sub));
        return job.WorkFolder;
    }

    private static async Task BlockAsync(BastDbContext db, ProductionJob job, DateTime now, string reason, string kind, CancellationToken ct)
    {
        if (job.BlockedReason == reason) return;
        job.BlockedReason = reason;
        job.CurrentStep = "Bloqueado: " + reason;
        job.UpdatedAtUtc = now;
        ProductionJournal.Add(db, job.Id, now, ProductionLogLevel.Warning, "espaco", reason);
        await NotificationOutbox.AddAsync(db, NotificationKind.ProductionAlert, $"production:{job.Id}:blocked:{kind}:{now:yyyyMMddHH}",
            $"Produção de {job.SourceFileName} parada: {reason}", now, ct);
        await db.SaveChangesAsync(ct);
    }

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
}

/// <summary>Subpastas de cada job no disco de trabalho.</summary>
public static class ProductionFolders
{
    public const string Analysis = "1 - analise";
    public const string Horizontal = "2 - horizontal";
    public const string Vertical = "3 - verticais";
    public const string Thumbnails = "4 - thumbnails";
    public const string Projects = "5 - projetos";
    public const string Report = "6 - relatorio";
    public const string Temp = "temp";

    public static readonly string[] All = [Analysis, Horizontal, Vertical, Thumbnails, Projects, Report, Temp];
}
