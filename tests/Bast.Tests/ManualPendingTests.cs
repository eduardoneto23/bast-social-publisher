using Bast.Application.Import;
using Bast.Application.Publishing;
using Bast.Domain;

namespace Bast.Tests;

/// <summary>
/// Redes desmarcadas não recebem nada, nem o que já estava agendado; verticais publicados no automático
/// ficam em PENDENTE MANUAL até o usuário confirmar no painel que postou à mão nas outras redes.
/// </summary>
public class ManualPendingTests
{
    private static Task<IReadOnlyList<string>> OnlyAsync(EngineHarness h, params Platform[] platforms) =>
        h.Commands.UpdateSettingsAsync(new ScheduleSettingsInput("10:00", "23:59", 240, 10, Enum.GetValues<DayOfWeek>(),
            "America/Sao_Paulo", platforms, MissedSlotPolicy.NextAllowedSlot, 30, 5), default);

    [Fact]
    public async Task Unchecking_a_network_cancels_what_was_already_scheduled_for_it()
    {
        using var h = new EngineHarness();
        await h.ImportAsync("Video 1");
        await h.CycleAsync();
        Assert.Equal(4, h.Video("Video 1").Publications.Count(p => p.State == PublicationState.Scheduled));

        Assert.Empty(await OnlyAsync(h, Platform.YouTube));
        h.SetTime(EngineHarness.Local(14, 10));
        await h.CycleAsync();

        var video = h.Video("Video 1");
        Assert.Equal(PublicationState.Published, h.Publication("Video 1", Platform.YouTube).State);
        foreach (var platform in new[] { Platform.Instagram, Platform.Facebook, Platform.Threads })
        {
            var publication = h.Publication("Video 1", platform);
            Assert.Equal(PublicationState.Cancelled, publication.State);
            Assert.Contains("desmarcada", publication.LastError);
            Assert.Empty(h.Connectors[platform].PublishCalls);
        }

        Assert.Equal(VideoState.Published, video.State);
    }

    [Fact]
    public async Task Published_vertical_waits_in_manual_pending_until_confirmed()
    {
        using var h = new EngineHarness(Platform.YouTube);
        Assert.Empty(await h.Commands.UpdateSettingsAsync(new ScheduleSettingsInput("10:00", "23:59", 240, 10, Enum.GetValues<DayOfWeek>(),
            "America/Sao_Paulo", [Platform.YouTube], MissedSlotPolicy.NextAllowedSlot, 30, 5, ManualPosting: true), default));
        await h.ImportAsync("Video 1");
        await h.CycleAsync();

        h.SetTime(EngineHarness.Local(14, 10));
        await h.CycleAsync();
        await h.CycleAsync();

        var video = h.Video("Video 1");
        Assert.Equal(VideoState.Published, video.State);
        Assert.True(video.AwaitsManual);
        Assert.Equal(WorkspaceFolder.ManualPending, video.CurrentFolder);
        Assert.True(Directory.Exists(Path.Combine(h.Env.Workspace.PathOf(WorkspaceFolder.ManualPending), "Video 1")));

        Assert.True(await h.Commands.ConfirmManualAsync(video.Id, default));
        await h.CycleAsync();

        video = h.Video("Video 1");
        Assert.False(video.AwaitsManual);
        Assert.NotNull(video.ManualDoneAtUtc);
        Assert.Equal(WorkspaceFolder.Sent, video.CurrentFolder);
        Assert.True(Directory.Exists(Path.Combine(h.Env.Workspace.PathOf(WorkspaceFolder.Sent), "Video 1")));

        // Confirmar de novo não muda nada.
        Assert.False(await h.Commands.ConfirmManualAsync(video.Id, default));
        Assert.Null(await h.Commands.ConfirmManualAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Horizontal_videos_and_disabled_manual_posting_go_straight_to_sent()
    {
        using var h = new EngineHarness(Platform.YouTube);
        h.Env.Analyzer.Handler = path => VideoAnalysisResult.Ok(path.Contains("ENVIAR YOUTUBE", StringComparison.OrdinalIgnoreCase)
            ? new VideoAnalysis(600, 1920, 1080, "h264", "aac", 30, 8_000_000, "mov,mp4")
            : new VideoAnalysis(30, 1080, 1920, "h264", "aac", 30, 5_000_000, "mov,mp4"));
        var folder = Path.Combine(h.Env.Workspace.PathOf(WorkspaceFolder.YouTubeInbox), "Longo 1");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "video.mp4"), "horizontal");
        File.WriteAllText(Path.Combine(folder, "legenda.txt"), "Título\nDescrição.");
        File.WriteAllBytes(Path.Combine(folder, "thumbnail.jpg"), [0xFF, 0xD8, 0xFF]);
        Assert.Equal(ImportOutcome.Imported, await h.Env.Service.ProcessFolderAsync(folder, default, VideoLane.YouTubeHorizontal));

        await h.Commands.UpdateSettingsAsync(new ScheduleSettingsInput("10:00", "23:59", 240, 10, Enum.GetValues<DayOfWeek>(),
            "America/Sao_Paulo", [Platform.YouTube], MissedSlotPolicy.NextAllowedSlot, 30, 5, ManualPosting: false), default);
        await h.ImportAsync("Video 1");
        await h.CycleAsync();

        h.SetTime(EngineHarness.Local(14, 10));
        await h.CycleAsync();
        await h.CycleAsync();
        h.SetTime(EngineHarness.Local(14, 11));
        await h.CycleAsync();
        await h.CycleAsync();

        Assert.Equal(WorkspaceFolder.Sent, h.Video("Longo 1").CurrentFolder);
        Assert.Null(h.Video("Longo 1").ManualPendingSinceUtc);
        Assert.Equal(WorkspaceFolder.Sent, h.Video("Video 1").CurrentFolder);
        Assert.Null(h.Video("Video 1").ManualPendingSinceUtc);
    }
}
