using Bast.Application;
using Bast.Application.Import;
using Bast.Domain;

namespace Bast.Tests;

/// <summary>
/// Verticais no modo de horários fixos: cada vídeo pega o próximo horário livre de 10:00, 16:00 e 18:00,
/// um por horário; dia cheio vai para o dia seguinte. O horário ainda pode ser mudado à mão no painel.
/// </summary>
public class FixedTimesTests
{
    // Relógio inicial: sábado, 14/11/2026 09:00 em São Paulo.
    private static DateTime At(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0);

    private static EngineHarness Harness() => new(ScheduleMode.FixedTimes);

    [Fact]
    public async Task Verticals_fill_the_fixed_times_one_by_one_and_spill_into_the_next_day()
    {
        using var h = Harness();
        await h.ImportAsync("Video 1", "Video 2", "Video 3", "Video 4", "Video 5");

        await h.CycleAsync();

        Assert.Equal(
            [EngineHarness.Local(14, 10), EngineHarness.Local(14, 16), EngineHarness.Local(14, 18),
             EngineHarness.Local(15, 10), EngineHarness.Local(15, 16)],
            Enumerable.Range(1, 5).Select(i => h.Video($"Video {i}").ScheduledAtUtc!.Value).ToArray());
        Assert.All(h.Videos(), v => Assert.Equal(4, v.Publications.Count));
    }

    [Fact]
    public async Task A_video_dropped_later_takes_the_next_free_time_and_published_ones_still_count_for_the_day()
    {
        using var h = Harness();
        await h.ImportAsync("Video 1");
        await h.CycleAsync();
        Assert.Equal(EngineHarness.Local(14, 10), h.Video("Video 1").ScheduledAtUtc);

        h.SetTime(EngineHarness.Local(14, 10));
        await h.CycleAsync();
        Assert.Equal(VideoState.Published, h.Video("Video 1").State);

        // Às 15:00 chegam mais três: 16:00 e 18:00 de hoje, e o terceiro vai para amanhã às 10:00.
        h.SetTime(EngineHarness.Local(14, 15));
        await h.ImportAsync("Video 2", "Video 3", "Video 4");
        await h.CycleAsync();
        Assert.Equal(EngineHarness.Local(14, 16), h.Video("Video 2").ScheduledAtUtc);
        Assert.Equal(EngineHarness.Local(14, 18), h.Video("Video 3").ScheduledAtUtc);
        Assert.Equal(EngineHarness.Local(15, 10), h.Video("Video 4").ScheduledAtUtc);
    }

    [Fact]
    public async Task A_time_changed_by_hand_counts_for_the_day_and_the_fourth_video_of_a_day_is_refused()
    {
        using var h = Harness();
        await h.ImportAsync("Video 1", "Video 2");
        await h.CycleAsync();

        // O usuário muda o Video 2 (16:00) para 12:30: o dia fica com 10:00 e 12:30, e o próximo pega 16:00.
        var moved = await h.Scheduling.SetTimeAsync(h.Video("Video 2").Id, At(14, 12, 30), default);
        Assert.True(moved.Ok, moved.Message);
        await h.ImportAsync("Video 3", "Video 4");
        await h.CycleAsync();
        Assert.Equal(EngineHarness.Local(14, 16), h.Video("Video 3").ScheduledAtUtc);
        Assert.Equal(EngineHarness.Local(15, 10), h.Video("Video 4").ScheduledAtUtc); // dia 14 já tem 3

        var fourth = await h.Scheduling.SetTimeAsync(h.Video("Video 4").Id, At(14, 21), default);
        Assert.False(fourth.Ok);
        Assert.Contains("máximo é 3", fourth.Message);
    }

    [Fact]
    public async Task Missed_times_move_to_the_next_free_fixed_times_without_publishing_in_a_burst()
    {
        using var h = Harness();
        await h.ImportAsync("Video 1", "Video 2", "Video 3");
        await h.CycleAsync();

        // Computador desligado das 09:00 às 17:00: 10:00 e 16:00 passaram.
        h.SetTime(EngineHarness.Local(14, 17));
        await h.CycleAsync();

        Assert.Equal(0, h.TotalPublishCalls);
        Assert.Equal(EngineHarness.Local(14, 18), h.Video("Video 1").ScheduledAtUtc);
        Assert.Equal(EngineHarness.Local(15, 10), h.Video("Video 2").ScheduledAtUtc);
        Assert.Equal(EngineHarness.Local(15, 16), h.Video("Video 3").ScheduledAtUtc);
        Assert.All(h.Video("Video 2").Publications, p => Assert.Equal(EngineHarness.Local(15, 10), p.ScheduledAtUtc));
    }

    [Fact]
    public async Task Disabled_weekdays_are_skipped()
    {
        using var h = Harness();
        var problems = await h.Commands.UpdateSettingsAsync(new Bast.Application.Publishing.ScheduleSettingsInput(
            "10:00", "23:59", 240, 10, [DayOfWeek.Monday], "America/Sao_Paulo",
            [Platform.Instagram, Platform.Facebook, Platform.Threads, Platform.YouTube], MissedSlotPolicy.NextAllowedSlot, 30, 5,
            ScheduleMode.FixedTimes, VerticalSlots: "10:00"), default);
        Assert.Empty(problems);
        await h.ImportAsync("Video 1", "Video 2");

        await h.CycleAsync();

        // Sábado 14: só segunda-feira está marcada, um horário por dia.
        Assert.Equal(EngineHarness.Local(16, 10), h.Video("Video 1").ScheduledAtUtc);
        Assert.Equal(EngineHarness.Local(23, 10), h.Video("Video 2").ScheduledAtUtc);
    }

    [Fact]
    public async Task Verticals_and_horizontals_never_share_a_minute()
    {
        using var h = Harness();
        var problems = await h.Commands.UpdateSettingsAsync(new Bast.Application.Publishing.ScheduleSettingsInput(
            "10:00", "23:59", 240, 10, Enum.GetValues<DayOfWeek>(), "America/Sao_Paulo",
            [Platform.Instagram, Platform.Facebook, Platform.Threads, Platform.YouTube], MissedSlotPolicy.NextAllowedSlot, 30, 5,
            ScheduleMode.FixedTimes, VerticalSlots: "11:00,16:00", HorizontalSlots: "11:00,14:00"), default);
        Assert.Empty(problems);
        await h.ImportAsync("Video 1");
        await h.CycleAsync();
        Assert.Equal(EngineHarness.Local(14, 11), h.Video("Video 1").ScheduledAtUtc);

        h.Env.Analyzer.Handler = path => VideoAnalysisResult.Ok(new VideoAnalysis(600, 1920, 1080, "h264", "aac", 30, 8_000_000, "mov,mp4"));
        var folder = Path.Combine(h.Env.Workspace.PathOf(WorkspaceFolder.YouTubeInbox), "Longo 1");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "video.mp4"), "horizontal");
        File.WriteAllText(Path.Combine(folder, "legenda.txt"), "Título\nDescrição.");
        File.WriteAllBytes(Path.Combine(folder, "thumbnail.jpg"), [0xFF, 0xD8, 0xFF]);
        await h.Env.Service.ProcessFolderAsync(folder, default, VideoLane.YouTubeHorizontal);
        await h.CycleAsync();

        Assert.Equal(EngineHarness.Local(14, 14), h.Video("Longo 1").ScheduledAtUtc);
    }
}

public class WorkspaceRenameTests
{
    [Fact]
    public void Old_inbox_is_renamed_to_the_vertical_inbox_keeping_its_folders()
    {
        var root = Path.Combine(Path.GetTempPath(), "bast-ws-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, Workspace.LegacyInboxName, "Video 1"));
            File.WriteAllText(Path.Combine(root, Workspace.LegacyInboxName, "Video 1", "legenda.txt"), "oi");

            var workspace = new Workspace(root);
            workspace.EnsureCreated();

            Assert.False(Directory.Exists(Path.Combine(root, Workspace.LegacyInboxName)));
            Assert.EndsWith("01 - ENVIAR VERTICAL", workspace.PathOf(WorkspaceFolder.Inbox));
            Assert.True(File.Exists(Path.Combine(workspace.PathOf(WorkspaceFolder.Inbox), "Video 1", "legenda.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void When_both_exist_the_old_content_moves_over_with_unique_names()
    {
        var root = Path.Combine(Path.GetTempPath(), "bast-ws-" + Guid.NewGuid().ToString("N"));
        try
        {
            var workspace = new Workspace(root);
            Directory.CreateDirectory(Path.Combine(workspace.PathOf(WorkspaceFolder.Inbox), "Video 1"));
            Directory.CreateDirectory(Path.Combine(root, Workspace.LegacyInboxName, "Video 1"));
            Directory.CreateDirectory(Path.Combine(root, Workspace.LegacyInboxName, "Video 2"));

            workspace.EnsureCreated();

            Assert.False(Directory.Exists(Path.Combine(root, Workspace.LegacyInboxName)));
            var names = Directory.GetDirectories(workspace.PathOf(WorkspaceFolder.Inbox)).Select(p => Path.GetFileName(p)!).Order().ToArray();
            Assert.Equal(["Video 1", "Video 1 (2)", "Video 2"], names);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public class VerticalCoverImportTests
{
    [Fact]
    public async Task Vertical_cover_is_kept_and_the_reference_image_is_never_taken_as_the_cover()
    {
        using var h = new EngineHarness(ScheduleMode.FixedTimes);
        var onlyReference = h.Env.CreateFolder("Video 1", videoContent: "conteudo-1");
        File.WriteAllBytes(Path.Combine(onlyReference, "referencia.png"), [0x89, 0x50, 0x4E, 0x47]);
        var withCover = h.Env.CreateFolder("Video 2", videoContent: "conteudo-2");
        File.WriteAllBytes(Path.Combine(withCover, "referencia.png"), [0x89, 0x50, 0x4E, 0x47]);
        File.WriteAllBytes(Path.Combine(withCover, "thumbnail.jpg"), [0xFF, 0xD8, 0xFF]);

        Assert.Equal(ImportOutcome.Imported, await h.Env.Service.ProcessFolderAsync(onlyReference, default));
        Assert.Equal(ImportOutcome.Imported, await h.Env.Service.ProcessFolderAsync(withCover, default));

        Assert.Null(h.Video("Video 1").ThumbnailFileName);
        Assert.Equal("thumbnail.jpg", h.Video("Video 2").ThumbnailFileName);
    }
}
