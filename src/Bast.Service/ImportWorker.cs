using Bast.Application;
using Bast.Application.Import;
using Bast.Domain;
using Bast.Infrastructure;

namespace Bast.Service;

/// <summary>
/// Monitora 01 - ENVIAR continuamente. O FileSystemWatcher apenas antecipa a varredura;
/// a varredura periódica garante que nada se perca se um evento do sistema for descartado.
/// </summary>
public sealed class ImportWorker(
    Workspace workspace,
    BastOptions options,
    ImportService importService,
    IImportStore store,
    TimeProvider time,
    ILogger<ImportWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>Pastas já analisadas sem sucesso, com a impressão do conteúdo analisado. Só são reanalisadas se mudarem.</summary>
    private readonly Dictionary<string, string> _settled = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var inbox = workspace.PathOf(WorkspaceFolder.Inbox);
        using var watcher = new FileSystemWatcher(inbox)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        watcher.Created += (_, _) => Wake();
        watcher.Changed += (_, _) => Wake();
        watcher.Renamed += (_, _) => Wake();
        watcher.Deleted += (_, _) => Wake();
        watcher.Error += (_, e) => logger.LogWarning(e.GetException(), "Monitor de pastas reportou erro; a varredura periódica continua ativa.");
        watcher.EnableRaisingEvents = true;

        logger.LogInformation("Monitorando {Inbox}.", inbox);
        var tracker = new StabilityTracker(time, TimeSpan.FromSeconds(options.StabilitySeconds));
        // Varre mais rápido que a janela de estabilidade para liberar a pasta assim que ela fecha.
        var interval = TimeSpan.FromSeconds(Math.Min(options.ScanIntervalSeconds, Math.Max(1, options.StabilitySeconds / 2.0)));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(inbox, tracker, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha na varredura de {Inbox}; nova tentativa no próximo ciclo.", inbox);
            }

            try
            {
                await _wake.WaitAsync(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ScanAsync(string inbox, StabilityTracker tracker, CancellationToken ct)
    {
        Directory.CreateDirectory(inbox);
        var folders = Directory.GetDirectories(inbox)
            .OrderBy(Path.GetFileName, NaturalStringComparer.Instance)
            .ToList();

        tracker.Prune(folders);
        foreach (var gone in _settled.Keys.Where(k => !folders.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            _settled.Remove(gone);
        }

        await store.ResolveIssuesForAbsentFoldersAsync(folders.Select(f => Path.GetFileName(f)!).ToList(), ct);

        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();
            if (!tracker.IsStable(folder, out var fingerprint)) continue;
            if (_settled.TryGetValue(folder, out var analysed) && analysed == fingerprint) continue;

            var name = Path.GetFileName(folder);
            try
            {
                var outcome = await importService.ProcessFolderAsync(folder, ct);
                switch (outcome)
                {
                    case ImportOutcome.Imported:
                        logger.LogInformation("{Folder}: importado e movido para AGENDADOS.", name);
                        tracker.Forget(folder);
                        break;
                    case ImportOutcome.Rejected:
                        logger.LogWarning("{Folder}: vídeo rejeitado e encaminhado para ERROS.", name);
                        tracker.Forget(folder);
                        break;
                    case ImportOutcome.ToolUnavailable or ImportOutcome.MoveFailed:
                        // Condição externa: tenta de novo no próximo ciclo.
                        logger.LogWarning("{Folder}: importação adiada ({Outcome}).", name, outcome);
                        break;
                    default:
                        logger.LogWarning("{Folder}: não importado ({Outcome}). Aguardando correção da pasta.", name, outcome);
                        _settled[folder] = fingerprint;
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Folder}: erro inesperado na importação.", name);
            }
        }
    }

    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Já existe uma varredura pendente.
        }
    }
}
