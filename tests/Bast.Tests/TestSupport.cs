using Bast.Application;
using Bast.Application.Import;
using Bast.Domain;
using Bast.Application.Persistence;
using Bast.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bast.Tests;

public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

public sealed class FakeAnalyzer : IVideoAnalyzer
{
    public Func<string, VideoAnalysisResult> Handler { get; set; } =
        _ => VideoAnalysisResult.Ok(new VideoAnalysis(30, 1080, 1920, "h264", "aac", 30, 5_000_000, "mov,mp4"));

    public Task<VideoAnalysisResult> AnalyzeAsync(string videoPath, CancellationToken ct) => Task.FromResult(Handler(videoPath));
}

/// <summary>Pasta principal temporária com banco SQLite real (migrações aplicadas).</summary>
public sealed class TestEnvironment : IDisposable
{
    private sealed class Factory(DbContextOptions<BastDbContext> options) : IDbContextFactory<BastDbContext>
    {
        public BastDbContext CreateDbContext() => new(options);
    }

    public TestEnvironment()
    {
        Root = Path.Combine(Path.GetTempPath(), "bast-tests", Guid.NewGuid().ToString("N"));
        Workspace = new Workspace(Root);
        Workspace.EnsureCreated();
        DbFactory = new Factory(((DbContextOptionsBuilder<BastDbContext>)new DbContextOptionsBuilder<BastDbContext>().UseBastSqlite(Workspace.DatabasePath)).Options);
        using (var db = DbFactory.CreateDbContext())
        {
            db.Database.Migrate();
        }

        Store = new EfImportStore(DbFactory, Time);
        Service = NewService();
    }

    public string Root { get; }
    public Workspace Workspace { get; }
    public ManualTimeProvider Time { get; } = new();
    public FakeAnalyzer Analyzer { get; } = new();
    public IDbContextFactory<BastDbContext> DbFactory { get; }
    public EfImportStore Store { get; }
    public ImportService Service { get; }

    public string Inbox => Workspace.PathOf(WorkspaceFolder.Inbox);

    /// <summary>Simula um novo processo após reinício: mesmo banco e pastas, serviço novo.</summary>
    public ImportService NewService() =>
        new(Workspace, new FolderInspector(new BastOptions()), Analyzer, Store, Time, NullLogger<ImportService>.Instance);

    public string CreateFolder(string name, string? videoContent = "video-bytes", string? caption = "TÍTULO\nDescrição 🎮\n#Bast")
    {
        var folder = Path.Combine(Inbox, name);
        Directory.CreateDirectory(folder);
        if (videoContent is not null) File.WriteAllText(Path.Combine(folder, "video.mp4"), videoContent);
        if (caption is not null) File.WriteAllText(Path.Combine(folder, "legenda.txt"), caption);
        return folder;
    }

    public List<Video> Videos()
    {
        using var db = DbFactory.CreateDbContext();
        return db.Videos.AsNoTracking().ToList();
    }

    public List<ImportIssue> OpenIssues()
    {
        using var db = DbFactory.CreateDbContext();
        return db.ImportIssues.AsNoTracking().Where(i => i.ResolvedAtUtc == null).ToList();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
