using Bast.Application;
using Bast.Application.Import;
using Bast.Application.Publishing;
using Bast.Infrastructure;

namespace Bast.Service;

/// <summary>
/// Roda antes dos demais serviços: prepara pastas e banco e conclui o que uma parada anterior
/// deixou pela metade (importações e envios interrompidos).
/// </summary>
public sealed class StartupService(
    DatabaseInitializer initializer,
    ImportService importService,
    PublishingEngine engine,
    IConnectorRegistry registry,
    ILogger<StartupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await initializer.InitializeAsync(cancellationToken);
        await importService.RecoverAsync(cancellationToken);
        await engine.RecoverAsync(cancellationToken);

        if (registry.SimulationMode)
        {
            logger.LogWarning("MODO DE SIMULAÇÃO ATIVO: nenhuma publicação será enviada às redes sociais.");
        }
        else if (registry.Available.Count == 0)
        {
            logger.LogWarning("Nenhuma rede social conectada: os vídeos são importados e aguardam em AGENDADOS sem horário.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Ciclo do motor: agenda, publica o que venceu, confirma pendências e ajusta as pastas.</summary>
public sealed class PublishingWorker(PublishingEngine engine, BastOptions options, ILogger<PublishingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.EngineIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await engine.RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha no ciclo de publicação; nova tentativa no próximo ciclo.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
