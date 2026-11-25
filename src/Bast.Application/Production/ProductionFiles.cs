namespace Bast.Application.Production;

/// <summary>
/// Arquivos de entrega de um job que o painel pode mostrar: vídeos, imagens e relatórios das pastas de entrega,
/// até dois níveis (ex.: "4 - thumbnails\conceitos"). Nunca serve nada fora da pasta de trabalho do job nem da
/// pasta de análise (quadros e áudios brutos).
/// </summary>
public static class ProductionFiles
{
    private static readonly string[] Folders =
        [ProductionFolders.Horizontal, ProductionFolders.Vertical, ProductionFolders.Thumbnails, ProductionFolders.Report];

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".md"] = "text/plain; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
    };

    public static string KindOf(string extension) => extension.ToLowerInvariant() switch
    {
        ".mp4" => "video",
        ".png" or ".jpg" or ".jpeg" => "image",
        _ => "text",
    };

    public static string ContentType(string extension) => Types.TryGetValue(extension, out var type) ? type : "application/octet-stream";

    public static IEnumerable<FileInfo> Deliverables(string workFolder)
    {
        foreach (var folder in Folders)
        {
            var root = new DirectoryInfo(Path.Combine(workFolder, folder));
            if (!root.Exists) continue;
            // Entregas finais primeiro; depois as subpastas (conceitos, versão anterior). "trabalho" = recortes
            // intermediários da thumbnail, não são entregas.
            var files = root.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Concat(root.EnumerateDirectories()
                    .Where(d => !d.Name.Equals("trabalho", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(d => d.Name.Equals("anterior", StringComparison.OrdinalIgnoreCase) ? 1 : 0).ThenBy(d => d.Name)
                    .SelectMany(d => d.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)));
            foreach (var file in files.Where(f => Types.ContainsKey(f.Extension) && !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                                                  && !f.Name.Contains(".part.", StringComparison.OrdinalIgnoreCase)))
            {
                yield return file;
            }
        }
    }

    /// <summary>Caminho absoluto de uma entrega a partir do caminho relativo do painel; nulo se for fora do permitido.</summary>
    public static string? Resolve(string workFolder, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var root = Path.GetFullPath(workFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        var first = Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar)[0];
        if (!Folders.Contains(first, StringComparer.OrdinalIgnoreCase)) return null;
        if (!Types.ContainsKey(Path.GetExtension(full)) || !File.Exists(full)) return null;
        return full;
    }
}
