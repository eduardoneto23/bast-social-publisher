using Bast.Domain;

namespace Bast.Application.Publishing;

public enum PublishOutcomeKind
{
    /// <summary>A plataforma confirmou a publicação.</summary>
    Published,
    /// <summary>Enviado; a plataforma ainda está processando. Consultar de novo depois.</summary>
    Processing,
    /// <summary>Falha temporária confirmada (nada foi publicado). Pode tentar de novo.</summary>
    Transient,
    /// <summary>Falha definitiva (vídeo rejeitado, permissão revogada). Não tentar de novo.</summary>
    Permanent,
    /// <summary>Não foi possível confirmar se a operação foi concluída.</summary>
    Unknown,
    /// <summary>Somente em consultas: a plataforma não tem registro da publicação, então repetir é seguro.</summary>
    NotFound,
    /// <summary>Credenciais recusadas (token expirado sem renovação, permissão revogada). Nada foi publicado; aguarda reconexão.</summary>
    AuthRequired,
    /// <summary>Sem conexão com a plataforma. Nada foi publicado; aguarda a internet voltar sem gastar tentativas.</summary>
    Offline,
}

public sealed record PublishOutcome(PublishOutcomeKind Kind, string? ExternalId = null, string? Url = null, string? Error = null, TimeSpan? RetryAfter = null)
{
    public static PublishOutcome Published(string externalId, string? url) => new(PublishOutcomeKind.Published, externalId, url);
    public static PublishOutcome Processing(string externalId, TimeSpan? checkAfter = null) => new(PublishOutcomeKind.Processing, externalId, RetryAfter: checkAfter);
    public static PublishOutcome Transient(string error, TimeSpan? retryAfter = null) => new(PublishOutcomeKind.Transient, Error: error, RetryAfter: retryAfter);
    public static PublishOutcome Permanent(string error) => new(PublishOutcomeKind.Permanent, Error: error);
    public static PublishOutcome Unknown(string error) => new(PublishOutcomeKind.Unknown, Error: error);
    public static PublishOutcome NotFound(string? error = null) => new(PublishOutcomeKind.NotFound, Error: error);
    public static PublishOutcome AuthRequired(string error) => new(PublishOutcomeKind.AuthRequired, Error: error);
    public static PublishOutcome Offline(string error) => new(PublishOutcomeKind.Offline, Error: error);
}

/// <param name="Text">Legenda integral (quebras de linha em LF).</param>
/// <param name="Title">Título, usado pelo YouTube.</param>
/// <param name="ExternalId">Identificador já gravado para esta publicação, se houver.</param>
/// <param name="SaveExternalIdAsync">Grava imediatamente um identificador recebido da plataforma (sessão de upload, contêiner),
/// antes de continuar, para que uma queda no meio do envio possa ser reconciliada.</param>
public sealed record PublishRequest(
    Guid PublicationId,
    string IdempotencyKey,
    string VideoPath,
    string? ThumbnailPath,
    string Text,
    string Title,
    string? ExternalId,
    Func<string, Task> SaveExternalIdAsync);

/// <summary>Integração com uma plataforma. Cada rede tem a sua, independente das demais.</summary>
public interface IPlatformConnector
{
    Platform Platform { get; }

    /// <summary>Verdadeiro para o conector de simulação, que não envia nada a nenhuma rede.</summary>
    bool IsSimulation { get; }

    /// <summary>Falso enquanto a conta não estiver conectada: nada é agendado nem enviado para a plataforma.</summary>
    bool IsAvailable { get; }

    Task<PublishOutcome> PublishAsync(PublishRequest request, CancellationToken ct);

    /// <summary>Consulta a plataforma sobre uma publicação em processamento ou de resultado incerto.</summary>
    Task<PublishOutcome> CheckAsync(PublishRequest request, CancellationToken ct);
}

public interface IConnectorRegistry
{
    IPlatformConnector? Get(Platform platform);
    IReadOnlyCollection<Platform> Available { get; }
    bool SimulationMode { get; }
}

/// <summary>
/// Conectores reais registrados ou, no modo de simulação, o simulador para todas as plataformas.
/// Sem conector para uma plataforma, nada é agendado para ela.
/// </summary>
public sealed class ConnectorRegistry : IConnectorRegistry
{
    private readonly Dictionary<Platform, IPlatformConnector> _connectors;

    public ConnectorRegistry(IEnumerable<IPlatformConnector> connectors, BastOptions options)
    {
        SimulationMode = options.SimulationMode;
        _connectors = SimulationMode
            ? Enum.GetValues<Platform>().ToDictionary(p => p, p => (IPlatformConnector)new SimulatorConnector(p))
            : connectors.Where(c => !c.IsSimulation).ToDictionary(c => c.Platform);
    }

    public bool SimulationMode { get; }
    public IReadOnlyCollection<Platform> Available => _connectors.Values.Where(c => c.IsAvailable).Select(c => c.Platform).ToList();
    public IPlatformConnector? Get(Platform platform) =>
        _connectors.TryGetValue(platform, out var connector) && connector.IsAvailable ? connector : null;
}

/// <summary>
/// Modo de simulação: percorre todo o fluxo sem publicar em nenhuma rede.
/// As publicações ficam marcadas como simuladas e sem link.
/// </summary>
public sealed class SimulatorConnector(Platform platform) : IPlatformConnector
{
    public Platform Platform => platform;
    public bool IsSimulation => true;
    public bool IsAvailable => true;

    public async Task<PublishOutcome> PublishAsync(PublishRequest request, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
        return PublishOutcome.Published($"simulacao-{request.IdempotencyKey}", null);
    }

    public Task<PublishOutcome> CheckAsync(PublishRequest request, CancellationToken ct) =>
        Task.FromResult(PublishOutcome.NotFound("Simulação: nenhuma publicação anterior registrada."));
}
