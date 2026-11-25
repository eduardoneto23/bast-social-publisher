using System.ComponentModel;
using System.Diagnostics;
using Bast.Application;
using Bast.Application.Publishing;
using Microsoft.Extensions.Logging;

namespace Bast.Infrastructure.Media;

/// <summary>
/// Reencoda o vídeo em H.264 com no máximo 6 Mbps e 30 qps (o que o Instagram entrega nos Reels de
/// qualquer jeito) e áudio AAC 128 kbps. Um vídeo de 90 s fica em torno de 70 MB.
/// A cópia é reaproveitada enquanto o original não muda e apagada depois de 3 dias.
/// </summary>
public sealed class FfmpegVideoCompactor(MediaTools tools, Workspace workspace, ILogger<FfmpegVideoCompactor> logger) : IVideoCompactor
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(3);
    private readonly SemaphoreSlim _one = new(1, 1);

    private string CacheFolder => Path.Combine(workspace.SystemPath, "cache", "compact");

    public async Task<string?> CompactAsync(string videoPath, CancellationToken ct)
    {
        var ffmpeg = tools.Ffmpeg;
        if (ffmpeg is null) return null;

        var source = new FileInfo(videoPath);
        if (!source.Exists) return null;

        await _one.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(CacheFolder);
            CleanOld();

            var key = $"{Path.GetFileNameWithoutExtension(source.Name)}-{source.Length:x}-{source.LastWriteTimeUtc.Ticks:x}";
            var target = Path.Combine(CacheFolder, Sanitize(key) + ".mp4");
            if (File.Exists(target)) return target;

            var partial = target + ".part.mp4";
            File.Delete(partial);

            var start = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[]
                     {
                         "-hide_banner", "-loglevel", "error", "-y", "-i", source.FullName,
                         "-map", "0:v:0", "-map", "0:a:0?",
                         "-fpsmax", "30",
                         "-c:v", "libx264", "-preset", "medium", "-profile:v", "high", "-pix_fmt", "yuv420p",
                         "-crf", "21", "-maxrate", "6M", "-bufsize", "12M",
                         "-c:a", "aac", "-b:a", "128k", "-ar", "44100",
                         "-movflags", "+faststart", partial,
                     })
            {
                start.ArgumentList.Add(arg);
            }

            using var process = Process.Start(start)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            _ = process.StandardOutput.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                File.Delete(partial);
                if (ct.IsCancellationRequested) throw;
                logger.LogWarning("{File}: a cópia leve passou de {Minutes} min e foi abandonada; vai o original.", source.Name, Timeout.TotalMinutes);
                return null;
            }

            if (process.ExitCode != 0 || !File.Exists(partial))
            {
                File.Delete(partial);
                logger.LogWarning("{File}: ffmpeg não criou a cópia leve ({Error}); vai o original.", source.Name, (await errors).Trim());
                return null;
            }

            File.Move(partial, target, overwrite: true);
            logger.LogInformation("{File}: cópia leve criada ({Before:N0} MB -> {After:N0} MB).", source.Name,
                source.Length / 1048576.0, new FileInfo(target).Length / 1048576.0);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            logger.LogWarning(ex, "{File}: não foi possível criar a cópia leve; vai o original.", source.Name);
            return null;
        }
        finally
        {
            _one.Release();
        }
    }

    private void CleanOld()
    {
        foreach (var file in Directory.EnumerateFiles(CacheFolder))
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > KeepFor) File.Delete(file);
            }
            catch (IOException)
            {
                // Em uso: fica para a próxima limpeza.
            }
        }
    }

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
