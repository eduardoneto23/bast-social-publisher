using System.Text;

namespace Bast.Application.Import;

/// <summary>
/// Decide quando a cópia de uma pasta terminou: nenhum arquivo mudou de tamanho ou data
/// durante a janela configurada e nenhum arquivo está aberto para escrita por outro processo.
/// </summary>
public sealed class StabilityTracker(TimeProvider time, TimeSpan window)
{
    private readonly Dictionary<string, (string Fingerprint, DateTimeOffset Since)> _seen = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="fingerprint">Impressão do conteúdo atual da pasta (nomes, tamanhos e datas).</param>
    public bool IsStable(string folderPath, out string fingerprint)
    {
        fingerprint = Fingerprint(folderPath);
        var now = time.GetUtcNow();

        if (!_seen.TryGetValue(folderPath, out var entry) || entry.Fingerprint != fingerprint)
        {
            _seen[folderPath] = (fingerprint, now);
            return false;
        }

        return now - entry.Since >= window && !AnyFileLocked(folderPath);
    }

    public void Forget(string folderPath) => _seen.Remove(folderPath);

    public void Prune(IReadOnlyCollection<string> existingFolders)
    {
        var existing = new HashSet<string>(existingFolders, StringComparer.OrdinalIgnoreCase);
        foreach (var key in _seen.Keys.Where(k => !existing.Contains(k)).ToList())
        {
            _seen.Remove(key);
        }
    }

    private static string Fingerprint(string folderPath)
    {
        var sb = new StringBuilder();
        try
        {
            foreach (var file in new DirectoryInfo(folderPath)
                         .EnumerateFiles("*", SearchOption.AllDirectories)
                         .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append(Path.GetRelativePath(folderPath, file.FullName)).Append('|')
                    .Append(file.Length).Append('|')
                    .Append(file.LastWriteTimeUtc.Ticks).Append('\n');
            }
        }
        catch (IOException)
        {
            // Pasta mudando durante a leitura: uma impressão única força nova espera.
            sb.Append("io:").Append(Guid.NewGuid());
        }
        catch (UnauthorizedAccessException)
        {
            sb.Append("acl:").Append(Guid.NewGuid());
        }

        return sb.ToString();
    }

    private static bool AnyFileLocked(string folderPath)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            }

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
}
