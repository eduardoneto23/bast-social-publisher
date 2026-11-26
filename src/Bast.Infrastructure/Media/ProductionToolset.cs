using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Bast.Application;
using Bast.Application.Production;

namespace Bast.Infrastructure.Media;

/// <summary>
/// ffmpeg e o ambiente Python da produção. Os scripts rodam com os modelos da pasta do BAST (HF_HOME) e sem
/// acesso à internet (HF_HUB_OFFLINE): nada é baixado durante um job.
/// </summary>
public sealed class ProductionToolset(MediaTools media, BastOptions options, Workspace workspace) : IProductionTools
{
    public string? Ffmpeg => media.Ffmpeg;

    public string? Python
    {
        get
        {
            var path = string.IsNullOrWhiteSpace(options.ProductionPython)
                ? Path.Combine(workspace.SystemPath, "tools", "python-producao", "Scripts", "python.exe")
                : options.ProductionPython;
            return File.Exists(path) ? path : null;
        }
    }

    public string ScriptsFolder => string.IsNullOrWhiteSpace(options.ProductionScriptsFolder)
        ? Path.Combine(AppContext.BaseDirectory, "producao")
        : options.ProductionScriptsFolder;

    private string ModelsFolder => Path.Combine(workspace.SystemPath, "tools", "modelos");

    public Task<ToolResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct) =>
        RunProcessAsync(executable, arguments, null, ct);

    public Task<ToolResult> RunScriptAsync(string script, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var python = Python ?? throw new InvalidOperationException("Ambiente Python da produção não instalado.");
        var env = new Dictionary<string, string>
        {
            ["HF_HOME"] = ModelsFolder,
            ["HF_HUB_OFFLINE"] = "1",
            ["TRANSFORMERS_OFFLINE"] = "1",
            ["PYTHONIOENCODING"] = "utf-8",
        };
        return RunProcessAsync(python, ["-I", Path.Combine(ScriptsFolder, script), .. arguments], env, ct);
    }

    private static async Task<ToolResult> RunProcessAsync(string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? env, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        if (env is not null)
        {
            foreach (var (key, value) in env) psi.Environment[key] = value;
        }

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new Win32Exception($"Não foi possível iniciar {executable}.");
        }
        catch (Win32Exception ex)
        {
            return new ToolResult(-1, "", ex.Message);
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Pausa, parada ou cancelamento: o processo não pode continuar escrevendo na pasta do job.
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw;
            }

            return new ToolResult(process.ExitCode, await stdout, await stderr);
        }
    }
}
