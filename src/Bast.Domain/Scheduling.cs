using System.Globalization;

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
    /// <summary>Cada vídeo pega o próximo horário fixo livre da lista (ex.: 10:00, 16:00, 18:00), um por horário.</summary>
    FixedTimes,
}

/// <summary>Lista de horários do dia escrita como texto ("10:00, 16:00, 18:00").</summary>
public static class TimeList
{
    private static readonly char[] Separators = [',', ';', ' ', '\n', '\r', '\t'];
    private static readonly string[] Formats = ["H:mm", "HH:mm"];

    /// <summary>Lê os horários (HH:mm), sem repetir e em ordem. Falso se algum item não é um horário.</summary>
    public static bool TryParse(string? text, out List<TimeOnly> times)
    {
        times = [];
        foreach (var item in (text ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TimeOnly.TryParseExact(item, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return false;
            times.Add(time);
        }

        times = times.Distinct().Order().ToList();
        return true;
    }

    public static IReadOnlyList<TimeOnly> Parse(string? text) => TryParse(text, out var times) ? times : [];

    public static string Format(IEnumerable<TimeOnly> times) =>
        string.Join(",", times.Select(t => t.ToString("HH:mm", CultureInfo.InvariantCulture)));
}

/// <summary>Regras de operação configuradas uma única vez pelo usuário. Existe uma única linha (Id = 1).</summary>
public sealed class ScheduleSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public ScheduleMode Mode { get; set; } = ScheduleMode.FixedTimes;

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

    /// <summary>Vídeos verticais no modo de horários fixos: os horários do dia, separados por vírgula. Um vídeo por horário.</summary>
    public string VerticalSlots { get; set; } = "10:00,16:00,18:00";

    /// <summary>Vídeos horizontais do YouTube: os horários do dia, separados por vírgula. Um vídeo por horário, todos os dias.</summary>
    public string HorizontalSlots { get; set; } = "08:00,11:00,14:00,17:00,20:00";

    public IReadOnlyList<TimeOnly> VerticalSlotTimes => TimeList.Parse(VerticalSlots);
    public IReadOnlyList<TimeOnly> HorizontalSlotTimes => TimeList.Parse(HorizontalSlots);

    /// <summary>Vídeos horizontais por dia: um por horário.</summary>
    public int HorizontalPerDay => HorizontalSlotTimes.Count;

    /// <summary>
    /// Verticais publicados no automático vão para PENDENTE MANUAL até o usuário confirmar no painel que
    /// postou à mão nas redes que não estão no automático.
    /// </summary>
    public bool ManualPosting { get; set; }

    /// <summary>
    /// Produção: o que passar em todas as verificações (status APROVADO, sem alerta) vai para a agenda sozinho.
    /// Desligado (padrão): cada conteúdo produzido só vai para a agenda quando o usuário clica em "Liberar".
    /// </summary>
    public bool AutoPublishApproved { get; set; }

    public bool Paused { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>Limite de vídeos por dia que vale para horários escolhidos à mão.</summary>
    public int ChosenTimeMaxPerDay => Mode switch
    {
        ScheduleMode.Manual => ManualMaxPerDay,
        ScheduleMode.FixedTimes => VerticalSlotTimes.Count,
        _ => MaxPerDay,
    };

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
        if (!TimeList.TryParse(VerticalSlots, out var vertical)) problems.Add("Os horários dos vídeos verticais devem estar no formato HH:mm, separados por vírgula.");
        else if (vertical.Count == 0) problems.Add("Informe pelo menos um horário para os vídeos verticais.");
        if (!TimeList.TryParse(HorizontalSlots, out var horizontal)) problems.Add("Os horários dos vídeos horizontais devem estar no formato HH:mm, separados por vírgula.");
        else if (horizontal.Count == 0) problems.Add("Informe pelo menos um horário para os vídeos horizontais.");
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(TimeZoneId, out _)) problems.Add($"Fuso horário desconhecido: {TimeZoneId}.");
        return problems;
    }
}

public static class SlotCalculator
{
    private const int SearchDays = 400;

    /// <summary>Horários do dia dos vídeos horizontais.</summary>
    public static IReadOnlyList<TimeOnly> HorizontalTimes(ScheduleSettings settings) => settings.HorizontalSlotTimes;

    /// <summary>
    /// Próxima janela dos vídeos horizontais estritamente depois de <paramref name="afterUtc"/> que não esteja
    /// ocupada (<paramref name="taken"/>). Vale todos os dias.
    /// </summary>
    public static DateTime? NextHorizontalSlotUtc(ScheduleSettings settings, DateTime afterUtc, Func<DateTime, bool>? taken = null) =>
        NextFixedSlotUtc(settings, settings.HorizontalSlotTimes, afterUtc, honorDays: false, utc => taken is null || !taken(utc));

    /// <summary>
    /// Próximo horário da lista <paramref name="times"/> estritamente depois de <paramref name="afterUtc"/> que
    /// <paramref name="isFree"/> aceita (sem outro vídeo colado e com vaga no dia).
    /// <paramref name="honorDays"/>: pula os dias da semana desmarcados na agenda.
    /// </summary>
    public static DateTime? NextFixedSlotUtc(ScheduleSettings settings, IReadOnlyList<TimeOnly> times, DateTime afterUtc, bool honorDays, Func<DateTime, bool>? isFree = null)
    {
        if (settings.Validate().Count > 0 || times.Count == 0) return null;

        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        afterUtc = DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc);
        var firstDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(afterUtc, zone));

        for (var day = 0; day < SearchDays; day++)
        {
            var date = firstDate.AddDays(day);
            if (honorDays && !settings.IsDayEnabled(date.DayOfWeek)) continue;

            foreach (var time in times)
            {
                var local = date.ToDateTime(time);
                if (zone.IsInvalidTime(local)) local = local.AddHours(1);
                var utc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
                if (utc > afterUtc && (isFree is null || isFree(utc))) return utc;
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
