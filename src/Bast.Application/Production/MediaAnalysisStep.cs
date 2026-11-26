using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bast.Domain;

namespace Bast.Application.Production;

/// <summary>
/// Etapa 3: análise automática da gravação. Extrai as faixas de áudio (jogo e microfone separados), quadros
/// e folhas de contato, o recorte da webcam; transcreve a fala; detecta tosse, pigarro, risada e grito no
/// microfone; presença do criador na câmera; exposição explícita (NudeNet); tela preta, congelamento, trocas
/// de cena e picos de voz. Junta tudo em "1 - analise\analise.json", base da seleção de momentos (fase 4).
/// Cada parte só roda se o resultado dela ainda não existe: numa retomada, o que já foi feito é mantido.
/// Os detectores apontam suspeitas; eles não aprovam nada sozinhos (a revisão visual vem depois).
/// </summary>
public sealed class MediaAnalysisStep(IProductionTools tools, BastOptions options) : IProductionStep
{
    public const string StepName = "analise-midia";

    public string Name => StepName;
    public string Title => "Análise de mídia (áudio, fala, câmera, conteúdo sensível)";
    public ProductionStatus Status => ProductionStatus.Analyzing;

    private sealed record Part(string Key, string Title, int Weight, Func<string, bool> Done, Func<Ctx, CancellationToken, Task<string>> Run);

    private sealed record Ctx(StepContext Step, string Dir, string Source, int Game, int Mic, int[] Cam);

    public async Task<string> RunAsync(StepContext context, CancellationToken ct)
    {
        if (tools.Ffmpeg is null) throw new ProductionStepException("ffmpeg não encontrado (Bast:FfmpegPath).", retry: true);
        if (tools.Python is null)
        {
            throw new ProductionStepException(
                "O ambiente Python da produção não está instalado. Rode scripts\\instalar-producao.ps1 e retome o job.", retry: false);
        }

        var job = context.Job;
        var dir = Path.Combine(context.WorkFolder, ProductionFolders.Analysis);
        Directory.CreateDirectory(dir);
        var (game, mic) = ConfiguredTracks(job.AudioTracks);
        if (job.AudioTracks < 3)
        {
            context.Log("Gravação sem faixas separadas: jogo e microfone vêm da mesma faixa; o controle da voz fica limitado.",
                ProductionLogLevel.Warning);
        }

        var ctx = new Ctx(context, dir, job.SourcePath, game, mic, ParseCam(options.ProductionWebcam));

        var parts = Parts(job.DurationSeconds);
        var total = parts.Sum(p => p.Weight);
        var doneWeight = 0;
        foreach (var part in parts)
        {
            ct.ThrowIfCancellationRequested();
            if (part.Done(dir))
            {
                context.Log($"{part.Title}: já feito, mantido.");
            }
            else
            {
                context.Log($"{part.Title}: iniciando.");
                var result = await part.Run(ctx, ct);
                context.Log($"{part.Title}: {result}");
            }

            doneWeight += part.Weight;
            context.ReportProgress(doneWeight * 100 / total);
        }

        return await WriteManifestAsync(ctx, ct);
    }

    /// <summary>OBS com faixas separadas: 0 = mix, 1 = jogo, 2 = microfone (configurável). Sem separação, tudo vem do mix.</summary>
    private (int Game, int Mic) ConfiguredTracks(int audioTracks) =>
        audioTracks >= 3 ? (options.ProductionGameTrack, options.ProductionMicTrack) : (0, 0);

    private static int[] ParseCam(string text)
    {
        var parts = (text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : -1).ToArray();
        return parts.Length == 4 && parts.All(p => p >= 0) && parts[2] > 0 && parts[3] > 0 ? parts : [0, 0, 372, 268];
    }

    private List<Part> Parts(double duration)
    {
        var frames = (int)Math.Floor(duration);
        bool Has(string dir, string file) => new FileInfo(Path.Combine(dir, file)) is { Exists: true, Length: > 0 };
        bool HasFrames(string dir, string sub, string prefix) =>
            Directory.Exists(Path.Combine(dir, sub)) && Directory.EnumerateFiles(Path.Combine(dir, sub), prefix + "*.jpg").Count() >= Math.Max(1, frames - 2);

        return
        [
            new("audio", "Faixas de áudio", 2,
                d => Has(d, "game.wav") && Has(d, "mic.wav") && Has(d, "mic16.wav"),
                async (c, ct) =>
                {
                    var (game, mic) = ConfiguredTracks(c.Step.Job.AudioTracks);
                    await Ffmpeg(c, ct, "-i", c.Source,
                        "-map", $"0:a:{game}", "-c:a", "pcm_s16le", "-ar", "48000", Tmp(c, "game.wav"),
                        "-map", $"0:a:{mic}", "-c:a", "pcm_s16le", "-ar", "48000", Tmp(c, "mic.wav"),
                        "-map", $"0:a:{mic}", "-ac", "1", "-ar", "16000", Tmp(c, "mic16.wav"));
                    foreach (var f in new[] { "game.wav", "mic.wav", "mic16.wav" }) Promote(c, f);
                    return $"jogo (faixa {game}) e microfone (faixa {mic}) separados.";
                }),
            new("frames", "Quadros (1 por segundo)", 6,
                d => HasFrames(d, "frames", "f"),
                async (c, ct) =>
                {
                    var target = Path.Combine(c.Dir, "frames");
                    Recreate(target);
                    await Ffmpeg(c, ct, "-i", c.Source, "-an", "-vf", "fps=1,scale=640:360", "-q:v", "4",
                        Path.Combine(target, "f%04d.jpg"));
                    return $"{Directory.EnumerateFiles(target, "f*.jpg").Count()} quadros.";
                }),
            new("folhas", "Folhas de contato", 1,
                d => Directory.Exists(Path.Combine(d, "folhas")) && Directory.EnumerateFiles(Path.Combine(d, "folhas"), "folha*.jpg").Any(),
                async (c, ct) =>
                {
                    var target = Path.Combine(c.Dir, "folhas");
                    Recreate(target);
                    await Ffmpeg(c, ct, "-framerate", "1", "-i", Path.Combine(c.Dir, "frames", "f%04d.jpg"), "-vf",
                        "scale=320:180,drawtext=fontfile='C\\:/Windows/Fonts/arialbd.ttf':text='%{eif\\:floor(n/60)\\:d}m%{eif\\:mod(n,60)\\:d\\:2}':" +
                        "x=4:y=4:fontsize=20:fontcolor=yellow:box=1:boxcolor=black@0.6,tile=6x5",
                        "-q:v", "3", Path.Combine(target, "folha%02d.jpg"));
                    return $"{Directory.EnumerateFiles(target, "folha*.jpg").Count()} folhas (30 s cada).";
                }),
            new("cam", "Recorte da webcam", 4,
                d => HasFrames(d, "cam", "cam"),
                async (c, ct) =>
                {
                    var target = Path.Combine(c.Dir, "cam");
                    Recreate(target);
                    await Ffmpeg(c, ct, "-i", c.Source, "-an", "-vf", $"fps=1,crop={c.Cam[2]}:{c.Cam[3]}:{c.Cam[0]}:{c.Cam[1]}",
                        "-q:v", "3", Path.Combine(target, "cam%04d.jpg"));
                    return $"{Directory.EnumerateFiles(target, "cam*.jpg").Count()} recortes ({c.Cam[2]}x{c.Cam[3]}).";
                }),
            new("transcricao", "Transcrição da fala", 8,
                d => Has(d, "transcricao.json"),
                (c, ct) => Script(c, ct, "transcrever.py", Path.Combine(c.Dir, "mic16.wav"), Path.Combine(c.Dir, "transcricao.json"),
                    options.ProductionWhisperModel, "auto")),
            new("eventos", "Tosse, pigarro, risada e grito", 4,
                d => Has(d, "eventos_audio.json"),
                (c, ct) => Script(c, ct, "eventos_audio.py", Path.Combine(c.Dir, "mic16.wav"), Path.Combine(c.Dir, "eventos_audio.json"))),
            new("camera", "Presença na webcam", 2,
                d => Has(d, "camera.json"),
                (c, ct) => Script(c, ct, "presenca_camera.py", Path.Combine(c.Dir, "cam"), Path.Combine(c.Dir, "camera.json"))),
            new("nudez", "Conteúdo explícito (NudeNet)", 4,
                d => Has(d, "nudez.json"),
                (c, ct) => Script(c, ct, "nudez.py", Path.Combine(c.Dir, "frames"), Path.Combine(c.Dir, "nudez.json"))),
            new("sinais", "Tela preta, congelamento, cenas e picos de voz", 3,
                d => Has(d, "sinais.json"),
                (c, ct) => Script(c, ct, "sinais.py", tools.Ffmpeg!, c.Source, Path.Combine(c.Dir, "mic16.wav"), Path.Combine(c.Dir, "sinais.json"))),
        ];
    }

    private static string Tmp(Ctx c, string file) => Path.Combine(c.Dir, file + ".part.wav");

    private static void Promote(Ctx c, string file) => File.Move(Tmp(c, file), Path.Combine(c.Dir, file), overwrite: true);

    private static void Recreate(string folder)
    {
        // Quadros de uma tentativa interrompida nunca se misturam com os novos.
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
    }

    private async Task Ffmpeg(Ctx c, CancellationToken ct, params string[] args)
    {
        var result = await tools.RunAsync(tools.Ffmpeg!, ["-hide_banner", "-loglevel", "error", "-y", .. args], ct);
        if (!result.Ok) throw new ProductionStepException($"ffmpeg falhou: {result.ErrorSummary}", retry: true);
    }

    private async Task<string> Script(Ctx c, CancellationToken ct, string script, params string[] args)
    {
        var result = await tools.RunScriptAsync(script, args, ct);
        if (!result.Ok) throw new ProductionStepException($"{script} falhou: {result.ErrorSummary}", retry: true);
        var line = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return string.IsNullOrWhiteSpace(line) ? "ok." : line;
    }

    /// <summary>Resumo da análise (analise.json) e alertas para as próximas fases.</summary>
    private static async Task<string> WriteManifestAsync(Ctx c, CancellationToken ct)
    {
        JsonNode? Load(string file)
        {
            var path = Path.Combine(c.Dir, file);
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
        }

        var transcript = Load("transcricao.json");
        var events = Load("eventos_audio.json")?["events"]?.AsArray() ?? [];
        var camera = Load("camera.json");
        var nudity = Load("nudez.json")?["flags"]?.AsArray() ?? [];
        var signals = Load("sinais.json");

        var segments = transcript?["segments"]?.AsArray() ?? [];
        var absences = camera?["absent"]?.AsArray() ?? [];
        var presentSeconds = camera?["seconds"]?.AsArray().Count(s => s?["face"]?.GetValue<bool>() == true) ?? 0;
        var totalSeconds = camera?["seconds"]?.AsArray().Count ?? 0;
        var eventCounts = events.GroupBy(e => e?["label"]?.GetValue<string>() ?? "?").ToDictionary(g => g.Key, g => g.Count());

        var alerts = new List<string>();
        if (nudity.Count > 0) alerts.Add($"Possível conteúdo explícito em {nudity.Count} trecho(s): revisão visual obrigatória antes de usar.");
        if (absences.Count > 0) alerts.Add($"Criador fora da câmera em {absences.Count} trecho(s) (confira: perfil e luz enganam o detector).");
        if (eventCounts.Count > 0) alerts.Add("Ruídos no microfone: " + string.Join(", ", eventCounts.Select(kv => $"{kv.Key} {kv.Value}")) + ".");
        if ((signals?["black"]?.AsArray().Count ?? 0) > 0) alerts.Add($"Tela preta em {signals!["black"]!.AsArray().Count} trecho(s).");

        var manifest = new JsonObject
        {
            ["job"] = c.Step.Job.Id.ToString(),
            ["original"] = c.Step.Job.SourceFileName,
            ["duracao_s"] = c.Step.Job.DurationSeconds,
            ["faixas"] = new JsonObject { ["jogo"] = c.Game, ["microfone"] = c.Mic },
            ["webcam"] = new JsonArray(c.Cam.Select(v => (JsonNode)v).ToArray()),
            ["arquivos"] = new JsonObject
            {
                ["quadros"] = "frames/f%04d.jpg (1 por segundo, f0001 = 0 s)",
                ["folhas"] = "folhas/folhaNN.jpg (30 s cada)",
                ["webcam"] = "cam/cam%04d.jpg",
                ["transcricao"] = "transcricao.json",
                ["eventos_audio"] = "eventos_audio.json",
                ["camera"] = "camera.json",
                ["nudez"] = "nudez.json",
                ["sinais"] = "sinais.json",
            },
            ["fala"] = new JsonObject
            {
                ["trechos"] = segments.Count,
                ["palavras"] = segments.Sum(s => s?["words"]?.AsArray().Count ?? 0),
                ["modelo"] = transcript?["model"]?.GetValue<string>(),
                ["dispositivo"] = transcript?["device"]?.GetValue<string>(),
            },
            ["eventos_audio"] = new JsonObject(eventCounts.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["camera"] = new JsonObject { ["presente_s"] = presentSeconds, ["total_s"] = totalSeconds, ["ausencias"] = absences.DeepClone() },
            ["nudez"] = nudity.DeepClone(),
            ["sinais"] = new JsonObject
            {
                ["tela_preta"] = signals?["black"]?.DeepClone(),
                ["congelado"] = signals?["freeze"]?.DeepClone(),
                ["trocas_de_cena"] = signals?["scene_changes"]?.AsArray().Count ?? 0,
                ["picos_de_voz"] = signals?["voice_peaks"]?.DeepClone(),
            },
            ["alertas"] = new JsonArray(alerts.Select(a => (JsonNode)a).ToArray()),
            ["gerado_em_utc"] = DateTime.UtcNow,
        };

        var target = Path.Combine(c.Dir, "analise.json");
        await File.WriteAllTextAsync(target + ".part", manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
        File.Move(target + ".part", target, overwrite: true);

        var words = manifest["fala"]!["palavras"]!.GetValue<int>();
        return $"{segments.Count} trechos de fala ({words} palavras); câmera {presentSeconds}/{totalSeconds} s com o criador; " +
               $"{events.Count} ruído(s) no microfone; {nudity.Count} suspeita(s) de conteúdo explícito; " +
               $"{manifest["sinais"]!["trocas_de_cena"]} trocas de cena." + (alerts.Count > 0 ? " Alertas: " + string.Join(" ", alerts) : "");
    }
}
