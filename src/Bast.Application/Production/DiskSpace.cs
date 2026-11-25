namespace Bast.Application.Production;

/// <summary>Espaço livre de um disco (separado para os testes simularem disco cheio).</summary>
public interface IDiskSpace
{
    /// <returns>Bytes livres no disco do caminho, ou nulo se o disco não existe.</returns>
    long? FreeBytes(string path);
}

public sealed class DriveDiskSpace : IDiskSpace
{
    public long? FreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return null;
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public static class ProductionSpace
{
    public const long Gb = 1024L * 1024 * 1024;

    /// <summary>
    /// Espaço que um job vai ocupar no disco de trabalho: análise, render e entregáveis. O original não é
    /// copiado. Estimativa inicial: um quarto do tamanho da gravação, no mínimo 5 GB.
    /// </summary>
    public static long EstimateBytes(long sourceBytes) => Math.Max(5 * Gb, sourceBytes / 4);
}
