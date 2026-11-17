namespace Bast.Application;

/// <summary>
/// A API escuta só em 127.0.0.1 e não tem login, porque quem a usa é o dono do computador.
/// Mas o navegador desse mesmo computador abre páginas de terceiros, e uma delas poderia tentar
/// falar com a API local (pausar a fila, cancelar um vídeo, desconectar uma conta).
/// Este filtro recusa pedidos que não partiram do próprio painel nem de um script local.
/// </summary>
public static class LocalRequestGuard
{
    /// <param name="method">Método HTTP.</param>
    /// <param name="host">Cabeçalho Host (com ou sem porta).</param>
    /// <param name="origin">Cabeçalho Origin, quando o navegador o envia.</param>
    /// <param name="secFetchSite">Cabeçalho Sec-Fetch-Site, quando o navegador o envia.</param>
    public static bool IsAllowed(string method, string? host, string? origin, string? secFetchSite)
    {
        // Um nome de domínio de terceiros apontado para 127.0.0.1 (DNS rebinding) chega com outro Host.
        if (!IsLoopbackHost(host)) return false;

        // Leituras só devolvem dados a quem o navegador permite ler; sem CORS, outra origem não lê nada.
        if (method is "GET" or "HEAD" or "OPTIONS") return true;

        // Scripts locais (PowerShell) não enviam Origin nem Sec-Fetch-Site.
        if (secFetchSite is not null && secFetchSite is not ("same-origin" or "none")) return false;
        if (origin is null) return true;

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
               && uri.Scheme == Uri.UriSchemeHttp
               && IsLoopbackHost(uri.Authority)
               && string.Equals(uri.Authority, host, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        var name = host;
        var colon = host.LastIndexOf(':');
        if (colon > 0 && !host.EndsWith(']')) name = host[..colon];
        return name is "127.0.0.1" or "localhost" or "[::1]";
    }
}
