using System.Text;
using Bast.Application.Persistence;
using Bast.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Bast.Application;

public enum ChannelSendStatus
{
    Sent,
    /// <summary>Falha passageira (rede, limite de envio): tentar de novo depois.</summary>
    Retry,
    /// <summary>O canal recusou (credencial inválida, conversa bloqueada): só volta a funcionar com ação do usuário.</summary>
    Rejected,
}

public sealed record ChannelSendResult(ChannelSendStatus Status, string? Error = null, TimeSpan? RetryAfter = null)
{
    public static readonly ChannelSendResult Sent = new(ChannelSendStatus.Sent);
}

/// <summary>Meio de entrega dos avisos (Telegram).</summary>
public interface INotificationChannel
{
    bool IsConfigured { get; }
    Task<ChannelSendResult> SendAsync(string text, CancellationToken ct);
}

/// <summary>Grava avisos na fila e monta os textos.</summary>
public static class NotificationOutbox
{
    /// <summary>
    /// Acrescenta o aviso ao contexto, sem salvar: ele é gravado na mesma transação da mudança que o causou.
    /// Com chave de deduplicação, um fato já avisado não gera outro aviso.
    /// </summary>
    public static async Task AddAsync(BastDbContext db, NotificationKind kind, string? dedupKey, string text, DateTime nowUtc, CancellationToken ct)
    {
        if (dedupKey is not null)
        {
            if (db.Notifications.Local.Any(n => n.DedupKey == dedupKey)) return;
            if (await db.Notifications.AnyAsync(n => n.DedupKey == dedupKey, ct)) return;
        }

        db.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            DedupKey = dedupKey,
            Text = text,
            CreatedAtUtc = nowUtc,
        });
    }

    /// <summary>Resumo do vídeo quando todas as redes terminaram. Nulo quando não há o que avisar (tudo cancelado).</summary>
    public static string? VideoFinished(Video video)
    {
        var publications = video.Publications.OrderBy(p => p.Platform).ToList();
        var published = publications.Count(p => p.State == PublicationState.Published);
        var failed = publications.Count(p => p.State == PublicationState.Failed);
        if (published == 0 && failed == 0) return null;

        var text = new StringBuilder();
        text.Append(failed == 0 ? "✅ Publicado: " : published == 0 ? "❌ Falhou: " : "⚠️ Publicado com falhas: ");
        text.Append(video.FolderName);
        if (publications.Any(p => p.Simulated)) text.Append(" (simulação)");

        foreach (var publication in publications)
        {
            text.Append('\n').Append(publication.Platform).Append(": ");
            switch (publication.State)
            {
                case PublicationState.Published:
                    text.Append("publicado");
                    if (!string.IsNullOrEmpty(publication.ExternalUrl)) text.Append('\n').Append(publication.ExternalUrl);
                    break;
                case PublicationState.Failed:
                    text.Append("falhou");
                    if (!string.IsNullOrEmpty(publication.LastError)) text.Append(" - ").Append(publication.LastError);
                    break;
                default:
                    text.Append("cancelado");
                    break;
            }
        }

        return text.ToString();
    }

    public static string PublicationUnknown(Video video, Publication publication) =>
        $"❓ Sem confirmação: {video.FolderName} em {publication.Platform}\n" +
        $"{publication.LastError ?? "O resultado do envio não pôde ser confirmado."}\n" +
        "O sistema continua consultando a plataforma e não envia de novo sem ter certeza. Vale conferir a conta.";

    public static string AccountNeedsReauth(Platform platform, string error) =>
        $"🔑 {platform}: é preciso conectar a conta de novo.\n{error}\n" +
        "As publicações dessa rede ficam em espera, sem gastar tentativas, até a reconexão.";

    public static string TimeNeeded(string folderName) =>
        $"🕒 Aguardando horário: {folderName}\nAbra o painel, aba Fila, e escolha quando publicar.";

    public static string ChosenTimeMissed(string folderName) =>
        $"⏰ Horário perdido: {folderName}\nO computador ou o serviço estava parado no horário escolhido. O vídeo não foi publicado e voltou a aguardar horário no painel.";

    public static string ImportIssue(string folderName, string message, string? movedToPath) =>
        $"📁 Pasta com problema: {folderName}\n{message}" + (movedToPath is null ? "" : $"\nMovida para: {movedToPath}");
}

/// <summary>Envia os avisos pendentes pelo canal configurado, em ordem, com novas tentativas.</summary>
public sealed class NotificationDispatcher(
    IDbContextFactory<BastDbContext> factory,
    INotificationChannel channel,
    TimeProvider time,
    ILogger<NotificationDispatcher> logger)
{
    private const int BatchSize = 20;
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan RejectedWait = TimeSpan.FromMinutes(15);

    /// <summary>Intervalo entre mensagens seguidas (o Telegram pede no máximo uma por segundo por conversa).</summary>
    public TimeSpan Spacing { get; set; } = TimeSpan.FromMilliseconds(1100);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Clamp(attempt - 1, 0, 10))));

    public async Task RunCycleAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var pending = await db.Notifications
            .Where(n => n.SentAtUtc == null && n.DiscardedAtUtc == null)
            .OrderBy(n => n.CreatedAtUtc)
            .Take(BatchSize)
            .ToListAsync(ct);
        if (pending.Count == 0) return;

        if (!channel.IsConfigured)
        {
            // Sem canal, o aviso não é guardado para depois: conectar o Telegram não despeja avisos antigos.
            var now = Now;
            foreach (var notification in pending)
            {
                notification.DiscardedAtUtc = now;
                notification.LastError = "Nenhum canal de aviso configurado.";
            }

            await db.SaveChangesAsync(ct);
            return;
        }

        var sentAny = false;
        foreach (var notification in pending)
        {
            ct.ThrowIfCancellationRequested();
            var now = Now;

            if (now - notification.CreatedAtUtc > MaxAge)
            {
                notification.DiscardedAtUtc = now;
                notification.LastError = "Aviso vencido: não foi possível entregar em 24 horas. " + notification.LastError;
                await db.SaveChangesAsync(ct);
                continue;
            }

            // Mantém a ordem: enquanto o mais antigo espera nova tentativa, os seguintes também esperam.
            if (notification.NextAttemptAtUtc > now) break;

            if (sentAny && Spacing > TimeSpan.Zero) await Task.Delay(Spacing, ct);

            ChannelSendResult result;
            try
            {
                result = await channel.SendAsync(notification.Text, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = new ChannelSendResult(ChannelSendStatus.Retry, $"Erro inesperado ao enviar o aviso: {ex.Message}");
            }

            now = Now;
            notification.Attempts++;
            if (result.Status == ChannelSendStatus.Sent)
            {
                notification.SentAtUtc = now;
                notification.NextAttemptAtUtc = null;
                notification.LastError = null;
                await db.SaveChangesAsync(ct);
                sentAny = true;
                continue;
            }

            notification.LastError = result.Error ?? "Falha ao enviar o aviso.";
            notification.NextAttemptAtUtc = now + (result.RetryAfter
                ?? (result.Status == ChannelSendStatus.Rejected ? RejectedWait : Backoff(notification.Attempts)));
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Aviso não entregue ({Status}): {Error}", result.Status, notification.LastError);
            break;
        }
    }
}
