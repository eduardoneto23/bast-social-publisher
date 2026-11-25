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
public sealed class ProductionCommands(IDbContextFactory<BastDbContext> factory, ProductionRunner runner, TimeProvider time)
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

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
