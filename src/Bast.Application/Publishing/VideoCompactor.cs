namespace Bast.Application.Publishing;

/// <summary>
/// Cópia mais leve do vídeo para redes que recusam arquivos grandes no upload. O arquivo original
/// nunca é alterado: a cópia fica na pasta de cache do sistema e é apagada depois de alguns dias.
/// </summary>
public interface IVideoCompactor
{
    /// <returns>Caminho da cópia leve, ou nulo quando não deu para criar (aí vai o original).</returns>
    Task<string?> CompactAsync(string videoPath, CancellationToken ct);
}
