using System.Net;
using System.Net.Http.Headers;

namespace SpokaPilote.Core.Infrastructure;

public static class Http
{
    private static readonly Lazy<HttpClient> SharedClient = new(Create);

    /// <summary>
    /// Client partagé (proxy système, décompression). Créé au premier usage : le User-Agent porte le nom du produit
    /// en vigueur (<see cref="AppIdentity.Current"/>).
    /// </summary>
    public static HttpClient Client => SharedClient.Value;

    private static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // Proxy système (WinHTTP/IE) utilisé automatiquement, avec les identifiants Windows de l'utilisateur.
            UseProxy = true,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
            MaxConnectionsPerServer = 8,
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppIdentity.Current.ProductName, "0.1"));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(Windows NT 10.0; Win64; x64)"));
        return client;
    }

    public static async Task<string> GetStringAsync(string url, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
        using var response = await Client.GetAsync(url, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
    }

    /// <summary>Télécharge un fichier de catalogue/métadonnées en cache disque, rafraîchi après <paramref name="maxAge"/>.</summary>
    public static async Task<string> GetCachedFileAsync(string url, string cacheName, TimeSpan maxAge, CancellationToken ct = default)
    {
        var path = Path.Combine(AppPaths.Cache, cacheName);
        if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < maxAge && new FileInfo(path).Length > 0)
            return path;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(10));
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var tmp = path + ".part";
        await using (var fs = File.Create(tmp))
        {
            await response.Content.CopyToAsync(fs, cts.Token).ConfigureAwait(false);
        }
        File.Move(tmp, path, overwrite: true);
        return path;
    }
}
