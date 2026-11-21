using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Bast.Application.Publishing;
using Bast.Domain;

namespace Bast.Connectors.Meta;

/// <summary>
/// Reels em Página do Facebook, pela Reels Publishing API: iniciar, enviar o arquivo e finalizar.
/// O identificador do vídeo é gravado em cada fase ("video-uploading", "video-publishing", "video"),
/// e um mesmo identificador só gera um Reel: retomar depois de uma queda nunca duplica a publicação.
/// A publicação só é dada como concluída quando a API informa publish_status = published.
/// </summary>
public sealed class FacebookConnector(IHttpClientFactory httpFactory, MetaAuth auth)
    : MetaConnectorBase(httpFactory, auth), IPlatformConnector
{
    internal const string Uploading = "video-uploading";
    internal const string Publishing = "video-publishing";
    internal const string Sent = "video";

    public Platform Platform => Platform.Facebook;
    public bool IsSimulation => false;
    public bool IsAvailable => Auth.Connection is not null;

    public async Task<PublishOutcome> PublishAsync(PublishRequest request, CancellationToken ct)
    {
        var connection = Auth.Connection;
        if (connection is null) return PublishOutcome.AuthRequired("A Página do Facebook não está conectada.");

        try
        {
            var (stage, videoId) = Graph.Split(request.ExternalId);
            // O arquivo já foi entregue: daqui em diante só consulta ou finaliza, nunca envia de novo.
            if (stage is Publishing or Sent) return await CheckVideoAsync(connection, request, stage, videoId, ct);

            var size = new FileInfo(request.VideoPath).Length;
            long offset = 0;

            if (stage == Uploading)
            {
                var status = await SendAsync(HttpMethod.Get, $"{Graph.Api}/{videoId}?fields=status", connection.PageToken, null, ct);
                var videoStatus = status.Error is null && status.Body.TryGetProperty("status", out var s) ? s : default;
                if (status.Error is not null || Graph.Text(videoStatus, "video_status") is "expired" or "error" or "upload_failed")
                {
                    stage = ""; // sessão perdida: nada foi publicado, abre outra
                }
                else if (videoStatus.TryGetProperty("uploading_phase", out var phase)
                         && phase.TryGetProperty("bytes_transfered", out var bytes) && bytes.TryGetInt64(out var sent))
                {
                    offset = Math.Min(sent, size);
                }
            }

            if (stage != Uploading)
            {
                var start = await SendAsync(HttpMethod.Post, $"{Graph.Api}/{connection.PageId}/video_reels", connection.PageToken,
                    Form(("upload_phase", "start")), ct);
                if (start.Error is not null) return await FailAsync(start.Error, start.Status, "O Facebook não abriu o envio do Reel", ct);
                videoId = Graph.Text(start.Body, "video_id") ?? throw new JsonException("Resposta do Facebook sem video_id.");
                await request.SaveExternalIdAsync($"{Uploading}:{videoId}");
                offset = 0;
            }

            if (offset < size)
            {
                await using var file = new FileStream(request.VideoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
                file.Position = offset;
                var content = new StreamContent(file);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                content.Headers.ContentLength = size - offset;
                content.Headers.Add("offset", offset.ToString(CultureInfo.InvariantCulture));
                content.Headers.Add("file_size", size.ToString(CultureInfo.InvariantCulture));

                var upload = await SendAsync(HttpMethod.Post, $"{Graph.Upload}/video-upload/{Graph.Version}/{videoId}", connection.PageToken, content, ct, scheme: "OAuth");
                if (upload.Error is not null) return await FailAsync(upload.Error, upload.Status, "O Facebook recusou o arquivo do Reel", ct);
            }

            await request.SaveExternalIdAsync($"{Publishing}:{videoId}");
            return await FinishAsync(connection, request, videoId, ct);
        }
        catch (Exception ex) when (Graph.IsConnectivity(ex, ct))
        {
            return PublishOutcome.Offline($"Sem conexão com o Facebook ({ex.Message}). O envio será retomado.");
        }
    }

    public async Task<PublishOutcome> CheckAsync(PublishRequest request, CancellationToken ct)
    {
        var connection = Auth.Connection;
        if (connection is null) return PublishOutcome.Unknown("A Página do Facebook não está conectada.");

        var (stage, videoId) = Graph.Split(request.ExternalId);
        if (stage is not (Publishing or Sent))
        {
            // O arquivo não chegou a ser entregue por completo: nenhum Reel foi criado.
            return PublishOutcome.NotFound("O envio do Reel não foi concluído; será retomado.");
        }

        try
        {
            return await CheckVideoAsync(connection, request, stage, videoId, ct);
        }
        catch (Exception ex) when (Graph.IsConnectivity(ex, ct))
        {
            return PublishOutcome.Unknown($"Sem conexão com o Facebook ({ex.Message}).");
        }
    }

    private async Task<PublishOutcome> FinishAsync(MetaConnection connection, PublishRequest request, string videoId, CancellationToken ct)
    {
        var finish = await SendAsync(HttpMethod.Post, $"{Graph.Api}/{connection.PageId}/video_reels", connection.PageToken,
            Form(("upload_phase", "finish"), ("video_id", videoId), ("video_state", "PUBLISHED"), ("description", request.Text)), ct);
        if (finish.Error is not null) return await FailAsync(finish.Error, finish.Status, "O Facebook não publicou o Reel", ct);

        await request.SaveExternalIdAsync($"{Sent}:{videoId}");
        return PublishOutcome.Processing($"{Sent}:{videoId}", TimeSpan.FromSeconds(20));
    }

    private async Task<PublishOutcome> CheckVideoAsync(MetaConnection connection, PublishRequest request, string stage, string videoId, CancellationToken ct)
    {
        var response = await SendAsync(HttpMethod.Get, $"{Graph.Api}/{videoId}?fields=status,permalink_url", connection.PageToken, null, ct);
        if (response.Error is not null)
        {
            if (response.Error.IsAuth || response.Error.IsPermission) await Auth.MarkNeedsReauthAsync(response.Error.Message, ct);
            return PublishOutcome.Unknown($"Não foi possível consultar o Reel no Facebook: {response.Error.Message}");
        }

        if (!response.Body.TryGetProperty("status", out var status)) return PublishOutcome.Unknown("O Facebook não informou o estado do Reel.");
        status.TryGetProperty("publishing_phase", out var publishing);
        status.TryGetProperty("processing_phase", out var processing);
        status.TryGetProperty("uploading_phase", out var uploading);
        var videoStatus = Graph.Text(status, "video_status");

        if (Graph.Text(publishing, "publish_status") == "published")
        {
            var permalink = Graph.Text(response.Body, "permalink_url");
            var url = permalink is null ? $"https://www.facebook.com/reel/{videoId}"
                : permalink.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? permalink
                : "https://www.facebook.com" + permalink;
            var thumbnailProblem = request.ThumbnailPath is null ? null : await TrySetThumbnailAsync(connection, videoId, request.ThumbnailPath, ct);
            return PublishOutcome.Published(videoId, url) with
            {
                Warning = thumbnailProblem is null ? null : $"A capa não foi aplicada no Facebook: {thumbnailProblem} O Reel saiu com a capa automática.",
            };
        }

        var failure = PhaseError(processing) ?? PhaseError(publishing) ?? PhaseError(uploading);
        if (failure is not null || videoStatus is "error" or "upload_failed")
        {
            return PublishOutcome.Permanent($"O Facebook não conseguiu publicar o Reel: {failure ?? videoStatus}.");
        }

        if (videoStatus == "expired")
        {
            await request.SaveExternalIdAsync("");
            return PublishOutcome.NotFound("O envio expirou no Facebook sem ser publicado; será refeito.");
        }

        // O pedido de publicação pode não ter chegado (queda entre o upload e a finalização).
        // Um vídeo só gera um Reel, então pedir de novo é seguro.
        if (stage == Publishing && Graph.Text(publishing, "status") is null or "not_started")
        {
            return await FinishAsync(connection, request, videoId, ct);
        }

        return PublishOutcome.Processing($"{stage}:{videoId}", TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Envia a imagem da pasta como capa preferida do Reel (POST /{video-id}/thumbnails). Uma falha não
    /// desfaz a publicação: vira aviso.
    /// </summary>
    /// <returns>Nulo quando a capa foi aplicada; senão, o motivo.</returns>
    private async Task<string?> TrySetThumbnailAsync(MetaConnection connection, string videoId, string thumbnailPath, CancellationToken ct)
    {
        if (!File.Exists(thumbnailPath)) return "o arquivo da imagem não foi encontrado.";
        try
        {
            using var form = new MultipartFormDataContent();
            var image = new ByteArrayContent(await File.ReadAllBytesAsync(thumbnailPath, ct));
            image.Headers.ContentType = new MediaTypeHeaderValue(
                Path.GetExtension(thumbnailPath).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");
            form.Add(image, "source", Path.GetFileName(thumbnailPath));
            form.Add(new StringContent("true"), "is_preferred");

            var result = await SendAsync(HttpMethod.Post, $"{Graph.Api}/{videoId}/thumbnails", connection.PageToken, form, ct);
            return result.Error is null ? null : result.Error.Message;
        }
        catch (Exception ex) when (Graph.IsConnectivity(ex, ct))
        {
            return $"sem conexão ({ex.Message}).";
        }
    }

    private static string? PhaseError(JsonElement phase)
    {
        if (phase.ValueKind != JsonValueKind.Object || Graph.Text(phase, "status") != "error") return null;
        if (phase.TryGetProperty("error", out var error)) return Graph.Text(error, "message") ?? "erro não detalhado";
        if (phase.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            return Graph.Text(errors[0], "message") ?? "erro não detalhado";
        }

        return "erro não detalhado";
    }
}
