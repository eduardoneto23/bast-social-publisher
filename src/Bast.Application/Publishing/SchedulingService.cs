using Bast.Application.Persistence;
using Bast.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Bast.Application.Publishing;

/// <summary>
/// Reserva horários para os vídeos validados e cria uma publicação por plataforma.
/// Os horários ficam gravados no banco: reiniciar o serviço não recalcula nem duplica nada.
/// </summary>
public sealed class SchedulingService(
    IDbContextFactory<BastDbContext> factory,
    IConnectorRegistry registry,
    TimeProvider time,
    ILogger<SchedulingService> logger)
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<ScheduleSettings> GetSettingsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var settings = await db.ScheduleSettings.FirstOrDefaultAsync(s => s.Id == ScheduleSettings.SingletonId, ct);
        if (settings is null)
        {
            settings = new ScheduleSettings { UpdatedAtUtc = Now };
            db.ScheduleSettings.Add(settings);
            await db.SaveChangesAsync(ct);
        }

        return settings;
    }

    /// <summary>Plataformas habilitadas nas configurações e com conector disponível.</summary>
    public IReadOnlyList<Platform> ActivePlatforms(ScheduleSettings settings) =>
        settings.Platforms.Where(p => registry.Available.Contains(p)).ToList();

    /// <summary>Agenda, em ordem numérica, os vídeos validados que ainda não têm horário.</summary>
    public async Task<int> ScheduleNewAsync(CancellationToken ct)
    {
        var settings = await GetSettingsAsync(ct);
        var platforms = ActivePlatforms(settings);
        if (platforms.Count == 0) return 0;

        await using var db = await factory.CreateDbContextAsync(ct);
        var waiting = (await db.Videos
                .Where(v => v.State == VideoState.Ready && v.ScheduledAtUtc == null && !v.Publications.Any())
                .ToListAsync(ct))
            .OrderBy(v => v.FolderName, NaturalStringComparer.Instance)
            .ThenBy(v => v.ImportedAtUtc)
            .ToList();
        if (waiting.Count == 0) return 0;

        var now = Now;
        var lastReserved = await db.Videos
            .Where(v => v.State == VideoState.Ready && v.ScheduledAtUtc != null)
            .MaxAsync(v => v.ScheduledAtUtc, ct);
        var cursor = lastReserved is { } last && last > now ? last : now;
        var scheduled = 0;

        foreach (var video in waiting)
        {
            var slot = SlotCalculator.NextSlotUtc(settings, cursor);
            if (slot is null)
            {
                logger.LogWarning("Nenhum horário disponível com a configuração atual; vídeos aguardando.");
                break;
            }

            var facts = new VideoFacts(video.DurationSeconds, video.FileSizeBytes);
            var anyScheduled = false;
            foreach (var platform in platforms)
            {
                var publication = new Publication
                {
                    Id = Guid.NewGuid(),
                    VideoId = video.Id,
                    Platform = platform,
                    IdempotencyKey = $"{video.Id:N}-{platform}",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };
                publication.TransitionTo(PublicationState.Validating, now);

                var problems = CaptionRules.Validate(platform, video.CaptionText, facts);
                if (problems.Count > 0)
                {
                    publication.LastError = "Incompatível com a plataforma: " + string.Join(" ", problems);
                    publication.TransitionTo(PublicationState.Failed, now);
                    logger.LogWarning("{Folder} não será publicado em {Platform}: {Problems}", video.FolderName, platform, publication.LastError);
                }
                else
                {
                    publication.ScheduledAtUtc = slot;
                    publication.TransitionTo(PublicationState.Scheduled, now);
                    anyScheduled = true;
                }

                db.Publications.Add(publication);
            }

            if (anyScheduled)
            {
                video.ScheduledAtUtc = slot;
                video.UpdatedAtUtc = now;
                cursor = slot.Value;
                scheduled++;
                logger.LogInformation("{Folder} agendado para {Slot:yyyy-MM-dd HH:mm} UTC.", video.FolderName, slot);
            }

            // Uma transação por vídeo: horário e publicações são gravados juntos.
            await db.SaveChangesAsync(ct);
        }

        return scheduled;
    }

    /// <summary>
    /// Trata horários perdidos (computador desligado, serviço parado, fila pausada): os vídeos que
    /// ainda não começaram a publicar são redistribuídos, na mesma ordem, pelos próximos horários.
    /// Nunca publica o acumulado de uma vez.
    /// </summary>
    public async Task<int> ReflowMissedAsync(CancellationToken ct)
    {
        var settings = await GetSettingsAsync(ct);
        var now = Now;
        var cutoff = now.AddMinutes(-settings.MissedGraceMinutes);

        await using var db = await factory.CreateDbContextAsync(ct);
        var queue = (await db.Videos
                .Include(v => v.Publications)
                .Where(v => v.State == VideoState.Ready && v.ScheduledAtUtc != null
                            && v.Publications.Any(p => p.State == PublicationState.Scheduled))
                .ToListAsync(ct))
            .OrderBy(v => v.ScheduledAtUtc)
            .ThenBy(v => v.FolderName, NaturalStringComparer.Instance)
            .ToList();

        // Um único vídeo atrasado dentro da tolerância mantém o horário e publica agora.
        // Qualquer outro vídeo com horário vencido é redistribuído: dois vídeos nunca saem juntos.
        var overdue = queue.Where(v => v.ScheduledAtUtc <= now).ToList();
        var keep = overdue.Count > 0 && overdue[0].ScheduledAtUtc >= cutoff ? overdue[0] : null;
        if (overdue.Count == (keep is null ? 0 : 1)) return 0;

        queue.Remove(keep!);
        var cursor = now;
        var first = true;
        foreach (var video in queue)
        {
            var slot = SlotCalculator.NextSlotUtc(settings, cursor, skipToNextDay: first && settings.MissedPolicy == MissedSlotPolicy.NextCycle);
            if (slot is null) return 0;

            first = false;
            cursor = slot.Value;
            video.ScheduledAtUtc = slot;
            video.UpdatedAtUtc = now;
            foreach (var publication in video.Publications.Where(p => p.State == PublicationState.Scheduled))
            {
                publication.ScheduledAtUtc = slot;
                publication.UpdatedAtUtc = now;
            }
        }

        await db.SaveChangesAsync(ct);
        logger.LogWarning("Horário perdido detectado: {Count} vídeo(s) redistribuído(s) a partir de {Slot:yyyy-MM-dd HH:mm} UTC.",
            queue.Count, queue[0].ScheduledAtUtc);
        return queue.Count;
    }
}
