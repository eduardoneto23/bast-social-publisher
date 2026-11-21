using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Bast.Application.Publishing;
using Bast.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace Bast.Connectors.Meta;

public sealed class InstagramOptions
{
    public const string SectionName = "Instagram";

    /// <summary>Quando verdadeiro, o Reel também aparece no feed do perfil.</summary>
    public bool ShareToFeed { get; set; } = true;
}

/// <summary>
/// Reels no Instagram, pela API de publicação de conteúdo com upload direto do arquivo:
/// criar o contêiner, enviar o vídeo, aguardar o processamento e publicar.
///
/// Nada fica visível no Instagram antes do pedido de publicação (media_publish), então todas as
/// etapas anteriores podem ser refeitas. Esse pedido é feito uma única vez por contêiner: o estado
/// "container-publishing" é gravado antes dele e, se a resposta se perder, o sistema só consulta o
/// contêiner até a Meta informar PUBLISHED, sem pedir de novo.
/// </summary>
/// <remarks>
/// Capa: se a pasta tem a imagem da thumbnail, ela vai como capa do Reel (cover_url). A API só aceita a capa
/// por link público, então o sistema abre um link temporário para a imagem (<see cref="IPublicFileShare"/>)
/// enquanto o Instagram processa o vídeo. Sem o link, o Reel sai com a capa automática. Se o Instagram não
/// conseguir processar o Reel com a capa, o envio é refeito uma vez sem ela.
/// </remarks>
public sealed class InstagramConnector(IHttpClientFactory httpFactory, MetaAuth auth, IOptions<InstagramOptions> options, IPublicFileShare? files = null)
    : MetaConnectorBase(httpFactory, auth), IPlatformConnector
{
    internal const string Uploading = "container-uploading";
    internal const string Ready = "container";
    internal const string Publishing = "container-publishing";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    /// <summary>Publicações cujo contêiner foi criado com capa (para refazer sem ela se o Instagram recusar).</summary>
    private readonly ConcurrentDictionary<Guid, bool> _withCover = new();

    /// <summary>Publicações que já falharam com a capa: o próximo envio vai sem ela.</summary>
    private readonly ConcurrentDictionary<Guid, bool> _withoutCover = new();

    public Platform Platform => Platform.Instagram;
    public bool IsSimulation => false;
    public bool IsAvailable => Auth.Connection?.InstagramUserId is not null;

    public async Task<PublishOutcome> PublishAsync(PublishRequest request, CancellationToken ct)
    {
        var connection = Auth.Connection;
        if (connection?.InstagramUserId is null) return PublishOutcome.AuthRequired("A conta do Instagram não está conectada.");

        try
        {
            var (stage, containerId) = Graph.Split(request.ExternalId);
            if (stage is Ready or Publishing) return await CheckContainerAsync(connection, request, stage, containerId, ct);
            if (stage == "" && containerId != "") return PublishOutcome.Published(containerId, null); // já publicado

            // Novo envio, ou upload anterior interrompido: um contêiner não publicado é descartável.
            var fields = new List<(string Key, string Value)>
            {
                ("media_type", "REELS"), ("upload_type", "resumable"), ("caption", request.Text),
                ("share_to_feed", options.Value.ShareToFeed ? "true" : "false"),
            };
            var cover = await ShareCoverAsync(request, ct);
            if (cover is not null) fields.Add(("cover_url", cover));

            var create = await SendAsync(HttpMethod.Post, $"{Graph.Api}/{connection.InstagramUserId}/media", connection.PageToken,
                Form([.. fields]), ct);
            if (create.Error is not null)
            {
                await StopCoverAsync(request);
                return await FailAsync(create.Error, create.Status, "O Instagram não criou o Reel", ct);
            }

            containerId = Graph.Text(create.Body, "id") ?? throw new JsonException("Resposta do Instagram sem id do contêiner.");
            var uploadUrl = Graph.Text(create.Body, "uri") ?? $"{Graph.Upload}/ig-api-upload/{Graph.Version}/{containerId}";
            await request.SaveExternalIdAsync($"{Uploading}:{containerId}");

            var size = new FileInfo(request.VideoPath).Length;
            await using var file = new FileStream(request.VideoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
            var content = new StreamContent(file);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = size;
            content.Headers.Add("offset", "0");
            content.Headers.Add("file_size", size.ToString(CultureInfo.InvariantCulture));

            var upload = await SendAsync(HttpMethod.Post, uploadUrl, connection.PageToken, content, ct, scheme: "OAuth");
            if (upload.Error is not null)
            {
                await StopCoverAsync(request);
                return await FailAsync(upload.Error, upload.Status, "O Instagram recusou o arquivo do Reel", ct);
            }

            await request.SaveExternalIdAsync($"{Ready}:{containerId}");
            return PublishOutcome.Processing($"{Ready}:{containerId}", PollInterval);
        }
        catch (Exception ex) when (Graph.IsConnectivity(ex, ct))
        {
            return PublishOutcome.Offline($"Sem conexão com o Instagram ({ex.Message}). O envio será refeito.");
        }
    }

    public async Task<PublishOutcome> CheckAsync(PublishRequest request, CancellationToken ct)
    {
        var connection = Auth.Connection;
        if (connection?.InstagramUserId is null) return PublishOutcome.Unknown("A conta do Instagram não está conectada.");

        var (stage, containerId) = Graph.Split(request.ExternalId);
        if (stage is not (Ready or Publishing))
        {
            return PublishOutcome.NotFound("O envio do Reel não foi concluído; será refeito.");
        }

        try
        {
            return await CheckContainerAsync(connection, request, stage, containerId, ct);
        }
        catch (Exception ex) when (Graph.IsConnectivity(ex, ct))
        {
            // Se a queda foi durante o pedido de publicação, o estado "container-publishing" já está gravado.
            return PublishOutcome.Unknown($"Sem conexão com o Instagram ({ex.Message}).");
        }
    }

    private async Task<PublishOutcome> CheckContainerAsync(MetaConnection connection, PublishRequest request, string stage, string containerId, CancellationToken ct)
    {
        var response = await SendAsync(HttpMethod.Get, $"{Graph.Api}/{containerId}?fields=status_code,status", connection.PageToken, null, ct);
        if (response.Error is not null)
        {
            if (response.Error.IsAuth || response.Error.IsPermission) await Auth.MarkNeedsReauthAsync(response.Error.Message, ct);
            return PublishOutcome.Unknown($"Não foi possível consultar o Reel no Instagram: {response.Error.Message}");
        }

        var statusCode = Graph.Text(response.Body, "status_code");
        // Fora de IN_PROGRESS o Instagram já buscou a capa: o link temporário pode fechar.
        if (statusCode != "IN_PROGRESS") await StopCoverAsync(request);

        switch (statusCode)
        {
            case "PUBLISHED":
                return PublishOutcome.Published(containerId, null);

            case "ERROR" when _withCover.TryRemove(request.PublicationId, out _):
                // Pode ter sido a capa: o próximo envio vai sem ela.
                _withoutCover[request.PublicationId] = true;
                await request.SaveExternalIdAsync("");
                return PublishOutcome.Transient(
                    $"O Instagram não processou o Reel com a capa ({Graph.Text(response.Body, "status") ?? "motivo não informado"}); o envio será refeito sem a capa.");

            case "ERROR":
                return PublishOutcome.Permanent($"O Instagram não conseguiu processar o vídeo: {Graph.Text(response.Body, "status") ?? "motivo não informado"}.");

            case "EXPIRED":
                await request.SaveExternalIdAsync("");
                return PublishOutcome.NotFound("O contêiner expirou no Instagram sem ser publicado; o envio será refeito.");

            case "IN_PROGRESS":
                return PublishOutcome.Processing($"{stage}:{containerId}", PollInterval);

            case "FINISHED" when stage == Publishing:
                // A publicação já foi pedida e a resposta se perdeu. Pedir de novo poderia duplicar o post.
                return PublishOutcome.Unknown("A publicação foi pedida ao Instagram, mas ainda não foi confirmada. Confira o perfil; o sistema continua consultando.");

            case "FINISHED":
                return await PublishContainerAsync(connection, request, containerId, ct);

            default:
                return PublishOutcome.Unknown("O Instagram informou um estado desconhecido para o Reel.");
        }
    }

    private async Task<PublishOutcome> PublishContainerAsync(MetaConnection connection, PublishRequest request, string containerId, CancellationToken ct)
    {
        await request.SaveExternalIdAsync($"{Publishing}:{containerId}");

        var publish = await SendAsync(HttpMethod.Post, $"{Graph.Api}/{connection.InstagramUserId}/media_publish", connection.PageToken,
            Form(("creation_id", containerId)), ct);
        if (publish.Error is not null)
        {
            // A Meta respondeu com erro: a publicação não aconteceu, então o pedido pode ser repetido depois.
            await request.SaveExternalIdAsync($"{Ready}:{containerId}");
            return await FailAsync(publish.Error, publish.Status, "O Instagram não publicou o Reel", ct);
        }

        var mediaId = Graph.Text(publish.Body, "id") ?? containerId;
        await request.SaveExternalIdAsync(mediaId);

        string? permalink = null;
        try
        {
            var media = await SendAsync(HttpMethod.Get, $"{Graph.Api}/{mediaId}?fields=permalink", connection.PageToken, null, ct);
            if (media.Error is null) permalink = Graph.Text(media.Body, "permalink");
        }
        catch (Exception ex) when (Graph.IsConnectivity(ex, ct))
        {
            // O Reel já está publicado; só o link ficou sem ser lido.
        }

        return PublishOutcome.Published(mediaId, permalink);
    }

    /// <summary>Abre o link temporário da capa. Nulo quando não há capa, ela já falhou ou o link não abriu.</summary>
    private async Task<string?> ShareCoverAsync(PublishRequest request, CancellationToken ct)
    {
        if (files is null || request.ThumbnailPath is null || !File.Exists(request.ThumbnailPath)) return null;
        if (_withoutCover.ContainsKey(request.PublicationId)) return null;

        var share = await files.ShareAsync(request.PublicationId, request.ThumbnailPath, ct);
        if (share.Url is null) return null; // sem o link, o Reel sai com a capa automática

        _withCover[request.PublicationId] = true;
        return share.Url;
    }

    private Task StopCoverAsync(PublishRequest request) => files?.StopAsync(request.PublicationId) ?? Task.CompletedTask;
}

public static class MetaServiceCollectionExtensions
{
    public static IServiceCollection AddMetaConnectors(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<InstagramOptions>(configuration.GetSection(InstagramOptions.SectionName));
        // Envio do arquivo em um único pedido: o prazo precisa cobrir vídeos grandes em conexões lentas.
        // Sem log de pedidos HTTP, para que nenhum identificador de sessão vá para os logs.
        services.AddHttpClient(MetaAuth.HttpClientName, client => client.Timeout = TimeSpan.FromMinutes(30)).RemoveAllLoggers();
        services.AddSingleton<MetaAuth>();
        services.AddSingleton<FacebookConnector>();
        services.AddSingleton<InstagramConnector>();
        services.AddSingleton<IPlatformConnector>(sp => sp.GetRequiredService<FacebookConnector>());
        services.AddSingleton<IPlatformConnector>(sp => sp.GetRequiredService<InstagramConnector>());

        // Threads: o token vai como parâmetro do pedido (é o formato documentado), então este cliente
        // também não registra pedidos, e os erros devolvidos nunca citam o endereço.
        services.AddHttpClient(ThreadsAuth.HttpClientName, client => client.Timeout = TimeSpan.FromMinutes(2)).RemoveAllLoggers();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ThreadsAuth>();
        services.AddSingleton<ThreadsConnector>(); // usa o IPublicFileShare registrado em AddBast
        services.AddSingleton<IPlatformConnector>(sp => sp.GetRequiredService<ThreadsConnector>());
        return services;
    }
}
