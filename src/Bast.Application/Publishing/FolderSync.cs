using Bast.Application.Persistence;
using Bast.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Bast.Application.Publishing;

/// <summary>
/// Faz as pastas refletirem o estado gravado no banco. O banco é a fonte oficial: se uma pasta
/// não puder ser movida agora (arquivo aberto em outro programa), a publicação segue e a
/// movimentação é tentada de novo no próximo ciclo.
/// </summary>
public sealed class FolderSync(IDbContextFactory<BastDbContext> factory, Workspace workspace, TimeProvider time, ILogger<FolderSync> logger)
{
    public static WorkspaceFolder? FolderFor(VideoState state) => state switch
    {
        VideoState.Ready => WorkspaceFolder.Scheduled,
        VideoState.Publishing => WorkspaceFolder.Publishing,
        VideoState.Published => WorkspaceFolder.Sent,
        VideoState.Error => WorkspaceFolder.Errors,
        VideoState.Archived => WorkspaceFolder.Archived,
        _ => null,
    };

    public async Task SyncAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var videos = await db.Videos
            .Where(v => v.State != VideoState.Importing && v.State != VideoState.Missing)
            .Where(v => v.PendingMovePath != null
                        || (v.State == VideoState.Ready && v.CurrentFolder != WorkspaceFolder.Scheduled)
                        || (v.State == VideoState.Publishing && v.CurrentFolder != WorkspaceFolder.Publishing)
                        || (v.State == VideoState.Published && v.CurrentFolder != WorkspaceFolder.Sent)
                        || (v.State == VideoState.Error && v.CurrentFolder != WorkspaceFolder.Errors)
                        || (v.State == VideoState.Archived && v.CurrentFolder != WorkspaceFolder.Archived))
            .ToListAsync(ct);

        foreach (var video in videos)
        {
            await SyncOneAsync(db, video, ct);
        }
    }

    private async Task SyncOneAsync(BastDbContext db, Video video, CancellationToken ct)
    {
        var source = workspace.ToAbsolute(video.RelativePath);

        // Movimentação que ficou pela metade (queda entre gravar a intenção e confirmar).
        if (video.PendingMovePath is not null && video.PendingMoveFolder is not null)
        {
            var pending = workspace.ToAbsolute(video.PendingMovePath);
            if (!Directory.Exists(source) && Directory.Exists(pending))
            {
                await CompleteAsync(db, video, pending, video.PendingMoveFolder.Value, ct);
                source = pending;
            }
            else
            {
                video.PendingMovePath = null;
                video.PendingMoveFolder = null;
            }
        }

        var target = FolderFor(video.State);
        if (target is null || target == video.CurrentFolder)
        {
            await db.SaveChangesAsync(ct);
            return;
        }

        if (!Directory.Exists(source))
        {
            await HandleMissingAsync(db, video, target.Value, ct);
            return;
        }

        var destination = workspace.UniqueDestination(target.Value, video.FolderName);
        video.PendingMovePath = workspace.ToRelative(destination);
        video.PendingMoveFolder = target;
        await db.SaveChangesAsync(ct);

        try
        {
            Directory.Move(source, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Não foi possível mover {Folder} para {Target} agora ({Reason}); nova tentativa no próximo ciclo.",
                video.FolderName, target, ex.Message);
            return;
        }

        await CompleteAsync(db, video, destination, target.Value, ct);
        logger.LogInformation("{Folder}: pasta movida para {Target}.", video.FolderName, target);
    }

    private async Task CompleteAsync(BastDbContext db, Video video, string destination, WorkspaceFolder folder, CancellationToken ct)
    {
        video.RelativePath = workspace.ToRelative(destination);
        video.CurrentFolder = folder;
        video.PendingMovePath = null;
        video.PendingMoveFolder = null;
        video.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    private async Task HandleMissingAsync(BastDbContext db, Video video, WorkspaceFolder target, CancellationToken ct)
    {
        video.PendingMovePath = null;
        video.PendingMoveFolder = null;
        video.Note = $"Pasta não encontrada em \"{video.RelativePath}\" (movida ou apagada fora do sistema).";
        video.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;

        if (video.State is VideoState.Ready or VideoState.Publishing)
        {
            // Ainda havia o que publicar: exige intervenção.
            video.State = VideoState.Missing;
        }
        else
        {
            // Trabalho já encerrado: apenas registra que a pasta não está mais sob controle.
            video.CurrentFolder = target;
        }

        await db.SaveChangesAsync(ct);
        logger.LogWarning("{Folder}: pasta não encontrada em {Path}.", video.FolderName, video.RelativePath);
    }
}
