namespace Bast.Domain;

/// <summary>
/// Situação de um job de produção (uma gravação do OBS virando vídeos). Renderizado, aprovado no QA,
/// liberado e publicado são estados distintos e nunca se confundem.
/// </summary>
public enum ProductionStatus
{
    Queued,
    Analyzing,
    SelectingHighlights,
    EditingHorizontal,
    EditingVertical,
    GeneratingThumbnails,
    Rendering,
    QualityCheck,
    Fixing,
    WaitingApproval,
    Approved,
    ReadyToPublish,
    Publishing,
    Published,
    Failed,
    Cancelled,
    NeedsHumanReview,
}

public enum ProductionStepState
{
    Pending,
    Running,
    Done,
    Failed,
    Skipped,
}

public enum ProductionLogLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>Uma gravação do OBS e tudo o que é produzido a partir dela.</summary>
public sealed class ProductionJob
{
    public Guid Id { get; set; }

    /// <summary>Nome do arquivo original. O original nunca é movido, alterado ou apagado.</summary>
    public string SourceFileName { get; set; } = "";
    public string SourcePath { get; set; } = "";

    /// <summary>Nome + tamanho + data de gravação: a mesma gravação nunca gera dois jobs.</summary>
    public string Fingerprint { get; set; } = "";

    public DateTime ReceivedAtUtc { get; set; }
    public double DurationSeconds { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public long SizeBytes { get; set; }
    public int AudioTracks { get; set; }

    public ProductionStatus Status { get; set; } = ProductionStatus.Queued;

    /// <summary>Etapa atual em texto, para o painel ("Verificação de integridade", "Aguardando autorização"...).</summary>
    public string CurrentStep { get; set; } = "";

    /// <summary>0 a 100, só quando há base para medir.</summary>
    public int Progress { get; set; }

    public string? LastError { get; set; }
    public int Attempts { get; set; }

    /// <summary>Autorização do usuário para este job. Sem ela nada é processado.</summary>
    public bool Authorized { get; set; }
    public DateTime? AuthorizedAtUtc { get; set; }

    public bool Paused { get; set; }
    public string? PauseReason { get; set; }

    /// <summary>Motivo que impede começar agora (ex.: pouco espaço no disco). Não é erro: o job espera.</summary>
    public string? BlockedReason { get; set; }

    /// <summary>Pasta de trabalho deste job no disco de produção.</summary>
    public string? WorkFolder { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    public List<ProductionStep> Steps { get; set; } = [];

    public bool IsFinished => Status is ProductionStatus.Published or ProductionStatus.Cancelled or ProductionStatus.Failed;
}

/// <summary>Uma etapa do pipeline de um job. Etapa concluída e validada nunca é refeita numa retomada.</summary>
public sealed class ProductionStep
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public string Name { get; set; } = "";
    public int Order { get; set; }
    public ProductionStepState State { get; set; } = ProductionStepState.Pending;
    public int Attempts { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>Resumo do resultado (curto, legível).</summary>
    public string? Output { get; set; }
    public string? Error { get; set; }
}

/// <summary>Registro do que foi feito: etapa, programa, arquivo, resultado.</summary>
public sealed class ProductionLog
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public DateTime AtUtc { get; set; }
    public ProductionLogLevel Level { get; set; }
    public string Step { get; set; } = "";
    public string Message { get; set; } = "";

    /// <summary>Programa usado (ffprobe, ffmpeg, ...), quando houver.</summary>
    public string? Program { get; set; }
    public string? File { get; set; }
}

/// <summary>Controle geral da produção (uma linha): o botão de parada de emergência.</summary>
public sealed class ProductionControl
{
    public int Id { get; set; } = 1;

    /// <summary>Parada de emergência: nenhuma etapa nova começa e a etapa em andamento é interrompida.</summary>
    public bool Stopped { get; set; }
    public string? StopReason { get; set; }
    public DateTime? StoppedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
