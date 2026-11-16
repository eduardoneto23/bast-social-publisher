using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Bast.Application;
using Bast.Application.Import;
using Microsoft.Extensions.Options;

namespace Bast.Infrastructure.Media;

/// <summary>Localiza ffprobe/ffmpeg: caminho configurado ou, se vazio, o PATH do processo.</summary>
public sealed class MediaTools(IOptions<BastOptions> options)
{
    public string? Ffprobe => Locate(options.Value.FfprobePath, "ffprobe");
    public string? Ffmpeg => Locate(options.Value.FfmpegPath, "ffmpeg");

    private static string? Locate(string configured, string name)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        var fileName = OperatingSystem.IsWindows() ? name + ".exe" : name;
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(dir => Path.Combine(dir, fileName))
            .FirstOrDefault(File.Exists);
    }
}

/// <summary>
/// Valida a integridade do vídeo em duas etapas: ffprobe lê a estrutura e os metadados;
/// ffmpeg decodifica o trecho final, o que detecta arquivos truncados por cópia incompleta.
/// </summary>
public sealed class FfprobeVideoAnalyzer(MediaTools tools) : IVideoAnalyzer
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    private const double TailSeconds = 3;

    public async Task<VideoAnalysisResult> AnalyzeAsync(string videoPath, CancellationToken ct)
    {
        var ffprobe = tools.Ffprobe;
        var ffmpeg = tools.Ffmpeg;
        if (ffprobe is null || ffmpeg is null)
        {
            return VideoAnalysisResult.Unavailable(
                "ffprobe/ffmpeg não encontrados. Configure Bast:FfprobePath e Bast:FfmpegPath com o caminho completo dos executáveis.");
        }

        ProcessResult probe;
        try
        {
            probe = await RunAsync(ffprobe, ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", videoPath], ct);
        }
        catch (Exception ex) when (ex is Win32Exception or TimeoutException)
        {
            return VideoAnalysisResult.Unavailable($"Falha ao executar ffprobe: {ex.Message}");
        }

        if (probe.ExitCode != 0)
        {
            return VideoAnalysisResult.Invalid($"ffprobe não conseguiu ler o arquivo: {FirstLine(probe.StdErr)}");
        }

        VideoAnalysis analysis;
        try
        {
            var parsed = Parse(probe.StdOut);
            if (parsed.Error is not null) return VideoAnalysisResult.Invalid(parsed.Error);
            analysis = parsed.Analysis!;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return VideoAnalysisResult.Invalid($"Metadados do vídeo ilegíveis: {ex.Message}");
        }

        List<string> args = ["-nostdin", "-v", "error", "-xerror"];
        if (analysis.DurationSeconds > TailSeconds * 2)
        {
            args.AddRange(["-sseof", (-TailSeconds).ToString(CultureInfo.InvariantCulture)]);
        }

        args.AddRange(["-i", videoPath, "-map", "0:v:0", "-f", "null", "-"]);

        ProcessResult decode;
        try
        {
            decode = await RunAsync(ffmpeg, args, ct);
        }
        catch (Exception ex) when (ex is Win32Exception or TimeoutException)
        {
            return VideoAnalysisResult.Unavailable($"Falha ao executar ffmpeg: {ex.Message}");
        }

        if (decode.ExitCode != 0 || !string.IsNullOrWhiteSpace(decode.StdErr))
        {
            return VideoAnalysisResult.Invalid($"O trecho final do vídeo não pôde ser decodificado (arquivo truncado ou corrompido): {FirstLine(decode.StdErr)}");
        }

        return VideoAnalysisResult.Ok(analysis);
    }

    internal static (VideoAnalysis? Analysis, string? Error) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("streams", out var streams) || !root.TryGetProperty("format", out var format))
        {
            return (null, "O arquivo não contém fluxos de mídia reconhecíveis.");
        }

        JsonElement? video = null;
        JsonElement? audio = null;
        foreach (var stream in streams.EnumerateArray())
        {
            var type = GetString(stream, "codec_type");
            var isCover = stream.TryGetProperty("disposition", out var disp) &&
                          disp.TryGetProperty("attached_pic", out var pic) && pic.ValueKind == JsonValueKind.Number && pic.GetInt32() == 1;
            if (type == "video" && !isCover) video ??= stream;
            if (type == "audio") audio ??= stream;
        }

        if (video is null) return (null, "O arquivo não contém fluxo de vídeo.");

        var v = video.Value;
        var width = v.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
        var height = v.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
        if (width <= 0 || height <= 0) return (null, "Resolução do vídeo inválida.");

        var rotation = ReadRotation(v);
        if (Math.Abs(rotation) % 180 == 90) (width, height) = (height, width);

        var duration = ParseDouble(GetString(format, "duration")) ?? ParseDouble(GetString(v, "duration")) ?? 0;
        if (duration <= 0) return (null, "Duração do vídeo inválida ou ausente.");

        var bitRate = long.TryParse(GetString(format, "bit_rate"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var br) ? br : (long?)null;

        return (new VideoAnalysis(
            duration,
            width,
            height,
            GetString(v, "codec_name") ?? "",
            audio is null ? null : GetString(audio.Value, "codec_name"),
            ParseRate(GetString(v, "avg_frame_rate")) ?? ParseRate(GetString(v, "r_frame_rate")) ?? 0,
            bitRate,
            GetString(format, "format_name") ?? ""), null);
    }

    private static int ReadRotation(JsonElement stream)
    {
        if (stream.TryGetProperty("side_data_list", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.TryGetProperty("rotation", out var r) && r.ValueKind == JsonValueKind.Number)
                {
                    return (int)Math.Round(r.GetDouble());
                }
            }
        }

        if (stream.TryGetProperty("tags", out var tags) &&
            int.TryParse(GetString(tags, "rotate"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rotate))
        {
            return rotate;
        }

        return 0;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? ParseDouble(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static double? ParseRate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var parts = text.Split('/');
        var num = ParseDouble(parts[0]);
        var den = parts.Length > 1 ? ParseDouble(parts[1]) : 1;
        return num is null || den is null or 0 ? null : num / den;
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "sem detalhes";

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

    private static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi) ?? throw new Win32Exception($"Não foi possível iniciar {fileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(fileName)} excedeu {Timeout.TotalMinutes:0} minutos.");
        }

        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }
}
