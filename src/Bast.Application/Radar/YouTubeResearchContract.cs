namespace Bast.Application.Radar;

/// <summary>Vídeo encontrado na pesquisa do Radar, com as métricas públicas que a API informou (nulo = não disponível).</summary>
public sealed record RadarVideo(
    string VideoId, string Url, string Title, string Channel, DateTime? PublishedAtUtc,
    long? Views, long? Likes, long? Comments, double? ViewsPerHour, string? Duration,
    string Language, string Region, DateTime ConsultedAtUtc)
{
    /// <summary>Crescimento desde a consulta anterior do mesmo vídeo (views por hora), quando houver.</summary>
    public double? RecentViewsPerHour { get; init; }
}

public interface IYouTubeResearch
{
    bool IsAvailable { get; }

    Task<IReadOnlyList<RadarVideo>> SearchAsync(string query, string language, string region, int days, int max, CancellationToken ct);
}

public sealed class YouTubeResearchException(string message) : Exception(message);
