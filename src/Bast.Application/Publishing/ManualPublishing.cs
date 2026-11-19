using Bast.Application.Persistence;
using Bast.Domain;
using Microsoft.EntityFrameworkCore;

namespace Bast.Application.Publishing;

/// <summary>
/// Publicação assistida (TikTok): o sistema entrega o vídeo e a legenda ao usuário, que publica no app
/// da rede. A resposta do usuário ("publiquei" ou "não vou publicar") fica gravada no identificador da
/// publicação; o motor a lê na próxima consulta e conclui a publicação. Nada aqui fala com a plataforma.
/// </summary>
public sealed class ManualPublishing(IDbContextFactory<BastDbContext> factory)
{
    public const string SentPrefix = "manual-sent:";
    public const string Done = "manual-done";
    public const string Skipped = "manual-skipped";

    public static bool IsAssisted(Platform platform) => platform == Platform.TikTok;

    /// <returns>Mensagem para o usuário.</returns>
    public async Task<(bool Ok, string Message)> AnswerAsync(Guid publicationId, bool published, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var publication = await db.Publications.Include(p => p.Video).FirstOrDefaultAsync(p => p.Id == publicationId, ct);
        if (publication is null || !IsAssisted(publication.Platform)) return (false, "Publicação não encontrada.");

        var name = publication.Video?.FolderName ?? "vídeo";
        if (publication.ExternalId is Done || publication.State == PublicationState.Published) return (true, $"{name}: já estava marcado como publicado.");
        if (publication.ExternalId is Skipped || publication.State == PublicationState.Cancelled) return (true, $"{name}: já estava marcado como não publicado.");
        if (publication.State != PublicationState.Processing || publication.ExternalId?.StartsWith(SentPrefix, StringComparison.Ordinal) != true)
        {
            return (false, $"{name}: esta publicação não está aguardando a sua confirmação.");
        }

        publication.ExternalId = published ? Done : Skipped;
        publication.NextRetryAtUtc = null; // o motor confere no próximo ciclo
        await db.SaveChangesAsync(ct);
        return (true, published ? $"{name}: marcado como publicado no TikTok." : $"{name}: marcado como não publicado no TikTok.");
    }
}
