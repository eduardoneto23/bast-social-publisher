using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bast.Application.Radar;
using Bast.Domain;

namespace Bast.Application.Production;

/// <summary>
/// Etapa 4b: edição horizontal a partir do plano de edição. Renderiza com ffmpeg (render.py): teaser + trechos,
/// webcam oculta (zoom na gameplay) onde o criador não está, voz abaixada nas tosses e falas a tirar, ducking do
/// jogo sob a voz, -14 LUFS e -1 dBTP. Junto do vídeo: a lista de cortes (EDL em JSON), o projeto para o DaVinci
/// (FCPXML) e os metadados (título, descrição, hashtags). Vídeo já renderizado numa tentativa anterior é mantido.
/// </summary>
public sealed class HorizontalEditStep(IProductionTools tools, BastOptions options) : IProductionStep
{
    public const string StepName = "edicao-horizontal";

    public string Name => StepName;
    public string Title => "Edição horizontal (render, mixagem e projeto editável)";
    public ProductionStatus Status => ProductionStatus.EditingHorizontal;

    public async Task<string> RunAsync(StepContext context, CancellationToken ct)
    {
        if (tools.Ffmpeg is null) throw new ProductionStepException("ffmpeg não encontrado (Bast:FfmpegPath).", retry: true);
        if (tools.Python is null) throw new ProductionStepException("O ambiente Python da produção não está instalado (scripts\\instalar-producao.ps1).");

        var job = context.Job;
        var planPath = SelectionStep.PlanPath(context.WorkFolder);
        if (!File.Exists(planPath)) throw new ProductionStepException("O plano de edição (plano_edicao.json) sumiu da pasta de análise.");
        var plan = JsonNode.Parse(await File.ReadAllTextAsync(planPath, ct)) as JsonObject
                   ?? throw new ProductionStepException("plano_edicao.json inválido.");

        var title = plan["titulo"]?.ToString() ?? plan["horizontal"]?["titulo_ideia"]?.ToString() ?? Path.GetFileNameWithoutExtension(job.SourceFileName);
        var name = StudioService.Slug(title, 60).Replace('_', ' ').Trim();
        if (name.Length == 0) name = Path.GetFileNameWithoutExtension(job.SourceFileName);
        var folder = Path.Combine(context.WorkFolder, ProductionFolders.Horizontal);
        var output = Path.Combine(folder, $"{name} - horizontal.mp4");
        var edl = Path.ChangeExtension(output, null) + ".edl.json";

        if (!(File.Exists(output) && File.Exists(edl)))
        {
            var (game, mic) = job.AudioTracks >= 3 ? (options.ProductionGameTrack, options.ProductionMicTrack) : (0, 0);
            var cam = (options.ProductionWebcam ?? "0,0,372,268").Split(',').Select(v => int.Parse(v.Trim(), CultureInfo.InvariantCulture)).ToArray();
            var segments = new JsonArray();
            if (plan["horizontal"]?["teaser"] is JsonArray teaser && teaser.Count == 2) segments.Add(teaser.DeepClone());
            foreach (var t in plan["horizontal"]?["trechos"]?.AsArray() ?? []) segments.Add(t!.DeepClone());

            var renderPlan = new JsonObject
            {
                ["ffmpeg"] = tools.Ffmpeg,
                ["source"] = job.SourcePath,
                ["output"] = output,
                ["mode"] = "horizontal",
                ["segments"] = segments,
                ["game_track"] = game,
                ["mic_track"] = mic,
                ["cam"] = new JsonObject { ["x"] = cam[0], ["y"] = cam[1], ["w"] = cam[2], ["h"] = cam[3] },
                ["cam_hide"] = plan["webcam_oculta"]?.DeepClone() ?? new JsonArray(),
                ["mic_duck"] = plan["voz_abaixar"]?.DeepClone() ?? new JsonArray(),
                ["mic_gain_db"] = 12,
                ["loudness"] = -14,
                ["fps"] = Math.Max(24, (int)Math.Round(job.Fps)),
            };
            var renderPlanPath = Path.Combine(folder, "plano-render.json");
            await File.WriteAllTextAsync(renderPlanPath, renderPlan.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
            context.Log($"Renderizando {segments.Count} trecho(s) com ffmpeg (NVENC).", program: "render.py/ffmpeg", file: Path.GetFileName(output));
            context.ReportProgress(5);

            var result = await tools.RunScriptAsync("render.py", [renderPlanPath], ct);
            if (!result.Ok || !File.Exists(output)) throw new ProductionStepException($"O render falhou: {result.ErrorSummary}", retry: true);
            context.Log(result.StdOut.Trim().Split('\n').LastOrDefault() ?? "render ok", program: "render.py");
        }
        else
        {
            context.Log("Vídeo horizontal já renderizado numa tentativa anterior: mantido.");
        }

        context.ReportProgress(85);
        var edlNode = JsonNode.Parse(await File.ReadAllTextAsync(edl, ct))!;
        var pieces = edlNode["pieces"]!.AsArray()
            .Select(p => new FcpxmlWriter.Piece(p!["inicio"]!.GetValue<double>(), p["fim"]!.GetValue<double>(), p["webcam_oculta"]?.GetValue<bool>() == true))
            .ToList();
        var projects = Path.Combine(context.WorkFolder, ProductionFolders.Projects);
        Directory.CreateDirectory(projects);
        var fcpxml = Path.Combine(projects, $"{name} - horizontal.fcpxml");
        await File.WriteAllTextAsync(fcpxml, FcpxmlWriter.Write(job.SourcePath, job.DurationSeconds, job.Fps, job.Width, job.Height, $"{name} - horizontal", pieces), new UTF8Encoding(false), ct);

        var metadata = new StringBuilder();
        metadata.AppendLine(title).AppendLine();
        if (plan["descricao"]?.ToString() is { Length: > 0 } description) metadata.AppendLine(description).AppendLine();
        if (plan["hashtags"] is JsonArray tags && tags.Count > 0) metadata.AppendLine(string.Join(' ', tags.Select(t => t?.ToString())));
        await File.WriteAllTextAsync(Path.ChangeExtension(output, null) + ".txt", metadata.ToString(), new UTF8Encoding(true), ct);
        context.ReportProgress(100);

        var seconds = edlNode["duracao_s"]?.GetValue<double>() ?? pieces.Sum(p => p.End - p.Start);
        return $"{Path.GetFileName(output)} ({TimeSpan.FromSeconds(seconds):m\\:ss}, {pieces.Count} trechos, {pieces.Count(p => p.CamHidden)} com a webcam oculta); " +
               $"projeto do DaVinci: {Path.GetFileName(fcpxml)}.";
    }
}

/// <summary>
/// Projeto editável para o DaVinci Resolve (FCPXML 1.10): uma timeline com os mesmos cortes do vídeo final,
/// apontando para a gravação original, e marcadores onde a webcam foi ocultada (o zoom e a mixagem ficam só no
/// render: no Resolve os cortes chegam limpos para ajuste manual).
/// </summary>
public static class FcpxmlWriter
{
    public sealed record Piece(double Start, double End, bool CamHidden);

    public static string Write(string sourcePath, double sourceDuration, double fps, int width, int height, string projectName, IReadOnlyList<Piece> pieces)
    {
        var rate = Math.Max(24, (int)Math.Round(fps));
        string T(double seconds) => $"{(long)Math.Round(seconds * rate)}/{rate}s";
        var src = new Uri(Path.GetFullPath(sourcePath)).AbsoluteUri;
        var name = System.Security.SecurityElement.Escape(projectName);
        var file = System.Security.SecurityElement.Escape(Path.GetFileName(sourcePath));

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<!DOCTYPE fcpxml>");
        sb.AppendLine("<fcpxml version=\"1.10\">");
        sb.AppendLine("  <resources>");
        sb.AppendLine($"    <format id=\"r1\" name=\"FFVideoFormat{height}p{rate}\" frameDuration=\"1/{rate}s\" width=\"{width}\" height=\"{height}\"/>");
        sb.AppendLine($"    <asset id=\"r2\" name=\"{file}\" start=\"0s\" duration=\"{T(sourceDuration)}\" hasVideo=\"1\" hasAudio=\"1\" format=\"r1\" audioSources=\"1\" audioChannels=\"2\">");
        sb.AppendLine($"      <media-rep kind=\"original-media\" src=\"{System.Security.SecurityElement.Escape(src)}\"/>");
        sb.AppendLine("    </asset>");
        sb.AppendLine("  </resources>");
        sb.AppendLine("  <library>");
        sb.AppendLine($"    <event name=\"{name}\">");
        var total = pieces.Sum(p => (long)Math.Round(p.End * rate) - (long)Math.Round(p.Start * rate));
        sb.AppendLine($"      <project name=\"{name}\">");
        sb.AppendLine($"        <sequence format=\"r1\" duration=\"{total}/{rate}s\" tcStart=\"0s\" tcFormat=\"NDF\">");
        sb.AppendLine("          <spine>");
        long offset = 0;
        foreach (var (piece, i) in pieces.Select((p, i) => (p, i + 1)))
        {
            var start = (long)Math.Round(piece.Start * rate);
            var length = (long)Math.Round(piece.End * rate) - start;
            if (length <= 0) continue;
            sb.Append($"            <asset-clip ref=\"r2\" name=\"Trecho {i}\" offset=\"{offset}/{rate}s\" start=\"{start}/{rate}s\" duration=\"{length}/{rate}s\" format=\"r1\" tcFormat=\"NDF\"");
            if (piece.CamHidden)
            {
                sb.AppendLine(">");
                sb.AppendLine($"              <marker start=\"{start}/{rate}s\" duration=\"1/{rate}s\" value=\"Webcam oculta neste trecho (no render: zoom na gameplay)\"/>");
                sb.AppendLine("            </asset-clip>");
            }
            else
            {
                sb.AppendLine("/>");
            }

            offset += length;
        }

        sb.AppendLine("          </spine>");
        sb.AppendLine("        </sequence>");
        sb.AppendLine("      </project>");
        sb.AppendLine("    </event>");
        sb.AppendLine("  </library>");
        sb.AppendLine("</fcpxml>");
        return sb.ToString();
    }
}
