using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Bast.Application.Radar;

namespace Bast.Connectors.YouTube;

/// <summary>
/// Pesquisa do Radar na API oficial do YouTube (search.list + videos.list) com a conta já conectada (escopo
/// youtube.readonly). Só devolve números que a API informou; o que ela não informa (ex.: curtidas ocultas)
/// volta como nulo ("não disponível"). Cada busca custa 100 unidades da cota diária, compartilhada com os envios.
/// </summary>
public sealed class YouTubeResearch(IHttpClientFactory httpFactory, GoogleAuth auth) : IYouTubeResearch
{
    private const string Api = "https://www.googleapis.com/youtube/v3";

    public bool IsAvailable => auth.IsConnected;

    public async Task<IReadOnlyList<RadarVideo>> SearchAsync(string query, string language, string region, int days, int max, CancellationToken ct)
    {
        var token = await auth.GetAccessTokenAsync(ct);
        var after = DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 60)).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var search = $"{Api}/search?part=snippet&type=video&maxResults={Math.Clamp(max, 1, 25)}&order=viewCount" +
                     $"&publishedAfter={Uri.EscapeDataString(after)}&q={Uri.EscapeDataString(query)}" +
                     $"&relevanceLanguage={Uri.EscapeDataString(language)}&regionCode={Uri.EscapeDataString(region)}";
        using var found = await GetAsync(search, token, ct);
        var ids = found.RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").TryGetProperty("videoId", out var v) ? v.GetString() : null)
            .Where(v => !string.IsNullOrEmpty(v)).Cast<string>().ToList();
        if (ids.Count == 0) return [];

        using var details = await GetAsync($"{Api}/videos?part=snippet,statistics,contentDetails&id={string.Join(',', ids)}", token, ct);
        var now = DateTime.UtcNow;
        var list = new List<RadarVideo>();
        foreach (var item in details.RootElement.GetProperty("items").EnumerateArray())
        {
            var snippet = item.GetProperty("snippet");
            var stats = item.TryGetProperty("statistics", out var s) ? s : default;
            var published = snippet.TryGetProperty("publishedAt", out var p) && p.TryGetDateTime(out var d) ? d.ToUniversalTime() : (DateTime?)null;
            var views = Number(stats, "viewCount");
            var hours = published is null ? (double?)null : Math.Max(1, (now - published.Value).TotalHours);
            list.Add(new RadarVideo(
                item.GetProperty("id").GetString()!,
                "https://www.youtube.com/watch?v=" + item.GetProperty("id").GetString(),
                snippet.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                snippet.TryGetProperty("channelTitle", out var c) ? c.GetString() ?? "" : "",
                published, views, Number(stats, "likeCount"), Number(stats, "commentCount"),
                views is null || hours is null ? null : Math.Round(views.Value / hours.Value, 1),
                item.TryGetProperty("contentDetails", out var cd) && cd.TryGetProperty("duration", out var dur) ? dur.GetString() : null,
                language, region, now));
        }

        return list.OrderByDescending(v => v.ViewsPerHour ?? 0).ToList();
    }

    private static long? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) &&
        long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private async Task<JsonDocument> GetAsync(string url, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpFactory.CreateClient(GoogleAuth.HttpClientName).SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var reason = body.Contains("quotaExceeded", StringComparison.Ordinal) ? "cota diária do YouTube esgotada"
                : body.Contains("authError", StringComparison.Ordinal) || (int)response.StatusCode == 401 ? "conta do YouTube precisa ser reconectada"
                : $"HTTP {(int)response.StatusCode}";
            throw new YouTubeResearchException($"A API do YouTube recusou a busca: {reason}.");
        }

        return JsonDocument.Parse(body);
    }
}
