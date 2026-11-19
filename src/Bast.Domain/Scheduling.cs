namespace Bast.Domain;

/// <summary>O que fazer quando o horário de um vídeo passou sem publicação (computador desligado, serviço parado).</summary>
public enum MissedSlotPolicy
{
    /// <summary>Publicar no próximo horário permitido.</summary>
    NextAllowedSlot,
    /// <summary>Aguardar o próximo ciclo (primeiro horário do próximo dia permitido).</summary>
    NextCycle,
}

/// <summary>Como os vídeos recebem horário.</summary>
public enum ScheduleMode
{
    /// <summary>O sistema reserva o próximo horário livre (início, intervalo, máximo por dia).</summary>
    Automatic,
    /// <summary>O vídeo espera o usuário escolher o horário no painel.</summary>
    Manual,
}

/// <summary>Regras de operação configuradas uma única vez pelo usuário. Existe uma única linha (Id = 1).</summary>
public sealed class ScheduleSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public ScheduleMode Mode { get; set; } = ScheduleMode.Automatic;

    /// <summary>No modo manual, quantos vídeos podem ser marcados para o mesmo dia (no fuso configurado).</summary>
    public int ManualMaxPerDay { get; set; } = 3;

    /// <summary>Horário da primeira publicação do dia, no fuso configurado.</summary>
    public TimeOnly StartTime { get; set; } = new(10, 0);

    /// <summary>Último horário permitido para iniciar uma publicação no dia.</summary>
    public TimeOnly EndTime { get; set; } = new(23, 59);

    public int IntervalMinutes { get; set; } = 240;
    public int MaxPerDay { get; set; } = 10;

    /// <summary>Um bit por dia da semana (bit 0 = domingo).</summary>
    public int DaysMask { get; set; } = 0b111_1111;

    public string TimeZoneId { get; set; } = "America/Sao_Paulo";

    /// <summary>Um bit por plataforma habilitada, na ordem do enum <see cref="Platform"/>.</summary>
    public int PlatformsMask { get; set; } = MaskOf(Platform.Instagram, Platform.Facebook, Platform.Threads, Platform.YouTube);

    public MissedSlotPolicy MissedPolicy { get; set; } = MissedSlotPolicy.NextAllowedSlot;

    /// <summary>Atraso tolerado: dentro deste prazo o vídeo ainda é publicado no horário original.</summary>
    public int MissedGraceMinutes { get; set; } = 30;

    public int MaxAttempts { get; set; } = 5;

    /// <summary>Vídeos horizontais do YouTube: primeira e última janela do dia e quantas janelas há entre elas (divisão igual).</summary>
    public TimeOnly HorizontalStartTime { get; set; } = new(6, 0);
    public TimeOnly HorizontalEndTime { get; set; } = new(23, 0);
    public int HorizontalPerDay { get; set; } = 7;

    public bool Paused { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>Limite de vídeos por dia que vale para horários escolhidos à mão.</summary>
    public int ChosenTimeMaxPerDay => Mode == ScheduleMode.Manual ? ManualMaxPerDay : MaxPerDay;

    public bool IsDayEnabled(DayOfWeek day) => (DaysMask & (1 << (int)day)) != 0;

    public bool IsPlatformEnabled(Platform platform) => (PlatformsMask & (1 << (int)platform)) != 0;

    public IReadOnlyList<Platform> Platforms => Enum.GetValues<Platform>().Where(IsPlatformEnabled).ToList();

    public IReadOnlyList<DayOfWeek> Days => Enum.GetValues<DayOfWeek>().Where(IsDayEnabled).ToList();

    public static int MaskOf(params Platform[] platforms) => platforms.Aggregate(0, (mask, p) => mask | (1 << (int)p));

    public static int MaskOf(params DayOfWeek[] days) => days.Aggregate(0, (mask, d) => mask | (1 << (int)d));

    /// <summary>Lista de problemas da configuração; vazia quando é válida.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (IntervalMinutes < 1) problems.Add("O intervalo entre vídeos deve ser de pelo menos 1 minuto.");
        if (MaxPerDay < 1) problems.Add("A quantidade máxima por dia deve ser de pelo menos 1.");
        if (ManualMaxPerDay < 1) problems.Add("A quantidade máxima por dia no modo manual deve ser de pelo menos 1.");
        if ((DaysMask & 0b111_1111) == 0) problems.Add("Selecione pelo menos um dia da semana.");
        if (EndTime < StartTime) problems.Add("O último horário permitido não pode ser anterior ao horário inicial.");
        if (MissedGraceMinutes < 0) problems.Add("A tolerância de atraso não pode ser negativa.");
        if (MaxAttempts < 1) problems.Add("O limite de tentativas deve ser de pelo menos 1.");
        if (HorizontalPerDay < 1) problems.Add("Os vídeos horizontais precisam de pelo menos 1 janela por dia.");
        if (HorizontalEndTime < HorizontalStartTime) problems.Add("A última janela dos vídeos horizontais não pode ser anterior à primeira.");
        else if (HorizontalPerDay > 1 && (HorizontalEndTime - HorizontalStartTime).TotalMinutes < HorizontalPerDay - 1)
            problems.Add("Há janelas demais para os vídeos horizontais nesse intervalo: elas precisam ficar pelo menos 1 minuto uma da outra.");
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(TimeZoneId, out _)) problems.Add($"Fuso horário desconhecido: {TimeZoneId}.");
        return problems;
    }
}

public static class SlotCalculator
{
    private const int SearchDays = 400;

    /// <summary>Janelas do dia dos vídeos horizontais: da primeira à última, divididas igualmente (arredondadas ao minuto).</summary>
    public static IReadOnlyList<TimeOnly> HorizontalTimes(ScheduleSettings settings)
    {
        var start = settings.HorizontalStartTime.Hour * 60 + settings.HorizontalStartTime.Minute;
        var end = settings.HorizontalEndTime.Hour * 60 + settings.HorizontalEndTime.Minute;
        if (settings.HorizontalPerDay <= 1) return [TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(start))];

        return Enumerable.Range(0, settings.HorizontalPerDay)
            .Select(k => start + (int)Math.Round(k * (double)(end - start) / (settings.HorizontalPerDay - 1), MidpointRounding.AwayFromZero))
            .Select(minutes => TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minutes)))
            .ToList();
    }

    /// <summary>
    /// Próxima janela dos vídeos horizontais estritamente depois de <paramref name="afterUtc"/> que não esteja
    /// ocupada (<paramref name="taken"/>: outro vídeo muito perto desse horário). Vale todos os dias.
    /// </summary>
    public static DateTime? NextHorizontalSlotUtc(ScheduleSettings settings, DateTime afterUtc, Func<DateTime, bool>? taken = null)
    {
        if (settings.Validate().Count > 0) return null;

        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        afterUtc = DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc);
        var firstDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(afterUtc, zone));
        var times = HorizontalTimes(settings);

        for (var day = 0; day < SearchDays; day++)
        {
            var date = firstDate.AddDays(day);
            foreach (var time in times)
            {
                var local = date.ToDateTime(time);
                if (zone.IsInvalidTime(local)) local = local.AddHours(1);
                var utc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
                if (utc > afterUtc && (taken is null || !taken(utc))) return utc;
            }
        }

        return null;
    }

    /// <summary>
    /// Próximo horário de publicação estritamente depois de <paramref name="afterUtc"/>.
    /// Os horários de um dia são início, início + intervalo, ... até o último horário permitido ou o máximo por dia.
    /// </summary>
    /// <param name="skipToNextDay">Ignora os horários restantes do dia de <paramref name="afterUtc"/>.</param>
    public static DateTime? NextSlotUtc(ScheduleSettings settings, DateTime afterUtc, bool skipToNextDay = false)
    {
        if (settings.Validate().Count > 0) return null;

        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        afterUtc = DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc);
        var firstDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(afterUtc, zone));
        if (skipToNextDay) firstDate = firstDate.AddDays(1);

        var startMinutes = settings.StartTime.Hour * 60 + settings.StartTime.Minute;
        var endMinutes = settings.EndTime.Hour * 60 + settings.EndTime.Minute;

        for (var day = 0; day < SearchDays; day++)
        {
            var date = firstDate.AddDays(day);
            if (!settings.IsDayEnabled(date.DayOfWeek)) continue;

            for (var k = 0; k < settings.MaxPerDay; k++)
            {
                var minutes = startMinutes + (long)k * settings.IntervalMinutes;
                if (minutes > endMinutes) break;

                var local = date.ToDateTime(TimeOnly.MinValue).AddMinutes(minutes);
                // Horário inexistente na virada para o horário de verão: usa a hora seguinte.
                if (zone.IsInvalidTime(local)) local = local.AddHours(1);
                var utc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
                if (utc > afterUtc) return utc;
            }
        }

        return null;
    }
}
