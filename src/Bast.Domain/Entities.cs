namespace Bast.Domain;

public sealed class Video
{
    public Guid Id { get; set; }

    /// <summary>Nome da pasta original (ex.: "Video 001").</summary>
    public string FolderName { get; set; } = "";

    /// <summary>Primeiro número encontrado no nome da pasta, usado na ordenação.</summary>
    public long? SequenceNumber { get; set; }

    /// <summary>SHA-256 do arquivo de vídeo, em hexadecimal minúsculo.</summary>
    public string ContentHash { get; set; } = "";

    /// <summary>1 para a primeira importação; incrementa a cada reimportação intencional do mesmo conteúdo.</summary>
    public int Generation { get; set; }

    public VideoState State { get; set; }
    public VideoLane Lane { get; set; } = VideoLane.Standard;
    public WorkspaceFolder CurrentFolder { get; set; }

    /// <summary>Caminho da pasta do vídeo, relativo à pasta principal.</summary>
    public string RelativePath { get; set; } = "";

    /// <summary>Destino de uma movimentação em andamento, relativo à pasta principal. Nulo quando não há movimentação pendente.</summary>
    public string? PendingMovePath { get; set; }

    /// <summary>Pasta de destino da movimentação em andamento.</summary>
    public WorkspaceFolder? PendingMoveFolder { get; set; }

    /// <summary>Horário de publicação reservado para este vídeo. Nulo enquanto não agendado.</summary>
    public DateTime? ScheduledAtUtc { get; set; }

    public string VideoFileName { get; set; } = "";
    public string CaptionFileName { get; set; } = "";
    public string? ThumbnailFileName { get; set; }

    /// <summary>Conteúdo integral do TXT, sem modificações.</summary>
    public string CaptionText { get; set; } = "";

    public long FileSizeBytes { get; set; }
    public double DurationSeconds { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string VideoCodec { get; set; } = "";
    public string? AudioCodec { get; set; }
    public double FrameRate { get; set; }
    public long? BitRate { get; set; }
    public string FormatName { get; set; } = "";

    public string? Note { get; set; }

    /// <summary>
    /// Vertical que terminou o envio automático e espera o usuário postar à mão nas outras redes
    /// (pasta PENDENTE MANUAL). <see cref="ManualDoneAtUtc"/> = o usuário confirmou no painel.
    /// </summary>
    public DateTime? ManualPendingSinceUtc { get; set; }
    public DateTime? ManualDoneAtUtc { get; set; }

    /// <summary>Publicado no automático e ainda aguardando o envio manual nas outras redes.</summary>
    public bool AwaitsManual => State == VideoState.Published && ManualPendingSinceUtc is not null && ManualDoneAtUtc is null;

    public DateTime ImportedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public List<Publication> Publications { get; set; } = [];
}

/// <summary>Uma publicação de um vídeo em uma plataforma. Controlada de forma independente das demais.</summary>
public sealed class Publication
{
    public Guid Id { get; set; }
    public Guid VideoId { get; set; }
    public Video? Video { get; set; }
    public Platform Platform { get; set; }
    public PublicationState State { get; private set; } = PublicationState.Pending;

    public DateTime? ScheduledAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? NextRetryAtUtc { get; set; }

    public int AttemptCount { get; set; }
    public string? LastError { get; set; }

    /// <summary>Algo que não impediu a publicação, mas o usuário precisa saber (ex.: thumbnail recusada).</summary>
    public string? Warning { get; set; }

    /// <summary>Identificador devolvido pela plataforma (sessão de upload, contêiner ou publicação).</summary>
    public string? ExternalId { get; set; }
    public string? ExternalUrl { get; set; }

    /// <summary>Chave estável usada para reconhecer uma operação já enviada à plataforma.</summary>
    public string IdempotencyKey { get; set; } = "";

    /// <summary>Verdadeiro quando executada pelo modo de simulação: nada foi enviado a nenhuma rede.</summary>
    public bool Simulated { get; set; }

    public string? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresAtUtc { get; set; }

    /// <summary>Controle de concorrência otimista.</summary>
    public int Version { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public List<PublicationAttempt> Attempts { get; set; } = [];

    public void TransitionTo(PublicationState next, DateTime nowUtc)
    {
        PublicationStateMachine.EnsureCanTransition(State, next);
        State = next;
        UpdatedAtUtc = nowUtc;
        Version++;
        if (next == PublicationState.Published)
        {
            CompletedAtUtc = nowUtc;
        }
    }
}

public sealed class PublicationAttempt
{
    public Guid Id { get; set; }
    public Guid PublicationId { get; set; }
    public int Number { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public PublicationState? ResultState { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>Problema encontrado em uma pasta de 01 - ENVIAR VERTICAL que impediu a importação.</summary>
public sealed class ImportIssue
{
    public Guid Id { get; set; }
    public string FolderName { get; set; } = "";
    public ImportIssueCode Code { get; set; }
    public string Message { get; set; } = "";

    /// <summary>Preenchido quando a pasta foi encaminhada para 05 - ERROS (relativo à pasta principal).</summary>
    public string? MovedToPath { get; set; }

    public DateTime FirstSeenAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
}

public enum AccountStatus
{
    Disconnected,
    Connected,
    /// <summary>Token expirado ou permissão revogada: a integração está interrompida até o usuário reconectar.</summary>
    NeedsReauth,
}

/// <summary>Conta de uma rede social. Não contém tokens: eles ficam no armazenamento seguro.</summary>
public sealed class PlatformAccount
{
    public Platform Platform { get; set; }
    public AccountStatus Status { get; set; }
    public string? ExternalAccountId { get; set; }
    public string? DisplayName { get; set; }
    public DateTime? ConnectedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? LastError { get; set; }
}
