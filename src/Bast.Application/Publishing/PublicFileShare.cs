namespace Bast.Application.Publishing;

/// <summary>Resultado de um compartilhamento: o endereço público, ou o motivo de não ter aberto.</summary>
public sealed record PublicFileShareResult(string? Url, string? Problem, bool Missing = false)
{
    public static PublicFileShareResult Opened(string url) => new(url, null);
    public static PublicFileShareResult Failed(string problem) => new(null, problem);
    public static PublicFileShareResult NotInstalled(string problem) => new(null, problem, Missing: true);
}

/// <summary>
/// Expõe um único arquivo local por um endereço público temporário, para APIs que só aceitam vídeo por
/// link (Threads). O endereço tem um caminho aleatório, serve só aquele arquivo e é fechado assim que a
/// plataforma termina de buscar, ou ao fim do prazo.
/// </summary>
public interface IPublicFileShare
{
    /// <summary>Abre (ou devolve o já aberto) o endereço do arquivo, identificado pela publicação.</summary>
    Task<PublicFileShareResult> ShareAsync(Guid key, string filePath, CancellationToken ct);

    /// <summary>Fecha o endereço da publicação, se estiver aberto.</summary>
    Task StopAsync(Guid key);
}
