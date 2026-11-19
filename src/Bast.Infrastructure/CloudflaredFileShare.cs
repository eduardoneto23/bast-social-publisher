using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Bast.Application;
using Bast.Application.Publishing;
using Microsoft.Extensions.Logging;

namespace Bast.Infrastructure;

/// <summary>
/// Servidor HTTP local que entrega um único arquivo, num caminho aleatório, só em 127.0.0.1.
/// Qualquer outro caminho responde 404. Não lista pastas e não aceita envio.
/// </summary>
public sealed class SingleFileServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _filePath;
    private readonly string _contentType;
    private readonly CancellationTokenSource _stop = new();
    private int _downloads;

    private SingleFileServer(HttpListener listener, int port, string filePath)
    {
        _listener = listener;
        _filePath = filePath;
        Port = port;
        PathAndName = "/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant() + Path.GetExtension(filePath).ToLowerInvariant();
        _contentType = Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".mov" => "video/quicktime",
            ".webm" => "video/webm",
            _ => "video/mp4",
        };
        _ = Task.Run(LoopAsync);
    }

    public int Port { get; }

    /// <summary>Caminho secreto do arquivo, com a barra inicial.</summary>
    public string PathAndName { get; }

    /// <summary>Quantas vezes o arquivo foi entregue por completo.</summary>
    public int Downloads => Volatile.Read(ref _downloads);

    public static SingleFileServer Start(string filePath)
    {
        for (var attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return new SingleFileServer(listener, port, filePath);
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                listener.Close(); // a porta foi tomada entre a escolha e o uso: tenta outra
            }
        }
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_stop.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }
            catch (HttpListenerException)
            {
                continue;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var method = context.Request.HttpMethod;
            if (context.Request.Url?.AbsolutePath != PathAndName || method is not ("GET" or "HEAD"))
            {
                response.StatusCode = 404;
                return;
            }

            await using var file = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            response.ContentType = _contentType;
            response.ContentLength64 = file.Length;
            if (method == "GET")
            {
                await file.CopyToAsync(response.OutputStream, _stop.Token);
                Interlocked.Increment(ref _downloads);
            }
        }
        catch (Exception)
        {
            // Quem pediu desistiu no meio, ou o servidor foi fechado: nada a fazer.
        }
        finally
        {
            try { response.Close(); } catch (Exception) { }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Close(); } catch (Exception) { }
        _stop.Dispose();
    }
}

/// <summary>
/// Endereço público temporário pelo túnel rápido do Cloudflare (cloudflared, sem conta). Cada
/// compartilhamento abre um túnel próprio com nome aleatório, que só alcança o <see cref="SingleFileServer"/>
/// daquele arquivo, e é fechado quando a plataforma termina de buscar ou após 30 minutos.
/// O endereço nunca vai para os logs.
/// </summary>
public sealed partial class CloudflaredFileShare(BastOptions options, Workspace workspace, ILogger<CloudflaredFileShare> logger)
    : IPublicFileShare, IAsyncDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    private sealed class Share(Process process, SingleFileServer server, string filePath, string url, CancellationTokenSource expiry)
    {
        public Process Process { get; } = process;
        public SingleFileServer Server { get; } = server;
        public string FilePath { get; } = filePath;
        public string Url { get; } = url;
        public CancellationTokenSource Expiry { get; } = expiry;
        public bool Alive => !Process.HasExited;
    }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, Share> _shares = [];

    public string ExecutablePath => string.IsNullOrWhiteSpace(options.CloudflaredPath)
        ? Path.Combine(workspace.SystemPath, "tools", "cloudflared.exe")
        : options.CloudflaredPath;

    public async Task<PublicFileShareResult> ShareAsync(Guid key, string filePath, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_shares.TryGetValue(key, out var existing))
            {
                if (existing.Alive && existing.FilePath == filePath) return PublicFileShareResult.Opened(existing.Url);
                Close(key, existing);
            }

            var exe = ExecutablePath;
            if (!File.Exists(exe))
            {
                return PublicFileShareResult.NotInstalled(
                    $"O cloudflared não foi encontrado em {exe}. Ele é necessário para o Threads buscar o vídeo; veja docs/CONECTAR-THREADS.md.");
            }

            if (!File.Exists(filePath)) return PublicFileShareResult.Failed("O arquivo do vídeo não foi encontrado.");

            var server = SingleFileServer.Start(filePath);
            var (process, publicHost, problem) = await StartTunnelAsync(exe, server.Port, ct);
            if (process is null)
            {
                server.Dispose();
                return PublicFileShareResult.Failed(problem!);
            }

            var expiry = new CancellationTokenSource(Lifetime);
            var share = new Share(process, server, filePath, publicHost + server.PathAndName, expiry);
            _shares[key] = share;
            expiry.Token.Register(() => _ = StopAsync(key));
            logger.LogInformation("Link temporário do vídeo aberto pelo túnel do Cloudflare.");
            return PublicFileShareResult.Opened(share.Url);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(Guid key)
    {
        await _gate.WaitAsync();
        try
        {
            if (_shares.TryGetValue(key, out var share)) Close(key, share);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Close(Guid key, Share share)
    {
        _shares.Remove(key);
        try { if (!share.Process.HasExited) share.Process.Kill(entireProcessTree: true); } catch (Exception) { }
        share.Process.Dispose();
        var downloads = share.Server.Downloads;
        share.Server.Dispose();
        share.Expiry.Dispose();
        logger.LogInformation("Link temporário do vídeo fechado ({Downloads} download(s)).", downloads);
    }

    private async Task<(Process? Process, string? PublicHost, string? Problem)> StartTunnelAsync(string exe, int port, CancellationToken ct)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var arg in new[] { "tunnel", "--no-autoupdate", "--url", $"http://127.0.0.1:{port}" }) info.ArgumentList.Add(arg);

        var address = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        DataReceivedEventHandler onLine = (_, e) =>
        {
            if (e.Data is not { } line) return;
            if (QuickTunnelAddress().Match(line) is { Success: true } m) address.TrySetResult(m.Value);
            if (line.Contains("Registered tunnel connection", StringComparison.Ordinal)) registered.TrySetResult();
        };
        process.ErrorDataReceived += onLine;
        process.OutputDataReceived += onLine;
        process.Exited += (_, _) => { address.TrySetCanceled(); registered.TrySetCanceled(); };

        try
        {
            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            await Task.WhenAll(address.Task, registered.Task).WaitAsync(StartTimeout, ct);
            return (process, address.Task.Result, null);
        }
        catch (Exception ex) when (ex is TimeoutException or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception) { }
            process.Dispose();
            ct.ThrowIfCancellationRequested();
            logger.LogWarning("O túnel do Cloudflare não abriu ({Error}).", ex.GetType().Name);
            return (null, null, "O túnel do Cloudflare não abriu (sem internet ou serviço do Cloudflare indisponível). O envio será refeito.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var (key, share) in _shares.ToArray()) Close(key, share);
        }
        finally
        {
            _gate.Release();
        }
    }

    [GeneratedRegex(@"https://[a-z0-9-]+\.trycloudflare\.com")]
    private static partial Regex QuickTunnelAddress();
}
