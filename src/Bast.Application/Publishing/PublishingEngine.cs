using Bast.Application.Persistence;
using Bast.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Bast.Application.Publishing;

/// <summary>
/// Executa a fila de publicações. Cada publicação (vídeo x plataforma) é independente:
/// o resultado de uma rede nunca desfaz nem repete o de outra.
/// Toda mudança de estado é gravada antes da chamada à plataforma e confirmada depois dela.
/// </summary>
public sealed class PublishingEngine(
    IDbContextFactory<BastDbContext> factory,
    IConnectorRegistry registry,
    SchedulingService scheduling,
    FolderSync folders,
    Workspace workspace,
    TimeProvider time,
    ILogger<PublishingEngine> logger)
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ProcessingInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NotConnectedInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OfflineInterval = TimeSpan.FromMinutes(2);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>Espera entre tentativas: 1, 2, 4, 8... minutos, no máximo 1 hora.</summary>
    public static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Clamp(attempt - 1, 0, 10))));

    /// <summary>
    /// Na inicialização: um envio que estava em andamento quando o serviço parou tem resultado incerto.
    /// Vai para UNKNOWN e só sai de lá depois de consultar a plataforma.
    /// </summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = Now;
        var interrupted = await db.Publications.Include(p => p.Video)
            .Where(p => p.State == PublicationState.Uploading).ToListAsync(ct);

        foreach (var publication in interrupted)
        {
            publication.LastError = "O serviço foi interrompido durante o envio; o resultado não pôde ser confirmado.";
            publication.NextRetryAtUtc = null;
            publication.TransitionTo(PublicationState.Unknown, now);
            await NotifyUnknownAsync(db, publication.Video!, publication, ct);
            logger.LogWarning("{Folder} em {Platform}: envio interrompido, resultado incerto. Será reconciliado com a plataforma.",
                publication.Video!.FolderName, publication.Platform);
        }

        var openAttempts = await db.PublicationAttempts.Where(a => a.FinishedAtUtc == null).ToListAsync(ct);
        foreach (var attempt in openAttempts)
        {
            attempt.FinishedAtUtc = now;
            attempt.ResultState = PublicationState.Unknown;
            attempt.ErrorMessage = "Serviço interrompido durante a tentativa.";
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task RunCycleAsync(CancellationToken ct)
    {
        var settings = await scheduling.GetSettingsAsync(ct);
        if (!settings.Paused)
        {
            await scheduling.ScheduleNewAsync(ct);
            await scheduling.ReflowMissedAsync(ct);
        }

        // Rede desmarcada na Agenda não recebe mais nada, nem o que já estava agendado.
        await CancelDisabledPlatformsAsync(settings, ct);

        // Consultas não publicam nada, então continuam mesmo com a fila pausada.
        await ConfirmPendingAsync(settings, ct);

        if (!settings.Paused)
        {
            await PublishDueAsync(settings, ct);
        }

        await AggregateAsync(settings, ct);
        await folders.SyncAsync(ct);
    }

    /// <summary>
    /// Cancela as publicações que ainda não começaram (agendadas ou aguardando nova tentativa) das redes
    /// desmarcadas na Agenda. Vale para os verticais; os horizontais vão sempre só para o YouTube.
    /// Envios já em andamento não são mexidos.
    /// </summary>
    private async Task CancelDisabledPlatformsAsync(ScheduleSettings settings, CancellationToken ct)
    {
        var disabled = Enum.GetValues<Platform>().Where(p => !settings.IsPlatformEnabled(p)).ToList();
        if (disabled.Count == 0) return;

        await using var db = await factory.CreateDbContextAsync(ct);
        var stale = await db.Publications.Include(p => p.Video)
            .Where(p => disabled.Contains(p.Platform)
                        && (p.State == PublicationState.Scheduled || p.State == PublicationState.RetryWait)
                        && p.Video!.Lane == VideoLane.Standard)
            .ToListAsync(ct);
        if (stale.Count == 0) return;

        var now = Now;
        foreach (var publication in stale)
        {
            publication.LastError = "Cancelada: a rede foi desmarcada na Agenda.";
            publication.NextRetryAtUtc = null;
            publication.TransitionTo(PublicationState.Cancelled, now);
            logger.LogInformation("{Folder}: publicação em {Platform} cancelada (rede desmarcada na Agenda).", publication.Video!.FolderName, publication.Platform);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task PublishDueAsync(ScheduleSettings settings, CancellationToken ct)
    {
        var now = Now;
        List<Guid> due;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            due = await db.Publications
                .Where(p => (p.State == PublicationState.Scheduled && p.ScheduledAtUtc <= now && (p.NextRetryAtUtc == null || p.NextRetryAtUtc <= now))
                            || (p.State == PublicationState.RetryWait && p.NextRetryAtUtc <= now))
                .OrderBy(p => p.ScheduledAtUtc)
                .Select(p => p.Id)
                .ToListAsync(ct);
        }

        foreach (var id in due)
        {
            ct.ThrowIfCancellationRequested();
            await ExecuteAsync(id, settings, ct);
        }
    }

    private async Task ExecuteAsync(Guid publicationId, ScheduleSettings settings, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var publication = await db.Publications.Include(p => p.Video).FirstAsync(p => p.Id == publicationId, ct);
        if (publication.State is not (PublicationState.Scheduled or PublicationState.RetryWait)) return;

        var video = publication.Video!;
        var now = Now;
        if (video.State == VideoState.Ready)
        {
            video.State = VideoState.Publishing;
            video.UpdatedAtUtc = now;
        }

        var connector = registry.Get(publication.Platform);
        if (connector is null)
        {
            publication.LastError = "Rede não conectada; aguardando conexão.";
            publication.NextRetryAtUtc = now + NotConnectedInterval;
            await db.SaveChangesAsync(ct);
            return;
        }

        var videoPath = Path.Combine(workspace.ToAbsolute(video.RelativePath), video.VideoFileName);
        if (!File.Exists(videoPath))
        {
            publication.LastError = $"Arquivo de vídeo não encontrado: {videoPath}";
            if (publication.State == PublicationState.Scheduled) publication.TransitionTo(PublicationState.Validating, now);
            publication.TransitionTo(PublicationState.Failed, now);
            await db.SaveChangesAsync(ct);
            logger.LogError("{Folder} em {Platform}: {Error}", video.FolderName, publication.Platform, publication.LastError);
            return;
        }

        publication.TransitionTo(PublicationState.Uploading, now);
        publication.AttemptCount++;
        publication.StartedAtUtc ??= now;
        publication.NextRetryAtUtc = null;
        publication.Simulated = connector.IsSimulation;
        var attempt = new PublicationAttempt
        {
            Id = Guid.NewGuid(),
            PublicationId = publication.Id,
            Number = await db.PublicationAttempts.CountAsync(a => a.PublicationId == publication.Id, ct) + 1,
            StartedAtUtc = now,
        };
        db.PublicationAttempts.Add(attempt);

        // Gravado antes de falar com a plataforma: se o serviço cair agora, a reinicialização
        // encontra UPLOADING e trata como resultado incerto, em vez de enviar de novo.
        await db.SaveChangesAsync(ct);

        PublishOutcome outcome;
        try
        {
            outcome = await connector.PublishAsync(await BuildRequestAsync(db, publication, video, videoPath, ct), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Erro inesperado (inclusive timeout) não prova que a publicação falhou.
            outcome = PublishOutcome.Unknown($"Erro inesperado durante o envio: {ex.Message}");
        }

        Apply(publication, attempt, outcome, settings, fromCheck: false);
        if (publication.State == PublicationState.Unknown) await NotifyUnknownAsync(db, video, publication, ct);
        await db.SaveChangesAsync(ct);
        Log(video, publication);
    }

    private Task NotifyUnknownAsync(BastDbContext db, Video video, Publication publication, CancellationToken ct) =>
        NotificationOutbox.AddAsync(db, NotificationKind.PublicationUnknown, $"publication:{publication.Id}:unknown",
            NotificationOutbox.PublicationUnknown(video, publication), Now, ct);

    /// <summary>Consulta a plataforma sobre publicações em processamento ou de resultado incerto.</summary>
    private async Task ConfirmPendingAsync(ScheduleSettings settings, CancellationToken ct)
    {
        var now = Now;
        List<Guid> pending;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            pending = await db.Publications
                .Where(p => (p.State == PublicationState.Unknown || p.State == PublicationState.Processing)
                            && (p.NextRetryAtUtc == null || p.NextRetryAtUtc <= now))
                .Select(p => p.Id)
                .ToListAsync(ct);
        }

        foreach (var id in pending)
        {
            ct.ThrowIfCancellationRequested();
            await using var db = await factory.CreateDbContextAsync(ct);
            var publication = await db.Publications.Include(p => p.Video).FirstAsync(p => p.Id == id, ct);
            var connector = registry.Get(publication.Platform);
            if (connector is null) continue;

            var video = publication.Video!;
            var videoPath = Path.Combine(workspace.ToAbsolute(video.RelativePath), video.VideoFileName);

            PublishOutcome outcome;
            try
            {
                outcome = await connector.CheckAsync(await BuildRequestAsync(db, publication, video, videoPath, ct), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                outcome = PublishOutcome.Unknown($"Não foi possível consultar a plataforma: {ex.Message}");
            }

            Apply(publication, null, outcome, settings, fromCheck: true);
            await db.SaveChangesAsync(ct);
            Log(video, publication);
        }
    }

    private void Apply(Publication publication, PublicationAttempt? attempt, PublishOutcome outcome, ScheduleSettings settings, bool fromCheck)
    {
        var now = Now;
        if (outcome.ExternalId is not null) publication.ExternalId = outcome.ExternalId;
        if (outcome.Url is not null) publication.ExternalUrl = outcome.Url;
        if (outcome.Warning is not null) publication.Warning = outcome.Warning;

        var kind = outcome.Kind;
        // Só uma consulta pode afirmar que a plataforma não tem a publicação.
        if (kind == PublishOutcomeKind.NotFound && !fromCheck) kind = PublishOutcomeKind.Transient;
        // Falha temporária ao consultar não diz nada sobre a publicação: continua aguardando confirmação.
        if (kind is PublishOutcomeKind.Transient or PublishOutcomeKind.AuthRequired or PublishOutcomeKind.Offline or PublishOutcomeKind.Waiting && fromCheck) kind = PublishOutcomeKind.Unknown;

        switch (kind)
        {
            case PublishOutcomeKind.Published:
                publication.LastError = null;
                publication.NextRetryAtUtc = null;
                publication.TransitionTo(PublicationState.Published, now);
                break;

            case PublishOutcomeKind.Processing:
                if (publication.State != PublicationState.Processing) publication.TransitionTo(PublicationState.Processing, now);
                publication.NextRetryAtUtc = now + (outcome.RetryAfter ?? ProcessingInterval);
                break;

            case PublishOutcomeKind.Permanent:
                publication.LastError = outcome.Error ?? "Falha definitiva informada pela plataforma.";
                publication.NextRetryAtUtc = null;
                publication.TransitionTo(PublicationState.Failed, now);
                break;

            case PublishOutcomeKind.Transient or PublishOutcomeKind.NotFound:
                publication.LastError = outcome.Error ?? "Falha temporária.";
                if (publication.AttemptCount >= settings.MaxAttempts)
                {
                    publication.LastError += $" Limite de {settings.MaxAttempts} tentativas atingido.";
                    publication.NextRetryAtUtc = null;
                    publication.TransitionTo(PublicationState.Failed, now);
                }
                else
                {
                    publication.NextRetryAtUtc = now + (outcome.RetryAfter ?? Backoff(publication.AttemptCount));
                    publication.TransitionTo(PublicationState.RetryWait, now);
                }

                break;

            case PublishOutcomeKind.AuthRequired or PublishOutcomeKind.Offline or PublishOutcomeKind.Waiting:
                // Condição externa (conta desconectada, internet fora, outra rede ainda publicando): espera sem gastar tentativa.
                publication.LastError = outcome.Error ?? "Aguardando conexão.";
                publication.AttemptCount = Math.Max(0, publication.AttemptCount - 1);
                publication.NextRetryAtUtc = now + (outcome.RetryAfter ?? (kind == PublishOutcomeKind.Offline ? OfflineInterval : NotConnectedInterval));
                publication.TransitionTo(PublicationState.RetryWait, now);
                break;

            case PublishOutcomeKind.Skipped:
                publication.LastError = outcome.Error ?? "Publicação dispensada.";
                publication.NextRetryAtUtc = null;
                if (publication.State == PublicationState.Uploading) publication.TransitionTo(PublicationState.Processing, now);
                publication.TransitionTo(PublicationState.Cancelled, now);
                break;

            case PublishOutcomeKind.Unknown:
                publication.LastError = outcome.Error ?? "Resultado não confirmado.";
                if (publication.State == PublicationState.Uploading) publication.TransitionTo(PublicationState.Unknown, now);
                publication.NextRetryAtUtc = now + CheckInterval;
                break;
        }

        if (attempt is not null)
        {
            attempt.FinishedAtUtc = now;
            attempt.ResultState = publication.State;
            attempt.ErrorMessage = outcome.Error;
        }
    }

    /// <summary>Atualiza a situação do vídeo quando todas as suas publicações chegaram a um estado final.</summary>
    private async Task AggregateAsync(ScheduleSettings settings, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var videos = await db.Videos.Include(v => v.Publications)
            .Where(v => (v.State == VideoState.Ready || v.State == VideoState.Publishing) && v.Publications.Any())
            .ToListAsync(ct);

        var now = Now;
        foreach (var video in videos.Where(v => v.Publications.All(p => PublicationStateMachine.IsTerminal(p.State))))
        {
            video.State = video.Publications.Any(p => p.State == PublicationState.Failed) ? VideoState.Error
                : video.Publications.Any(p => p.State == PublicationState.Published) ? VideoState.Published
                : VideoState.Archived;
            video.UpdatedAtUtc = now;
            // Vertical publicado no automático: espera o envio manual nas outras redes (pasta PENDENTE MANUAL).
            if (video.State == VideoState.Published && video.Lane == VideoLane.Standard && settings.ManualPosting)
            {
                video.ManualPendingSinceUtc ??= now;
            }
            if (NotificationOutbox.VideoFinished(video) is { } summary)
            {
                await NotificationOutbox.AddAsync(db, NotificationKind.VideoFinished, $"video:{video.Id}:finished", summary, now, ct);
            }

            logger.LogInformation("{Folder} finalizado: {State} ({Summary}).", video.FolderName, video.State,
                string.Join(", ", video.Publications.OrderBy(p => p.Platform).Select(p => $"{p.Platform}: {p.State}")));
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task<PublishRequest> BuildRequestAsync(BastDbContext db, Publication publication, Video video, string videoPath, CancellationToken ct)
    {
        var siblings = await db.Publications.AsNoTracking()
            .Where(p => p.VideoId == publication.VideoId && p.Id != publication.Id)
            .Select(p => new SiblingPublication(p.Platform, p.State, p.ExternalId))
            .ToListAsync(ct);
        var folder = Path.GetDirectoryName(videoPath)!;
        return new PublishRequest(
            publication.Id,
            publication.IdempotencyKey,
            videoPath,
            video.ThumbnailFileName is null ? null : Path.Combine(folder, video.ThumbnailFileName),
            CaptionRules.TextFor(video.CaptionText),
            CaptionRules.YouTubeTitle(video.CaptionText),
            publication.ExternalId,
            async externalId =>
            {
                publication.ExternalId = externalId;
                await db.SaveChangesAsync();
            },
            siblings);
    }

    private void Log(Video video, Publication publication)
    {
        var level = publication.State is PublicationState.Failed or PublicationState.Unknown ? LogLevel.Warning : LogLevel.Information;
        logger.Log(level, "{Folder} em {Platform}: {State}{Simulated}{Error}", video.FolderName, publication.Platform, publication.State,
            publication.Simulated ? " (simulação)" : "", publication.LastError is null ? "" : " - " + publication.LastError);
    }
}
