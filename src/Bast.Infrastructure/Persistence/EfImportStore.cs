using Bast.Application.Import;
using Bast.Application.Persistence;
using Bast.Domain;
using Microsoft.EntityFrameworkCore;

namespace Bast.Infrastructure.Persistence;

public sealed class EfImportStore(IDbContextFactory<BastDbContext> factory, TimeProvider time) : IImportStore
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<Video>> FindByHashAsync(string contentHash, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Videos.AsNoTracking().Where(v => v.ContentHash == contentHash).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Video>> GetImportingAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Videos.AsNoTracking().Where(v => v.State == VideoState.Importing).ToListAsync(ct);
    }

    public async Task AddAsync(Video video, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Videos.Add(video);
        await db.SaveChangesAsync(ct);
    }

    public Task SetPendingMoveAsync(Guid videoId, string pendingMovePath, CancellationToken ct) =>
        UpdateVideoAsync(videoId, v => v.PendingMovePath = pendingMovePath, ct);

    public Task CompleteImportAsync(Guid videoId, string relativePath, CancellationToken ct) =>
        UpdateVideoAsync(videoId, v =>
        {
            v.State = VideoState.Ready;
            v.CurrentFolder = WorkspaceFolder.Scheduled;
            v.RelativePath = relativePath;
            v.PendingMovePath = null;
        }, ct);

    public Task MarkMissingAsync(Guid videoId, string note, CancellationToken ct) =>
        UpdateVideoAsync(videoId, v =>
        {
            v.State = VideoState.Missing;
            v.PendingMovePath = null;
            v.Note = note;
        }, ct);

    public async Task ReportIssueAsync(string folderName, ImportIssueCode code, string message, string? movedToPath, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = Now;
        var issue = await db.ImportIssues.FirstOrDefaultAsync(
            i => i.FolderName == folderName && i.Code == code && i.ResolvedAtUtc == null && i.MovedToPath == null, ct);

        if (issue is null)
        {
            db.ImportIssues.Add(new ImportIssue
            {
                Id = Guid.NewGuid(),
                FolderName = folderName,
                Code = code,
                Message = message,
                MovedToPath = movedToPath,
                FirstSeenAtUtc = now,
                LastSeenAtUtc = now,
            });
        }
        else
        {
            issue.Message = message;
            issue.MovedToPath = movedToPath;
            issue.LastSeenAtUtc = now;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task ResolveIssuesAsync(string folderName, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = Now;
        await db.ImportIssues
            .Where(i => i.FolderName == folderName && i.ResolvedAtUtc == null && i.MovedToPath == null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.ResolvedAtUtc, now), ct);
    }

    public async Task ResolveIssuesForAbsentFoldersAsync(IReadOnlyCollection<string> presentFolderNames, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = Now;
        var open = await db.ImportIssues.Where(i => i.ResolvedAtUtc == null && i.MovedToPath == null).ToListAsync(ct);
        var present = new HashSet<string>(presentFolderNames, StringComparer.OrdinalIgnoreCase);
        foreach (var issue in open.Where(i => !present.Contains(i.FolderName)))
        {
            issue.ResolvedAtUtc = now;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task UpdateVideoAsync(Guid videoId, Action<Video> change, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var video = await db.Videos.FirstAsync(v => v.Id == videoId, ct);
        change(video);
        video.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(ct);
    }
}
