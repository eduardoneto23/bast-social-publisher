using System.Text;
using Bast.Application;
using Bast.Application.Import;
using Bast.Domain;

namespace Bast.Tests;

public class StabilityTrackerTests
{
    [Fact]
    public void Folder_is_stable_only_after_window_without_changes()
    {
        using var env = new TestEnvironment();
        var folder = env.CreateFolder("Video 001");
        var tracker = new StabilityTracker(env.Time, TimeSpan.FromSeconds(10));

        Assert.False(tracker.IsStable(folder, out _));
        env.Time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(tracker.IsStable(folder, out _));

        // Cópia ainda em andamento: o arquivo cresce e a janela recomeça.
        File.AppendAllText(Path.Combine(folder, "video.mp4"), "mais-bytes");
        env.Time.Advance(TimeSpan.FromSeconds(6));
        Assert.False(tracker.IsStable(folder, out _));

        env.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.True(tracker.IsStable(folder, out _));
    }

    [Fact]
    public void Folder_with_file_open_for_writing_is_not_stable()
    {
        using var env = new TestEnvironment();
        var folder = env.CreateFolder("Video 001");
        var tracker = new StabilityTracker(env.Time, TimeSpan.FromSeconds(10));
        tracker.IsStable(folder, out _);
        env.Time.Advance(TimeSpan.FromSeconds(11));

        using (new FileStream(Path.Combine(folder, "video.mp4"), FileMode.Open, FileAccess.Write, FileShare.None))
        {
            Assert.False(tracker.IsStable(folder, out _));
        }

        Assert.True(tracker.IsStable(folder, out _));
    }
}

public class CaptionReaderTests
{
    [Fact]
    public void Preserves_emojis_line_breaks_hashtags_and_punctuation()
    {
        using var env = new TestEnvironment();
        const string text = "GTA 6 VAI TER POSTER NA VERSÃO FÍSICA!\r\n\r\nE você, curtiu? 🎮🔥\r\n#GTA6 #Bast\r\n";
        var path = Path.Combine(env.Root, "legenda.txt");

        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Equal(text, CaptionReader.Read(path));

        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Assert.Equal(text, CaptionReader.Read(path));

        File.WriteAllText(path, text, Encoding.Unicode);
        Assert.Equal(text, CaptionReader.Read(path));
    }

    [Fact]
    public void Reads_legacy_ansi_file()
    {
        using var env = new TestEnvironment();
        var path = Path.Combine(env.Root, "legenda.txt");
        File.WriteAllBytes(path, [0x56, 0x45, 0x52, 0x53, 0xC3, 0x4F]); // "VERSÃO" em Windows-1252
        Assert.Equal("VERSÃO", CaptionReader.Read(path));
    }
}

public class ImportServiceTests
{
    [Fact]
    public async Task Valid_folder_is_registered_and_moved_to_scheduled_untouched()
    {
        using var env = new TestEnvironment();
        var folder = env.CreateFolder("Video 001");

        var outcome = await env.Service.ProcessFolderAsync(folder, default);

        Assert.Equal(ImportOutcome.Imported, outcome);
        Assert.False(Directory.Exists(folder));
        var video = Assert.Single(env.Videos());
        Assert.Equal(VideoState.Ready, video.State);
        Assert.Equal(WorkspaceFolder.Scheduled, video.CurrentFolder);
        Assert.Equal(1, video.Generation);
        Assert.Equal(1L, video.SequenceNumber);
        Assert.Null(video.PendingMovePath);
        Assert.Equal("TÍTULO\nDescrição 🎮\n#Bast", video.CaptionText);
        Assert.Equal(64, video.ContentHash.Length);

        var moved = env.Workspace.ToAbsolute(video.RelativePath);
        Assert.StartsWith(env.Workspace.PathOf(WorkspaceFolder.Scheduled), moved);
        Assert.Equal("video-bytes", File.ReadAllText(Path.Combine(moved, "video.mp4")));
        Assert.Equal("TÍTULO\nDescrição 🎮\n#Bast", File.ReadAllText(Path.Combine(moved, "legenda.txt")));
    }

    [Fact]
    public async Task Missing_caption_keeps_folder_in_inbox_and_reports_issue_until_fixed()
    {
        using var env = new TestEnvironment();
        var folder = env.CreateFolder("Video 001", caption: null);

        Assert.Equal(ImportOutcome.Incomplete, await env.Service.ProcessFolderAsync(folder, default));
        Assert.Equal(ImportOutcome.Incomplete, await env.Service.ProcessFolderAsync(folder, default));

        Assert.True(Directory.Exists(folder));
        Assert.Empty(env.Videos());
        Assert.Equal(ImportIssueCode.MissingCaption, Assert.Single(env.OpenIssues()).Code);

        File.WriteAllText(Path.Combine(folder, "legenda.txt"), "Legenda");
        Assert.Equal(ImportOutcome.Imported, await env.Service.ProcessFolderAsync(folder, default));
        Assert.Empty(env.OpenIssues());
    }

    [Fact]
    public async Task Empty_caption_and_missing_video_are_not_imported()
    {
        using var env = new TestEnvironment();
        var empty = env.CreateFolder("Video 001", caption: "   \r\n");
        var noVideo = env.CreateFolder("Video 002", videoContent: null);

        Assert.Equal(ImportOutcome.Incomplete, await env.Service.ProcessFolderAsync(empty, default));
        Assert.Equal(ImportOutcome.Incomplete, await env.Service.ProcessFolderAsync(noVideo, default));

        Assert.Empty(env.Videos());
        Assert.Equal(
            [ImportIssueCode.MissingVideo, ImportIssueCode.EmptyCaption],
            env.OpenIssues().Select(i => i.Code).Order().ToArray());
    }

    [Fact]
    public async Task Corrupt_video_goes_to_errors_and_is_not_registered()
    {
        using var env = new TestEnvironment();
        env.Analyzer.Handler = _ => VideoAnalysisResult.Invalid("moov atom not found");
        var folder = env.CreateFolder("Video 001");

        Assert.Equal(ImportOutcome.Rejected, await env.Service.ProcessFolderAsync(folder, default));

        Assert.False(Directory.Exists(folder));
        Assert.True(Directory.Exists(Path.Combine(env.Workspace.PathOf(WorkspaceFolder.Errors), "Video 001")));
        Assert.Empty(env.Videos());
        using var db = env.DbFactory.CreateDbContext();
        var issue = Assert.Single(db.ImportIssues);
        Assert.Equal(ImportIssueCode.CorruptVideo, issue.Code);
        Assert.NotNull(issue.MovedToPath);
    }

    [Fact]
    public async Task Unavailable_tools_never_reject_or_import()
    {
        using var env = new TestEnvironment();
        env.Analyzer.Handler = _ => VideoAnalysisResult.Unavailable("ffprobe ausente");
        var folder = env.CreateFolder("Video 001");

        Assert.Equal(ImportOutcome.ToolUnavailable, await env.Service.ProcessFolderAsync(folder, default));

        Assert.True(Directory.Exists(folder));
        Assert.Empty(env.Videos());
    }

    [Fact]
    public async Task Same_content_under_another_name_is_a_duplicate()
    {
        using var env = new TestEnvironment();
        await env.Service.ProcessFolderAsync(env.CreateFolder("Video 001"), default);
        var copy = env.CreateFolder("Outro nome qualquer");

        Assert.Equal(ImportOutcome.Duplicate, await env.Service.ProcessFolderAsync(copy, default));

        Assert.True(Directory.Exists(copy));
        Assert.Single(env.Videos());
        Assert.Equal(ImportIssueCode.Duplicate, Assert.Single(env.OpenIssues()).Code);
    }

    [Fact]
    public async Task Same_name_with_different_content_is_not_a_duplicate()
    {
        using var env = new TestEnvironment();
        await env.Service.ProcessFolderAsync(env.CreateFolder("Video 001", videoContent: "conteudo-a"), default);
        var second = env.CreateFolder("Video 001", videoContent: "conteudo-b");

        Assert.Equal(ImportOutcome.Imported, await env.Service.ProcessFolderAsync(second, default));

        var videos = env.Videos();
        Assert.Equal(2, videos.Count);
        Assert.Equal(2, videos.Select(v => v.RelativePath).Distinct().Count());
        Assert.All(videos, v => Assert.True(Directory.Exists(env.Workspace.ToAbsolute(v.RelativePath))));
    }

    [Fact]
    public async Task Intentional_reimport_creates_a_new_generation()
    {
        using var env = new TestEnvironment();
        await env.Service.ProcessFolderAsync(env.CreateFolder("Video 001"), default);
        var again = env.CreateFolder("Video 001");
        File.WriteAllText(Path.Combine(again, FolderInspector.ReimportMarker), "");

        Assert.Equal(ImportOutcome.Imported, await env.Service.ProcessFolderAsync(again, default));

        var videos = env.Videos().OrderBy(v => v.Generation).ToList();
        Assert.Equal([1, 2], videos.Select(v => v.Generation).ToArray());
        Assert.NotEqual(videos[0].Id, videos[1].Id);
        Assert.Equal(videos[0].ContentHash, videos[1].ContentHash);
        Assert.False(File.Exists(Path.Combine(env.Workspace.ToAbsolute(videos[1].RelativePath), FolderInspector.ReimportMarker)));
    }

    [Fact]
    public async Task Several_folders_are_all_imported()
    {
        using var env = new TestEnvironment();
        var folders = Enumerable.Range(1, 12).Select(i => env.CreateFolder($"Video {i}", videoContent: $"conteudo-{i}")).ToList();

        foreach (var folder in folders.OrderBy(Path.GetFileName, NaturalStringComparer.Instance))
        {
            Assert.Equal(ImportOutcome.Imported, await env.Service.ProcessFolderAsync(folder, default));
        }

        Assert.Equal(12, env.Videos().Count);
        Assert.Empty(Directory.GetDirectories(env.Inbox));
        Assert.Equal(Enumerable.Range(1, 12).Select(i => (long?)i), env.Videos().Select(v => v.SequenceNumber).Order());
    }

    [Fact]
    public async Task Crash_after_registering_and_before_moving_is_finished_on_restart_without_duplicating()
    {
        using var env = new TestEnvironment();
        var folder = env.CreateFolder("Video 001");
        await env.Store.AddAsync(ImportingRow(env, folder), default);

        await env.NewService().RecoverAsync(default);

        var video = Assert.Single(env.Videos());
        Assert.Equal(VideoState.Ready, video.State);
        Assert.False(Directory.Exists(folder));
        Assert.True(Directory.Exists(env.Workspace.ToAbsolute(video.RelativePath)));
    }

    [Fact]
    public async Task Crash_after_moving_and_before_confirming_is_finished_on_restart()
    {
        using var env = new TestEnvironment();
        var folder = env.CreateFolder("Video 001");
        var row = ImportingRow(env, folder);
        await env.Store.AddAsync(row, default);
        Directory.Move(folder, env.Workspace.ToAbsolute(row.PendingMovePath!));

        await env.NewService().RecoverAsync(default);

        var video = Assert.Single(env.Videos());
        Assert.Equal(VideoState.Ready, video.State);
        Assert.Equal(row.PendingMovePath, video.RelativePath);
    }

    [Fact]
    public async Task Interrupted_import_found_by_scan_is_resumed_not_flagged_as_duplicate()
    {
        using var env = new TestEnvironment();
        var folder = env.CreateFolder("Video 001");
        var row = ImportingRow(env, folder);
        row.ContentHash = await FileHasher.Sha256Async(Path.Combine(folder, "video.mp4"), default);
        await env.Store.AddAsync(row, default);

        Assert.Equal(ImportOutcome.Imported, await env.Service.ProcessFolderAsync(folder, default));

        Assert.Equal(VideoState.Ready, Assert.Single(env.Videos()).State);
        Assert.Empty(env.OpenIssues());
    }

    [Fact]
    public async Task Folder_vanished_during_import_is_marked_missing()
    {
        using var env = new TestEnvironment();
        var folder = env.CreateFolder("Video 001");
        await env.Store.AddAsync(ImportingRow(env, folder), default);
        Directory.Delete(folder, recursive: true);

        await env.NewService().RecoverAsync(default);

        Assert.Equal(VideoState.Missing, Assert.Single(env.Videos()).State);
    }

    [Fact]
    public async Task Database_refuses_same_content_and_generation_twice()
    {
        using var env = new TestEnvironment();
        var folder = env.CreateFolder("Video 001");
        await env.Store.AddAsync(ImportingRow(env, folder), default);

        await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(
            () => env.Store.AddAsync(ImportingRow(env, folder), default));
    }

    private static Video ImportingRow(TestEnvironment env, string folder) => new()
    {
        Id = Guid.NewGuid(),
        FolderName = Path.GetFileName(folder),
        ContentHash = new string('a', 64),
        Generation = 1,
        State = VideoState.Importing,
        CurrentFolder = WorkspaceFolder.Inbox,
        RelativePath = env.Workspace.ToRelative(folder),
        PendingMovePath = env.Workspace.ToRelative(Path.Combine(env.Workspace.PathOf(WorkspaceFolder.Scheduled), Path.GetFileName(folder))),
        VideoFileName = "video.mp4",
        CaptionFileName = "legenda.txt",
        CaptionText = "x",
    };
}
