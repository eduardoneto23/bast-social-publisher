using Bast.Application.Persistence;
using Bast.Domain;
using Microsoft.EntityFrameworkCore;

namespace Bast.Application.Production;

public enum CommandResult
{
    Done,
    NotFound,
    NotAllowed,
}

/// <summary>
/// Ações do usuário sobre a produção. A autorização vale só para o job indicado e só para as etapas do
/// pipeline dele: nunca para outros arquivos, outras tarefas ou publicação.
/// </summary>
public sealed class ProductionCommands(IDbContextFactory<BastDbContext> factory, ProductionRunner runner, ProductionPipeline pipeline, TimeProvider time)
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// O Claude entregou uma etapa externa (ex.: seleção dos momentos). O resultado é conferido: com problemas, a
    /// etapa continua esperando, com o motivo; com dúvida de segurança, o job para em revisão humana.
    /// </summary>
    public async Task<(CommandResult Result, string Message, IReadOnlyList<string> Problems)> CompleteExternalAsync(Guid jobId, string stepName, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = await db.ProductionJobs.Include(j => j.Steps).FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return (CommandResult.NotFound, "Job não encontrado.", []);
        var step = job.Steps.FirstOrDefault(s => s.Name == stepName);
        var definition = pipeline.Steps.FirstOrDefault(s => s.Name == stepName);
        if (step is null || definition.Step is not IExternalStep external) return (CommandResult.NotFound, "Etapa externa não encontrada.", []);
        if (step.State == ProductionStepState.Done) return (CommandResult.NotAllowed, "Esta etapa já foi entregue.", []);
        if (job.WorkFolder is null || job.IsFinished || job.Paused) return (CommandResult.NotAllowed, "O job não está esperando esta etapa.", []);
        var previous = job.Steps.Where(s => s.Order < step.Order).ToList();
        if (previous.Any(s => s.State is not (ProductionStepState.Done or ProductionStepState.Skipped)))
        {
            return (CommandResult.NotAllowed, "As etapas anteriores ainda não terminaram.", []);
        }

        var logs = new List<string>();
        var check = await external.CheckAsync(new StepContext(job, job.WorkFolder, (_, m, _, _) => logs.Add(m)), ct);
        var now = Now;
        step.FinishedAtUtc = now;
        step.Attempts++;
        if (check.Problems.Count > 0)
        {
            step.Error = string.Join(" ", check.Problems);
            job.CurrentStep = external.WaitingText + " Última entrega recusada: " + step.Error;
            ProductionJournal.Add(db, job.Id, now, ProductionLogLevel.Warning, stepName, "Entrega recusada: " + step.Error);
            await db.SaveChangesAsync(ct);
            return (CommandResult.NotAllowed, "Entrega recusada.", check.Problems);
        }

        step.State = ProductionStepState.Done;
        step.Error = null;
        step.Output = check.Summary;
        job.UpdatedAtUtc = now;
        job.LastError = null;
        ProductionJournal.Add(db, job.Id, now, ProductionLogLevel.Info, stepName, "Entregue pelo Claude e conferido: " + check.Summary);
        if (check.Review is not null)
        {
            job.Status = ProductionStatus.NeedsHumanReview;
            job.CurrentStep = "Revisão humana: " + check.Review;
            ProductionJournal.Add(db, job.Id, now, ProductionLogLevel.Warning, stepName, "Parado para revisão humana: " + check.Review);
            await NotificationOutbox.AddAsync(db, NotificationKind.ProductionAlert, $"production:{job.Id}:review:{stepName}",
                $"Produção de {job.SourceFileName} precisa da sua revisão: {check.Review}", now, ct);
        }
        else
        {
            job.CurrentStep = definition.Title + ": concluída";
        }

        await db.SaveChangesAsync(ct);
        return (CommandResult.Done, check.Review is null ? "Etapa entregue: " + check.Summary : "Entregue, mas parado para revisão humana.", []);
    }

    /// <summary>O usuário revisou o que parou o job (cena sensível, alerta) e libera a continuação.</summary>
    public async Task<(CommandResult Result, string Message)> ContinueAfterReviewAsync(Guid jobId, CancellationToken ct) =>
        await ChangeAsync(jobId, job =>
        {
            if (job.Status != ProductionStatus.NeedsHumanReview) return (CommandResult.NotAllowed, "O job não está em revisão humana.");
            job.Status = ProductionStatus.Analyzing;
            job.CurrentStep = "Revisado pelo usuário; continua no próximo ciclo";
            return (CommandResult.Done, "Revisão registrada: o job continua de onde parou.");
        }, "revisao", "Revisado e liberado pelo usuário.", ct);

    public async Task<(CommandResult Result, string Message)> AuthorizeAsync(Guid jobId, CancellationToken ct) =>
        await ChangeAsync(jobId, job =>
        {
            if (job.IsFinished) return (CommandResult.NotAllowed, "Este job já terminou.");
            if (job.Authorized) return (CommandResult.Done, "O job já estava autorizado.");
            job.Authorized = true;
            job.AuthorizedAtUtc = Now;
            job.CurrentStep = "Autorizado; começa no próximo ciclo";
            return (CommandResult.Done, "Job autorizado: as etapas de produção dele podem rodar, inclusive com você ausente.");
        }, "autorizacao", "Autorizado pelo usuário (vale só para as etapas deste job).", ct);

    public async Task<(CommandResult Result, string Message)> PauseAsync(Guid jobId, CancellationToken ct)
    {
        var result = await ChangeAsync(jobId, job =>
        {
            if (job.IsFinished) return (CommandResult.NotAllowed, "Este job já terminou.");
            job.Paused = true;
            job.PauseReason = "Pausado pelo usuário";
            return (CommandResult.Done, "Job pausado. A etapa em andamento é interrompida e recomeça ao retomar.");
        }, "pausa", "Pausado pelo usuário.", ct);
        if (result.Result == CommandResult.Done) runner.Interrupt(jobId, "job pausado");
        return result;
    }

    public async Task<(CommandResult Result, string Message)> ResumeAsync(Guid jobId, CancellationToken ct) =>
        await ChangeAsync(jobId, job =>
        {
            if (job.IsFinished) return (CommandResult.NotAllowed, "Este job já terminou.");
            if (!job.Paused) return (CommandResult.Done, "O job não estava pausado.");
            job.Paused = false;
            job.PauseReason = null;
            return (CommandResult.Done, "Job retomado: continua da etapa em que parou.");
        }, "pausa", "Retomado pelo usuário.", ct);

    public async Task<(CommandResult Result, string Message)> CancelAsync(Guid jobId, CancellationToken ct)
    {
        var result = await ChangeAsync(jobId, job =>
        {
            if (job.Status is ProductionStatus.Published or ProductionStatus.Publishing)
            {
                return (CommandResult.NotAllowed, "Já está publicando ou publicado; cancele na Fila.");
            }

            if (job.Status == ProductionStatus.Cancelled) return (CommandResult.Done, "O job já estava cancelado.");
            job.Status = ProductionStatus.Cancelled;
            job.CurrentStep = "Cancelado pelo usuário";
            job.CompletedAtUtc = Now;
            return (CommandResult.Done, "Job cancelado. O original e os arquivos já gerados ficam onde estão.");
        }, "cancelamento", "Cancelado pelo usuário. Nada foi apagado.", ct);
        if (result.Result == CommandResult.Done) runner.Interrupt(jobId, "job cancelado");
        return result;
    }

    /// <summary>Parada de emergência: nenhuma etapa nova começa e a atual é interrompida. Nada é apagado.</summary>
    public async Task<string> EmergencyStopAsync(string? reason, CancellationToken ct)
    {
        var now = Now;
        await using var db = await factory.CreateDbContextAsync(ct);
        var control = await GetControlAsync(db, now, ct);
        control.Stopped = true;
        control.StopReason = string.IsNullOrWhiteSpace(reason) ? "Parada de emergência pelo painel" : reason.Trim();
        control.StoppedAtUtc = now;
        control.UpdatedAtUtc = now;
        if (runner.RunningJobId is { } running)
        {
            ProductionJournal.Add(db, running, now, ProductionLogLevel.Warning, "parada", $"Parada de emergência: {control.StopReason}");
        }

        await db.SaveChangesAsync(ct);
        runner.Interrupt(null, "parada de emergência");
        return "Produção parada: nenhuma etapa nova começa e a que estava rodando foi interrompida. Os arquivos foram preservados.";
    }

    public async Task<string> ReleaseStopAsync(CancellationToken ct)
    {
        var now = Now;
        await using var db = await factory.CreateDbContextAsync(ct);
        var control = await GetControlAsync(db, now, ct);
        control.Stopped = false;
        control.StopReason = null;
        control.StoppedAtUtc = null;
        control.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);
        return "Produção liberada: os jobs autorizados continuam de onde pararam.";
    }

    public static async Task<ProductionControl> GetControlAsync(BastDbContext db, DateTime now, CancellationToken ct)
    {
        var control = await db.ProductionControl.FirstOrDefaultAsync(c => c.Id == 1, ct);
        if (control is null)
        {
            control = new ProductionControl { Id = 1, UpdatedAtUtc = now };
            db.ProductionControl.Add(control);
        }

        return control;
    }

    private async Task<(CommandResult Result, string Message)> ChangeAsync(Guid jobId, Func<ProductionJob, (CommandResult, string)> change,
        string step, string logMessage, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = await db.ProductionJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return (CommandResult.NotFound, "Job não encontrado.");

        var (result, message) = change(job);
        if (result == CommandResult.Done)
        {
            job.UpdatedAtUtc = Now;
            ProductionJournal.Add(db, job.Id, Now, ProductionLogLevel.Info, step, logMessage);
            await db.SaveChangesAsync(ct);
        }

        return (result, message);
    }
}
