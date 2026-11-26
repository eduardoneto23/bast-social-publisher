namespace Bast.Application.Production;

public sealed record ToolResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;

    /// <summary>Última linha útil da saída de erro, para o registro (sem despejar o log inteiro).</summary>
    public string ErrorSummary => StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .LastOrDefault(l => !l.StartsWith("warn", StringComparison.OrdinalIgnoreCase)) ?? $"código {ExitCode}";
}

/// <summary>
/// Programas externos da produção (ffmpeg e o ambiente Python com os modelos). Separado para os testes
/// simularem as ferramentas. Uma execução interrompida (pausa, parada, cancelamento) mata o processo.
/// </summary>
public interface IProductionTools
{
    string? Ffmpeg { get; }

    /// <summary>Python do ambiente da produção (SYSTEM\tools\python-producao), ou nulo se não instalado.</summary>
    string? Python { get; }

    /// <summary>Pasta com os scripts (transcrever.py, eventos_audio.py, ...).</summary>
    string ScriptsFolder { get; }

    Task<ToolResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct);

    /// <summary>Roda um script Python da pasta de scripts, com os modelos locais (sem baixar nada).</summary>
    Task<ToolResult> RunScriptAsync(string script, IReadOnlyList<string> arguments, CancellationToken ct);
}
