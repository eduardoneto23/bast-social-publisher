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
    int MaxAttempts,
    ScheduleMode? Mode = null,
    int? ManualMaxPerDay = null);

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
        // Opcionais: quem não informa (scripts antigos) mantém o que está salvo.
        if (input.Mode is { } mode && Enum.IsDefined(mode)) settings.Mode = mode;
        if (input.ManualMaxPerDay is { } manualMax) settings.ManualMaxPerDay = manualMax;
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
            if (publication.State != PublicationState.Processing && PublicationStateMachine.CanTransition(publication.State, PublicationState.Cancelled))
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

    /// <summary>
    /// Antecipa para agora as publicações do vídeo que ainda aguardam o horário. Quem envia continua
    /// sendo o motor, no próximo ciclo, com as mesmas regras de sempre (pausa, tentativas, sem duplicar).
    /// </summary>
    /// <returns>Nulo se o vídeo não existe; senão, quantas publicações foram antecipadas.</returns>
    public async Task<int?> PublishNowAsync(Guid videoId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var video = await db.Videos.Include(v => v.Publications).FirstOrDefaultAsync(v => v.Id == videoId, ct);
        if (video is null) return null;
        if (video.State != VideoState.Ready) return 0;

        // Modo manual: vídeo ainda sem horário nem publicações recebe as publicações agora.
        if (video.Publications.Count == 0)
        {
            await db.DisposeAsync();
            return await scheduling.ScheduleNowAsync(videoId, ct);
        }

        var now = Now;
        var waiting = video.Publications.Where(p => p.State == PublicationState.Scheduled).ToList();
        if (waiting.Count == 0) return 0;

        foreach (var publication in waiting)
        {
            publication.ScheduledAtUtc = now;
            publication.UpdatedAtUtc = now;
        }

        video.ScheduledAtUtc = now;
        video.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);
        return waiting.Count;
    }
}
