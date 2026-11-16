using Bast.Domain;
using Microsoft.Extensions.Logging;

namespace Bast.Application.Import;

public enum ImportOutcome
{
    Imported,
    /// <summary>Pasta incompleta ou legenda inválida; permanece em 01 - ENVIAR.</summary>
    Incomplete,
    /// <summary>Conteúdo já processado e sem autorização de reimportação; permanece em 01 - ENVIAR.</summary>
    Duplicate,
    /// <summary>Vídeo corrompido; pasta encaminhada para 05 - ERROS.</summary>
    Rejected,
    ToolUnavailable,
    MoveFailed,
}

/// <summary>
/// Importa uma pasta estável de 01 - ENVIAR: valida, registra no banco e move para 02 - AGENDADOS.
/// O registro no banco acontece antes da movimentação, com o destino gravado, para que uma
/// queda no meio do caminho seja concluída por <see cref="RecoverAsync"/> sem duplicar nada.
/// </summary>
public sealed class ImportService(
    Workspace workspace,
    FolderInspector inspector,
    IVideoAnalyzer analyzer,
    IImportStore store,
    TimeProvider time,
    ILogger<ImportService> logger)
{
    public async Task<ImportOutcome> ProcessFolderAsync(string folderPath, CancellationToken ct)
    {
        var folderName = Path.GetFileName(folderPath);

        var inspection = inspector.Inspect(folderPath);
        if (inspection.Problems.Count > 0)
        {
            foreach (var problem in inspection.Problems)
            {
                await store.ReportIssueAsync(folderName, problem.Code, problem.Message, null, ct);
            }

            return ImportOutcome.Incomplete;
        }

        string caption;
        try
        {
            caption = CaptionReader.Read(inspection.CaptionFile!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await store.ReportIssueAsync(folderName, ImportIssueCode.UnreadableCaption, $"Não foi possível ler a legenda: {ex.Message}", null, ct);
            return ImportOutcome.Incomplete;
        }

        if (string.IsNullOrWhiteSpace(caption))
        {
            await store.ReportIssueAsync(folderName, ImportIssueCode.EmptyCaption, "O arquivo de legenda está vazio.", null, ct);
            return ImportOutcome.Incomplete;
        }

        var videoPath = inspection.VideoFile!;
        var result = await analyzer.AnalyzeAsync(videoPath, ct);
        if (result.Status == VideoAnalysisStatus.ToolUnavailable)
        {
            await store.ReportIssueAsync(folderName, ImportIssueCode.ToolUnavailable, result.Error ?? "ffprobe/ffmpeg indisponíveis.", null, ct);
            return ImportOutcome.ToolUnavailable;
        }

        if (result.Status == VideoAnalysisStatus.Invalid)
        {
            return await RejectAsync(folderPath, folderName, result.Error ?? "Vídeo inválido.", ct);
        }

        var hash = await FileHasher.Sha256Async(videoPath, ct);
        var existing = await store.FindByHashAsync(hash, ct);

        // Importação interrompida desta mesma pasta (falha ao mover): retoma em vez de tratar como duplicata.
        var interrupted = existing.FirstOrDefault(v =>
            v.State == VideoState.Importing &&
            string.Equals(workspace.ToAbsolute(v.RelativePath), Path.GetFullPath(folderPath), StringComparison.OrdinalIgnoreCase));
        if (interrupted is not null)
        {
            return await MoveToScheduledAsync(interrupted.Id, folderPath, folderName, ct);
        }

        if (existing.Count > 0 && !inspection.ReimportRequested)
        {
            var first = existing.OrderBy(v => v.ImportedAtUtc).First();
            await store.ReportIssueAsync(folderName, ImportIssueCode.Duplicate,
                $"Este vídeo já foi importado como \"{first.FolderName}\" em {first.ImportedAtUtc:yyyy-MM-dd HH:mm} UTC. " +
                $"Para reimportar de propósito, crie um arquivo vazio chamado {FolderInspector.ReimportMarker} dentro da pasta.",
                null, ct);
            return ImportOutcome.Duplicate;
        }

        var analysis = result.Analysis!;
        var now = time.GetUtcNow().UtcDateTime;
        var destination = workspace.UniqueDestination(WorkspaceFolder.Scheduled, folderName);
        var video = new Video
        {
            Id = Guid.NewGuid(),
            FolderName = folderName,
            SequenceNumber = NaturalStringComparer.FirstNumber(folderName),
            ContentHash = hash,
            Generation = existing.Count == 0 ? 1 : existing.Max(v => v.Generation) + 1,
            State = VideoState.Importing,
            CurrentFolder = WorkspaceFolder.Inbox,
            RelativePath = workspace.ToRelative(folderPath),
            PendingMovePath = workspace.ToRelative(destination),
            VideoFileName = Path.GetFileName(videoPath),
            CaptionFileName = Path.GetFileName(inspection.CaptionFile!),
            ThumbnailFileName = inspection.ThumbnailFile is null ? null : Path.GetFileName(inspection.ThumbnailFile),
            CaptionText = caption,
            FileSizeBytes = new FileInfo(videoPath).Length,
            DurationSeconds = analysis.DurationSeconds,
            Width = analysis.Width,
            Height = analysis.Height,
            VideoCodec = analysis.VideoCodec,
            AudioCodec = analysis.AudioCodec,
            FrameRate = analysis.FrameRate,
            BitRate = analysis.BitRate,
            FormatName = analysis.FormatName,
            ImportedAtUtc = now,
            UpdatedAtUtc = now,
        };

        await store.AddAsync(video, ct);
        return await MoveAsync(video.Id, folderPath, destination, folderName, ct);
    }

    /// <summary>Conclui importações interrompidas (queda de energia, serviço encerrado) antes de novas varreduras.</summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        foreach (var video in await store.GetImportingAsync(ct))
        {
            var source = workspace.ToAbsolute(video.RelativePath);
            var destination = video.PendingMovePath is null ? null : workspace.ToAbsolute(video.PendingMovePath);

            if (destination is not null && Directory.Exists(destination) && !Directory.Exists(source))
            {
                await FinishAsync(video.Id, destination, video.FolderName, ct);
                logger.LogInformation("Importação de {Folder} concluída na recuperação (pasta já estava em AGENDADOS).", video.FolderName);
            }
            else if (Directory.Exists(source))
            {
                var outcome = await MoveToScheduledAsync(video.Id, source, video.FolderName, ct);
                logger.LogInformation("Importação de {Folder} retomada na recuperação: {Outcome}.", video.FolderName, outcome);
            }
            else
            {
                await store.MarkMissingAsync(video.Id, $"Pasta não encontrada em \"{video.RelativePath}\" nem no destino previsto.", ct);
                logger.LogWarning("Pasta de {Folder} não encontrada na recuperação; marcada como ausente.", video.FolderName);
            }
        }
    }

    private async Task<ImportOutcome> MoveToScheduledAsync(Guid videoId, string source, string folderName, CancellationToken ct)
    {
        var destination = workspace.UniqueDestination(WorkspaceFolder.Scheduled, folderName);
        await store.SetPendingMoveAsync(videoId, workspace.ToRelative(destination), ct);
        return await MoveAsync(videoId, source, destination, folderName, ct);
    }

    private async Task<ImportOutcome> MoveAsync(Guid videoId, string source, string destination, string folderName, CancellationToken ct)
    {
        try
        {
            Directory.Move(source, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await store.ReportIssueAsync(folderName, ImportIssueCode.MoveFailed,
                $"Não foi possível mover a pasta para AGENDADOS: {ex.Message}", null, ct);
            return ImportOutcome.MoveFailed;
        }

        await FinishAsync(videoId, destination, folderName, ct);
        return ImportOutcome.Imported;
    }

    private async Task FinishAsync(Guid videoId, string destination, string folderName, CancellationToken ct)
    {
        await store.CompleteImportAsync(videoId, workspace.ToRelative(destination), ct);
        await store.ResolveIssuesAsync(folderName, ct);

        // O marcador é um arquivo de controle do sistema, não um original do usuário.
        var marker = Path.Combine(destination, FolderInspector.ReimportMarker);
        try
        {
            File.Delete(marker);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Não foi possível remover {Marker}.", marker);
        }
    }

    private async Task<ImportOutcome> RejectAsync(string folderPath, string folderName, string reason, CancellationToken ct)
    {
        var destination = workspace.UniqueDestination(WorkspaceFolder.Errors, folderName);
        string? movedTo = null;
        try
        {
            Directory.Move(folderPath, destination);
            movedTo = workspace.ToRelative(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Não foi possível mover {Folder} para ERROS.", folderName);
        }

        await store.ReportIssueAsync(folderName, ImportIssueCode.CorruptVideo, reason, movedTo, ct);
        return ImportOutcome.Rejected;
    }
}
