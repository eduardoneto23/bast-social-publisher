using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bast.Application;
using Bast.Application.Publishing;
using Bast.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bast.Connectors.YouTube;

public sealed class YouTubeOptions
{
    public const string SectionName = "YouTube";

    /// <summary>public, unlisted ou private. Enviado sempre de forma explícita.</summary>
    public string PrivacyStatus { get; set; } = "public";

    /// <summary>Categoria do vídeo. 22 = Pessoas e blogs; 20 = Jogos.</summary>
    public string CategoryId { get; set; } = "22";

    public bool MadeForKids { get; set; }

    /// <summary>Tamanho de cada bloco do upload, em MB. O protocolo exige múltiplos de 256 KB.</summary>
    public int ChunkSizeMb { get; set; } = 8;
}

/// <summary>
/// Publicação no YouTube pela YouTube Data API v3, com upload retomável.
/// O endereço da sessão de upload é gravado antes do primeiro byte: uma mesma sessão só cria um vídeo,
/// então retomar depois de queda ou falha de rede nunca duplica a publicação.
/// A publicação só é dada como concluída quando a API informa uploadStatus = processed.
/// </summary>
public sealed class YouTubeConnector(
    IHttpClientFactory httpFactory,
    GoogleAuth auth,
    IOptions<YouTubeOptions> options,
    TimeProvider time,
    ILogger<YouTubeConnector> logger) : IPlatformConnector
{
    internal const string SessionPrefix = "session:";
    private const string UploadEndpoint = "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status";
    private const string VideosEndpoint = "https://www.googleapis.com/youtube/v3/videos?part=status&id=";
    private const string ThumbnailEndpoint = "https://www.googleapis.com/upload/youtube/v3/thumbnails/set?videoId=";
    private const int MaxConsecutiveChunkFailures = 4;

    public Platform Platform => Platform.YouTube;
    public bool IsSimulation => false;
    public bool IsAvailable => auth.IsConnected;

    public async Task<PublishOutcome> PublishAsync(PublishRequest request, CancellationToken ct)
    {
        try
        {
            // Já existe um vídeo para esta publicação: nunca envia de novo, apenas confere o estado.
            if (!string.IsNullOrEmpty(request.ExternalId) && !request.ExternalId.StartsWith(SessionPrefix, StringComparison.Ordinal))
            {
                return await CheckVideoAsync(request.ExternalId, ct);
            }

            var total = new FileInfo(request.VideoPath).Length;
            var session = request.ExternalId?.StartsWith(SessionPrefix, StringComparison.Ordinal) == true
                ? request.ExternalId[SessionPrefix.Length..]
                : null;
            long offset = 0;
            string? videoId = null;

            if (session is not null)
            {
                var status = await QuerySessionAsync(session, total, ct);
                if (status.Failure is not null) return status.Failure;
                if (status.Expired) session = null;
                videoId = status.VideoId;
                offset = status.NextOffset;
            }

            if (session is null)
            {
                var (uri, failure) = await InitiateAsync(request, total, ct);
                if (failure is not null) return failure;
                session = uri!;
                await request.SaveExternalIdAsync(SessionPrefix + session);
            }

            if (videoId is null)
            {
                var (id, failure) = await UploadAsync(request.VideoPath, session, offset, total, ct);
                if (failure is not null) return failure;
                videoId = id!;
            }

            await request.SaveExternalIdAsync(videoId);
            var thumbnailProblem = request.ThumbnailPath is null ? null : await TrySetThumbnailAsync(videoId, request.ThumbnailPath, ct);

            // O upload terminou, mas só a consulta do estado final confirma a publicação.
            return PublishOutcome.Processing(videoId, TimeSpan.FromSeconds(20)) with
            {
                Warning = thumbnailProblem is null ? null : $"A thumbnail não foi aplicada: {thumbnailProblem} O vídeo foi enviado com a imagem automática do YouTube.",
            };
        }
        catch (AuthRequiredException ex)
        {
            return PublishOutcome.AuthRequired(ex.Message);
        }
        catch (Exception ex) when (IsConnectivity(ex, ct))
        {
            return PublishOutcome.Offline($"Sem conexão com o YouTube ({ex.Message}). O envio será retomado de onde parou.");
        }
    }

    public async Task<PublishOutcome> CheckAsync(PublishRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.ExternalId))
        {
            return PublishOutcome.NotFound("Nenhuma sessão de upload foi aberta para esta publicação.");
        }

        if (!request.ExternalId.StartsWith(SessionPrefix, StringComparison.Ordinal))
        {
            return await CheckVideoAsync(request.ExternalId, ct);
        }

        var total = new FileInfo(request.VideoPath).Length;
        var status = await QuerySessionAsync(request.ExternalId[SessionPrefix.Length..], total, ct);
        if (status.Failure is not null) return status.Failure;
        if (status.VideoId is not null)
        {
            await request.SaveExternalIdAsync(status.VideoId);
            return PublishOutcome.Processing(status.VideoId, TimeSpan.FromSeconds(20));
        }

        // Sessão incompleta ou expirada: o vídeo não chegou a ser criado. Repetir é seguro,
        // e o novo envio continua a mesma sessão quando ela ainda existe.
        return PublishOutcome.NotFound(status.Expired
            ? "A sessão de upload expirou sem concluir; o envio será reiniciado."
            : $"Upload incompleto ({status.NextOffset} de {total} bytes); o envio será retomado.");
    }

    private async Task<(string? SessionUri, PublishOutcome? Failure)> InitiateAsync(PublishRequest request, long total, CancellationToken ct)
    {
        var settings = options.Value;
        var metadata = JsonSerializer.Serialize(new
        {
            snippet = new { title = request.Title, description = request.Text, categoryId = settings.CategoryId },
            status = new { privacyStatus = settings.PrivacyStatus, selfDeclaredMadeForKids = settings.MadeForKids },
        });

        using var message = new HttpRequestMessage(HttpMethod.Post, UploadEndpoint)
        {
            Content = new StringContent(metadata, Encoding.UTF8, "application/json"),
        };
        message.Headers.Add("X-Upload-Content-Length", total.ToString(CultureInfo.InvariantCulture));
        message.Headers.Add("X-Upload-Content-Type", "video/*");

        using var response = await SendAsync(message, ct);
        if (response.StatusCode == HttpStatusCode.OK && response.Headers.Location is { } location)
        {
            return (location.ToString(), null);
        }

        return (null, await FailureAsync(response, ct));
    }

    private async Task<(string? VideoId, PublishOutcome? Failure)> UploadAsync(string path, string session, long offset, long total, CancellationToken ct)
    {
        var chunkSize = Math.Max(1, options.Value.ChunkSizeMb) * 4 * 256 * 1024;
        var buffer = new byte[(int)Math.Min(chunkSize, Math.Max(1, total))];
        var failures = 0;

        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
        while (true)
        {
            file.Position = offset;
            var count = await file.ReadAtLeastAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, total - offset)), (int)Math.Min(buffer.Length, total - offset), false, ct);

            using var message = new HttpRequestMessage(HttpMethod.Put, session)
            {
                Content = new ByteArrayContent(buffer, 0, count),
            };
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("video/*");
            message.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + count - 1, total);

            using var response = await SendAsync(message, ct);
            var code = (int)response.StatusCode;

            if (code is 200 or 201)
            {
                return (await ReadVideoIdAsync(response, ct), null);
            }

            if (code == 308)
            {
                failures = 0;
                offset = NextOffset(response);
                continue;
            }

            if (code is 500 or 502 or 503 or 504 && ++failures <= MaxConsecutiveChunkFailures)
            {
                // Erro do servidor: pergunta o que ele já recebeu antes de continuar, como manda o protocolo.
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, failures)), time, ct);
                var status = await QuerySessionAsync(session, total, ct);
                if (status.Failure is not null) return (null, status.Failure);
                if (status.VideoId is not null) return (status.VideoId, null);
                if (status.Expired) return (null, PublishOutcome.Transient("A sessão de upload expirou; o envio será reiniciado."));
                offset = status.NextOffset;
                continue;
            }

            if (code == 404)
            {
                // A sessão expirada não cria vídeo; o próximo envio detecta a expiração e abre outra.
                return (null, PublishOutcome.Transient("A sessão de upload expirou; o envio será reiniciado."));
            }

            return (null, await FailureAsync(response, ct));
        }
    }

    private sealed record SessionStatus(long NextOffset, string? VideoId, bool Expired, PublishOutcome? Failure);

    /// <summary>Pergunta ao YouTube quanto da sessão já foi recebido (PUT vazio com Content-Range: bytes */total).</summary>
    private async Task<SessionStatus> QuerySessionAsync(string session, long total, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, session) { Content = new ByteArrayContent([]) };
        message.Content.Headers.ContentRange = new ContentRangeHeaderValue(total);

        using var response = await SendAsync(message, ct);
        var code = (int)response.StatusCode;
        if (code is 200 or 201) return new SessionStatus(total, await ReadVideoIdAsync(response, ct), false, null);
        if (code == 308) return new SessionStatus(NextOffset(response), null, false, null);
        if (code == 404) return new SessionStatus(0, null, true, null);
        return new SessionStatus(0, null, false, await FailureAsync(response, ct));
    }

    /// <summary>Cabeçalho Range: bytes=0-N indica que os bytes 0 a N foram guardados; sem o cabeçalho, nada foi.</summary>
    private static long NextOffset(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Range", out var values)) return 0;
        var range = values.FirstOrDefault() ?? "";
        var dash = range.LastIndexOf('-');
        return dash >= 0 && long.TryParse(range[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var last) ? last + 1 : 0;
    }

    private static async Task<string> ReadVideoIdAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.GetProperty("id").GetString()
               ?? throw new JsonException("Resposta do YouTube sem identificador do vídeo.");
    }

    private async Task<PublishOutcome> CheckVideoAsync(string videoId, CancellationToken ct)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, VideosEndpoint + Uri.EscapeDataString(videoId));
            using var response = await SendAsync(message, ct);
            if (!response.IsSuccessStatusCode)
            {
                // Falha ao consultar não muda o que se sabe sobre o vídeo.
                var failure = await FailureAsync(response, ct);
                return PublishOutcome.Unknown($"Não foi possível consultar o vídeo {videoId}: {failure.Error}") with { ExternalId = videoId };
            }

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var items = json.RootElement.GetProperty("items");
            if (items.GetArrayLength() == 0)
            {
                return PublishOutcome.Permanent($"O vídeo {videoId} não existe mais no YouTube (removido após o envio).") with { ExternalId = videoId };
            }

            var status = items[0].GetProperty("status");
            var upload = Text(status, "uploadStatus");
            var url = "https://www.youtube.com/watch?v=" + videoId;
            return upload switch
            {
                "processed" => PublishOutcome.Published(videoId, url),
                "uploaded" => PublishOutcome.Processing(videoId, TimeSpan.FromSeconds(30)),
                "failed" => PublishOutcome.Permanent($"O YouTube não conseguiu processar o vídeo ({Text(status, "failureReason") ?? "motivo não informado"}).") with { ExternalId = videoId, Url = url },
                "rejected" => PublishOutcome.Permanent($"O YouTube rejeitou o vídeo ({Text(status, "rejectionReason") ?? "motivo não informado"}).") with { ExternalId = videoId, Url = url },
                "deleted" => PublishOutcome.Permanent("O vídeo foi apagado no YouTube.") with { ExternalId = videoId },
                _ => PublishOutcome.Unknown($"Estado de upload desconhecido informado pelo YouTube: {upload}.") with { ExternalId = videoId },
            };
        }
        catch (AuthRequiredException ex)
        {
            return PublishOutcome.Unknown(ex.Message) with { ExternalId = videoId };
        }
        catch (Exception ex) when (IsConnectivity(ex, ct))
        {
            return PublishOutcome.Unknown($"Sem conexão com o YouTube ({ex.Message}).") with { ExternalId = videoId };
        }
    }

    /// <returns>Nulo quando a thumbnail foi aplicada; senão, o motivo.</returns>
    private async Task<string?> TrySetThumbnailAsync(string videoId, string thumbnailPath, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(thumbnailPath)) return "o arquivo da imagem não foi encontrado.";
            using var message = new HttpRequestMessage(HttpMethod.Post, ThumbnailEndpoint + Uri.EscapeDataString(videoId))
            {
                Content = new ByteArrayContent(await File.ReadAllBytesAsync(thumbnailPath, ct)),
            };
            message.Content.Headers.ContentType = new MediaTypeHeaderValue(
                Path.GetExtension(thumbnailPath).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");
            using var response = await SendAsync(message, ct);
            if (!response.IsSuccessStatusCode)
            {
                var failure = await FailureAsync(response, ct);
                logger.LogWarning("YouTube: a thumbnail do vídeo {VideoId} não foi aplicada ({Reason}). O vídeo foi enviado normalmente.", videoId, failure.Error);
                // 403 nesse pedido é o caso documentado de canal sem permissão para miniaturas personalizadas.
                return response.StatusCode == HttpStatusCode.Forbidden
                    ? $"o YouTube recusou ({failure.Error}). Miniaturas personalizadas exigem o canal verificado (youtube.com/verify)."
                    : $"o YouTube recusou ({failure.Error}).";
            }

            return null;
        }
        catch (Exception ex) when (ex is AuthRequiredException or IOException || IsConnectivity(ex, ct))
        {
            logger.LogWarning("YouTube: a thumbnail do vídeo {VideoId} não foi aplicada ({Reason}).", videoId, ex.Message);
            return "falha ao enviar a imagem.";
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
    {
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await auth.GetAccessTokenAsync(ct));
        var response = await httpFactory.CreateClient(GoogleAuth.HttpClientName).SendAsync(message, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) auth.InvalidateAccessToken();
        return response;
    }

    /// <summary>Traduz uma resposta de erro da API em falha temporária ou definitiva.</summary>
    private async Task<PublishOutcome> FailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var code = (int)response.StatusCode;
        var (reason, message) = ParseError(await response.Content.ReadAsStringAsync(ct));
        var detail = $"YouTube respondeu {code}{(reason is null ? "" : $" ({reason})")}{(message is null ? "" : $": {message}")}";
        var retryAfter = response.Headers.RetryAfter?.Delta;

        if (code == 401) return PublishOutcome.Transient(detail, TimeSpan.FromMinutes(1));
        if (code == 429 || reason is "rateLimitExceeded" or "userRateLimitExceeded") return PublishOutcome.Transient(detail, retryAfter ?? TimeSpan.FromMinutes(5));
        if (reason is "quotaExceeded" or "uploadLimitExceeded" or "dailyLimitExceeded")
        {
            // A cota diária zera à meia-noite do horário do Pacífico.
            return PublishOutcome.Transient(detail + " A cota diária será renovada automaticamente.", UntilQuotaReset());
        }

        if (code >= 500) return PublishOutcome.Transient(detail, retryAfter);
        return PublishOutcome.Permanent(detail);
    }

    private TimeSpan UntilQuotaReset()
    {
        var pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        var now = TimeZoneInfo.ConvertTime(time.GetUtcNow(), pacific);
        var midnight = new DateTimeOffset(now.Date.AddDays(1), pacific.GetUtcOffset(now.Date.AddDays(1)));
        return midnight - now + TimeSpan.FromMinutes(10);
    }

    private static (string? Reason, string? Message) ParseError(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object) return (null, null);
            var message = Text(error, "message");
            var reason = error.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0
                ? Text(errors[0], "reason")
                : null;
            return (reason, message);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Falha de rede ou tempo esgotado (não um cancelamento pedido pelo serviço).</summary>
    private static bool IsConnectivity(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested);
}

public static class YouTubeServiceCollectionExtensions
{
    public static IServiceCollection AddYouTubeConnector(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<YouTubeOptions>(configuration.GetSection(YouTubeOptions.SectionName));
        services.AddHttpClient(GoogleAuth.HttpClientName, client => client.Timeout = TimeSpan.FromMinutes(10))
            // 308 é a resposta normal de bloco recebido; não pode ser tratada como redirecionamento.
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            // Sem log de pedidos HTTP: o endereço da sessão de upload não deve ir para os logs.
            .RemoveAllLoggers();
        services.AddSingleton<GoogleAuth>();
        services.AddSingleton<YouTubeConnector>();
        services.AddSingleton<IPlatformConnector>(sp => sp.GetRequiredService<YouTubeConnector>());
        return services;
    }
}
