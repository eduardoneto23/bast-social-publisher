using Bast.Application.Publishing;
using Bast.Application;
using Bast.Domain;
using Microsoft.Extensions.Logging;

namespace Bast.Notifications.Telegram;

/// <summary>
/// TikTok em modo assistido. A API oficial do TikTok não permite publicação pública automática para
/// ferramenta de uso próprio (ver docs/APIS.md), então o sistema não fala com o TikTok: no horário, o bot
/// do Telegram manda o arquivo original e a legenda pronta, com os botões "Publiquei" e "Não vou publicar".
/// A publicação fica aguardando até o usuário responder (pelo Telegram ou pelo painel).
/// </summary>
public sealed class TikTokAssistedConnector(TelegramBot telegram) : IPlatformConnector
{
    internal const string DoneData = "tt:ok:";
    internal const string SkipData = "tt:no:";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    public Platform Platform => Platform.TikTok;
    public bool IsSimulation => false;
    public bool IsAvailable => telegram.IsConfigured;

    public async Task<PublishOutcome> PublishAsync(PublishRequest request, CancellationToken ct)
    {
        // Mensagens já entregues: só aguarda a resposta, nunca reenvia.
        if (request.ExternalId is { } existing && (existing.StartsWith(ManualPublishing.SentPrefix, StringComparison.Ordinal)
                                                    || existing is ManualPublishing.Done or ManualPublishing.Skipped))
        {
            return Interpret(existing);
        }

        var folder = Path.GetFileName(Path.GetDirectoryName(request.VideoPath)) ?? "vídeo";
        var size = new FileInfo(request.VideoPath).Length;

        string intro;
        if (size <= TelegramBot.MaxUploadBytes)
        {
            var file = await telegram.SendDocumentAsync(request.VideoPath, $"{folder}{Path.GetExtension(request.VideoPath)}",
                $"🎵 Hora de publicar no TikTok: {folder}", ct);
            if (file.Status != ChannelSendStatus.Sent) return Failure(file, "o vídeo");
            intro = "Legenda para o TikTok (toque e segure para copiar). Depois de publicar no app, toque em Publiquei.";
        }
        else
        {
            intro = $"🎵 Hora de publicar no TikTok: {folder}\n" +
                    $"O vídeo tem {size / (1024 * 1024)} MB, acima do limite de 50 MB que o Telegram aceita de bots. " +
                    $"Pegue o arquivo no computador, na pasta \"03 - PUBLICANDO\\{folder}\".\n\n" +
                    "Legenda (toque e segure para copiar). Depois de publicar no app, toque em Publiquei.";
        }

        var id = request.PublicationId.ToString("N");
        var message = await telegram.SendWithButtonsAsync($"{intro}\n\n{request.Text}",
            [new TelegramButton("✅ Publiquei", DoneData + id), new TelegramButton("⏭️ Não vou publicar", SkipData + id)], ct);
        if (message.Status != ChannelSendStatus.Sent) return Failure(message, "a legenda");

        var sent = $"{ManualPublishing.SentPrefix}{message.MessageId}";
        await request.SaveExternalIdAsync(sent);
        return PublishOutcome.Processing(sent, CheckInterval);
    }

    public Task<PublishOutcome> CheckAsync(PublishRequest request, CancellationToken ct) =>
        Task.FromResult(request.ExternalId is { } id ? Interpret(id) : PublishOutcome.NotFound("O vídeo ainda não foi entregue pelo Telegram; será enviado de novo."));

    private static PublishOutcome Interpret(string externalId) => externalId switch
    {
        ManualPublishing.Done => PublishOutcome.Published("tiktok-manual", null),
        ManualPublishing.Skipped => PublishOutcome.Skipped("Você marcou que não vai publicar este vídeo no TikTok."),
        _ when externalId.StartsWith(ManualPublishing.SentPrefix, StringComparison.Ordinal) => PublishOutcome.StillWaiting(CheckInterval),
        _ => PublishOutcome.NotFound("O vídeo ainda não foi entregue pelo Telegram; será enviado de novo."),
    };

    private static PublishOutcome Failure(TelegramSent sent, string what) => sent.Status == ChannelSendStatus.Retry
        ? PublishOutcome.Offline($"Não foi possível enviar {what} pelo Telegram: {sent.Error}")
        : PublishOutcome.AuthRequired($"O Telegram recusou {what}: {sent.Error}");

    /// <summary>Lê o botão tocado: (publicação, publicou?) ou nulo se não for deste fluxo.</summary>
    public static (Guid PublicationId, bool Published)? ParseButton(string data)
    {
        var published = data.StartsWith(DoneData, StringComparison.Ordinal);
        if (!published && !data.StartsWith(SkipData, StringComparison.Ordinal)) return null;
        return Guid.TryParseExact(data[DoneData.Length..], "N", out var id) ? (id, published) : null;
    }
}

/// <summary>Recebe os toques nos botões do Telegram e registra a resposta do usuário.</summary>
public sealed class TelegramButtonsHandler(TelegramBot telegram, ManualPublishing manual, ILogger<TelegramButtonsHandler> logger)
{
    public async Task RunOnceAsync(TimeSpan wait, CancellationToken ct)
    {
        foreach (var callback in await telegram.ReadCallbacksAsync(wait, ct))
        {
            if (!callback.FromLinkedChat)
            {
                await telegram.AnswerCallbackAsync(callback.Id, "Esta conversa não está vinculada.", ct);
                continue;
            }

            var parsed = TikTokAssistedConnector.ParseButton(callback.Data);
            if (parsed is null)
            {
                await telegram.AnswerCallbackAsync(callback.Id, "Botão desconhecido.", ct);
                continue;
            }

            var (ok, message) = await manual.AnswerAsync(parsed.Value.PublicationId, parsed.Value.Published, ct);
            await telegram.AnswerCallbackAsync(callback.Id, message, ct);
            if (ok && callback.MessageId is { } messageId)
            {
                await telegram.EditTextAsync(messageId, (parsed.Value.Published ? "✅ " : "⏭️ ") + message, ct);
            }

            logger.LogInformation("TikTok assistido: {Message}", message);
        }
    }
}
