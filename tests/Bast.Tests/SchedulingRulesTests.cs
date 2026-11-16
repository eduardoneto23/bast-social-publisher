using Bast.Domain;

namespace Bast.Tests;

public class SlotCalculatorTests
{
    // São Paulo está em UTC-3 o ano todo: 10:00 local = 13:00 UTC.
    private static DateTime Local(int day, int hour, int minute = 0) => new DateTime(2026, 11, day, 0, 0, 0, DateTimeKind.Utc).AddHours(hour + 3).AddMinutes(minute);

    private static List<DateTime> Take(ScheduleSettings settings, DateTime after, int count)
    {
        var slots = new List<DateTime>();
        var cursor = after;
        for (var i = 0; i < count; i++)
        {
            cursor = SlotCalculator.NextSlotUtc(settings, cursor)!.Value;
            slots.Add(cursor);
        }

        return slots;
    }

    [Fact]
    public void Example_from_the_specification_10h_every_4_hours()
    {
        var settings = new ScheduleSettings { StartTime = new(10, 0), IntervalMinutes = 240 };

        var slots = Take(settings, Local(16, 8), 5);

        Assert.Equal([Local(16, 10), Local(16, 14), Local(16, 18), Local(16, 22), Local(17, 10)], slots);
    }

    [Fact]
    public void Slot_is_strictly_after_the_reference_time()
    {
        var settings = new ScheduleSettings();
        Assert.Equal(Local(16, 14), SlotCalculator.NextSlotUtc(settings, Local(16, 10)));
        Assert.Equal(Local(16, 14), SlotCalculator.NextSlotUtc(settings, Local(16, 10, 1)));
    }

    [Fact]
    public void Respects_max_per_day_end_time_and_weekdays()
    {
        var settings = new ScheduleSettings { MaxPerDay = 2 };
        Assert.Equal([Local(16, 10), Local(16, 14), Local(17, 10)], Take(settings, Local(16, 0), 3));

        settings = new ScheduleSettings { EndTime = new(15, 0) };
        Assert.Equal([Local(16, 10), Local(16, 14), Local(17, 10)], Take(settings, Local(16, 0), 3));

        // 16/11/2026 é segunda-feira; só quartas e sextas habilitadas.
        settings = new ScheduleSettings { DaysMask = ScheduleSettings.MaskOf(DayOfWeek.Wednesday, DayOfWeek.Friday), MaxPerDay = 1 };
        Assert.Equal([Local(18, 10), Local(20, 10), Local(25, 10)], Take(settings, Local(16, 0), 3));
    }

    [Fact]
    public void Next_cycle_skips_the_rest_of_today()
    {
        var settings = new ScheduleSettings();
        Assert.Equal(Local(17, 10), SlotCalculator.NextSlotUtc(settings, Local(16, 11), skipToNextDay: true));
    }

    [Fact]
    public void Invalid_settings_produce_no_slot()
    {
        Assert.Null(SlotCalculator.NextSlotUtc(new ScheduleSettings { DaysMask = 0 }, Local(16, 0)));
        Assert.Null(SlotCalculator.NextSlotUtc(new ScheduleSettings { TimeZoneId = "Marte/Olympus" }, Local(16, 0)));
        Assert.NotEmpty(new ScheduleSettings { IntervalMinutes = 0 }.Validate());
    }
}

public class CaptionRulesTests
{
    private const string Caption = "GTA 6 VAI TER POSTER NA VERSÃO FÍSICA!\r\n\r\nA Rockstar confirmou. 🎮\r\nE você, curtiu?\r\n\r\n#GTA6 #GTA #Bast";
    private static readonly VideoFacts Short = new(30, 20 * 1024 * 1024);

    [Fact]
    public void YouTube_title_is_first_line_and_description_keeps_everything()
    {
        Assert.Equal("GTA 6 VAI TER POSTER NA VERSÃO FÍSICA!", CaptionRules.YouTubeTitle(Caption));
        var description = CaptionRules.YouTubeDescription(Caption);
        Assert.StartsWith("GTA 6 VAI TER POSTER NA VERSÃO FÍSICA!\n\nA Rockstar", description);
        Assert.EndsWith("🎮\nE você, curtiu?\n\n#GTA6 #GTA #Bast", description);
    }

    [Fact]
    public void Text_is_never_modified_beyond_line_break_style()
    {
        Assert.Equal(Caption.Replace("\r\n", "\n"), CaptionRules.TextFor(Caption));
    }

    [Fact]
    public void Normal_caption_is_valid_everywhere()
    {
        foreach (var platform in Enum.GetValues<Platform>())
        {
            Assert.Empty(CaptionRules.Validate(platform, Caption, Short));
        }
    }

    [Fact]
    public void Long_text_is_flagged_only_where_it_does_not_fit()
    {
        var caption = "Título\n" + new string('a', 600);
        Assert.Single(CaptionRules.Validate(Platform.Threads, caption, Short));
        Assert.Empty(CaptionRules.Validate(Platform.Instagram, caption, Short));
        Assert.Empty(CaptionRules.Validate(Platform.YouTube, caption, Short));
    }

    [Fact]
    public void Threads_counts_emojis_as_utf8_bytes_and_accents_as_one()
    {
        Assert.Equal(6, CaptionRules.ThreadsLength("VERSÃO"));
        Assert.Equal(4, CaptionRules.ThreadsLength("🎮"));
        Assert.Empty(CaptionRules.Validate(Platform.Threads, new string('ã', 500), Short));
        Assert.Single(CaptionRules.Validate(Platform.Threads, new string('a', 497) + "🎮", Short));
    }

    [Fact]
    public void Instagram_limits_hashtags_and_mentions()
    {
        var hashtags = "Título\n" + string.Join(" ", Enumerable.Range(1, 31).Select(i => $"#tag{i}"));
        Assert.Single(CaptionRules.Validate(Platform.Instagram, hashtags, Short));
        var mentions = "Título\n" + string.Join(" ", Enumerable.Range(1, 21).Select(i => $"@user{i}"));
        Assert.Single(CaptionRules.Validate(Platform.Instagram, mentions, Short));
    }

    [Fact]
    public void YouTube_limits_title_description_bytes_and_angle_brackets()
    {
        Assert.Single(CaptionRules.Validate(Platform.YouTube, new string('T', 101) + "\ncorpo", Short));
        Assert.Single(CaptionRules.Validate(Platform.YouTube, "Título\n" + new string('ã', 2600), Short)); // 5200 bytes
        Assert.Single(CaptionRules.Validate(Platform.YouTube, "Título\nA <b>B</b>", Short));
    }

    [Fact]
    public void Video_duration_is_checked_per_platform()
    {
        var twoMinutes = new VideoFacts(120, 50 * 1024 * 1024);
        Assert.Single(CaptionRules.Validate(Platform.Facebook, Caption, twoMinutes));
        Assert.Empty(CaptionRules.Validate(Platform.Instagram, Caption, twoMinutes));
        Assert.Empty(CaptionRules.Validate(Platform.Threads, Caption, twoMinutes));
        Assert.Single(CaptionRules.Validate(Platform.Threads, Caption, new VideoFacts(301, 1)));
    }
}
