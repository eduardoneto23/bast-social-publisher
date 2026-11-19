using Bast.Application.Publishing;
using Bast.Domain;
using Microsoft.EntityFrameworkCore;

namespace Bast.Tests;

/// <summary>Modo manual: o vídeo espera o usuário escolher o horário no painel.</summary>
public class ManualScheduleTests
{
    // Relógio inicial dos testes: 14/11/2026 09:00 em São Paulo (12:00 UTC).
    private static DateTime At(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0);

    private static async Task<EngineHarness> ManualAsync(int manualMaxPerDay = 3)
    {
        var h = new EngineHarness(Platform.YouTube, Platform.Instagram);
        await SetModeAsync(h, ScheduleMode.Manual, manualMaxPerDay);
        return h;
    }

    private static async Task SetModeAsync(EngineHarness h, ScheduleMode mode, int manualMaxPerDay = 3)
    {
        var problems = await h.Commands.UpdateSettingsAsync(new ScheduleSettingsInput(
            "10:00", "23:59", 240, 10, Enum.GetValues<DayOfWeek>(), "America/Sao_Paulo",
            [Platform.Instagram, Platform.YouTube], MissedSlotPolicy.NextAllowedSlot, 30, 5, mode, manualMaxPerDay), default);
        Assert.Empty(problems);
    }

    private static List<Notification> Notifications(EngineHarness h)
    {
        using var db = h.Env.DbFactory.CreateDbContext();
        return db.Notifications.AsNoTracking().Where(n => n.Kind == NotificationKind.TimeNeeded).ToList();
    }

    [Fact]
    public async Task Imported_video_waits_for_a_chosen_time_and_is_never_published_on_its_own()
    {
        using var h = await ManualAsync();
        await h.ImportAsync("Video 1");

        for (var hour = 9; hour <= 23; hour++)
        {
            h.SetTime(EngineHarness.Local(14, hour));
            await h.CycleAsync();
        }

        var video = h.Video("Video 1");
        Assert.Equal(VideoState.Ready, video.State);
        Assert.Null(video.ScheduledAtUtc);
        Assert.Empty(video.Publications);
        Assert.Equal(0, h.TotalPublishCalls);

        // Um único aviso, mesmo com vários ciclos.
        var notice = Assert.Single(Notifications(h));
        Assert.Contains("Video 1", notice.Text);
        Assert.Contains("escolha quando publicar", notice.Text);
    }

    [Fact]
    public async Task Chosen_time_in_the_schedule_time_zone_is_kept_and_published_only_then()
    {
        using var h = await ManualAsync();
        await h.ImportAsync("Video 1");
        await h.CycleAsync();

        var result = await h.Scheduling.SetTimeAsync(h.Video("Video 1").Id, At(14, 14, 37), default);

        Assert.True(result.Ok, result.Message);
        Assert.Contains("14/11 às 14:37", result.Message);
        var video = h.Video("Video 1");
        Assert.Equal(EngineHarness.Local(14, 14, 37), video.ScheduledAtUtc);
        Assert.Equal(2, video.Publications.Count);
        Assert.All(video.Publications, p =>
        {
            Assert.Equal(PublicationState.Scheduled, p.State);
            Assert.Equal(EngineHarness.Local(14, 14, 37), p.ScheduledAtUtc);
        });

        h.SetTime(EngineHarness.Local(14, 14, 36));
        await h.CycleAsync();
        Assert.Equal(0, h.TotalPublishCalls);

        h.SetTime(EngineHarness.Local(14, 14, 37));
        await h.CycleAsync();
        Assert.Equal(2, h.TotalPublishCalls);
        Assert.Equal(VideoState.Published, h.Video("Video 1").State);
    }

    [Fact]
    public async Task At_most_the_daily_limit_and_the_published_ones_count()
    {
        using var h = await ManualAsync(manualMaxPerDay: 3);
        await h.ImportAsync("Video 1", "Video 2", "Video 3", "Video 4");
        await h.CycleAsync();

        Assert.True((await h.Scheduling.SetTimeAsync(h.Video("Video 1").Id, At(14, 10), default)).Ok);
        Assert.True((await h.Scheduling.SetTimeAsync(h.Video("Video 2").Id, At(14, 15), default)).Ok);
        Assert.True((await h.Scheduling.SetTimeAsync(h.Video("Video 3").Id, At(14, 20), default)).Ok);

        var fourth = await h.Scheduling.SetTimeAsync(h.Video("Video 4").Id, At(14, 23), default);
        Assert.False(fourth.Ok);
        Assert.Contains("máximo é 3", fourth.Message);
        Assert.Empty(h.Video("Video 4").Publications);

        // O primeiro já publicado continua contando para aquele dia.
        h.SetTime(EngineHarness.Local(14, 10));
        await h.CycleAsync();
        Assert.Equal(VideoState.Published, h.Video("Video 1").State);
        Assert.False((await h.Scheduling.SetTimeAsync(h.Video("Video 4").Id, At(14, 23), default)).Ok);

        // O dia seguinte começa do zero.
        Assert.True((await h.Scheduling.SetTimeAsync(h.Video("Video 4").Id, At(15, 10), default)).Ok);

        // Mudar o horário de um vídeo do próprio dia não conta ele duas vezes.
        Assert.True((await h.Scheduling.SetTimeAsync(h.Video("Video 3").Id, At(14, 21), default)).Ok);
    }

    [Fact]
    public async Task Day_limit_uses_the_local_day_not_the_utc_day()
    {
        using var h = await ManualAsync(manualMaxPerDay: 1);
        await h.ImportAsync("Video 1", "Video 2");
        await h.CycleAsync();

        // 22:30 em São Paulo já é dia 15 em UTC, mas ainda é dia 14 no fuso da agenda.
        Assert.True((await h.Scheduling.SetTimeAsync(h.Video("Video 1").Id, At(14, 22, 30), default)).Ok);
        Assert.False((await h.Scheduling.SetTimeAsync(h.Video("Video 2").Id, At(14, 11), default)).Ok);
        Assert.True((await h.Scheduling.SetTimeAsync(h.Video("Video 2").Id, At(15, 0, 30), default)).Ok);
    }

    [Fact]
    public async Task Past_time_and_the_same_minute_as_another_video_are_refused()
    {
        using var h = await ManualAsync();
        await h.ImportAsync("Video 1", "Video 2");
        await h.CycleAsync();

        var past = await h.Scheduling.SetTimeAsync(h.Video("Video 1").Id, At(14, 8, 59), default);
        Assert.False(past.Ok);
        Assert.Contains("futuro", past.Message);

        Assert.True((await h.Scheduling.SetTimeAsync(h.Video("Video 1").Id, At(14, 15), default)).Ok);
        var clash = await h.Scheduling.SetTimeAsync(h.Video("Video 2").Id, At(14, 15), default);
        Assert.False(clash.Ok);
        Assert.Contains("Video 1", clash.Message);
        Assert.True((await h.Scheduling.SetTimeAsync(h.Video("Video 2").Id, At(14, 15, 1), default)).Ok);

        Assert.True((await h.Scheduling.SetTimeAsync(Guid.NewGuid(), At(14, 16), default)).NotFound);
    }

    [Fact]
    public async Task Time_can_change_until_a_network_starts_publishing()
    {
        using var h = await ManualAsync();
        await h.ImportAsync("Video 1");
        await h.CycleAsync();
        var id = h.Video("Video 1").Id;
        await h.Scheduling.SetTimeAsync(id, At(14, 12), default);

        var moved = await h.Scheduling.SetTimeAsync(id, At(14, 18, 45), default);
        Assert.True(moved.Ok, moved.Message);
        Assert.All(h.Video("Video 1").Publications, p => Assert.Equal(EngineHarness.Local(14, 18, 45), p.ScheduledAtUtc));
        Assert.Equal(2, h.Video("Video 1").Publications.Count);

        h.Connectors[Platform.YouTube].OnPublish = _ => PublishOutcome.Processing("yt-1", TimeSpan.FromMinutes(5));
        h.SetTime(EngineHarness.Local(14, 18, 45));
        await h.CycleAsync();

        var late = await h.Scheduling.SetTimeAsync(id, At(14, 22), default);
        Assert.False(late.Ok);
        Assert.Contains("já começou", late.Message);
    }

    [Fact]
    public async Task Automatic_mode_also_accepts_a_chosen_time_for_a_scheduled_video()
    {
        using var h = new EngineHarness(Platform.YouTube);
        await h.ImportAsync("Video 1");
        await h.CycleAsync();
        Assert.Equal(EngineHarness.Local(14, 10), h.Video("Video 1").ScheduledAtUtc);

        var result = await h.Scheduling.SetTimeAsync(h.Video("Video 1").Id, At(14, 16, 20), default);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(EngineHarness.Local(14, 16, 20), h.Publication("Video 1", Platform.YouTube).ScheduledAtUtc);
        h.SetTime(EngineHarness.Local(14, 10, 5));
        await h.CycleAsync();
        Assert.Equal(0, h.TotalPublishCalls);
    }

    [Fact]
    public async Task Missed_chosen_time_goes_back_to_waiting_with_a_notice_instead_of_publishing_later()
    {
        using var h = await ManualAsync();
        await h.ImportAsync("Video 1", "Video 2");
        await h.CycleAsync();
        await h.Scheduling.SetTimeAsync(h.Video("Video 1").Id, At(14, 10), default);
        await h.Scheduling.SetTimeAsync(h.Video("Video 2").Id, At(14, 11), default);

        // Computador desligado das 09:00 às 13:00: os dois horários passaram da tolerância de 30 min.
        h.SetTime(EngineHarness.Local(14, 13));
        await h.CycleAsync();

        Assert.Equal(0, h.TotalPublishCalls);
        foreach (var name in new[] { "Video 1", "Video 2" })
        {
            var video = h.Video(name);
            Assert.Null(video.ScheduledAtUtc);
            Assert.All(video.Publications, p => Assert.Equal(PublicationState.Scheduled, p.State));
            Assert.All(video.Publications, p => Assert.Null(p.ScheduledAtUtc));
        }

        Assert.Equal(2, Notifications(h).Count(n => n.Text.Contains("Horário perdido")));

        // Escolher de novo reaproveita as publicações e publica no novo horário.
        Assert.True((await h.Scheduling.SetTimeAsync(h.Video("Video 1").Id, At(14, 15), default)).Ok);
        Assert.Equal(2, h.Video("Video 1").Publications.Count);
        h.SetTime(EngineHarness.Local(14, 15));
        await h.CycleAsync();
        Assert.Equal(VideoState.Published, h.Video("Video 1").State);
        Assert.Null(h.Video("Video 2").ScheduledAtUtc);
    }

    [Fact]
    public async Task Late_within_tolerance_still_publishes_one_video()
    {
        using var h = await ManualAsync();
        await h.ImportAsync("Video 1");
        await h.CycleAsync();
        await h.Scheduling.SetTimeAsync(h.Video("Video 1").Id, At(14, 10), default);

        h.SetTime(EngineHarness.Local(14, 10, 20));
        await h.CycleAsync();

        Assert.Equal(VideoState.Published, h.Video("Video 1").State);
    }

    [Fact]
    public async Task Publish_now_works_for_a_video_still_waiting_for_a_time()
    {
        using var h = await ManualAsync();
        await h.ImportAsync("Video 1");
        await h.CycleAsync();

        var moved = await h.Commands.PublishNowAsync(h.Video("Video 1").Id, default);
        Assert.Equal(2, moved);
        await h.CycleAsync();

        Assert.Equal(VideoState.Published, h.Video("Video 1").State);
        Assert.Equal(2, h.TotalPublishCalls);
    }

    [Fact]
    public async Task Back_to_automatic_the_waiting_videos_get_the_next_slots()
    {
        using var h = await ManualAsync();
        await h.ImportAsync("Video 1", "Video 2");
        await h.CycleAsync();
        await h.Scheduling.SetTimeAsync(h.Video("Video 1").Id, At(14, 9, 10), default);
        h.SetTime(EngineHarness.Local(14, 13));
        await h.CycleAsync(); // Video 1 perdeu o horário e voltou a aguardar, já com publicações.

        await SetModeAsync(h, ScheduleMode.Automatic);
        await h.CycleAsync();

        Assert.Equal(EngineHarness.Local(14, 14), h.Video("Video 1").ScheduledAtUtc);
        Assert.Equal(EngineHarness.Local(14, 18), h.Video("Video 2").ScheduledAtUtc);
        Assert.All(h.Video("Video 1").Publications, p => Assert.Equal(EngineHarness.Local(14, 14), p.ScheduledAtUtc));
        Assert.Equal(2, h.Video("Video 1").Publications.Count);
    }

    [Fact]
    public async Task Settings_keep_the_mode_when_an_old_script_omits_it_and_refuse_a_zero_limit()
    {
        using var h = await ManualAsync(manualMaxPerDay: 3);

        var oldScript = await h.Commands.UpdateSettingsAsync(new ScheduleSettingsInput(
            "09:00", "23:59", 180, 5, [DayOfWeek.Monday], "America/Sao_Paulo",
            [Platform.YouTube], MissedSlotPolicy.NextAllowedSlot, 30, 5), default);
        Assert.Empty(oldScript);
        var settings = await h.Scheduling.GetSettingsAsync(default);
        Assert.Equal(ScheduleMode.Manual, settings.Mode);
        Assert.Equal(3, settings.ManualMaxPerDay);
        Assert.Equal(180, settings.IntervalMinutes);

        var zero = await h.Commands.UpdateSettingsAsync(new ScheduleSettingsInput(
            "09:00", "23:59", 180, 5, [DayOfWeek.Monday], "America/Sao_Paulo",
            [Platform.YouTube], MissedSlotPolicy.NextAllowedSlot, 30, 5, ScheduleMode.Manual, 0), default);
        Assert.Contains(zero, p => p.Contains("modo manual"));
    }

    [Fact]
    public void New_installations_start_in_automatic_mode_with_three_per_day_for_manual()
    {
        var settings = new ScheduleSettings();
        Assert.Equal(ScheduleMode.Automatic, settings.Mode);
        Assert.Equal(3, settings.ManualMaxPerDay);
        Assert.Equal(10, settings.ChosenTimeMaxPerDay);
        settings.Mode = ScheduleMode.Manual;
        Assert.Equal(3, settings.ChosenTimeMaxPerDay);
    }
}
