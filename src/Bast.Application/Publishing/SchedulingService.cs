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

    /// <summary>Redes que recebem o vídeo: as marcadas na agenda, ou só o YouTube para os horizontais.</summary>
    public IReadOnlyList<Platform> PlatformsFor(VideoLane lane, ScheduleSettings settings) => lane == VideoLane.YouTubeHorizontal
        ? registry.Available.Contains(Platform.YouTube) ? [Platform.YouTube] : []
        : ActivePlatforms(settings);

    /// <summary>
    /// Fila principal, modo automático: agenda, em ordem numérica, os vídeos validados que ainda não têm horário.
    /// Fila principal, modo manual: só avisa (uma vez por vídeo) que há vídeo aguardando horário.
    /// Vídeos horizontais do YouTube: sempre a próxima janela livre do dia, um vídeo por janela.
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
        var bookings = await BookingsAsync(db, ct);
        var scheduled = await ScheduleFixedAsync(db, settings, VideoLane.YouTubeHorizontal,
            waiting.Where(v => v.Lane == VideoLane.YouTubeHorizontal).ToList(), bookings, now, ct);

        var standard = waiting.Where(v => v.Lane == VideoLane.Standard).ToList();
        if (standard.Count == 0) return scheduled;

        if (settings.Mode == ScheduleMode.FixedTimes)
        {
            return scheduled + await ScheduleFixedAsync(db, settings, VideoLane.Standard, standard, bookings, now, ct);
        }

        if (settings.Mode == ScheduleMode.Manual)
        {
            foreach (var video in standard)
            {
                await NotificationOutbox.AddAsync(db, NotificationKind.TimeNeeded, $"video:{video.Id}:time-needed",
                    NotificationOutbox.TimeNeeded(video.FolderName), now, ct);
            }

            await db.SaveChangesAsync(ct);
            return scheduled;
        }

        var platforms = ActivePlatforms(settings);
        if (platforms.Count == 0) return scheduled;

        var lastReserved = await db.Videos
            .Where(v => v.State == VideoState.Ready && v.ScheduledAtUtc != null && v.Lane == VideoLane.Standard)
            .MaxAsync(v => v.ScheduledAtUtc, ct);
        var cursor = lastReserved is { } last && last > now ? last : now;

        foreach (var video in standard)
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
    /// Horários fixos (horizontais sempre; verticais no modo <see cref="ScheduleMode.FixedTimes"/>): cada vídeo, na
    /// ordem, pega o primeiro horário da lista a partir de agora que esteja livre. Livre = nenhum vídeo de
    /// qualquer fila colado nesse horário e o dia ainda com vaga nesta fila (um vídeo por horário da lista,
    /// contando os já publicados no dia). Dia cheio: vai para o dia seguinte.
    /// </summary>
    private async Task<int> ScheduleFixedAsync(BastDbContext db, ScheduleSettings settings, VideoLane lane, List<Video> videos,
        List<Booking> bookings, DateTime now, CancellationToken ct)
    {
        if (videos.Count == 0) return 0;
        var platforms = PlatformsFor(lane, settings);
        if (platforms.Count == 0) return 0;

        var scheduled = 0;
        foreach (var video in videos)
        {
            var slot = NextFreeSlot(settings, lane, now, bookings);
            if (slot is null)
            {
                logger.LogWarning("Nenhum horário livre para os vídeos {Lane}; vídeos aguardando.", lane == VideoLane.YouTubeHorizontal ? "horizontais" : "verticais");
                break;
            }

            if (Assign(db, video, slot.Value, platforms, now))
            {
                bookings.Add(new Booking(video.Id, lane, slot.Value));
                scheduled++;
                logger.LogInformation("{Folder} agendado para {Slot:yyyy-MM-dd HH:mm} UTC ({Lane}).", video.FolderName, slot, lane);
            }

            // Uma transação por vídeo: horário e publicações são gravados juntos.
            await db.SaveChangesAsync(ct);
        }

        return scheduled;
    }

    /// <summary>Horário marcado de um vídeo que ocupa a agenda (aguardando, publicando ou já publicado).</summary>
    private sealed record Booking(Guid VideoId, VideoLane Lane, DateTime AtUtc);

    private static async Task<List<Booking>> BookingsAsync(BastDbContext db, CancellationToken ct) =>
        await db.Videos
            .Where(v => v.ScheduledAtUtc != null
                        && (v.State == VideoState.Ready || v.State == VideoState.Publishing || v.State == VideoState.Published))
            .Select(v => new Booking(v.Id, v.Lane, v.ScheduledAtUtc!.Value))
            .ToListAsync(ct);

    /// <summary>Horários do dia da fila: horizontais sempre na lista deles; verticais na lista de horários fixos.</summary>
    private static (IReadOnlyList<TimeOnly> Times, bool HonorDays) FixedTimesOf(ScheduleSettings settings, VideoLane lane) =>
        lane == VideoLane.YouTubeHorizontal ? (settings.HorizontalSlotTimes, false) : (settings.VerticalSlotTimes, true);

    private static DateTime? NextFreeSlot(ScheduleSettings settings, VideoLane lane, DateTime afterUtc, List<Booking> bookings)
    {
        var (times, honorDays) = FixedTimesOf(settings, lane);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        DateOnly DayOf(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone));

        return SlotCalculator.NextFixedSlotUtc(settings, times, afterUtc, honorDays, utc =>
            !bookings.Any(b => (b.AtUtc - utc).Duration() < MinimumGap)
            && bookings.Count(b => b.Lane == lane && DayOf(b.AtUtc) == DayOf(utc)) < times.Count);
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
            .Select(v => new { v.FolderName, v.Lane, ScheduledAtUtc = v.ScheduledAtUtc!.Value })
            .ToListAsync(ct);

        // O limite do dia é de cada fila; o choque de horário vale entre todas.
        var limit = video.Lane == VideoLane.YouTubeHorizontal ? settings.HorizontalPerDay : settings.ChosenTimeMaxPerDay;
        var sameDay = others.Count(o => o.Lane == video.Lane && o.ScheduledAtUtc >= dayStart && o.ScheduledAtUtc < dayEnd);
        if (sameDay >= limit)
        {
            return new(false, $"O dia {localTime:dd/MM} já tem {sameDay} vídeo(s), e o máximo é {limit} por dia. Escolha outro dia ou mude o máximo na Agenda.");
        }

        var clash = others.FirstOrDefault(o => (o.ScheduledAtUtc - slot).Duration() < MinimumGap);
        if (clash is not null) return new(false, $"\"{clash.FolderName}\" já está marcado para esse horário. Dois vídeos nunca saem juntos; escolha outro minuto.");

        if (video.Publications.Count == 0)
        {
            var platforms = PlatformsFor(video.Lane, settings);
            if (platforms.Count == 0)
            {
                return new(false, video.Lane == VideoLane.YouTubeHorizontal ? "O YouTube não está conectado." : "Nenhuma rede conectada e marcada na Agenda.");
            }
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

        var platforms = PlatformsFor(video.Lane, settings);
        if (platforms.Count == 0) return 0;

        var now = Now;
        if (!Assign(db, video, now, platforms, now)) { await db.SaveChangesAsync(ct); return 0; }
        await db.SaveChangesAsync(ct);
        return video.Publications.Count(p => p.State == PublicationState.Scheduled);
    }

    /// <summary>
    /// Trata horários perdidos (computador desligado, serviço parado, fila pausada). Nunca publica o
    /// acumulado de uma vez: só um vídeo atrasado dentro da tolerância mantém o horário.
    /// Fila principal, automático: os vídeos que não começaram são redistribuídos pelos próximos horários.
    /// Fila principal, manual: o horário escolhido é desfeito e o vídeo volta a aguardar horário, com aviso.
    /// Horizontais do YouTube: são redistribuídos, na mesma ordem, pelas próximas janelas livres.
    /// </summary>
    public async Task<int> ReflowMissedAsync(CancellationToken ct)
    {
        var settings = await GetSettingsAsync(ct);
        var now = Now;
        var cutoff = now.AddMinutes(-settings.MissedGraceMinutes);

        await using var db = await factory.CreateDbContextAsync(ct);
        // Vídeo que já saiu em alguma rede (nova tentativa das que falharam) não é post novo: não é remarcado.
        var queue = (await db.Videos
                .Include(v => v.Publications)
                .Where(v => v.State == VideoState.Ready && v.ScheduledAtUtc != null
                            && v.Publications.Any(p => p.State == PublicationState.Scheduled)
                            && !v.Publications.Any(p => p.State == PublicationState.Published))
                .ToListAsync(ct))
            .OrderBy(v => v.ScheduledAtUtc)
            .ThenBy(v => v.FolderName, NaturalStringComparer.Instance)
            .ToList();

        var overdue = queue.Where(v => v.ScheduledAtUtc <= now).ToList();
        var keep = overdue.Count > 0 && overdue[0].ScheduledAtUtc >= cutoff ? overdue[0] : null;
        var missed = overdue.Where(v => v != keep).ToList();
        if (missed.Count == 0) return 0;

        var changed = 0;
        if (missed.Any(v => v.Lane == VideoLane.Standard))
        {
            if (settings.Mode == ScheduleMode.Manual)
            {
                foreach (var video in missed.Where(v => v.Lane == VideoLane.Standard))
                {
                    var original = video.ScheduledAtUtc!.Value;
                    Move(video, null, now);
                    await NotificationOutbox.AddAsync(db, NotificationKind.TimeNeeded, $"video:{video.Id}:missed:{original:yyyyMMddHHmm}",
                        NotificationOutbox.ChosenTimeMissed(video.FolderName), now, ct);
                    changed++;
                }
            }
            else if (settings.Mode == ScheduleMode.FixedTimes)
            {
                changed += await ReflowFixedAsync(db, settings, VideoLane.Standard, queue, keep, now, ct);
            }
            else
            {
                var cursor = now;
                var first = true;
                foreach (var video in queue.Where(v => v.Lane == VideoLane.Standard && v != keep))
                {
                    var slot = SlotCalculator.NextSlotUtc(settings, cursor, skipToNextDay: first && settings.MissedPolicy == MissedSlotPolicy.NextCycle);
                    if (slot is null) break;

                    first = false;
                    cursor = slot.Value;
                    Move(video, slot, now);
                    changed++;
                }
            }
        }

        if (missed.Any(v => v.Lane == VideoLane.YouTubeHorizontal))
        {
            changed += await ReflowFixedAsync(db, settings, VideoLane.YouTubeHorizontal, queue, keep, now, ct);
        }

        await db.SaveChangesAsync(ct);
        logger.LogWarning("Horário perdido detectado: {Count} vídeo(s) remarcado(s) ou devolvido(s) para escolha de horário.", changed);
        return changed;
    }

    /// <summary>
    /// Redistribui, na mesma ordem, os vídeos da fila que ainda não começaram pelos próximos horários fixos livres.
    /// Com <see cref="MissedSlotPolicy.NextCycle"/>, começa no dia seguinte.
    /// </summary>
    private async Task<int> ReflowFixedAsync(BastDbContext db, ScheduleSettings settings, VideoLane lane, List<Video> queue, Video? keep,
        DateTime now, CancellationToken ct)
    {
        var moving = queue.Where(v => v.Lane == lane && v != keep).ToList();
        var ids = moving.Select(v => v.Id).ToHashSet();
        var bookings = (await BookingsAsync(db, ct)).Where(b => !ids.Contains(b.VideoId)).ToList();

        var after = now;
        if (settings.MissedPolicy == MissedSlotPolicy.NextCycle)
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
            var tomorrow = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, zone)).AddDays(1);
            after = TimeZoneInfo.ConvertTimeToUtc(SafeLocal(tomorrow.ToDateTime(TimeOnly.MinValue), zone), zone).AddTicks(-1);
        }

        var changed = 0;
        foreach (var video in moving)
        {
            var slot = NextFreeSlot(settings, lane, after, bookings);
            if (slot is null) break;

            bookings.Add(new Booking(video.Id, lane, slot.Value));
            Move(video, slot, now);
            changed++;
        }

        return changed;
    }

    /// <summary>Muda o horário do vídeo e das publicações que ainda aguardam. Nulo = volta a aguardar horário.</summary>
    private static void Move(Video video, DateTime? slot, DateTime now)
    {
        video.ScheduledAtUtc = slot;
        video.UpdatedAtUtc = now;
        foreach (var publication in video.Publications.Where(p => p.State == PublicationState.Scheduled))
        {
            publication.ScheduledAtUtc = slot;
            publication.UpdatedAtUtc = now;
        }
    }

    /// <summary>
    /// Cria a publicação do vídeo numa rede, validando a legenda: agendada no horário, ou com falha
    /// quando a rede não aceita o vídeo. O vídeo precisa estar sendo acompanhado pelo contexto.
    /// </summary>
    internal Publication AddPublication(BastDbContext db, Video video, Platform platform, DateTime? slot, DateTime now)
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

        var problems = CaptionRules.Validate(platform, video.CaptionText, new VideoFacts(video.DurationSeconds, video.FileSizeBytes));
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
        }

        // O vídeo está sendo acompanhado pelo contexto: a publicação entra na lista dele sozinha.
        db.Publications.Add(publication);
        return publication;
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
            foreach (var platform in platforms)
            {
                anyScheduled |= AddPublication(db, video, platform, slot, now).State == PublicationState.Scheduled;
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
