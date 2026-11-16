using Bast.Domain;

namespace Bast.Application.Import;

public sealed record ImportProblem(ImportIssueCode Code, string Message);

public sealed record FolderInspection(
    string FolderPath,
    string? VideoFile,
    string? CaptionFile,
    string? ThumbnailFile,
    bool ReimportRequested,
    IReadOnlyList<ImportProblem> Problems);

/// <summary>Identifica vídeo, legenda e thumbnail de uma pasta de 01 - ENVIAR, sem alterar nada.</summary>
public sealed class FolderInspector(BastOptions options)
{
    /// <summary>Arquivo vazio que o usuário cria para autorizar a reimportação de um conteúdo já processado.</summary>
    public const string ReimportMarker = "REIMPORTAR.txt";
    public const string PreferredCaptionName = "legenda.txt";

    private static readonly string[] ThumbnailExtensions = [".jpg", ".jpeg", ".png"];
    private static readonly string[] IgnoredNames = ["desktop.ini", "thumbs.db", ".ds_store"];

    public FolderInspection Inspect(string folderPath)
    {
        var files = new DirectoryInfo(folderPath)
            .EnumerateFiles()
            .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
            .Where(f => !IgnoredNames.Contains(f.Name, StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var problems = new List<ImportProblem>();
        var reimport = files.Any(f => IsNamed(f, ReimportMarker));

        var videos = files.Where(f => options.VideoExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase)).ToList();
        if (videos.Count == 0)
        {
            problems.Add(new(ImportIssueCode.MissingVideo, "Nenhum arquivo de vídeo encontrado na pasta."));
        }
        else if (videos.Count > 1)
        {
            problems.Add(new(ImportIssueCode.MultipleVideos,
                $"A pasta contém {videos.Count} vídeos; cada pasta deve ter apenas um: {string.Join(", ", videos.Select(v => v.Name))}."));
        }

        var captions = files.Where(f => f.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) && !IsNamed(f, ReimportMarker)).ToList();
        var caption = captions.FirstOrDefault(f => IsNamed(f, PreferredCaptionName));
        if (caption is null)
        {
            if (captions.Count == 1)
            {
                caption = captions[0];
            }
            else if (captions.Count == 0)
            {
                problems.Add(new(ImportIssueCode.MissingCaption, $"Arquivo {PreferredCaptionName} não encontrado na pasta."));
            }
            else
            {
                problems.Add(new(ImportIssueCode.MultipleCaptions,
                    $"A pasta contém {captions.Count} arquivos TXT e nenhum se chama {PreferredCaptionName}."));
            }
        }

        var thumbnails = files.Where(f => ThumbnailExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase)).ToList();
        var thumbnail = thumbnails.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f.Name).Equals("thumbnail", StringComparison.OrdinalIgnoreCase))
            ?? (thumbnails.Count == 1 ? thumbnails[0] : null);

        return new FolderInspection(
            folderPath,
            videos.Count == 1 ? videos[0].FullName : null,
            caption?.FullName,
            thumbnail?.FullName,
            reimport,
            problems);
    }

    private static bool IsNamed(FileInfo file, string name) => file.Name.Equals(name, StringComparison.OrdinalIgnoreCase);
}
