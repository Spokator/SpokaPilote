using System.Net;
using System.Net.Http.Headers;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;

namespace SpokaPilote.Core.Printers;

/// <summary>
/// Chargement d'un profil d'imprimantes : fichier local/UNC, URL HTTPS, ou console centrale
/// (GET {ServerUrl}/api/printer-profile, en-tête « Authorization: Bearer {ServerToken} », 204 = aucun profil).
/// </summary>
public static class PrinterProfileSource
{
    public const string ServerEndpoint = "/api/printer-profile";
    private const int MaxSize = 2 * 1024 * 1024;

    /// <summary>Mots-clés désignant la console centrale à la place d'un fichier.</summary>
    public static bool IsServerKeyword(string? source) =>
        string.IsNullOrWhiteSpace(source) || source.Trim().ToLowerInvariant() is "serveur" or "server" or "console";

    /// <summary>HTTPS obligatoire, sauf vers le poste lui-même (localhost, 127.0.0.1, [::1]) pour les essais.</summary>
    public static bool IsAllowedUrl(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)));

    public static Uri ServerProfileUri(string serverUrl) =>
        new(serverUrl.Trim().TrimEnd('/') + ServerEndpoint, UriKind.Absolute);

    /// <summary>Retourne le profil (null si la console n'en publie aucun pour ce poste) et son origine lisible.</summary>
    public static async Task<(PrinterProfile? Profile, string Origin)> LoadAsync(string? source, Settings settings, CancellationToken ct = default)
    {
        if (IsServerKeyword(source))
        {
            if (string.IsNullOrWhiteSpace(settings.ServerUrl))
                throw new InvalidOperationException("Aucun profil indiqué et aucune console centrale configurée (réglage ServerUrl).");
            var uri = ServerProfileUri(settings.ServerUrl);
            var json = await DownloadAsync(uri, settings.ServerToken, ct).ConfigureAwait(false);
            return (json is null ? null : PrinterProfile.Parse(json), $"console centrale {uri.GetLeftPart(UriPartial.Authority)}");
        }

        var s = source!.Trim();
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(s, UriKind.Absolute);
            // Le jeton de la console n'est jamais envoyé à un autre hôte que la console elle-même.
            var token = settings.ServerUrl is not null && Uri.TryCreate(settings.ServerUrl, UriKind.Absolute, out var server) &&
                        Uri.Compare(server, uri, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0
                ? settings.ServerToken
                : null;
            var json = await DownloadAsync(uri, token, ct).ConfigureAwait(false);
            return (json is null ? null : PrinterProfile.Parse(json), uri.GetLeftPart(UriPartial.Path));
        }

        var path = Path.GetFullPath(s);
        if (!File.Exists(path)) throw new FileNotFoundException($"Profil d'imprimantes introuvable : {path}");
        if (new FileInfo(path).Length > MaxSize) throw new InvalidDataException("Profil d'imprimantes trop volumineux (2 Mo maximum).");
        return (PrinterProfile.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)), path);
    }

    private static async Task<string?> DownloadAsync(Uri uri, string? token, CancellationToken ct)
    {
        if (!IsAllowedUrl(uri))
            throw new SecurityException($"Profil refusé : HTTPS obligatoire ({uri.GetLeftPart(UriPartial.Authority)}).");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());

        using var response = await Http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        // Une redirection ne doit pas faire quitter HTTPS.
        if (response.RequestMessage?.RequestUri is { } final && !IsAllowedUrl(final))
            throw new SecurityException($"Profil refusé : redirection hors HTTPS ({final.GetLeftPart(UriPartial.Authority)}).");
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException($"La console a refusé l'accès au profil (HTTP {(int)response.StatusCode}) : vérifiez le jeton ServerToken.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Profil indisponible : HTTP {(int)response.StatusCode} {response.ReasonPhrase} ({uri.GetLeftPart(UriPartial.Path)}).");
        if (response.Content.Headers.ContentLength > MaxSize)
            throw new InvalidDataException("Profil d'imprimantes trop volumineux (2 Mo maximum).");

        var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        if (json.Length > MaxSize) throw new InvalidDataException("Profil d'imprimantes trop volumineux (2 Mo maximum).");
        return json;
    }
}
