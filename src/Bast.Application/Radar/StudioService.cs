using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bast.Application.Import;
using Bast.Application.Persistence;
using Bast.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Bast.Application.Radar;

/// <summary>
/// Estúdio de Ideias: produções criadas a partir de sugestões do Radar, com pastas próprias (separadas das
/// lives). Cada produção tem um código único (BAST-AAAAMMDD-NNN) gravado no roteiro.json da pasta: é por ele,
/// e não pelo nome da pasta, que os arquivos gravados são associados. Nada daqui é publicado sem a liberação
/// individual do usuário; a publicação automática dos outros fluxos não vale para o Estúdio.
/// </summary>
public sealed class StudioService(
    IDbContextFactory<BastDbContext> factory,
    BastOptions options,
    IVideoAnalyzer analyzer,
    TimeProvider time,
    ILogger<StudioService> logger)
{
    public static readonly string[] Stages =
    [
        "00 - GRAVACOES_PENDENTES", "01 - EM_ANALISE", "02 - EM_EDICAO", "03 - RENDERIZADOS", "04 - AGUARDANDO_APROVACAO",
        "05 - APROVADOS", "06 - PUBLICADOS", "07 - ERROS_E_REVISAO", "08 - PROJETOS_E_ASSETS",
    ];

    public const string ScriptFile = "roteiro.json";
    public const string ReadableScriptFile = "ROTEIRO.txt";
    public const string DoneMarker = "PRONTO.txt";

    private static readonly string[] MediaExtensions = [".mp4", ".mov", ".mkv", ".m4v", ".wav", ".mp3", ".m4a", ".png", ".jpg", ".jpeg"];
    private static readonly TimeSpan StableFor = TimeSpan.FromMinutes(2);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public bool Enabled => !string.IsNullOrWhiteSpace(options.StudioFolder);

    public string Stage(int index) => Path.Combine(options.StudioFolder, Stages[index]);

    public void EnsureFolders()
    {
        if (!Enabled) return;
        foreach (var stage in Stages) Directory.CreateDirectory(Path.Combine(options.StudioFolder, stage));
    }

    /// <summary>Cria a produção a partir da sugestão: código, pasta própria, roteiro (JSON e texto) e checklist.</summary>
    public async Task<(StudioProduction? Production, string Message)> StartAsync(Guid suggestionId, string timeZoneId, CancellationToken ct)
    {
        if (!Enabled) return (null, "A pasta do Estúdio de Ideias não está configurada (Bast:StudioFolder).");
        await using var db = await factory.CreateDbContextAsync(ct);
        var suggestion = await db.RadarSuggestions.FirstOrDefaultAsync(s => s.Id == suggestionId, ct);
        if (suggestion is null) return (null, "Sugestão não encontrada.");
        if (suggestion.StudioProductionId is { } existing)
        {
            return (await db.StudioProductions.FirstOrDefaultAsync(p => p.Id == existing, ct), "Esta sugestão já está em produção.");
        }

        EnsureFolders();
        var now = Now;
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
        catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.Utc; }
        var day = TimeZoneInfo.ConvertTimeFromUtc(now, zone).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var sameDay = await db.StudioProductions.CountAsync(p => p.Code.StartsWith($"BAST-{day}-"), ct);
        var code = $"BAST-{day}-{sameDay + 1:000}";
        var folderName = $"BAST_{day}_{Slug(suggestion.TagName, 12)}_{Slug(suggestion.Title, 36)}_{sameDay + 1:000}";
        var folder = Path.Combine(Stage(0), folderName);

        var production = new StudioProduction
        {
            Id = Guid.NewGuid(), Code = code, SuggestionId = suggestion.Id, TagName = suggestion.TagName, Title = suggestion.Title,
            Format = suggestion.Format, PlanJson = suggestion.PlanJson, SourcesJson = suggestion.SourcesJson,
            Folder = folder, FolderName = folderName, Status = StudioStatus.AwaitingRecording,
            StatusDetail = "Grave as cenas do checklist e coloque os arquivos nesta pasta. Quando terminar, crie PRONTO.txt ou clique em \"Terminei de gravar\".",
            CreatedAtUtc = now, UpdatedAtUtc = now,
        };

        Directory.CreateDirectory(folder);
        await WriteScriptFilesAsync(production, suggestion, ct);

        db.StudioProductions.Add(production);
        suggestion.Status = RadarSuggestionStatus.InProduction;
        suggestion.StudioProductionId = production.Id;
        suggestion.SavedAtUtc ??= now;
        await NotificationOutbox.AddAsync(db, NotificationKind.StudioAlert, $"studio:{production.Id}:created",
            $"🎬 Estúdio de Ideias: {code} criado — {production.Title}.\nPasta: {folder}\nO roteiro e o checklist estão em {ReadableScriptFile}.", now, ct);
        await db.SaveChangesAsync(ct);
        return (production, $"Produção {code} criada. Pasta: {folder}");
    }

    private static async Task WriteScriptFilesAsync(StudioProduction p, RadarSuggestion s, CancellationToken ct)
    {
        var plan = JsonNode.Parse(s.PlanJson) as JsonObject ?? [];
        var manifest = new JsonObject
        {
            ["id"] = p.Id.ToString(),
            ["codigo"] = p.Code,
            ["tag"] = p.TagName,
            ["titulo"] = p.Title,
            ["formato"] = p.Format == RadarFormat.Horizontal ? "Horizontal YouTube (16:9)" : "Vertical Shorts/Reels/TikTok (9:16, BAST_VERTICAL_V1)",
            ["categoria"] = s.Category,
            ["sugestao_id"] = s.Id.ToString(),
            ["criado_em_utc"] = p.CreatedAtUtc,
            ["plano"] = plan.DeepClone(),
            ["fontes"] = JsonNode.Parse(s.SourcesJson),
            ["como_usar"] = "Grave as cenas pedidas, coloque os arquivos (vídeos, áudios, imagens) nesta pasta e, quando terminar, crie um arquivo PRONTO.txt ou clique em \"Terminei de gravar\" no painel. Não apague este arquivo: é por ele que o BAST reconhece a produção.",
        };
        await File.WriteAllTextAsync(Path.Combine(p.Folder, ScriptFile), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);

        var text = new StringBuilder();
        text.AppendLine($"ID: {p.Code}").AppendLine($"Tag: {p.TagName}").AppendLine($"Título: {p.Title}").AppendLine($"Formato: {manifest["formato"]}")
            .AppendLine($"Categoria: {s.Category}").AppendLine();
        foreach (var (key, value) in plan)
        {
            text.AppendLine(Heading(key));
            if (value is JsonArray list)
            {
                foreach (var item in list) text.AppendLine("  - " + item?.ToString());
            }
            else
            {
                text.AppendLine("  " + value?.ToString());
            }

            text.AppendLine();
        }

        text.AppendLine("FONTES");
        foreach (var src in JsonNode.Parse(s.SourcesJson)?.AsArray() ?? []) text.AppendLine("  - " + src?["url"]);
        text.AppendLine().AppendLine("Quando terminar de gravar: crie PRONTO.txt nesta pasta (ou clique em \"Terminei de gravar\" no painel).");
        await File.WriteAllTextAsync(Path.Combine(p.Folder, ReadableScriptFile), text.ToString(), new UTF8Encoding(true), ct);
    }

    private static string Heading(string key) => key.Replace('_', ' ').ToUpperInvariant();

    public async Task<(bool Found, string Message)> MarkRecordingFinishedAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var p = await db.StudioProductions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return (false, "Produção não encontrada.");
        p.RecordingFinishedAtUtc ??= Now;
        p.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(ct);
        return (true, "Ok: assim que os arquivos pararem de mudar por 2 minutos, a análise começa.");
    }

    /// <summary>Liberação individual para publicar: só depois de aprovada. Nada no Estúdio é liberado automaticamente.</summary>
    public async Task<(bool Found, string Message)> ReleaseAsync(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var p = await db.StudioProductions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return (false, "Produção não encontrada.");
        if (p.Status != StudioStatus.Approved) return (true, "Só um vídeo aprovado pode ser liberado para publicação.");
        p.Status = StudioStatus.ReleasedForPublishing;
        p.ReleasedAtUtc = Now;
        p.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(ct);
        return (true, "Liberado para publicação.");
    }

    /// <summary>
    /// Procura gravações nas pastas pendentes. A pasta é associada pelo código do roteiro.json (renomear a pasta não
    /// quebra nada). A gravação conta como terminada quando o usuário avisa (PRONTO.txt ou botão) e nenhum arquivo
    /// muda há 2 minutos nem está aberto por outro programa; aí os vídeos são conferidos (abrem do começo ao fim) e a
    /// pasta vai para 01 - EM_ANALISE. Nada é apagado.
    /// </summary>
    public async Task<int> ScanAsync(CancellationToken ct)
    {
        if (!Enabled || !Directory.Exists(Stage(0))) return 0;
        await using var db = await factory.CreateDbContextAsync(ct);
        var waiting = await db.StudioProductions
            .Where(p => p.Status == StudioStatus.AwaitingRecording || p.Status == StudioStatus.RecordingDetected)
            .ToListAsync(ct);
        if (waiting.Count == 0) return 0;

        var now = Now;
        var moved = 0;
        foreach (var dir in Directory.EnumerateDirectories(Stage(0)))
        {
            var production = await IdentifyAsync(dir, waiting, ct);
            if (production is null) continue;
            if (!string.Equals(production.Folder, dir, StringComparison.OrdinalIgnoreCase))
            {
                production.Folder = dir;
                production.FolderName = Path.GetFileName(dir);
            }

            var media = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(f => MediaExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase)).ToList();
            if (media.Count == 0) continue;

            var finished = production.RecordingFinishedAtUtc is not null || File.Exists(Path.Combine(dir, DoneMarker));
            var newest = media.Max(f => f.LastWriteTimeUtc);
            var busy = media.Any(Production.RecordingScanner.IsOpenForWriting);
            var detail = $"{media.Count} arquivo(s) na pasta ({media.Sum(f => f.Length) / 1048576.0:0} MB).";
            if (production.Status == StudioStatus.AwaitingRecording)
            {
                production.Status = StudioStatus.RecordingDetected;
                await NotificationOutbox.AddAsync(db, NotificationKind.StudioAlert, $"studio:{production.Id}:detected",
                    $"🎬 {production.Code}: gravação detectada ({detail}) Quando terminar, crie {DoneMarker} ou clique em \"Terminei de gravar\".", now, ct);
            }

            if (!finished || busy || now - newest < StableFor)
            {
                production.StatusDetail = detail + (!finished ? " Aguardando você marcar que terminou de gravar."
                    : busy ? " Algum arquivo ainda está sendo gravado ou copiado." : " Aguardando os arquivos pararem de mudar.");
                production.UpdatedAtUtc = now;
                continue;
            }

            var files = new JsonArray();
            var broken = new List<string>();
            foreach (var file in media)
            {
                var entry = new JsonObject
                {
                    ["arquivo"] = Path.GetRelativePath(dir, file.FullName),
                    ["mb"] = Math.Round(file.Length / 1048576.0, 1),
                };
                if (file.Extension is ".mp4" or ".mov" or ".mkv" or ".m4v")
                {
                    var result = await analyzer.AnalyzeAsync(file.FullName, ct);
                    if (result.Status == VideoAnalysisStatus.Ok)
                    {
                        entry["duracao_s"] = Math.Round(result.Analysis!.DurationSeconds, 1);
                        entry["resolucao"] = $"{result.Analysis.Width}x{result.Analysis.Height}";
                        entry["faixas_audio"] = result.Analysis.AudioStreams;
                    }
                    else
                    {
                        broken.Add($"{file.Name}: {result.Error}");
                    }
                }

                files.Add(entry);
            }

            production.FilesJson = files.ToJsonString();
            if (broken.Count > 0)
            {
                production.Status = StudioStatus.NeedsReview;
                production.StatusDetail = "Arquivo(s) que não abrem até o fim: " + string.Join(" ", broken);
                production.UpdatedAtUtc = now;
                continue;
            }

            var target = Path.Combine(Stage(1), Path.GetFileName(dir));
            try
            {
                Directory.Move(dir, target);
            }
            catch (IOException ex)
            {
                production.StatusDetail = $"Não foi possível mover a pasta para análise ({ex.Message}); nova tentativa no próximo ciclo.";
                continue;
            }

            production.Folder = target;
            production.Status = StudioStatus.AnalyzingFiles;
            production.StatusDetail = $"{detail} Gravação completa. A análise e a edição guiadas pelo roteiro chegam com as fases 4 a 8 da produção.";
            production.UpdatedAtUtc = now;
            moved++;
            await NotificationOutbox.AddAsync(db, NotificationKind.StudioAlert, $"studio:{production.Id}:ready",
                $"🎬 {production.Code}: gravação completa ({detail}) A pasta foi para 01 - EM_ANALISE.", now, ct);
            logger.LogInformation("Estúdio: {Code} com gravação completa.", production.Code);
        }

        await db.SaveChangesAsync(ct);
        return moved;
    }

    private static async Task<StudioProduction?> IdentifyAsync(string dir, List<StudioProduction> waiting, CancellationToken ct)
    {
        var script = Path.Combine(dir, ScriptFile);
        if (!File.Exists(script)) return null;
        try
        {
            var node = JsonNode.Parse(await File.ReadAllTextAsync(script, ct));
            var id = node?["id"]?.ToString();
            var code = node?["codigo"]?.ToString();
            return waiting.FirstOrDefault(p => p.Id.ToString() == id || p.Code == code);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Slug(string text, int max)
    {
        var normalized = (text ?? "").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
        }

        var slug = string.Join('_', sb.ToString().Split('_', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length <= max ? slug : slug[..max].TrimEnd('_');
    }
}
