using Bast.Application.Persistence;
using Bast.Domain;
using Microsoft.EntityFrameworkCore;

namespace Bast.Application.Publishing;

public sealed record ScheduleSettingsInput(
    string StartTime,
    string EndTime,
    int IntervalMinutes,
    int MaxPerDay,
    DayOfWeek[] Days,
    string TimeZoneId,
    Platform[] Platforms,
    MissedSlotPolicy MissedPolicy,
    int MissedGraceMinutes,
    int MaxAttempts);

/// <summary>Controles do usuário sobre a fila: configurar, pausar, retomar e cancelar.</summary>
public sealed class QueueCommands(IDbContextFactory<BastDbContext> factory, SchedulingService scheduling, TimeProvider time)
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <returns>Problemas da configuração; vazio quando foi salva.</returns>
    public async Task<IReadOnlyList<string>> UpdateSettingsAsync(ScheduleSettingsInput input, CancellationToken ct)
    {
        if (!TimeOnly.TryParse(input.StartTime, out var start) || !TimeOnly.TryParse(input.EndTime, out var end))
        {
            return ["Horários devem estar no formato HH:mm."];
        }

        await scheduling.GetSettingsAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var settings = await db.ScheduleSettings.FirstAsync(s => s.Id == ScheduleSettings.SingletonId, ct);
        settings.StartTime = start;
        settings.EndTime = end;
        settings.IntervalMinutes = input.IntervalMinutes;
        settings.MaxPerDay = input.MaxPerDay;
        settings.DaysMask = ScheduleSettings.MaskOf(input.Days ?? []);
        settings.TimeZoneId = input.TimeZoneId ?? "";
        settings.PlatformsMask = ScheduleSettings.MaskOf(input.Platforms ?? []);
        settings.MissedPolicy = input.MissedPolicy;
        settings.MissedGraceMinutes = input.MissedGraceMinutes;
        settings.MaxAttempts = input.MaxAttempts;
        settings.UpdatedAtUtc = Now;

        var problems = settings.Validate();
        if (problems.Count > 0) return problems;

        // Os vídeos já agendados mantêm seus horários; as novas regras valem para os próximos.
        await db.SaveChangesAsync(ct);
        return [];
    }

    public async Task SetPausedAsync(bool paused, CancellationToken ct)
    {
        await scheduling.GetSettingsAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var settings = await db.ScheduleSettings.FirstAsync(s => s.Id == ScheduleSettings.SingletonId, ct);
        settings.Paused = paused;
        settings.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Cancela as publicações do vídeo que ainda não começaram. Envios em andamento ou de resultado
    /// incerto não são cancelados, porque a plataforma pode já ter recebido o conteúdo.
    /// </summary>
    /// <returns>Nulo se o vídeo não existe; senão, quantas foram canceladas e quantas não puderam ser.</returns>
    public async Task<(int Cancelled, int NotCancellable)?> CancelVideoAsync(Guid videoId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var video = await db.Videos.Include(v => v.Publications).FirstOrDefaultAsync(v => v.Id == videoId, ct);
        if (video is null) return null;

        var now = Now;
        int cancelled = 0, blocked = 0;
        foreach (var publication in video.Publications.Where(p => !PublicationStateMachine.IsTerminal(p.State)))
        {
            if (PublicationStateMachine.CanTransition(publication.State, PublicationState.Cancelled))
            {
                publication.TransitionTo(PublicationState.Cancelled, now);
                cancelled++;
            }
            else
            {
                blocked++;
            }
        }

        await db.SaveChangesAsync(ct);
        return (cancelled, blocked);
    }
}
