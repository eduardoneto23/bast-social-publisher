using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bast.Application.Persistence;
using Bast.Domain;
using Microsoft.EntityFrameworkCore;

namespace Bast.Application.Radar;

public sealed record RadarTagInput(string Name, string? Synonyms, string? IncludeTerms, string? ExcludeTerms, bool Active);

public sealed record RadarSourceInput(string Url, string? Title, string? Platform, string? Channel, string? Market,
    long? Views, long? Likes, long? Comments, DateTime? PublishedAtUtc, double? ViewsPerHour, DateTime? ConsultedAtUtc);

public sealed record RadarSuggestionInput(
    string Tag, string Format, int? Rank, string Title, string Category, string Market, string Opportunity,
    List<RadarSourceInput> Sources, JsonObject Analysis, JsonObject Plan);

public sealed record RadarRunInput(List<RadarSuggestionInput> Suggestions, string? Summary, string? Shortfall);

public sealed record RadarDue(bool Due, string Reason, Guid? RunningRunId, IReadOnlyList<object> Tags, int SearchesLeft, string DailyTime, string TimeZone);

/// <summary>
/// Radar de Conteúdo: tags com sinônimos, controle do horário diário, recebimento das pesquisas feitas pela
/// tarefa do Claude e resumo no Telegram. Os dados ficam no BAST (o painel é a fonte principal); o Telegram
/// recebe uma cópia resumida com links. Pesquisa que falhou nunca reaproveita sugestões antigas.
/// </summary>
public sealed class RadarService(IDbContextFactory<BastDbContext> factory, BastOptions options, TimeProvider time)
{
    public const int PerFormat = 5;

    /// <summary>Plano de produção: campos sem os quais uma sugestão não serve para gravar.</summary>
    public static readonly string[] RequiredPlanFields = ["ideia", "gancho", "roteiro", "gravar"];

    public static readonly string[] Categories =
    [
        "Tutorial", "Curiosidade", "Easter egg", "Atualização/notícia", "Descoberta", "Humor", "Meme/trend",
        "Desafio", "Gameplay", "Reação", "Comparação", "Experiência", "Segredo ou mistério",
    ];

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>Primeira execução: tag GTA VI (com GTA 6 e Grand Theft Auto VI) e a configuração padrão.</summary>
    public async Task EnsureDefaultsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = Now;
        if (!await db.RadarTags.AnyAsync(ct))
        {
            db.RadarTags.Add(new RadarTag
            {
                Id = Guid.NewGuid(), Name = "GTA VI", Synonyms = "GTA 6; Grand Theft Auto VI; GTA6", Active = true,
                CreatedAtUtc = now, UpdatedAtUtc = now,
            });
        }

        if (!await db.RadarSettings.AnyAsync(ct)) db.RadarSettings.Add(new RadarSettings { Id = 1, UpdatedAtUtc = now });
        await db.SaveChangesAsync(ct);
    }

    // ---------- tags ----------

    public async Task<(RadarTag? Tag, IReadOnlyList<string> Problems)> SaveTagAsync(Guid? id, RadarTagInput input, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var name = (input.Name ?? "").Trim();
        if (name.Length is < 1 or > 80) return (null, ["Informe o nome da tag (até 80 caracteres)."]);

        var tag = id is null ? new RadarTag { Id = Guid.NewGuid(), CreatedAtUtc = Now } : await db.RadarTags.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tag is null) return (null, ["Tag não encontrada."]);

        tag.Name = name;
        tag.Synonyms = string.Join("; ", RadarTag.SplitTerms(input.Synonyms ?? "").Where(s => !s.Equals(name, StringComparison.OrdinalIgnoreCase)));
        tag.IncludeTerms = string.Join("; ", RadarTag.SplitTerms(input.IncludeTerms ?? ""));
        tag.ExcludeTerms = string.Join("; ", RadarTag.SplitTerms(input.ExcludeTerms ?? ""));
        tag.Active = input.Active;
        tag.UpdatedAtUtc = Now;

        // Um mesmo nome em duas tags geraria pesquisas duplicadas.
        var others = await db.RadarTags.Where(t => t.Id != tag.Id).ToListAsync(ct);
        var clash = others.SelectMany(o => o.AllNames.Select(n => (o.Name, n)))
            .FirstOrDefault(x => tag.AllNames.Contains(x.n, StringComparer.OrdinalIgnoreCase));
        if (clash != default) return (null, [$"\"{clash.n}\" já pertence à tag {clash.Name}."]);

        if (id is null) db.RadarTags.Add(tag);
        await db.SaveChangesAsync(ct);
        return (tag, []);
    }

    public async Task<bool> DeleteTagAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var tag = await db.RadarTags.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tag is null) return false;
        // As sugestões antigas continuam no histórico (com o nome da tag gravado nelas).
        db.RadarTags.Remove(tag);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Tag a que um nome pertence (nome ou sinônimo, sem diferenciar maiúsculas).</summary>
    public static RadarTag? Resolve(IEnumerable<RadarTag> tags, string name) =>
        tags.FirstOrDefault(t => t.AllNames.Any(n => n.Equals((name ?? "").Trim(), StringComparison.OrdinalIgnoreCase)));

    // ---------- horário e execução ----------

    public TimeZoneInfo Zone(string? id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(id) ? "America/Sao_Paulo" : id); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
    }

    /// <summary>
    /// A pesquisa está para rodar? Sim quando já passou do horário diário e ainda não houve pesquisa hoje (perdeu
    /// o horário: roda assim que a tarefa conseguir), ou quando há um pedido manual mais novo que a última pesquisa.
    /// </summary>
    public async Task<RadarDue> DueAsync(string? timeZoneId, CancellationToken ct)
    {
        await EnsureDefaultsAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var settings = await db.RadarSettings.FirstAsync(s => s.Id == 1, ct);
        var tags = await db.RadarTags.AsNoTracking().Where(t => t.Active).OrderBy(t => t.Name).ToListAsync(ct);
        var zone = Zone(timeZoneId);
        var now = Now;
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(now, zone);
        var dayStartUtc = TimeZoneInfo.ConvertTimeToUtc(localNow.Date, zone);
        var last = await db.RadarRuns.AsNoTracking().OrderByDescending(r => r.StartedAtUtc).FirstOrDefaultAsync(ct);
        var tagViews = tags.Select(t => (object)new { t.Name, names = t.AllNames, include = RadarTag.SplitTerms(t.IncludeTerms), exclude = RadarTag.SplitTerms(t.ExcludeTerms) }).ToList();
        var left = SearchesLeft(settings, localNow);

        if (tags.Count == 0) return new(false, "Nenhuma tag ativa.", null, tagViews, left, settings.DailyTime, zone.Id);
        if (last is { Status: RadarRunStatus.Running } && now - last.StartedAtUtc < TimeSpan.FromHours(2))
        {
            return new(false, "Já há uma pesquisa em andamento.", last.Id, tagViews, left, settings.DailyTime, zone.Id);
        }

        if (settings.ManualRequestedAtUtc is { } manual && (last is null || manual > last.StartedAtUtc))
        {
            return new(true, "Pesquisa manual pedida no painel.", null, tagViews, left, settings.DailyTime, zone.Id);
        }

        var dailyTime = TimeOnly.TryParse(settings.DailyTime, CultureInfo.InvariantCulture, out var t0) ? t0 : new TimeOnly(8, 0);
        var ranToday = last is not null && last.StartedAtUtc >= dayStartUtc && last.Status != RadarRunStatus.Running;
        if (TimeOnly.FromDateTime(localNow) >= dailyTime && !ranToday)
        {
            var late = TimeOnly.FromDateTime(localNow) > dailyTime.AddMinutes(45);
            return new(true, late ? $"Pesquisa do dia atrasada (horário {settings.DailyTime})." : "Horário da pesquisa diária.",
                null, tagViews, left, settings.DailyTime, zone.Id);
        }

        return new(false, ranToday ? "A pesquisa de hoje já foi feita." : $"Antes do horário ({settings.DailyTime}).", null, tagViews, left, settings.DailyTime, zone.Id);
    }

    private static int SearchesLeft(RadarSettings settings, DateTime localNow) =>
        settings.SearchesDate == localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            ? Math.Max(0, settings.YouTubeSearchBudget - settings.SearchesUsed)
            : settings.YouTubeSearchBudget;

    /// <summary>Gasta uma busca do orçamento diário do YouTube. Falso = orçamento do dia acabou.</summary>
    public async Task<bool> TakeSearchAsync(string? timeZoneId, CancellationToken ct)
    {
        await EnsureDefaultsAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var settings = await db.RadarSettings.FirstAsync(s => s.Id == 1, ct);
        var today = TimeZoneInfo.ConvertTimeFromUtc(Now, Zone(timeZoneId)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (settings.SearchesDate != today)
        {
            settings.SearchesDate = today;
            settings.SearchesUsed = 0;
        }

        if (settings.SearchesUsed >= settings.YouTubeSearchBudget) return false;
        settings.SearchesUsed++;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<RadarRun> BeginRunAsync(string trigger, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var run = new RadarRun { Id = Guid.NewGuid(), StartedAtUtc = Now, Status = RadarRunStatus.Running, Trigger = trigger };
        db.RadarRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run;
    }

    public async Task RequestManualAsync(CancellationToken ct)
    {
        await EnsureDefaultsAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var settings = await db.RadarSettings.FirstAsync(s => s.Id == 1, ct);
        settings.ManualRequestedAtUtc = Now;
        settings.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<string>> UpdateSettingsAsync(string dailyTime, int budget, CancellationToken ct)
    {
        if (!TimeOnly.TryParse(dailyTime, CultureInfo.InvariantCulture, out var parsed)) return ["Horário no formato HH:mm."];
        if (budget is < 0 or > 60) return ["Buscas por dia entre 0 e 60 (cada uma usa 100 das 10.000 unidades diárias do YouTube)."];
        await EnsureDefaultsAsync(ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var settings = await db.RadarSettings.FirstAsync(s => s.Id == 1, ct);
        settings.DailyTime = parsed.ToString("HH:mm", CultureInfo.InvariantCulture);
        settings.YouTubeSearchBudget = budget;
        settings.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(ct);
        return [];
    }

    /// <summary>
    /// Recebe o resultado de uma pesquisa. Cada sugestão precisa de tag ativa, formato, título, ao menos uma fonte com
    /// link e plano de produção com ideia, gancho, roteiro e o que gravar; as que faltam algo são recusadas com o motivo.
    /// No máximo 5 por formato e tag (as de melhor posição). Menos de 5 = registrado como falta, nunca completado.
    /// </summary>
    public async Task<(RadarRun? Run, IReadOnlyList<string> Rejected)> SubmitRunAsync(Guid runId, RadarRunInput input, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var run = await db.RadarRuns.Include(r => r.Suggestions).FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return (null, ["Pesquisa não encontrada."]);
        if (run.Status != RadarRunStatus.Running) return (null, ["Esta pesquisa já foi encerrada."]);

        var tags = await db.RadarTags.Where(t => t.Active).ToListAsync(ct);
        var now = Now;
        var rejected = new List<string>();
        var accepted = new List<RadarSuggestion>();
        foreach (var (s, index) in (input.Suggestions ?? []).Select((s, i) => (s, i + 1)))
        {
            var problems = new List<string>();
            var tag = Resolve(tags, s.Tag);
            if (tag is null) problems.Add($"tag \"{s.Tag}\" não está ativa");
            if (!Enum.TryParse<RadarFormat>(s.Format, true, out var format)) problems.Add("formato deve ser Horizontal ou Vertical");
            if (string.IsNullOrWhiteSpace(s.Title)) problems.Add("sem título");
            var sources = (s.Sources ?? []).Where(src => Uri.TryCreate(src.Url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https").ToList();
            if (sources.Count == 0) problems.Add("sem fonte verificável (link http/https)");
            var missing = RequiredPlanFields.Where(f => s.Plan?[f] is null || string.IsNullOrWhiteSpace(s.Plan[f]!.ToJsonString().Trim('"', '[', ']'))).ToList();
            if (missing.Count > 0) problems.Add("plano sem " + string.Join(", ", missing));
            if (problems.Count > 0)
            {
                rejected.Add($"Sugestão {index} ({s.Title}): {string.Join("; ", problems)}.");
                continue;
            }

            accepted.Add(new RadarSuggestion
            {
                Id = Guid.NewGuid(), RunId = run.Id, TagId = tag!.Id, TagName = tag.Name, Format = format,
                Rank = s.Rank ?? index, Title = s.Title.Trim(), Category = (s.Category ?? "").Trim(), Market = (s.Market ?? "").Trim(),
                Opportunity = (s.Opportunity ?? "").Trim(), DiscoveredAtUtc = now,
                SourcesJson = JsonSerializer.Serialize(sources.Select(src => src with { ConsultedAtUtc = src.ConsultedAtUtc ?? now }), Json),
                AnalysisJson = (s.Analysis ?? []).ToJsonString(), PlanJson = (s.Plan ?? []).ToJsonString(),
            });
        }

        // Até 5 por formato e tag, na ordem de posição.
        var kept = accepted.GroupBy(a => (a.TagId, a.Format))
            .SelectMany(g => g.OrderBy(a => a.Rank).Take(PerFormat).Select((a, i) => { a.Rank = i + 1; return a; }))
            .ToList();
        foreach (var extra in accepted.Except(kept)) rejected.Add($"\"{extra.Title}\": acima de {PerFormat} para {extra.Format} em {extra.TagName}.");

        var shortfall = new List<string>();
        foreach (var tag in tags)
        {
            foreach (var format in Enum.GetValues<RadarFormat>())
            {
                var count = kept.Count(k => k.TagId == tag.Id && k.Format == format);
                if (count < PerFormat) shortfall.Add($"{tag.Name} {Label(format)}: {count} de {PerFormat}");
            }
        }

        db.RadarSuggestions.AddRange(kept);
        run.FinishedAtUtc = now;
        run.Summary = input.Summary?.Trim();
        run.Shortfall = shortfall.Count == 0 ? null
            : string.Join("; ", shortfall) + (string.IsNullOrWhiteSpace(input.Shortfall) ? "" : " — " + input.Shortfall.Trim());
        run.Status = kept.Count == 0 ? RadarRunStatus.Failed : shortfall.Count == 0 ? RadarRunStatus.Completed : RadarRunStatus.Partial;
        if (kept.Count == 0) run.Error = "Nenhuma sugestão válida. " + string.Join(" ", rejected);

        if (run.Status == RadarRunStatus.Failed) await AddFailureMessageAsync(db, run, now, ct);
        else await AddDailyMessagesAsync(db, run, kept, now, ct);
        await db.SaveChangesAsync(ct);
        return (run, rejected);
    }

    public async Task<RadarRun?> FailRunAsync(Guid runId, string error, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var run = await db.RadarRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return null;
        run.Status = RadarRunStatus.Failed;
        run.Error = string.IsNullOrWhiteSpace(error) ? "Falha sem descrição." : error.Trim();
        run.FinishedAtUtc = Now;
        await AddFailureMessageAsync(db, run, Now, ct);
        await db.SaveChangesAsync(ct);
        return run;
    }

    /// <summary>Pesquisa parada há mais de 2 horas: falhou (a tarefa caiu no meio).</summary>
    public async Task<int> ExpireStaleRunsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var limit = Now - TimeSpan.FromHours(2);
        var stale = await db.RadarRuns.Where(r => r.Status == RadarRunStatus.Running && r.StartedAtUtc < limit).ToListAsync(ct);
        foreach (var run in stale)
        {
            run.Status = RadarRunStatus.Failed;
            run.Error = "A pesquisa não terminou em 2 horas (a tarefa do Claude parou ou o computador desligou).";
            run.FinishedAtUtc = Now;
            await AddFailureMessageAsync(db, run, Now, ct);
        }

        if (stale.Count > 0) await db.SaveChangesAsync(ct);
        return stale.Count;
    }

    public async Task<(bool Found, string Message)> SetSuggestionStatusAsync(Guid id, RadarSuggestionStatus status, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var s = await db.RadarSuggestions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return (false, "Sugestão não encontrada.");
        if (s.Status == RadarSuggestionStatus.InProduction) return (true, "Esta sugestão já está em produção no Estúdio de Ideias.");
        s.Status = status;
        if (status == RadarSuggestionStatus.Saved) s.SavedAtUtc = Now;
        if (status == RadarSuggestionStatus.Ignored) s.IgnoredAtUtc = Now;
        await db.SaveChangesAsync(ct);
        return (true, status == RadarSuggestionStatus.Saved ? "Sugestão salva." : status == RadarSuggestionStatus.Ignored ? "Sugestão ignorada." : "Ok.");
    }

    // ---------- Telegram ----------

    public static string Label(RadarFormat format) => format == RadarFormat.Horizontal ? "horizontais (YouTube)" : "verticais (Shorts/Reels/TikTok)";

    private string Link(string anchor) => options.PanelUrl.TrimEnd('/') + "/#" + anchor;

    private async Task AddDailyMessagesAsync(BastDbContext db, RadarRun run, List<RadarSuggestion> kept, DateTime now, CancellationToken ct)
    {
        var header = new StringBuilder();
        header.AppendLine($"📡 Radar de Conteúdo — {now.ToLocalTime():dd/MM}");
        header.AppendLine($"{kept.Count(k => k.Format == RadarFormat.Horizontal)} horizontais e {kept.Count(k => k.Format == RadarFormat.Vertical)} verticais · " +
                          string.Join(", ", kept.Select(k => k.TagName).Distinct()));
        if (!string.IsNullOrWhiteSpace(run.Summary)) header.AppendLine().AppendLine(Trim(run.Summary, 900));
        if (run.Shortfall is not null) header.AppendLine().AppendLine("Menos oportunidades reais que o esperado: " + Trim(run.Shortfall, 400));
        header.AppendLine().Append("Painel: ").Append(Link("radar"));
        await NotificationOutbox.AddAsync(db, NotificationKind.RadarDaily, $"radar:{run.Id}:0", header.ToString(), now, ct);

        foreach (var (format, n) in new[] { (RadarFormat.Horizontal, 1), (RadarFormat.Vertical, 2) })
        {
            var items = kept.Where(k => k.Format == format).OrderBy(k => k.TagName).ThenBy(k => k.Rank).ToList();
            if (items.Count == 0) continue;
            var text = new StringBuilder();
            text.AppendLine(format == RadarFormat.Horizontal ? "▶️ HORIZONTAIS (YouTube)" : "📱 VERTICAIS (Shorts/Reels/TikTok)");
            foreach (var s in items)
            {
                var why = TryText(s.AnalysisJson, "por_que_agora") ?? TryText(s.AnalysisJson, "por_que_atencao") ?? "";
                text.AppendLine()
                    .AppendLine($"{(format == RadarFormat.Horizontal ? "H" : "V")}{s.Rank}. {s.Title}")
                    .AppendLine($"{s.Category} · {s.TagName} · {s.Market} · oportunidade {s.Opportunity}")
                    .AppendLine(Trim(why, 220))
                    .AppendLine(Link($"radar/{s.Id.ToString("N")[..8]}"));
            }

            await NotificationOutbox.AddAsync(db, NotificationKind.RadarDaily, $"radar:{run.Id}:{n}", Trim(text.ToString(), 3900), now, ct);
        }
    }

    private async Task AddFailureMessageAsync(BastDbContext db, RadarRun run, DateTime now, CancellationToken ct) =>
        await NotificationOutbox.AddAsync(db, NotificationKind.RadarDaily, $"radar:{run.Id}:falha",
            $"⚠️ Radar de Conteúdo: a pesquisa de {now.ToLocalTime():dd/MM HH:mm} falhou. Nenhuma sugestão nova hoje.\n{Trim(run.Error ?? "", 600)}\nPainel: {Link("radar")}",
            now, ct);

    private static string? TryText(string json, string field)
    {
        try { return JsonNode.Parse(json)?[field]?.ToString(); }
        catch (JsonException) { return null; }
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
