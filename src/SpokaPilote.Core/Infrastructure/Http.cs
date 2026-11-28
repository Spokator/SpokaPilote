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

    /// <summary>
    /// Télécharge un fichier de catalogue/métadonnées en cache disque, rafraîchi après <paramref name="maxAge"/>. Avec une
    /// console 4.0 (cache actif), le catalogue est d'abord demandé à la console : la date du fichier local est alors celle à
    /// laquelle la console l'a obtenu de la source, pour que l'âge maximal compte depuis la source. Un catalogue absent de
    /// la source (404) lève HttpRequestException comme en accès direct.
    /// </summary>
    public static async Task<string> GetCachedFileAsync(string url, string cacheName, TimeSpan maxAge, CancellationToken ct = default)
    {
        var path = Path.Combine(AppPaths.Cache, cacheName);
        if (IsFresh(path, maxAge, DateTime.UtcNow)) return path;

        if (Online.ConsoleCache.Current is { Catalogs: true } console && Uri.TryCreate(url, UriKind.Absolute, out var official))
        {
            var viaConsole = path + ".console";
            var result = await console.TryCatalogFileAsync(official, viaConsole, ct).ConfigureAwait(false);
            if (result is { Status: Online.ConsoleCatalogStatus.Ok })
            {
                File.Move(viaConsole, path, overwrite: true);
                if (result.FetchedAt is { } fetched && fetched.UtcDateTime <= DateTime.UtcNow) File.SetLastWriteTimeUtc(path, fetched.UtcDateTime);
                return path;
            }
            if (result is { Status: Online.ConsoleCatalogStatus.NotFoundAtSource })
                throw new HttpRequestException($"{official.Host} : {Path.GetFileName(official.AbsolutePath)} introuvable (404).", null, HttpStatusCode.NotFound);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(10));
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        // Fichier temporaire neuf (nom aléatoire, CreateNew) : jamais un « .part » laissé par quelqu'un d'autre.
        var tmp = $"{path}.{DataProtection.RandomName(6)}.part";
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await response.Content.CopyToAsync(fs, cts.Token).ConfigureAwait(false);
            }
            DataProtection.ClaimOwnership(tmp);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { /* nettoyé avec le cache */ }
        }
        return path;
    }

    /// <summary>
    /// Fichier en cache encore utilisable : non vide, daté de moins de <paramref name="maxAge"/> ; une date dans le futur
    /// (horloge déréglée, ou fichier daté exprès pour rester « frais » indéfiniment) le rend périmé. Exécution élevée : un
    /// fichier qui n'a pas été écrit par SYSTEM ou un administrateur est périmé.
    /// </summary>
    internal static bool IsFresh(string path, TimeSpan maxAge, DateTime nowUtc)
    {
        if (!File.Exists(path)) return false;
        var info = new FileInfo(path);
        var age = nowUtc - info.LastWriteTimeUtc;
        if (info.Length == 0 || age < TimeSpan.Zero || age >= maxAge) return false;
        return DataProtection.IsTrustedForElevatedUse(path);
    }
}
