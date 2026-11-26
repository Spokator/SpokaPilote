using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using SpokaPilote.Core.Engine;
using SpokaPilote.Core.Infrastructure;

namespace SpokaPilote.Core.Online;

/// <summary>Échec d'échange avec la console centrale ; le message (en français) est destiné à l'utilisateur.</summary>
public sealed class ServerException(string message, HttpStatusCode? statusCode = null, Exception? inner = null) : Exception(message, inner)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

/// <summary>Résultat de « spokapilote server status ».</summary>
/// <param name="Reachable">La console répond (sonde /api/health).</param>
/// <param name="Authorized">Le jeton est accepté.</param>
/// <param name="Access">« agent » (jeton d'enrôlement) ou « admin », si autorisé.</param>
public sealed record ServerStatus(bool Reachable, bool Authorized, string? Access, string? ServerVersion, string Message);

/// <summary>Accusé de réception d'un rapport par la console.</summary>
/// <param name="ReportId">Identifiant du rapport dans l'historique de la console (consoles 3.0 et ultérieures).</param>
public sealed record ServerUploadResult(string Machine, bool Healthy, int DriverProblems, int Unresolved, long? ReportId = null);

/// <summary>Tâche à distance reçue de la console (spokapilote agent run).</summary>
/// <param name="Type">Action demandée ; l'agent n'exécute que les types de sa propre liste blanche (AgentJobTypes).</param>
/// <param name="DryRun">Simulation : montrer ce qui serait fait, sans rien modifier.</param>
public sealed record AgentJob(long Id, string Type, bool DryRun, DateTimeOffset? CreatedAt, string? CreatedBy);

/// <summary>Réponse de la console à l'interrogation de la file de tâches.</summary>
/// <param name="Known">La console connaît ce poste (au moins un rapport reçu).</param>
public sealed record AgentJobBatch(bool Known, IReadOnlyList<AgentJob> Jobs);

/// <summary>Résultat d'une tâche renvoyé à la console.</summary>
public sealed record AgentJobResult(bool Success, int ExitCode, string Message, long? ReportId = null, string? Log = null);

/// <summary>Liste blanche des tâches que l'agent accepte d'exécuter (la console applique la même).</summary>
public static class AgentJobTypes
{
    public const string Scan = "scan";
    public const string InstallMissing = "install-missing";
    public const string InstallUpdates = "install-updates";
    public const string DeployPrinters = "deploy-printers";
    public const string RollbackLast = "rollback-last";
    public const string SelfUpdate = "self-update";

    public static readonly IReadOnlyList<string> All = [Scan, InstallMissing, InstallUpdates, DeployPrinters, RollbackLast, SelfUpdate];

    public static bool IsAllowed(string? type) => type is not null && All.Contains(type, StringComparer.Ordinal);

    public static string Label(string type) => type switch
    {
        Scan => "analyse du poste",
        InstallMissing => "installation des pilotes manquants",
        InstallUpdates => "installation des pilotes manquants et des mises à jour",
        DeployPrinters => "déploiement du profil d'imprimantes",
        RollbackLast => "annulation de la dernière intervention",
        SelfUpdate => "mise à jour de SpokaPilote",
        _ => type,
    };
}

/// <summary>
/// Client de la console centrale SpokaPilote Server (Settings.ServerUrl + Settings.ServerToken) : envoi des rapports,
/// récupération de la politique de pilotes et du profil d'imprimantes. HTTPS obligatoire (sauf http://localhost pour
/// les essais) ; les redirections sont refusées pour que le jeton ne parte jamais ailleurs que vers l'adresse configurée.
/// </summary>
public sealed class ServerClient
{
    private static readonly Lazy<HttpClient> SharedClient = new(() => CreateClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        UseProxy = true,
        DefaultProxyCredentials = CredentialCache.DefaultCredentials,
    }));

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http;
    private readonly string? _token;

    /// <summary>Adresse de base normalisée (se termine par « / »).</summary>
    public Uri BaseUri { get; }

    public ServerClient(Settings settings) : this(settings.ServerUrl, settings.ServerToken, null) { }

    internal ServerClient(string? serverUrl, string? token, HttpMessageHandler? handler)
    {
        BaseUri = ValidateUrl(serverUrl);
        _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        if (_token is not null && _token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ServerException("ServerToken invalide : le jeton ne doit contenir ni espace ni retour à la ligne.");
        _http = handler is null ? SharedClient.Value : CreateClient(handler);
    }

    public static bool IsConfigured(Settings settings) => !string.IsNullOrWhiteSpace(settings.ServerUrl);

    /// <summary>
    /// Vérifie l'adresse de la console : absolue, HTTPS (HTTP accepté uniquement vers localhost / 127.0.0.1 / ::1),
    /// sans identifiants, requête ni fragment. Renvoie l'adresse normalisée terminée par « / ».
    /// </summary>
    public static Uri ValidateUrl(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
            throw new ServerException("Aucune console centrale configurée : spokapilote config set ServerUrl https://...");
        if (!Uri.TryCreate(serverUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new ServerException($"ServerUrl invalide : « {serverUrl} » (attendu : https://console.mon-entreprise.fr).");
        if (uri.Scheme == Uri.UriSchemeHttp && !IsLocalHost(uri.Host))
            throw new ServerException($"ServerUrl doit utiliser HTTPS (« {serverUrl} ») : le jeton et les rapports ne circulent jamais en clair. " +
                                      "HTTP n'est accepté que vers localhost, pour les essais.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new ServerException("ServerUrl ne doit pas contenir d'identifiants : le jeton se règle avec « config set ServerToken ».");
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ServerException($"ServerUrl ne doit contenir ni « ? » ni « # » : « {serverUrl} ».");
        var path = uri.AbsolutePath.EndsWith('/') ? uri.AbsolutePath : uri.AbsolutePath + "/";
        return new UriBuilder(uri.Scheme, uri.Host, uri.Port, path).Uri;
    }

    private static bool IsLocalHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && (ip.Equals(IPAddress.Loopback) || ip.Equals(IPAddress.IPv6Loopback)));

    // ------------------------------------------------------------------ opérations

    /// <summary>Envoie un rapport d'analyse / d'installation (JSON compressé gzip).</summary>
    public async Task<ServerUploadResult> UploadReportAsync(ScanReport report, CancellationToken ct = default)
    {
        var json = Encoding.UTF8.GetBytes(report.ToJson());
        using var compressed = new MemoryStream();
        await using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            await gzip.WriteAsync(json, ct).ConfigureAwait(false);

        var content = new ByteArrayContent(compressed.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        content.Headers.ContentEncoding.Add("gzip");

        using var response = await SendAsync(HttpMethod.Post, "api/reports", content, UploadTimeout, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "Envoi du rapport", ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            return new ServerUploadResult(
                root.TryGetProperty("machine", out var m) ? m.GetString() ?? report.System.ComputerName : report.System.ComputerName,
                root.TryGetProperty("healthy", out var h) && h.ValueKind == JsonValueKind.True,
                root.TryGetProperty("driverProblems", out var p) && p.TryGetInt32(out var pv) ? pv : 0,
                root.TryGetProperty("unresolved", out var u) && u.TryGetInt32(out var uv) ? uv : 0,
                root.TryGetProperty("reportId", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out var rv) ? rv : null);
        }
        catch (JsonException)
        {
            // Rapport accepté (2xx) mais accusé illisible : l'envoi a réussi.
            return new ServerUploadResult(report.System.ComputerName, false, 0, 0);
        }
    }

    /// <summary>Politique de pilotes publiée sur la console (JSON brut), ou null si aucune n'est définie.</summary>
    public Task<string?> GetPolicyJsonAsync(CancellationToken ct = default) => GetDocumentAsync("api/policy", "Politique de pilotes", ct);

    /// <summary>Profil d'imprimantes publié sur la console (JSON brut), ou null si aucun n'est défini.</summary>
    public Task<string?> GetPrinterProfileJsonAsync(CancellationToken ct = default) => GetDocumentAsync("api/printer-profile", "Profil d'imprimantes", ct);

    // ------------------------------------------------------------------ tâches à distance (agent)

    public const string MachineIdHeader = "X-SpokaPilote-Machine-Id";
    public const string MachineNameHeader = "X-SpokaPilote-Machine";

    /// <summary>
    /// Récupère les tâches en attente de ce poste (la console les passe « en cours »). Le poste s'identifie par son
    /// identifiant machine (MachineGuid) et son nom : il ne reçoit jamais les tâches d'un autre poste.
    /// </summary>
    public async Task<AgentJobBatch> GetPendingJobsAsync(string machineId, string machineName, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/agent/jobs", null, RequestTimeout, ct, identity: (machineId, machineName)).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "Récupération des tâches", ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var known = root.TryGetProperty("known", out var k) && k.ValueKind == JsonValueKind.True;
            var jobs = new List<AgentJob>();
            if (root.TryGetProperty("jobs", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var j in list.EnumerateArray())
                {
                    if (!j.TryGetProperty("id", out var id) || !id.TryGetInt64(out var idValue)) continue;
                    jobs.Add(new AgentJob(
                        idValue,
                        j.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                        j.TryGetProperty("dryRun", out var d) && d.ValueKind == JsonValueKind.True,
                        j.TryGetProperty("createdAt", out var c) && c.ValueKind == JsonValueKind.String && c.TryGetDateTimeOffset(out var cv) ? cv : null,
                        j.TryGetProperty("createdBy", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null));
                }
            return new AgentJobBatch(known, jobs);
        }
        catch (JsonException ex)
        {
            throw new ServerException($"Récupération des tâches : réponse illisible de la console ({ex.Message}).");
        }
    }

    /// <summary>Renvoie le résultat d'une tâche (succès, code de sortie, message, rapport produit, fin du journal).</summary>
    public async Task SendJobResultAsync(long jobId, string machineId, string machineName, AgentJobResult result, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            success = result.Success,
            exitCode = result.ExitCode,
            message = result.Message,
            reportId = result.ReportId,
            log = result.Log,
        });
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await SendAsync(HttpMethod.Post, $"api/agent/jobs/{jobId}/result", content, UploadTimeout, ct,
            identity: (machineId, machineName)).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"Envoi du résultat de la tâche {jobId}", ct).ConfigureAwait(false);
    }

    /// <summary>Teste l'adresse puis le jeton ; ne lève pas d'exception pour un échec attendu (le message l'explique).</summary>
    public async Task<ServerStatus> TestAsync(CancellationToken ct = default)
    {
        string? version = null;
        try
        {
            using var health = await SendAsync(HttpMethod.Get, "api/health", null, RequestTimeout, ct, authenticate: false).ConfigureAwait(false);
            await EnsureSuccessAsync(health, "Sonde de la console", ct).ConfigureAwait(false);
            version = ReadString(await health.Content.ReadAsStringAsync(ct).ConfigureAwait(false), "version");
        }
        catch (ServerException ex)
        {
            return new ServerStatus(false, false, null, null, ex.Message);
        }

        if (_token is null)
            return new ServerStatus(true, false, null, version, "Console joignable, mais aucun jeton configuré : spokapilote config set ServerToken <jeton>.");
        try
        {
            using var who = await SendAsync(HttpMethod.Get, "api/whoami", null, RequestTimeout, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(who, "Vérification du jeton", ct).ConfigureAwait(false);
            var access = ReadString(await who.Content.ReadAsStringAsync(ct).ConfigureAwait(false), "access") ?? "agent";
            var message = access == "admin"
                ? "Connexion établie, mais avec le jeton ADMINISTRATEUR : utilisez le jeton d'enrôlement sur les postes."
                : "Connexion établie, jeton d'enrôlement accepté.";
            return new ServerStatus(true, true, access, version, message);
        }
        catch (ServerException ex)
        {
            return new ServerStatus(true, false, null, version, ex.Message);
        }
    }

    // ------------------------------------------------------------------ plomberie HTTP

    private async Task<string?> GetDocumentAsync(string path, string label, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, RequestTimeout, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        await EnsureSuccessAsync(response, label, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(json) ? null : json;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, TimeSpan timeout,
        CancellationToken ct, bool authenticate = true, (string MachineId, string Name)? identity = null)
    {
        if (authenticate && _token is null)
            throw new ServerException("Aucun jeton d'enrôlement configuré : spokapilote config set ServerToken <jeton>.");

        using var request = new HttpRequestMessage(method, new Uri(BaseUri, path)) { Content = content };
        request.Headers.Accept.ParseAdd("application/json");
        if (authenticate) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (identity is { } id)
        {
            request.Headers.TryAddWithoutValidation(MachineIdHeader, id.MachineId);
            request.Headers.TryAddWithoutValidation(MachineNameHeader, id.Name);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ServerException($"La console ({BaseUri.Host}) ne répond pas (délai de {timeout.TotalSeconds:0} s dépassé).");
        }
        catch (HttpRequestException ex)
        {
            var reason = ex.InnerException is AuthenticationException auth
                ? $"certificat HTTPS refusé ({auth.Message})"
                : ex.Message;
            throw new ServerException($"Console injoignable ({BaseUri.Host}) : {reason}", null, ex);
        }
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var code = (int)response.StatusCode;
        if (code is >= 300 and < 400)
            throw new ServerException($"{operation} : la console redirige vers {response.Headers.Location?.ToString() ?? "une autre adresse"}. " +
                                      "Les redirections sont refusées : corrigez ServerUrl avec l'adresse finale.", response.StatusCode);

        string? detail = null;
        try { detail = ReadString(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), "error"); }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { /* corps illisible */ }

        throw new ServerException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Jeton refusé par la console : vérifiez ServerToken (jeton d'enrôlement).",
            HttpStatusCode.Forbidden => $"{operation} : ce jeton n'autorise pas l'opération{(detail is null ? "" : $" ({detail})")}.",
            HttpStatusCode.NotFound => $"{operation} : {BaseUri} ne semble pas être une console SpokaPilote (réponse 404).",
            HttpStatusCode.RequestEntityTooLarge => $"{operation} : refusé, trop volumineux pour la console{(detail is null ? "" : $" ({detail})")}.",
            HttpStatusCode.BadRequest => $"{operation} : refusé par la console — {detail ?? "requête invalide"}.",
            HttpStatusCode.TooManyRequests => $"{operation} : trop de tentatives avec un jeton refusé depuis cette adresse ; " +
                                              "la console bloque temporairement les accès (réessayez dans une minute, vérifiez ServerToken).",
            HttpStatusCode.Conflict => $"{operation} : {detail ?? "conflit"}.",
            _ => $"{operation} : erreur de la console ({code}){(detail is null ? "" : $" — {detail}")}.",
        }, response.StatusCode);
    }

    private static string? ReadString(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(property, out var v) &&
                   v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SpokaPilote", UpdateChecker.CurrentVersion.ToString(3)));
        return client;
    }
}
