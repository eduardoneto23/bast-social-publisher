namespace Bast.Domain;

public enum NotificationKind
{
    /// <summary>Todas as publicações de um vídeo chegaram a um estado final.</summary>
    VideoFinished,
    /// <summary>Um envio ficou sem confirmação da plataforma.</summary>
    PublicationUnknown,
    AccountNeedsReauth,
    ImportIssue,
    Test,
}

/// <summary>
/// Aviso ao usuário. É gravado junto com a mudança que o causou e enviado depois por outro ciclo,
/// de modo que uma queda entre os dois passos não perde o aviso nem o envia em dobro.
/// </summary>
public sealed class Notification
{
    public Guid Id { get; set; }
    public NotificationKind Kind { get; set; }

    /// <summary>Quando preenchida, impede dois avisos sobre o mesmo fato.</summary>
    public string? DedupKey { get; set; }

    public string Text { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? SentAtUtc { get; set; }

    /// <summary>Aviso que não será enviado (canal não configurado ou aviso vencido).</summary>
    public DateTime? DiscardedAtUtc { get; set; }

    public int Attempts { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public string? LastError { get; set; }
}
