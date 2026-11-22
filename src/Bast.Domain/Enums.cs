namespace Bast.Domain;

public enum Platform
{
    TikTok,
    Instagram,
    Facebook,
    Threads,
    YouTube,
}

/// <summary>Estados de uma publicação (um vídeo em uma plataforma).</summary>
public enum PublicationState
{
    Pending,
    Validating,
    Scheduled,
    Uploading,
    Processing,
    Published,
    RetryWait,
    Failed,
    Cancelled,
    /// <summary>Não foi possível confirmar se a operação foi concluída. Exige reconciliação antes de repetir.</summary>
    Unknown,
}

/// <summary>Situação agregada do vídeo. O banco é a fonte oficial; a pasta apenas reflete este estado.</summary>
public enum VideoState
{
    /// <summary>Registrado no banco, movimentação para AGENDADOS ainda não confirmada.</summary>
    Importing,
    /// <summary>Validado e em AGENDADOS.</summary>
    Ready,
    Publishing,
    Published,
    Error,
    Archived,
    /// <summary>A pasta não foi encontrada onde o banco esperava. Exige intervenção.</summary>
    Missing,
}

public enum WorkspaceFolder
{
    Inbox,
    Scheduled,
    Publishing,
    Sent,
    Errors,
    Archived,
    /// <summary>Entrada dos vídeos horizontais que vão só para o YouTube.</summary>
    YouTubeInbox,
    /// <summary>Vertical já publicado no automático, aguardando o usuário postar à mão nas outras redes.</summary>
    ManualPending,
}

/// <summary>Fila do vídeo: define a pasta de entrada, as redes e a agenda.</summary>
public enum VideoLane
{
    /// <summary>01 - ENVIAR VERTICAL: todas as redes marcadas, agenda principal (automática ou manual).</summary>
    Standard,
    /// <summary>01 - ENVIAR YOUTUBE: vídeo horizontal com thumbnail, só YouTube, janelas fixas por dia.</summary>
    YouTubeHorizontal,
}

public enum ImportIssueCode
{
    MissingVideo,
    MultipleVideos,
    MissingCaption,
    MultipleCaptions,
    EmptyCaption,
    UnreadableCaption,
    CorruptVideo,
    Duplicate,
    ToolUnavailable,
    MoveFailed,
    /// <summary>Vídeo horizontal sem a imagem da thumbnail.</summary>
    MissingThumbnail,
    /// <summary>Vídeo em 01 - ENVIAR YOUTUBE que não é horizontal.</summary>
    NotHorizontal,
}
