using System.Net;
using System.Net.Http.Headers;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Server.Api;

namespace SpokaPilote.Server.Cache;

/// <summary>Adresse refusée par la liste des domaines officiels (requête initiale ou redirection).</summary>
public sealed class CacheRefusedException(string message) : Exception(message);

/// <summary>
/// Accès de la console aux sources officielles pour le cache : HTTPS uniquement, domaines de la liste partagée avec le
/// client (<see cref="OfficialHosts"/>), redirections suivies une par une et chacune revérifiée (une redirection vers un
/// autre domaine est refusée), contenu reçu tel quel (pas de décompression : les octets mis en cache sont ceux de la source).
/// Proxy sortant : variables HTTPS_PROXY / NO_PROXY du conteneur.
/// </summary>
public sealed class DriverCacheOrigin : IDisposable
{
    public const int MaxRedirects = 5;
    private readonly HttpClient _http;

    public DriverCacheOrigin() : this(null) { }

    /// <summary>Gestionnaire HTTP remplaçable (tests : source officielle simulée en mémoire, jamais de vrai réseau).</summary>
    public DriverCacheOrigin(HttpMessageHandler? handler)
    {
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SpokaPilote-Server", ApiEndpoints.Version));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(driver-cache)"));
    }

    /// <summary>
    /// Envoie la requête construite par <paramref name="build"/> pour <paramref name="url"/> puis suit les redirections
    /// (au plus <see cref="MaxRedirects"/>), chacune vérifiée par <paramref name="allowed"/>. La réponse finale (en-têtes
    /// seulement, corps en flux) est renvoyée quel que soit son statut.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(Uri url, Func<Uri, HttpRequestMessage> build, Func<Uri, bool> allowed, CancellationToken ct)
    {
        var current = url;
        for (var hop = 0; ; hop++)
        {
            if (!allowed(current))
                throw new CacheRefusedException(hop == 0
                    ? $"{current.Host} ne fait pas partie des sources officielles autorisées."
                    : $"Redirection refusée vers {current.Host} (hors des sources officielles).");
            var request = build(current);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                response.Dispose();
                if (hop >= MaxRedirects) throw new HttpRequestException($"Trop de redirections depuis {url.Host}.");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }
            return response;
        }
    }

    public Task<HttpResponseMessage> GetAsync(Uri url, Func<Uri, bool> allowed, CancellationToken ct) =>
        SendAsync(url, u => new HttpRequestMessage(HttpMethod.Get, u), allowed, ct);

    public void Dispose() => _http.Dispose();
}
