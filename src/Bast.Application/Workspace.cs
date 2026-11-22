using Bast.Domain;

namespace Bast.Application;

/// <summary>Estrutura de pastas do BAST SOCIAL PUBLISHER dentro da pasta principal configurada.</summary>
public sealed class Workspace
{
    private static readonly Dictionary<WorkspaceFolder, string> Names = new()
    {
        [WorkspaceFolder.Inbox] = "01 - ENVIAR VERTICAL",
        [WorkspaceFolder.YouTubeInbox] = "01 - ENVIAR YOUTUBE",
        [WorkspaceFolder.Scheduled] = "02 - AGENDADOS",
        [WorkspaceFolder.Publishing] = "03 - PUBLICANDO",
        [WorkspaceFolder.Sent] = "04 - ENVIADOS",
        [WorkspaceFolder.Errors] = "05 - ERROS",
        [WorkspaceFolder.Archived] = "06 - ARQUIVADOS",
        [WorkspaceFolder.ManualPending] = "07 - PENDENTE MANUAL",
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

    /// <summary>Nome antigo da pasta de entrada dos verticais, renomeada para "01 - ENVIAR VERTICAL".</summary>
    public const string LegacyInboxName = "01 - ENVIAR";

    public void EnsureCreated()
    {
        MigrateLegacyInbox();
        foreach (var folder in Names.Keys)
        {
            Directory.CreateDirectory(PathOf(folder));
        }

        Directory.CreateDirectory(SystemPath);
    }

    /// <summary>
    /// Renomeia a antiga "01 - ENVIAR" para "01 - ENVIAR VERTICAL". Se as duas existem, o conteúdo da antiga
    /// passa para a nova (nome repetido ganha " (2)") e a antiga é removida quando fica vazia.
    /// </summary>
    private void MigrateLegacyInbox()
    {
        var legacy = Path.Combine(Root, LegacyInboxName);
        if (!Directory.Exists(legacy)) return;

        var inbox = PathOf(WorkspaceFolder.Inbox);
        try
        {
            if (!Directory.Exists(inbox))
            {
                Directory.Move(legacy, inbox);
                return;
            }

            foreach (var entry in new DirectoryInfo(legacy).EnumerateFileSystemInfos())
            {
                var destination = UniqueDestination(WorkspaceFolder.Inbox, entry.Name);
                if (entry is DirectoryInfo directory) directory.MoveTo(destination);
                else ((FileInfo)entry).MoveTo(destination);
            }

            if (!Directory.EnumerateFileSystemEntries(legacy).Any()) Directory.Delete(legacy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Pasta aberta em outro programa: tenta de novo no próximo início; o que sobrou continua na antiga.
        }
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
