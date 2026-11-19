using Bast.Domain;

namespace Bast.Application;

/// <summary>Estrutura de pastas do BAST SOCIAL PUBLISHER dentro da pasta principal configurada.</summary>
public sealed class Workspace
{
    private static readonly Dictionary<WorkspaceFolder, string> Names = new()
    {
        [WorkspaceFolder.Inbox] = "01 - ENVIAR",
        [WorkspaceFolder.YouTubeInbox] = "01 - ENVIAR YOUTUBE",
        [WorkspaceFolder.Scheduled] = "02 - AGENDADOS",
        [WorkspaceFolder.Publishing] = "03 - PUBLICANDO",
        [WorkspaceFolder.Sent] = "04 - ENVIADOS",
        [WorkspaceFolder.Errors] = "05 - ERROS",
        [WorkspaceFolder.Archived] = "06 - ARQUIVADOS",
    };

    public Workspace(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("A pasta principal (Bast:RootFolder) não foi configurada.", nameof(root));
        }

        Root = Path.GetFullPath(root);
    }

    public string Root { get; }
    public string SystemPath => Path.Combine(Root, "SYSTEM");
    public string DatabasePath => Path.Combine(SystemPath, "bast.db");

    public string PathOf(WorkspaceFolder folder) => Path.Combine(Root, Names[folder]);

    public void EnsureCreated()
    {
        foreach (var folder in Names.Keys)
        {
            Directory.CreateDirectory(PathOf(folder));
        }

        Directory.CreateDirectory(SystemPath);
    }

    public string ToRelative(string absolutePath) => Path.GetRelativePath(Root, absolutePath);

    public string ToAbsolute(string relativePath) => Path.GetFullPath(Path.Combine(Root, relativePath));

    /// <summary>Caminho de destino que ainda não existe; acrescenta " (2)", " (3)"... em caso de conflito de nome.</summary>
    public string UniqueDestination(WorkspaceFolder folder, string folderName)
    {
        var candidate = Path.Combine(PathOf(folder), folderName);
        for (var i = 2; Directory.Exists(candidate) || File.Exists(candidate); i++)
        {
            candidate = Path.Combine(PathOf(folder), $"{folderName} ({i})");
        }

        return candidate;
    }
}
