using Bast.Application.Persistence;
using Bast.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Bast.Application.Publishing;

/// <summary>Resposta à escolha de horário feita pelo usuário.</summary>
public sealed record SetTimeResult(bool Ok, string Message, DateTime? ScheduledAtUtc = null, bool NotFound = false);

/// <summary>
/// Reserva horários para os vídeos validados e cria uma publicação por plataforma.
/// Os horários ficam gravados no banco: reiniciar o serviço não recalcula nem duplica nada.
///
/// Modo automático: cada vídeo novo recebe o próximo horário livre da agenda.
/// Modo manual: o vídeo fica "aguardando horário" (Ready, sem ScheduledAtUtc) até o usuário escolher
/// o horário no painel (<see cref="SetTimeAsync"/>).
/// </summary>
public sealed class SchedulingService(
    IDbContextFactory<BastDbContext> factory,
    IConnectorRegistry registry,
    TimeProvider time,
    ILogger<SchedulingService> logger)
{
    /// <summary>Dois vídeos precisam ficar pelo menos este tempo um do outro.</summary>
    public static readonly TimeSpan MinimumGap = TimeSpan.FromMinutes(1);

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

    /// <summary>
    /// Modo automático: agenda, em ordem numérica, os vídeos validados que ainda não têm horário.
    /// Modo manual: só avisa (uma vez por vídeo) que há vídeo aguardando horário.
    /// </summary>
    public async Task<int> ScheduleNewAsync(CancellationToken ct)
    {
        var settings = await GetSettingsAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var waiting = (await db.Videos
                .Include(v => v.Publications)
                .Where(v => v.State == VideoState.Ready && v.ScheduledAtUtc == null)
                .ToListAsync(ct))
            .Where(v => v.Publications.Count == 0 || v.Publications.Any(p => p.State == PublicationState.Scheduled))
            .OrderBy(v => v.FolderName, NaturalStringComparer.Instance)
            .ThenBy(v => v.ImportedAtUtc)
            .ToList();
        if (waiting.Count == 0) return 0;

        var now = Now;
        if (settings.Mode == ScheduleMode.Manual)
        {
            foreach (var video in waiting)
            {
                await NotificationOutbox.AddAsync(db, NotificationKind.TimeNeeded, $"video:{video.Id}:time-needed",
                    NotificationOutbox.TimeNeeded(video.FolderName), now, ct);
            }

            await db.SaveChangesAsync(ct);
            return 0;
        }

        var platforms = ActivePlatforms(settings);
        if (platforms.Count == 0) return 0;

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

            if (Assign(db, video, slot.Value, platforms, now))
            {
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
    /// Marca (ou muda) o horário de um vídeo, escolhido pelo usuário no fuso da agenda. Vale nos dois
    /// modos. Recusa horário no passado, dia que já atingiu o máximo, horário colado em outro vídeo e
    /// vídeo que já começou a publicar.
    /// </summary>
    public async Task<SetTimeResult> SetTimeAsync(Guid videoId, DateTime localTime, CancellationToken ct)
    {
        var settings = await GetSettingsAsync(ct);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        localTime = DateTime.SpecifyKind(new DateTime(localTime.Year, localTime.Month, localTime.Day, localTime.Hour, localTime.Minute, 0), DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(localTime)) return new(false, "Esse horário não existe nesse dia (mudança de horário de verão). Escolha outro.");

        var slot = TimeZoneInfo.ConvertTimeToUtc(localTime, zone);
        var now = Now;
        if (slot <= now) return new(false, "Escolha um horário no futuro.");

        await using var db = await factory.CreateDbContextAsync(ct);
        var video = await db.Videos.Include(v => v.Publications).FirstOrDefaultAsync(v => v.Id == videoId, ct);
        if (video is null) return new(false, "Vídeo não encontrado.", NotFound: true);
        if (video.State == VideoState.Publishing) return new(false, "Este vídeo já começou a ser publicado; o horário não pode mais mudar.");
        if (video.State != VideoState.Ready) return new(false, "Este vídeo não está mais na fila.");

        var started = video.Publications.Where(p => p.State is not (PublicationState.Scheduled or PublicationState.Failed or PublicationState.Cancelled)).ToList();
        if (started.Count > 0) return new(false, "Este vídeo já começou a ser publicado; o horário não pode mais mudar.");
        if (video.Publications.Count > 0 && video.Publications.All(p => p.State != PublicationState.Scheduled))
        {
            return new(false, "Este vídeo não tem nenhuma rede para publicar.");
        }

        // Outros vídeos do mesmo dia (no fuso da agenda), contando os já publicados naquele dia.
        var day = DateOnly.FromDateTime(localTime);
        var dayStart = TimeZoneInfo.ConvertTimeToUtc(SafeLocal(day.ToDateTime(TimeOnly.MinValue), zone), zone);
        var dayEnd = TimeZoneInfo.ConvertTimeToUtc(SafeLocal(day.AddDays(1).ToDateTime(TimeOnly.MinValue), zone), zone);
        var others = await db.Videos
            .Where(v => v.Id != video.Id && v.ScheduledAtUtc != null
                        && (v.State == VideoState.Ready || v.State == VideoState.Publishing || v.State == VideoState.Published))
            .Select(v => new { v.FolderName, ScheduledAtUtc = v.ScheduledAtUtc!.Value })
            .ToListAsync(ct);

        var limit = settings.ChosenTimeMaxPerDay;
        var sameDay = others.Count(o => o.ScheduledAtUtc >= dayStart && o.ScheduledAtUtc < dayEnd);
        if (sameDay >= limit)
        {
            return new(false, $"O dia {localTime:dd/MM} já tem {sameDay} vídeo(s), e o máximo é {limit} por dia. Escolha outro dia ou mude o máximo na Agenda.");
        }

        var clash = others.FirstOrDefault(o => (o.ScheduledAtUtc - slot).Duration() < MinimumGap);
        if (clash is not null) return new(false, $"\"{clash.FolderName}\" já está marcado para esse horário. Dois vídeos nunca saem juntos; escolha outro minuto.");

        if (video.Publications.Count == 0)
        {
            var platforms = ActivePlatforms(settings);
            if (platforms.Count == 0) return new(false, "Nenhuma rede conectada e marcada na Agenda.");
            if (!Assign(db, video, slot, platforms, now)) return new(false, "O vídeo não é compatível com nenhuma das redes marcadas; veja o motivo em Problemas.");
        }
        else
        {
            foreach (var publication in video.Publications.Where(p => p.State == PublicationState.Scheduled))
            {
                publication.ScheduledAtUtc = slot;
                publication.UpdatedAtUtc = now;
            }

            video.ScheduledAtUtc = slot;
            video.UpdatedAtUtc = now;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("{Folder}: horário escolhido para {Slot:yyyy-MM-dd HH:mm} UTC.", video.FolderName, slot);
        return new(true, $"Agendado para {localTime:dd/MM 'às' HH:mm}.", slot);
    }

    /// <summary>"Publicar agora" de um vídeo que ainda aguarda horário: cria as publicações para já.</summary>
    /// <returns>Quantas publicações ficaram agendadas.</returns>
    public async Task<int> ScheduleNowAsync(Guid videoId, CancellationToken ct)
    {
        var settings = await GetSettingsAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var video = await db.Videos.Include(v => v.Publications).FirstOrDefaultAsync(v => v.Id == videoId, ct);
        if (video is null || video.State != VideoState.Ready || video.Publications.Count > 0) return 0;

        var platforms = ActivePlatforms(settings);
        if (platforms.Count == 0) return 0;

        var now = Now;
        if (!Assign(db, video, now, platforms, now)) { await db.SaveChangesAsync(ct); return 0; }
        await db.SaveChangesAsync(ct);
        return video.Publications.Count(p => p.State == PublicationState.Scheduled);
    }

    /// <summary>
    /// Trata horários perdidos (computador desligado, serviço parado, fila pausada).
    /// Modo automático: os vídeos que ainda não começaram são redistribuídos, na mesma ordem, pelos
    /// próximos horários. Modo manual: o horário escolhido é desfeito e o vídeo volta a aguardar
    /// horário, com aviso. Nos dois casos nunca publica o acumulado de uma vez.
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

        if (settings.Mode == ScheduleMode.Manual)
        {
            var missed = overdue.Where(v => v != keep).ToList();
            foreach (var video in missed)
            {
                var original = video.ScheduledAtUtc!.Value;
                video.ScheduledAtUtc = null;
                video.UpdatedAtUtc = now;
                foreach (var publication in video.Publications.Where(p => p.State == PublicationState.Scheduled))
                {
                    publication.ScheduledAtUtc = null;
                    publication.UpdatedAtUtc = now;
                }

                await NotificationOutbox.AddAsync(db, NotificationKind.TimeNeeded, $"video:{video.Id}:missed:{original:yyyyMMddHHmm}",
                    NotificationOutbox.ChosenTimeMissed(video.FolderName), now, ct);
            }

            await db.SaveChangesAsync(ct);
            logger.LogWarning("Horário perdido no modo manual: {Count} vídeo(s) voltaram a aguardar horário.", missed.Count);
            return missed.Count;
        }

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

    /// <summary>
    /// Dá o horário ao vídeo: cria as publicações (validando a legenda de cada rede) ou, se já
    /// existem, move as que aguardam. Devolve falso quando nenhuma rede pode receber o vídeo.
    /// </summary>
    private bool Assign(BastDbContext db, Video video, DateTime slot, IReadOnlyList<Platform> platforms, DateTime now)
    {
        var anyScheduled = false;
        if (video.Publications.Count > 0)
        {
            foreach (var publication in video.Publications.Where(p => p.State == PublicationState.Scheduled))
            {
                publication.ScheduledAtUtc = slot;
                publication.UpdatedAtUtc = now;
                anyScheduled = true;
            }
        }
        else
        {
            var facts = new VideoFacts(video.DurationSeconds, video.FileSizeBytes);
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

                // O vídeo está sendo acompanhado pelo contexto: a publicação entra na lista dele sozinha.
                db.Publications.Add(publication);
            }
        }

        if (anyScheduled)
        {
            video.ScheduledAtUtc = slot;
            video.UpdatedAtUtc = now;
        }

        return anyScheduled;
    }

    private static DateTime SafeLocal(DateTime local, TimeZoneInfo zone) => zone.IsInvalidTime(local) ? local.AddHours(1) : local;
}
