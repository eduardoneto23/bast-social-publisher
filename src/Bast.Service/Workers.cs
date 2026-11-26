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
    Bast.Application.Production.ProductionRunner production,
    IConnectorRegistry registry,
    ILogger<StartupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await initializer.InitializeAsync(cancellationToken);
        await importService.RecoverAsync(cancellationToken);
        await engine.RecoverAsync(cancellationToken);
        await production.RecoverAsync(cancellationToken);

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

/// <summary>Entrega os avisos pendentes. Uma falha aqui nunca interfere nas publicações.</summary>
public sealed class NotificationWorker(NotificationDispatcher dispatcher, ILogger<NotificationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await dispatcher.RunCycleAsync(stoppingToken);
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError("Falha ao entregar avisos ({Type}); nova tentativa no próximo ciclo.", ex.GetType().Name);
                try
                {
                    await Task.Delay(Interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}

/// <summary>
/// Renova o token de 60 dias do Threads mesmo sem publicações, para a conta não cair por falta de uso.
/// </summary>
public sealed class ThreadsTokenWorker(Bast.Connectors.Meta.ThreadsAuth threads, ILogger<ThreadsTokenWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await threads.RenewIfNeededAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Só o tipo: a mensagem de uma falha de rede poderia citar o endereço, que leva o token.
                logger.LogWarning("Threads: falha ao renovar o token ({Type}); nova tentativa mais tarde.", ex.GetType().Name);
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

/// <summary>Escuta os botões do Telegram ("Publiquei" / "Não vou publicar") do TikTok assistido.</summary>
public sealed class TelegramButtonsWorker(Bast.Notifications.Telegram.TelegramBot telegram, Bast.Notifications.Telegram.TelegramButtonsHandler handler,
    ILogger<TelegramButtonsWorker> logger) : BackgroundService
{
    private static readonly TimeSpan LongPoll = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (telegram.IsConfigured) await handler.RunOnceAsync(LongPoll, stoppingToken);
                else await Task.Delay(Idle, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Só o tipo: a mensagem de uma falha de rede poderia citar o endereço, que leva o token do bot.
                logger.LogWarning("Telegram: falha ao ler os botões ({Type}); nova tentativa em instantes.", ex.GetType().Name);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}

/// <summary>
/// Produção: procura gravações prontas em 00 - GRAVACOES e roda as etapas dos jobs autorizados.
/// Varredura e etapas correm em laços separados: uma etapa longa (render) não atrasa a detecção de gravações.
/// </summary>
public sealed class ProductionWorker(Bast.Application.Production.RecordingScanner scanner, Bast.Application.Production.ProductionRunner runner,
    Bast.Application.Radar.RadarService radar, Bast.Application.Radar.StudioService studio,
    BastOptions options, ILogger<ProductionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, options.ProductionIntervalSeconds));
        try
        {
            await radar.EnsureDefaultsAsync(stoppingToken);
            studio.EnsureFolders();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Radar/Estúdio: falha ao preparar tags e pastas.");
        }

        await Task.WhenAll(
            LoopAsync("varredura de gravações", async ct => { await scanner.ScanAsync(ct); return false; }, interval, stoppingToken),
            LoopAsync("etapas de produção", runner.RunCycleAsync, interval, stoppingToken),
            // Fluxo separado: gravações do Estúdio de Ideias e pesquisas do Radar que travaram.
            LoopAsync("estúdio de ideias", async ct => { await studio.ScanAsync(ct); await radar.ExpireStaleRunsAsync(ct); return false; },
                TimeSpan.FromSeconds(30), stoppingToken));
    }

    private async Task LoopAsync(string what, Func<CancellationToken, Task<bool>> cycle, TimeSpan interval, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try
            {
                worked = await cycle(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Produção: falha no ciclo de {What}; nova tentativa no próximo ciclo.", what);
            }

            try
            {
                // Depois de uma etapa, a próxima começa logo; sem trabalho, espera o intervalo.
                await Task.Delay(worked ? TimeSpan.FromSeconds(1) : interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
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
