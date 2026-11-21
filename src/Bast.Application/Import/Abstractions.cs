using Bast.Domain;

namespace Bast.Application.Import;

public enum VideoAnalysisStatus
{
    Ok,
    /// <summary>O arquivo não é um vídeo íntegro.</summary>
    Invalid,
    /// <summary>ffprobe/ffmpeg não encontrados ou não executaram; nada pode ser concluído sobre o arquivo.</summary>
    ToolUnavailable,
}

public sealed record VideoAnalysis(
    double DurationSeconds,
    int Width,
    int Height,
    string VideoCodec,
    string? AudioCodec,
    double FrameRate,
    long? BitRate,
    string FormatName);

public sealed record VideoAnalysisResult(VideoAnalysisStatus Status, VideoAnalysis? Analysis, string? Error)
{
    public static VideoAnalysisResult Ok(VideoAnalysis analysis) => new(VideoAnalysisStatus.Ok, analysis, null);
    public static VideoAnalysisResult Invalid(string error) => new(VideoAnalysisStatus.Invalid, null, error);
    public static VideoAnalysisResult Unavailable(string error) => new(VideoAnalysisStatus.ToolUnavailable, null, error);
}

public interface IVideoAnalyzer
{
    Task<VideoAnalysisResult> AnalyzeAsync(string videoPath, CancellationToken ct);
}

/// <summary>Persistência usada pela importação. Cada método é uma transação completa.</summary>
public interface IImportStore
{
    Task<IReadOnlyList<Video>> FindByHashAsync(string contentHash, CancellationToken ct);
    Task<IReadOnlyList<Video>> GetImportingAsync(CancellationToken ct);
    Task AddAsync(Video video, CancellationToken ct);
    Task SetPendingMoveAsync(Guid videoId, string pendingMovePath, CancellationToken ct);
    Task CompleteImportAsync(Guid videoId, string relativePath, CancellationToken ct);
    Task MarkMissingAsync(Guid videoId, string note, CancellationToken ct);
    Task ReportIssueAsync(string folderName, ImportIssueCode code, string message, string? movedToPath, CancellationToken ct);

    /// <summary>Encerra os problemas em aberto de uma pasta (importada com sucesso).</summary>
    Task ResolveIssuesAsync(string folderName, CancellationToken ct);

    /// <summary>Encerra problemas em aberto de pastas que não estão mais em 01 - ENVIAR VERTICAL e não foram movidas pelo sistema.</summary>
    Task ResolveIssuesForAbsentFoldersAsync(IReadOnlyCollection<string> presentFolderNames, CancellationToken ct);
}
