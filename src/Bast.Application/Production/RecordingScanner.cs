using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Bast.Application.Import;
using Bast.Application.Persistence;
using Bast.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Bast.Application.Production;

/// <summary>Como uma gravação da pasta 00 - GRAVACOES foi vista na última varredura (para o painel).</summary>
public sealed record RecordingView(string FileName, long SizeBytes, string State, string Detail, Guid? JobId);

/// <summary>
/// Acha as gravações do OBS que terminaram e cria um job para cada uma. O arquivo fica onde o OBS gravou:
/// nada é movido, renomeado ou alterado. Uma gravação só conta como pronta quando, ao mesmo tempo:
/// o nome não é de Replay Buffer nem captura de tela; o OBS largou o arquivo (ninguém mais o tem aberto
/// para escrita); tamanho e data não mudam há <see cref="BastOptions.ProductionStableSeconds"/>; o ffprobe lê
/// duração, vídeo e áudio; os últimos segundos decodificam sem erro (não está truncada); e a duração passa de
/// <see cref="BastOptions.ProductionMinDurationSeconds"/> (as mais curtas são testes).
/// </summary>
public sealed class RecordingScanner(
    IDbContextFactory<BastDbContext> factory,
    Workspace workspace,
    BastOptions options,
    IVideoAnalyzer analyzer,
    ProductionPipeline pipeline,
    TimeProvider time,
    ILogger<RecordingScanner> logger)
{
    private static readonly string[] Extensions = [".mp4", ".mov", ".mkv", ".m4v"];

    private sealed record Seen(long Size, DateTime WriteUtc, DateTime SinceUtc);

    private readonly ConcurrentDictionary<string, Seen> _seen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Veredito definitivo por impressão digital (curta, ilegível): não analisa de novo.</summary>
    private readonly ConcurrentDictionary<string, RecordingView> _verdicts = new();

    private IReadOnlyList<Regex>? _ignore;

    public IReadOnlyList<RecordingView> LastScan { get; private set; } = [];

    public string Folder => workspace.PathOf(WorkspaceFolder.Recordings);

    private IReadOnlyList<Regex> Ignore => _ignore ??= options.ProductionIgnorePatterns
        .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToList();

    public static string FingerprintOf(FileInfo file) =>
        $"{file.Name}|{file.Length}|{file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)}";

    /// <returns>Quantos jobs foram criados.</returns>
    public async Task<int> ScanAsync(CancellationToken ct)
    {
        if (!Directory.Exists(Folder))
        {
            LastScan = [];
            return 0;
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        var jobs = await db.ProductionJobs.AsNoTracking()
            .Select(j => new { j.Id, j.Fingerprint, j.SourcePath })
            .ToListAsync(ct);
        var byFingerprint = jobs.ToDictionary(j => j.Fingerprint, j => j.Id);

        var now = time.GetUtcNow().UtcDateTime;
        var views = new List<RecordingView>();
        var created = 0;
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in new DirectoryInfo(Folder).EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            present.Add(file.FullName);
            var fingerprint = FingerprintOf(file);

            if (byFingerprint.TryGetValue(fingerprint, out var jobId))
            {
                views.Add(new(file.Name, file.Length, "Job criado", "Gravação já registrada na produção.", jobId));
                continue;
            }

            if (Ignore.FirstOrDefault(r => r.IsMatch(file.Name)) is { } pattern)
            {
                views.Add(new(file.Name, file.Length, "Ignorado", $"Nome de Replay Buffer ou captura (padrão \"{pattern}\").", null));
                continue;
            }

            if (!Extensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
            {
                views.Add(new(file.Name, file.Length, "Ignorado", "Não é um arquivo de vídeo de gravação.", null));
                continue;
            }

            if (_verdicts.TryGetValue(fingerprint, out var verdict))
            {
                views.Add(verdict);
                continue;
            }

            // Tamanho ou data mudaram: ainda gravando (ou copiando). O relógio de estabilidade recomeça.
            var seen = _seen.AddOrUpdate(file.FullName,
                _ => new Seen(file.Length, file.LastWriteTimeUtc, now),
                (_, old) => old.Size == file.Length && old.WriteUtc == file.LastWriteTimeUtc ? old : new Seen(file.Length, file.LastWriteTimeUtc, now));

            if (IsOpenForWriting(file))
            {
                views.Add(new(file.Name, file.Length, "Gravando", "O OBS ainda está com o arquivo aberto.", null));
                continue;
            }

            var stableFor = now - seen.SinceUtc;
            if (stableFor < TimeSpan.FromSeconds(options.ProductionStableSeconds))
            {
                views.Add(new(file.Name, file.Length, "Estabilizando",
                    $"Sem mudanças há {stableFor.TotalSeconds:0} s; conta como pronta com {options.ProductionStableSeconds} s.", null));
                continue;
            }

            var analysis = await analyzer.AnalyzeAsync(file.FullName, ct);
            if (analysis.Status == VideoAnalysisStatus.ToolUnavailable)
            {
                views.Add(new(file.Name, file.Length, "Aguardando", analysis.Error ?? "ffprobe/ffmpeg indisponíveis.", null));
                continue;
            }

            // A análise leva segundos: se o arquivo mudou nesse meio-tempo, ele não estava pronto.
            file.Refresh();
            if (FingerprintOf(file) != fingerprint)
            {
                views.Add(new(file.Name, file.Length, "Gravando", "O arquivo mudou durante a verificação.", null));
                continue;
            }

            if (analysis.Status == VideoAnalysisStatus.Invalid)
            {
                var invalid = new RecordingView(file.Name, file.Length, "Ilegível",
                    $"{analysis.Error} Se o OBS ainda estiver finalizando, ela é verificada de novo quando o arquivo mudar.", null);
                _verdicts[fingerprint] = invalid;
                views.Add(invalid);
                logger.LogWarning("Produção: {File} não pode ser usada: {Error}", file.Name, analysis.Error);
                continue;
            }

            var media = analysis.Analysis!;
            if (media.DurationSeconds < options.ProductionMinDurationSeconds)
            {
                var shortOne = new RecordingView(file.Name, file.Length, "Ignorado",
                    $"Gravação de {Duration(media.DurationSeconds)}: menos de {Duration(options.ProductionMinDurationSeconds)} é tratada como teste.", null);
                _verdicts[fingerprint] = shortOne;
                views.Add(shortOne);
                continue;
            }

            var job = NewJob(file, fingerprint, media, now);
            pipeline.EnsureSteps(db, job);
            db.ProductionJobs.Add(job);
            ProductionJournal.Add(db, job.Id, now, ProductionLogLevel.Info, "recebida",
                $"Gravação pronta: {file.Name} ({Duration(media.DurationSeconds)}, {media.Width}x{media.Height}, {media.FrameRate:0.##} qps, " +
                $"{media.AudioStreams} faixa(s) de áudio, {file.Length / (double)ProductionSpace.Gb:0.0} GB). Aguardando autorização.",
                "ffprobe", file.Name);
            if (media.AudioStreams < 3)
            {
                ProductionJournal.Add(db, job.Id, now, ProductionLogLevel.Warning, "recebida",
                    "Menos de 3 faixas de áudio: jogo e microfone podem não estar separados, o que limita o controle da voz.");
            }

            await NotificationOutbox.AddAsync(db, NotificationKind.ProductionAlert, $"production:{job.Id}:received",
                $"Nova gravação pronta para produção: {file.Name} ({Duration(media.DurationSeconds)}). Autorize o job na aba Produção.", now, ct);
            await db.SaveChangesAsync(ct);
            byFingerprint[fingerprint] = job.Id;
            created++;
            views.Add(new(file.Name, file.Length, "Job criado", "Gravação pronta; aguardando autorização.", job.Id));
            logger.LogInformation("Produção: job criado para {File}.", file.Name);
        }

        foreach (var gone in _seen.Keys.Where(k => !present.Contains(k)).ToList()) _seen.TryRemove(gone, out _);
        LastScan = views;
        return created;
    }

    private static ProductionJob NewJob(FileInfo file, string fingerprint, VideoAnalysis media, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        SourceFileName = file.Name,
        SourcePath = file.FullName,
        Fingerprint = fingerprint,
        ReceivedAtUtc = now,
        DurationSeconds = media.DurationSeconds,
        Width = media.Width,
        Height = media.Height,
        Fps = media.FrameRate,
        SizeBytes = file.Length,
        AudioTracks = media.AudioStreams,
        Status = ProductionStatus.Queued,
        CurrentStep = "Aguardando autorização",
        CreatedAtUtc = now,
        UpdatedAtUtc = now,
    };

    /// <summary>
    /// Verdadeiro quando outro programa tem o arquivo aberto para escrita: abrir só para leitura, recusando quem
    /// escreve, falha enquanto o OBS grava.
    /// </summary>
    public static bool IsOpenForWriting(FileInfo file)
    {
        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}min" : $"{t.Minutes}min{t.Seconds:00}s";
    }
}

public static class ProductionJournal
{
    /// <summary>Acrescenta um registro ao contexto (gravado junto com a mudança que o causou).</summary>
    public static void Add(BastDbContext db, Guid jobId, DateTime nowUtc, ProductionLogLevel level, string step, string message,
        string? program = null, string? file = null) =>
        db.ProductionLogs.Add(new ProductionLog
        {
            JobId = jobId,
            AtUtc = nowUtc,
            Level = level,
            Step = step,
            Message = message.Length > 2000 ? message[..2000] : message,
            Program = program,
            File = file,
        });
}
