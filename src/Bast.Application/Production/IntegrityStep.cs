using System.Text.Json;
using Bast.Application.Import;
using Bast.Domain;

namespace Bast.Application.Production;

/// <summary>
/// Etapa 2: confere de novo o original (mesmo arquivo do recebimento, legível até o fim) e grava os
/// metadados do job (job.json) na pasta de trabalho. Só lê o original.
/// </summary>
public sealed class IntegrityStep(IVideoAnalyzer analyzer) : IProductionStep
{
    public const string StepName = "integridade";

    public string Name => StepName;
    public string Title => "Verificação de integridade";
    public ProductionStatus Status => ProductionStatus.Analyzing;

    public async Task<string> RunAsync(StepContext context, CancellationToken ct)
    {
        var job = context.Job;
        var source = new FileInfo(job.SourcePath);
        if (!source.Exists) throw new ProductionStepException("O arquivo original não está mais em 00 - GRAVACOES.");
        if (RecordingScanner.FingerprintOf(source) != job.Fingerprint)
        {
            throw new ProductionStepException("O arquivo original mudou depois do recebimento (tamanho ou data diferentes).");
        }

        context.Log("Lendo o original com ffprobe e decodificando o final.", program: "ffprobe/ffmpeg", file: source.Name);
        var result = await analyzer.AnalyzeAsync(source.FullName, ct);
        if (result.Status != VideoAnalysisStatus.Ok)
        {
            throw new ProductionStepException(result.Error ?? "O original não pôde ser lido.", retry: result.Status == VideoAnalysisStatus.ToolUnavailable);
        }

        var media = result.Analysis!;
        context.ReportProgress(80);

        var manifest = new
        {
            job.Id,
            original = new { job.SourceFileName, job.SourcePath, job.SizeBytes, job.Fingerprint },
            media = new
            {
                media.DurationSeconds,
                media.Width,
                media.Height,
                media.FrameRate,
                media.VideoCodec,
                media.AudioCodec,
                media.AudioStreams,
                media.BitRate,
                media.FormatName,
            },
            verifiedAtUtc = DateTime.UtcNow,
        };

        var target = Path.Combine(context.WorkFolder, "job.json");
        var partial = target + ".part";
        await File.WriteAllTextAsync(partial, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), ct);
        File.Move(partial, target, overwrite: true);
        context.ReportProgress(100);

        return $"{media.Width}x{media.Height}, {media.FrameRate:0.##} qps, {media.VideoCodec}, {media.AudioStreams} faixa(s) de áudio, " +
               $"{RecordingScanner.Duration(media.DurationSeconds)}; final decodificado sem erro.";
    }
}

/// <summary>Falha de etapa com mensagem para o usuário. <c>retry</c> falso = não adianta tentar de novo.</summary>
public sealed class ProductionStepException(string message, bool retry = false) : Exception(message)
{
    public bool Retry { get; } = retry;
}
