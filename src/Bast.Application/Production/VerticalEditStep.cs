using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bast.Application.Radar;
using Bast.Domain;

namespace Bast.Application.Production;

/// <summary>
/// Etapa 5: um vertical para cada corte escolhido no plano de edição, no perfil BAST_VERTICAL_V1 (webcam pequena no
/// topo só com o criador presente, gameplay no centro acompanhando a ação, faixa preta embaixo). Gancho nos primeiros
/// segundos e legendas palavra a palavra da fala real do criador (nunca de trechos com a voz abaixada). Para ajuste no
/// CapCut: o trecho original limpo (16:9) e as legendas em SRT, que o CapCut importa. Corte já renderizado é mantido.
/// </summary>
public sealed class VerticalEditStep(IProductionTools tools, BastOptions options) : IProductionStep
{
    public const string StepName = "edicao-vertical";

    public string Name => StepName;
    public string Title => "Edição vertical (BAST_VERTICAL_V1, legendas e arquivos para o CapCut)";
    public ProductionStatus Status => ProductionStatus.EditingVertical;

    public sealed record Caption(double Start, double End, string Text);

    public async Task<string> RunAsync(StepContext context, CancellationToken ct)
    {
        if (tools.Ffmpeg is null) throw new ProductionStepException("ffmpeg não encontrado (Bast:FfmpegPath).", retry: true);
        if (tools.Python is null) throw new ProductionStepException("O ambiente Python da produção não está instalado (scripts\\instalar-producao.ps1).");

        var job = context.Job;
        var plan = JsonNode.Parse(await File.ReadAllTextAsync(SelectionStep.PlanPath(context.WorkFolder), ct)) as JsonObject
                   ?? throw new ProductionStepException("plano_edicao.json inválido.");
        var clips = plan["verticais"] as JsonArray ?? [];
        if (clips.Count == 0) return "O plano não tem cortes verticais (o Claude não achou momento bom o bastante).";

        var words = await LoadWordsAsync(Path.Combine(context.WorkFolder, ProductionFolders.Analysis, "transcricao.json"), ct);
        var ducked = Ranges(plan["voz_abaixar"]);
        var folder = Path.Combine(context.WorkFolder, ProductionFolders.Vertical);
        var projects = Path.Combine(context.WorkFolder, ProductionFolders.Projects);
        Directory.CreateDirectory(projects);
        var (game, mic) = job.AudioTracks >= 3 ? (options.ProductionGameTrack, options.ProductionMicTrack) : (0, 0);
        var cam = (options.ProductionWebcam ?? "0,0,372,268").Split(',').Select(v => int.Parse(v.Trim(), CultureInfo.InvariantCulture)).ToArray();

        var done = new List<string>();
        foreach (var (clip, index) in clips.Select((c, i) => (c!, i + 1)))
        {
            ct.ThrowIfCancellationRequested();
            var start = clip["inicio"]!.GetValue<double>();
            var end = clip["fim"]!.GetValue<double>();
            var title = clip["titulo"]?.ToString() ?? clip["gancho"]?.ToString() ?? $"Corte {index}";
            var name = $"{index:00} - {StudioService.Slug(title, 50).Replace('_', ' ').Trim()}";
            var output = Path.Combine(folder, name + " - vertical.mp4");
            var edl = Path.ChangeExtension(output, null) + ".edl.json";

            var captions = clip["falas"] is JsonArray falas && falas.Count > 0
                ? falas.Select(f => new Caption(f!["start"]!.GetValue<double>(), f["end"]!.GetValue<double>(), f["text"]!.ToString())).ToList()
                : CaptionsFromWords(words, start, end, ducked);
            captions = captions.Where(c => c.End > start && c.Start < end && !ducked.Any(d => Math.Max(d.Start, c.Start) < Math.Min(d.End, c.End))).ToList();

            if (!(File.Exists(output) && File.Exists(edl)))
            {
                var renderPlan = new JsonObject
                {
                    ["ffmpeg"] = tools.Ffmpeg,
                    ["source"] = job.SourcePath,
                    ["output"] = output,
                    ["mode"] = "vertical",
                    ["profile"] = "BAST_VERTICAL_V1",
                    ["segments"] = new JsonArray(new JsonArray(start, end)),
                    ["game_track"] = game,
                    ["mic_track"] = mic,
                    ["cam"] = new JsonObject { ["x"] = cam[0], ["y"] = cam[1], ["w"] = cam[2], ["h"] = cam[3] },
                    ["cam_hide"] = plan["webcam_oculta"]?.DeepClone() ?? new JsonArray(),
                    ["mic_duck"] = plan["voz_abaixar"]?.DeepClone() ?? new JsonArray(),
                    ["mic_gain_db"] = 12,
                    ["loudness"] = -14,
                    ["fps"] = Math.Max(24, (int)Math.Round(job.Fps)),
                    ["hook"] = clip["gancho"]?.ToString() ?? "",
                    ["captions"] = new JsonArray(captions.Select(c => (JsonNode)new JsonObject { ["start"] = c.Start, ["end"] = c.End, ["text"] = c.Text }).ToArray()),
                    ["fonts_dir"] = "C:/Windows/Fonts",
                };
                if (clip["foco"] is JsonArray focus && focus.Count > 0) renderPlan["focus"] = focus.DeepClone();

                var planPath = Path.Combine(folder, $"{name} - plano-render.json");
                await File.WriteAllTextAsync(planPath, renderPlan.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);

                // Prévia da composição antes do render definitivo (meio do corte).
                var preview = await tools.RunScriptAsync("render.py", [planPath, "--previa", ((start + end) / 2).ToString("0.###", CultureInfo.InvariantCulture),
                    Path.Combine(folder, $"{name} - previa.png")], ct);
                if (!preview.Ok) context.Log($"Prévia de {name} não gerada: {preview.ErrorSummary}", ProductionLogLevel.Warning);

                context.Log($"Renderizando {name} ({end - start:0} s).", program: "render.py/ffmpeg", file: Path.GetFileName(output));
                var result = await tools.RunScriptAsync("render.py", [planPath], ct);
                if (!result.Ok || !File.Exists(output)) throw new ProductionStepException($"O render de {name} falhou: {result.ErrorSummary}", retry: true);
            }
            else
            {
                context.Log($"{name}: já renderizado, mantido.");
            }

            // Arquivos para ajuste no CapCut: o trecho original limpo e as legendas em SRT (tempos do corte).
            var raw = Path.Combine(projects, $"{name} - trecho original 16x9.mp4");
            if (!File.Exists(raw))
            {
                var cut = await tools.RunAsync(tools.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-ss", Seconds(start), "-to", Seconds(end),
                    "-i", job.SourcePath, "-map", "0:v:0", "-map", "0:a", "-c:v", "h264_nvenc", "-preset", "p5", "-cq", "20", "-c:a", "copy",
                    raw + ".part.mp4"], ct);
                if (cut.Ok) File.Move(raw + ".part.mp4", raw, overwrite: true);
                else context.Log($"Trecho original de {name} não gerado: {cut.ErrorSummary}", ProductionLogLevel.Warning);
            }

            await File.WriteAllTextAsync(Path.Combine(projects, $"{name} - legendas.srt"), Srt(captions, start), new UTF8Encoding(false), ct);
            var metadata = new StringBuilder().AppendLine(title).AppendLine();
            if (clip["legenda"]?.ToString() is { Length: > 0 } caption) metadata.AppendLine(caption).AppendLine();
            if (plan["hashtags"] is JsonArray tags && tags.Count > 0) metadata.AppendLine(string.Join(' ', tags.Select(t => t?.ToString())));
            await File.WriteAllTextAsync(Path.ChangeExtension(output, null) + ".txt", metadata.ToString(), new UTF8Encoding(true), ct);

            done.Add($"{name} ({end - start:0} s, {captions.Count} legenda(s))");
            context.ReportProgress(index * 100 / clips.Count);
        }

        return $"{done.Count} vertical(is): " + string.Join("; ", done) + ".";
    }

    private static string Seconds(double s) => s.ToString("0.###", CultureInfo.InvariantCulture);

    private static List<(double Start, double End)> Ranges(JsonNode? node) =>
        (node as JsonArray ?? []).OfType<JsonArray>().Where(p => p.Count == 2)
            .Select(p => (p[0]!.GetValue<double>(), p[1]!.GetValue<double>())).ToList();

    private sealed record Word(double Start, double End, string Text);

    private static async Task<List<Word>> LoadWordsAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return [];
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path, ct));
        return (node?["segments"] as JsonArray ?? []).SelectMany(s => s?["words"] as JsonArray ?? [])
            .Select(w => new Word(w!["start"]!.GetValue<double>(), w["end"]!.GetValue<double>(), w["word"]!.ToString().Trim()))
            .Where(w => w.Text.Length > 0).ToList();
    }

    /// <summary>
    /// Legendas a partir das palavras transcritas dentro do corte: linhas de até 3 palavras e 18 caracteres, quebrando
    /// em pausa maior que 0,6 s. Palavras em trechos de voz abaixada (tosse, fala a tirar) não entram.
    /// </summary>
    private static List<Caption> CaptionsFromWords(List<Word> words, double start, double end, List<(double Start, double End)> ducked)
    {
        var inside = words.Where(w => w.Start >= start && w.End <= end && !ducked.Any(d => Math.Max(d.Start, w.Start) < Math.Min(d.End, w.End))).ToList();
        var captions = new List<Caption>();
        var line = new List<Word>();
        void Flush()
        {
            if (line.Count == 0) return;
            captions.Add(new Caption(line[0].Start, Math.Max(line[^1].End, line[0].Start + 0.4), string.Join(' ', line.Select(w => w.Text))));
            line.Clear();
        }

        foreach (var word in inside)
        {
            var length = line.Sum(w => w.Text.Length + 1) + word.Text.Length;
            if (line.Count > 0 && (line.Count >= 3 || length > 18 || word.Start - line[^1].End > 0.6)) Flush();
            line.Add(word);
        }

        Flush();
        return captions;
    }

    /// <summary>SRT com os tempos do corte (0 = início do vertical), sem as marcações de destaque.</summary>
    public static string Srt(IReadOnlyList<Caption> captions, double clipStart)
    {
        static string T(double s)
        {
            var t = TimeSpan.FromMilliseconds(Math.Round(Math.Max(0, s) * 1000));
            return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";
        }

        var sb = new StringBuilder();
        foreach (var (c, i) in captions.Select((c, i) => (c, i + 1)))
        {
            sb.AppendLine(i.ToString(CultureInfo.InvariantCulture))
              .AppendLine($"{T(c.Start - clipStart)} --> {T(c.End - clipStart)}")
              .AppendLine(c.Text.Replace("*", ""))
              .AppendLine();
        }

        return sb.ToString();
    }
}
