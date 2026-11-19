using Bast.Application.Import;
using Bast.Application.Publishing;
using Bast.Domain;
using Microsoft.EntityFrameworkCore;

namespace Bast.Tests;

/// <summary>Fila dos vídeos horizontais: pasta 01 - ENVIAR YOUTUBE, só YouTube, 7 janelas por dia das 06:00 às 23:00.</summary>
public class YouTubeHorizontalTests
{
    // Relógio inicial: 14/11/2026 09:00 em São Paulo. Janelas: 06:00, 08:50, 11:40, 14:30, 17:20, 20:10, 23:00.
    private static DateTime At(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0);

    private static EngineHarness Harness(params Platform[] connected)
    {
        var h = new EngineHarness(connected);
        h.Env.Analyzer.Handler = path => VideoAnalysisResult.Ok(path.Contains("ENVIAR YOUTUBE", StringComparison.OrdinalIgnoreCase)
            ? new VideoAnalysis(600, 1920, 1080, "h264", "aac", 30, 8_000_000, "mov,mp4")
            : new VideoAnalysis(30, 1080, 1920, "h264", "aac", 30, 5_000_000, "mov,mp4"));
        return h;
    }

    private static string HorizontalFolder(EngineHarness h, string name, bool thumbnail = true)
    {
        var folder = Path.Combine(h.Env.Workspace.PathOf(WorkspaceFolder.YouTubeInbox), name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "video.mp4"), "horizontal-" + name);
        File.WriteAllText(Path.Combine(folder, "legenda.txt"), $"Título {name}\nDescrição do vídeo longo.");
        if (thumbnail) File.WriteAllBytes(Path.Combine(folder, "thumbnail.jpg"), [0xFF, 0xD8, 0xFF]);
        return folder;
    }

    private static async Task ImportHorizontalAsync(EngineHarness h, params string[] names)
    {
        foreach (var name in names)
        {
            var outcome = await h.Env.Service.ProcessFolderAsync(HorizontalFolder(h, name), default, VideoLane.YouTubeHorizontal);
            Assert.Equal(ImportOutcome.Imported, outcome);
        }
    }

    [Fact]
    public void Seven_windows_from_6_to_23_are_split_evenly()
    {
        var times = SlotCalculator.HorizontalTimes(new ScheduleSettings());

        Assert.Equal(["06:00", "08:50", "11:40", "14:30", "17:20", "20:10", "23:00"], times.Select(t => t.ToString("HH:mm")).ToArray());
    }

    [Fact]
    public async Task Horizontal_video_goes_only_to_youtube_with_its_thumbnail_at_the_next_window()
    {
        using var h = Harness();
        await ImportHorizontalAsync(h, "Longo 1");

        await h.CycleAsync();

        var video = h.Video("Longo 1");
        Assert.Equal(VideoLane.YouTubeHorizontal, video.Lane);
        Assert.Equal("thumbnail.jpg", video.ThumbnailFileName);
        Assert.Equal(EngineHarness.Local(14, 11, 40), video.ScheduledAtUtc);
        var publication = Assert.Single(video.Publications);
        Assert.Equal(Platform.YouTube, publication.Platform);
        Assert.True(Directory.Exists(Path.Combine(h.Env.Workspace.PathOf(WorkspaceFolder.Scheduled), "Longo 1")));
    }

    [Fact]
    public async Task Each_video_takes_the_next_window_one_per_window_spilling_into_the_next_day()
    {
        using var h = Harness();
        await ImportHorizontalAsync(h, "Longo 1", "Longo 2", "Longo 3", "Longo 4", "Longo 5", "Longo 6");

        await h.CycleAsync();

        Assert.Equal(
            [EngineHarness.Local(14, 11, 40), EngineHarness.Local(14, 14, 30), EngineHarness.Local(14, 17, 20),
             EngineHarness.Local(14, 20, 10), EngineHarness.Local(14, 23, 0), EngineHarness.Local(15, 6, 0)],
            Enumerable.Range(1, 6).Select(i => h.Video($"Longo {i}").ScheduledAtUtc!.Value).ToArray());
    }

    [Fact]
    public async Task A_video_dropped_later_goes_after_the_last_reserved_window()
    {
        using var h = Harness();
        await ImportHorizontalAsync(h, "Longo 1");
        await h.CycleAsync();

        // Uma hora depois, outro vídeo: não ocupa a mesma janela, vai para a seguinte.
        h.SetTime(EngineHarness.Local(14, 10));
        await ImportHorizontalAsync(h, "Longo 2");
        await h.CycleAsync();
        Assert.Equal(EngineHarness.Local(14, 14, 30), h.Video("Longo 2").ScheduledAtUtc);

        // Depois que o primeiro saiu, o próximo vídeo ainda respeita a fila.
        h.SetTime(EngineHarness.Local(14, 11, 40));
        await h.CycleAsync();
        Assert.Equal(VideoState.Published, h.Video("Longo 1").State);
        await ImportHorizontalAsync(h, "Longo 3");
        await h.CycleAsync();
        Assert.Equal(EngineHarness.Local(14, 17, 20), h.Video("Longo 3").ScheduledAtUtc);
        Assert.Single(h.Connectors[Platform.YouTube].PublishCalls);
    }

    [Fact]
    public async Task Folder_without_thumbnail_waits_with_the_reason_and_imports_once_the_image_arrives()
    {
        using var h = Harness();
        var folder = HorizontalFolder(h, "Longo 1", thumbnail: false);

        var outcome = await h.Env.Service.ProcessFolderAsync(folder, default, VideoLane.YouTubeHorizontal);

        Assert.Equal(ImportOutcome.Incomplete, outcome);
        var issue = Assert.Single(h.Env.OpenIssues());
        Assert.Equal(ImportIssueCode.MissingThumbnail, issue.Code);
        Assert.True(Directory.Exists(folder));
        Assert.Empty(h.Env.Videos());

        File.WriteAllBytes(Path.Combine(folder, "thumbnail.png"), [0x89, 0x50, 0x4E, 0x47]);
        Assert.Equal(ImportOutcome.Imported, await h.Env.Service.ProcessFolderAsync(folder, default, VideoLane.YouTubeHorizontal));
        Assert.Empty(h.Env.OpenIssues());
    }

    [Fact]
    public async Task Vertical_video_in_the_youtube_folder_is_not_imported()
    {
        using var h = Harness();
        h.Env.Analyzer.Handler = _ => VideoAnalysisResult.Ok(new VideoAnalysis(30, 1080, 1920, "h264", "aac", 30, 5_000_000, "mov,mp4"));
        var folder = HorizontalFolder(h, "Curto 1");

        var outcome = await h.Env.Service.ProcessFolderAsync(folder, default, VideoLane.YouTubeHorizontal);

        Assert.Equal(ImportOutcome.Incomplete, outcome);
        var issue = Assert.Single(h.Env.OpenIssues());
        Assert.Equal(ImportIssueCode.NotHorizontal, issue.Code);
        Assert.Contains("1080x1920", issue.Message);
        Assert.Empty(h.Env.Videos());
    }

    [Fact]
    public async Task Main_queue_keeps_its_own_rules_and_slots()
    {
        using var h = Harness();
        await ImportHorizontalAsync(h, "Longo 1", "Longo 2");
        await h.ImportAsync("Video 1");

        await h.CycleAsync();

        // A fila principal continua no automático de 4 em 4 h, a partir das 10:00, em todas as redes.
        Assert.Equal(EngineHarness.Local(14, 10), h.Video("Video 1").ScheduledAtUtc);
        Assert.Equal(4, h.Video("Video 1").Publications.Count);
        Assert.Equal(VideoLane.Standard, h.Video("Video 1").Lane);
        Assert.Equal(EngineHarness.Local(14, 14, 30), h.Video("Longo 2").ScheduledAtUtc);
    }

    [Fact]
    public async Task Horizontal_queue_is_automatic_even_when_the_main_queue_is_manual()
    {
        using var h = Harness(Platform.YouTube, Platform.Instagram);
        await h.Commands.UpdateSettingsAsync(new ScheduleSettingsInput("10:00", "23:59", 240, 10, Enum.GetValues<DayOfWeek>(),
            "America/Sao_Paulo", [Platform.Instagram, Platform.YouTube], MissedSlotPolicy.NextAllowedSlot, 30, 5, ScheduleMode.Manual, 3), default);
        await ImportHorizontalAsync(h, "Longo 1");
        await h.ImportAsync("Video 1");

        await h.CycleAsync();

        Assert.Equal(EngineHarness.Local(14, 11, 40), h.Video("Longo 1").ScheduledAtUtc);
        Assert.Null(h.Video("Video 1").ScheduledAtUtc);

        // Três verticais no dia não bloqueiam os horizontais, e vice-versa: cada fila tem o seu limite.
        await h.ImportAsync("Video 2", "Video 3");
        await h.CycleAsync();
        Assert.True((await h.Scheduling.SetTimeAsync(h.Video("Video 1").Id, At(14, 12), default)).Ok);
        Assert.True((await h.Scheduling.SetTimeAsync(h.Video("Video 2").Id, At(14, 13), default)).Ok);
        Assert.True((await h.Scheduling.SetTimeAsync(h.Video("Video 3").Id, At(14, 15), default)).Ok);
    }

    [Fact]
    public async Task Two_videos_never_share_the_same_minute_across_queues()
    {
        using var h = Harness(Platform.YouTube, Platform.Instagram);
        await h.Commands.UpdateSettingsAsync(new ScheduleSettingsInput("10:00", "23:59", 240, 10, Enum.GetValues<DayOfWeek>(),
            "America/Sao_Paulo", [Platform.Instagram, Platform.YouTube], MissedSlotPolicy.NextAllowedSlot, 30, 5, ScheduleMode.Manual, 3), default);
        await h.ImportAsync("Video 1");
        await h.CycleAsync();
        Assert.True((await h.Scheduling.SetTimeAsync(h.Video("Video 1").Id, At(14, 11, 40), default)).Ok);

        await ImportHorizontalAsync(h, "Longo 1");
        await h.CycleAsync();
        Assert.Equal(EngineHarness.Local(14, 14, 30), h.Video("Longo 1").ScheduledAtUtc);

        // E um vertical não pode ser marcado em cima de uma janela já ocupada por um horizontal.
        await h.ImportAsync("Video 2");
        await h.CycleAsync();
        var clash = await h.Scheduling.SetTimeAsync(h.Video("Video 2").Id, At(14, 14, 30), default);
        Assert.False(clash.Ok);
        Assert.Contains("Longo 1", clash.Message);
    }

    [Fact]
    public async Task Missed_windows_are_moved_to_the_next_free_ones_without_publishing_in_a_burst()
    {
        using var h = Harness();
        await ImportHorizontalAsync(h, "Longo 1", "Longo 2");
        await h.CycleAsync();

        // Computador desligado das 09:00 às 15:30: as janelas 11:40 e 14:30 passaram.
        h.SetTime(EngineHarness.Local(14, 15, 30));
        await h.CycleAsync();

        Assert.Equal(0, h.TotalPublishCalls);
        Assert.Equal(EngineHarness.Local(14, 17, 20), h.Video("Longo 1").ScheduledAtUtc);
        Assert.Equal(EngineHarness.Local(14, 20, 10), h.Video("Longo 2").ScheduledAtUtc);
        Assert.Equal(EngineHarness.Local(14, 17, 20), h.Publication("Longo 1", Platform.YouTube).ScheduledAtUtc);
    }

    [Fact]
    public async Task Without_youtube_connected_horizontal_videos_wait_unscheduled()
    {
        using var h = Harness(Platform.Instagram);
        await ImportHorizontalAsync(h, "Longo 1");

        await h.CycleAsync();

        var video = h.Video("Longo 1");
        Assert.Null(video.ScheduledAtUtc);
        Assert.Empty(video.Publications);
    }

    [Fact]
    public async Task Refused_thumbnail_is_kept_as_a_warning_and_reaches_the_notice()
    {
        using var h = Harness(Platform.YouTube);
        h.Connectors[Platform.YouTube].OnPublish = _ => PublishOutcome.Published("yt-1", "https://youtu.be/yt-1") with
        {
            Warning = "A thumbnail não foi aplicada: o YouTube recusou.",
        };
        await ImportHorizontalAsync(h, "Longo 1");
        await h.CycleAsync();

        h.SetTime(EngineHarness.Local(14, 11, 40));
        await h.CycleAsync();

        var publication = h.Publication("Longo 1", Platform.YouTube);
        Assert.Equal(PublicationState.Published, publication.State);
        Assert.Equal("A thumbnail não foi aplicada: o YouTube recusou.", publication.Warning);
        using var db = h.Env.DbFactory.CreateDbContext();
        var notice = db.Notifications.AsNoTracking().Single(n => n.Kind == NotificationKind.VideoFinished);
        Assert.Contains("thumbnail não foi aplicada", notice.Text);
    }

    [Fact]
    public async Task Window_settings_are_saved_and_impossible_ones_refused()
    {
        using var h = Harness();
        var input = new ScheduleSettingsInput("10:00", "23:59", 240, 10, Enum.GetValues<DayOfWeek>(), "America/Sao_Paulo",
            [Platform.YouTube], MissedSlotPolicy.NextAllowedSlot, 30, 5, HorizontalStartTime: "07:00", HorizontalEndTime: "22:00", HorizontalPerDay: 4);

        Assert.Empty(await h.Commands.UpdateSettingsAsync(input, default));
        var settings = await h.Scheduling.GetSettingsAsync(default);
        Assert.Equal(["07:00", "12:00", "17:00", "22:00"], SlotCalculator.HorizontalTimes(settings).Select(t => t.ToString("HH:mm")).ToArray());

        var tooMany = await h.Commands.UpdateSettingsAsync(input with { HorizontalStartTime = "10:00", HorizontalEndTime = "10:02", HorizontalPerDay = 5 }, default);
        Assert.Contains(tooMany, p => p.Contains("janelas demais"));

        // Script antigo, sem os campos novos, mantém as janelas salvas.
        Assert.Empty(await h.Commands.UpdateSettingsAsync(input with { HorizontalStartTime = null, HorizontalEndTime = null, HorizontalPerDay = null }, default));
        Assert.Equal(4, (await h.Scheduling.GetSettingsAsync(default)).HorizontalPerDay);
    }
}
