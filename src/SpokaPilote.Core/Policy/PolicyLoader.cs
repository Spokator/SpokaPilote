using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Models;

namespace SpokaPilote.Core.Policy;

/// <summary>Politique effective d'une analyse, avec son origine (pour l'affichage et les rapports).</summary>
public sealed class LoadedPolicy
{
    /// <summary>Politique appliquée : organisation + liste de référence.</summary>
    public required DriverPolicy Effective { get; init; }
    public DriverPolicy? Organization { get; init; }
    /// <summary>Chemin ou URL de la politique de l'organisation (null = aucune configurée).</summary>
    public string? OrganizationOrigin { get; init; }
    /// <summary>La politique de l'organisation vient du cache local (source injoignable ou invalide).</summary>
    public bool FromCache { get; init; }
    public DateTime? CachedAt { get; init; }
    /// <summary>
    /// Interface non élevée : politique de la console lue dans le cache écrit par les exécutions SYSTEM du poste (ou
    /// indisponible), faute de jeton. Affichage seulement : la politique est revérifiée avec les droits administrateur au
    /// moment d'installer.
    /// </summary>
    public bool MachineCacheOnly { get; set; }
    public DriverPolicy? Reference { get; init; }
    /// <summary>« intégrée » ou chemin du cache rafraîchi depuis GitHub.</summary>
    public string ReferenceOrigin { get; init; } = "intégrée";
    /// <summary>Avertissements à afficher (source injoignable, politique invalide, cache utilisé...).</summary>
    public List<string> Messages { get; init; } = new();

    public static LoadedPolicy None { get; } = new() { Effective = DriverPolicy.Combine(null, null) };

    public string Summary => Organization is null
        ? $"aucune politique d'organisation · liste de référence : {ReferenceRuleCount} règle(s)"
        : $"{Organization.DisplayName} ({Organization.Rules.Count(r => !r.IsRollout)} règle(s){(FromCache ? ", depuis le cache" : "")}) · liste de référence : {ReferenceRuleCount} règle(s)" +
          RolloutSummary;

    /// <summary>« · diffusion par anneaux : anneau « Général », 3 retenue(s) » (console 4.0), vide sinon.</summary>
    public string RolloutSummary
    {
        get
        {
            if (Organization?.Rollout is not { } r) return "";
            var holds = Organization.Rules.Count(x => x.IsRollout && x.Id!.StartsWith(PolicyRollout.HoldPrefix, StringComparison.Ordinal));
            var suspended = Organization.Rules.Count(x => x.IsRollout && x.Id!.StartsWith(PolicyRollout.SuspendedPrefix, StringComparison.Ordinal) &&
                                                         !x.Id.StartsWith(PolicyRollout.SuspendedHardwarePrefix, StringComparison.Ordinal));
            var parts = new List<string>();
            if (r.Enabled) parts.Add($"anneau {r.RingLabel}");
            if (r.Decisions && Effective.Decisions is { } d)
            {
                var held = d.All.Values.Count(x => x.IsHold);
                parts.Add($"décisions de la console{(d.FromCache ? " (cache)" : "")} : {held} proposition(s) retenue(s) sur {d.Count}");
            }
            else if (r.Decisions && r.Enabled) parts.Add("décisions demandées à la console pour chaque proposition");
            if (holds > 0) parts.Add($"{holds} version(s) retenue(s)");
            if (suspended > 0) parts.Add($"{suspended} suspendue(s)");
            return parts.Count == 0 ? "" : $" · diffusion par anneaux : {string.Join(", ", parts)}";
        }
    }

    public int ReferenceRuleCount => Organization?.Settings.UseReferenceBlocklist == false ? 0 : Reference?.Rules.Count ?? 0;
}

/// <summary>
/// Charge la politique de pilotes : fichier local/UNC ou URL HTTPS (<see cref="Settings.PolicySource"/>), sinon
/// la console centrale ({ServerUrl}/api/policy, jeton Bearer). La dernière politique valide est gardée en cache
/// (%ProgramData%\SpokaPilote\policy-cache.json) pour fonctionner hors ligne. Une politique injoignable ou invalide
/// ne fait jamais échouer l'analyse : message clair + politique précédente (ou aucune).
/// </summary>
public sealed class PolicyLoader
{
    /// <summary>
    /// Liste de référence publiée sur le dépôt GitHub du projet. Simple fichier de métadonnées (aucun pilote n'est
    /// téléchargé depuis ce domaine) : il n'apparaît donc volontairement pas dans les domaines de <see cref="TrustPolicy"/>.
    /// </summary>
    public const string ReferenceListUrl = "https://raw.githubusercontent.com/Spokator/SpokaPilote/main/src/SpokaPilote.Core/Policy/known-bad-drivers.json";
    private const string EmbeddedResource = "SpokaPilote.Core.Policy.known-bad-drivers.json";
    private const int MaxPolicyBytes = 2 * 1024 * 1024;

    private readonly Settings _settings;
    private readonly HttpClient _http;

    public PolicyLoader(Settings settings, HttpClient? http = null)
    {
        _settings = settings;
        _http = http ?? Http.Client;
    }

    /// <summary>Remplace la source configurée pour cette exécution (option --policy). La politique n'est alors pas mise en cache.</summary>
    public string? SourceOverride { get; init; }
    public bool UseCache { get; init; } = true;
    /// <summary>Rafraîchit la liste de référence depuis GitHub (au plus une fois par <see cref="ReferenceRefreshInterval"/>).</summary>
    public bool RefreshReferenceList { get; init; } = true;
    public TimeSpan ReferenceRefreshInterval { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
    public string CacheFile { get; init; } = AppPaths.PolicyCache;
    public string ReferenceCacheFile { get; init; } = Path.Combine(AppPaths.Cache, "known-bad-drivers.json");

    /// <summary>En-tête par lequel un poste 4.0 demande la section « rollout » de sa politique (diffusion par anneaux).</summary>
    public const string RolloutHeader = "X-SpokaPilote-Rollout";

    /// <summary>En-tête par lequel un poste 4.0 demande ses retenues proposition par proposition (console en mode décisions).</summary>
    public const string DecisionsHeader = "X-SpokaPilote-Rollout-Decisions";

    /// <summary>Cache local des dernières décisions de diffusion (console injoignable).</summary>
    public string DecisionCacheFile { get; init; } = AppPaths.RolloutDecisionsCache;

    /// <summary>
    /// Interface non élevée de la version installée : le jeton de la console est réservé aux administrateurs, la console
    /// refuserait la demande. Les blocages et retenues sont alors affichés d'après la dernière politique reçue par les
    /// exécutions SYSTEM du poste (ce fichier, lu seulement, s'il a été écrit par SYSTEM ou un administrateur) ; la
    /// politique complète est revérifiée avec les droits administrateur au moment d'installer. Null = sans objet.
    /// </summary>
    public string? MachineCacheFile { get; init; } = AppPaths.MachinePolicyCache;
    /// <summary>Décisions de diffusion écrites par les exécutions SYSTEM du poste (voir <see cref="MachineCacheFile"/>).</summary>
    public string? MachineDecisionCacheFile { get; init; } = AppPaths.MachineRolloutDecisionsCache;

    /// <summary>Identité envoyée à la console avec la demande de politique (identifiant machine, nom) ; par défaut, celle du poste.</summary>
    public Func<(string? MachineId, string Name)>? Identity { get; init; }

    private static (string? MachineId, string Name) DefaultIdentity() => (Inventory.SystemInfoProvider.ReadMachineId(), Environment.MachineName);

    /// <summary>Source effective de la politique de l'organisation (null = aucune).</summary>
    public string? ResolveSource()
    {
        var source = SourceOverride ?? _settings.PolicySource;
        if (!string.IsNullOrWhiteSpace(source)) return Environment.ExpandEnvironmentVariables(source.Trim());
        if (!string.IsNullOrWhiteSpace(_settings.ServerUrl)) return _settings.ServerUrl.Trim().TrimEnd('/') + "/api/policy";
        return null;
    }

    /// <summary>Ne lève jamais d'exception (hors annulation).</summary>
    public async Task<LoadedPolicy> LoadAsync(CancellationToken ct = default)
    {
        var messages = new List<string>();
        var (reference, referenceOrigin) = await LoadReferenceAsync(messages, ct).ConfigureAwait(false);

        DriverPolicy? organization = null;
        var fromCache = false;
        DateTime? cachedAt = null;
        var source = ResolveSource();
        if (source is not null && IsConsoleWithoutToken(source))
        {
            // Interface non élevée : la console refuserait la demande (jeton lisible des seuls administrateurs).
            var machine = UseCache ? ReadMachineCache(source) : null;
            if (machine is not null)
            {
                messages.Add($"Interface sans droits administrateur : politique de pilotes {machine.Value.Policy.DisplayName} lue dans le cache du poste " +
                             $"(reçue par les exécutions administrateur le {machine.Value.SavedAt:dd/MM/yyyy HH:mm}). Blocages et retenues affichés d'après " +
                             "elle ; la politique à jour est revérifiée, avec les droits administrateur, au moment d'installer.");
                Log.Info(messages[^1]);
                var loaded = Build(machine.Value.Policy, source, true, machine.Value.SavedAt, reference, referenceOrigin, messages);
                loaded.MachineCacheOnly = true;
                return loaded;
            }
            messages.Add("Interface sans droits administrateur : politique de la console indisponible (jeton réservé aux administrateurs, aucune " +
                         "politique reçue par les exécutions administrateur de ce poste). Des pilotes affichés comme installables peuvent être bloqués " +
                         "ou retenus par votre organisation : la politique est appliquée, avec les droits administrateur, au moment d'installer.");
            Log.Info(messages[^1]);
            var none = Build(null, source, false, null, reference, referenceOrigin, messages);
            none.MachineCacheOnly = true;
            return none;
        }
        if (source is not null)
        {
            string? problem;
            try
            {
                var (json, noPolicy) = await FetchAsync(source, ct).ConfigureAwait(false);
                if (noPolicy)
                {
                    // La console répond explicitement « pas de politique » : on oublie l'ancienne.
                    if (UseCache) TryDelete(CacheFile);
                    Log.Info($"Politique de pilotes : aucune politique publiée par {source}.");
                    return Build(null, null, false, null, reference, referenceOrigin, messages);
                }
                var parsed = DriverPolicy.Parse(json!);
                if (parsed.IsValid)
                {
                    organization = parsed.Policy;
                    foreach (var w in parsed.Warnings) Log.Debug($"Politique {source} : {w}");
                    if (UseCache) SaveCache(source, json!);
                    Log.Info($"Politique de pilotes {organization!.DisplayName} chargée depuis {source} ({organization.Rules.Count} règle(s)).");
                    return Build(organization, source, false, null, reference, referenceOrigin, messages);
                }
                problem = $"politique invalide ({string.Join(" ; ", parsed.Errors.Take(3))}{(parsed.Errors.Count > 3 ? $" ; +{parsed.Errors.Count - 3} autre(s)" : "")})";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                problem = $"source injoignable ({ex.Message})";
            }

            // Repli : dernière politique valide connue (interface non élevée : à défaut, celle reçue par les exécutions SYSTEM).
            var cached = UseCache ? ReadCache() ?? ReadMachineCache(source) : null;
            if (cached is not null)
            {
                organization = cached.Value.Policy;
                fromCache = true;
                cachedAt = cached.Value.SavedAt;
                messages.Add($"Politique de pilotes {source} : {problem}. Dernière politique valide conservée : {organization.DisplayName} (cache du {cachedAt:dd/MM/yyyy HH:mm}).");
            }
            else
            {
                messages.Add($"Politique de pilotes {source} : {problem}. Aucune politique précédente en cache : seule la liste de référence s'applique.");
            }
            // Journalisé ; l'affichage passe par LoadedPolicy.Messages (évite les doublons à l'écran).
            Log.Info(messages[^1]);
        }
        return Build(organization, source, fromCache, cachedAt, reference, referenceOrigin, messages);
    }

    private static LoadedPolicy Build(DriverPolicy? org, string? origin, bool fromCache, DateTime? cachedAt,
        DriverPolicy? reference, string referenceOrigin, List<string> messages) => new()
    {
        Effective = DriverPolicy.Combine(org, reference),
        Organization = org,
        OrganizationOrigin = origin,
        FromCache = fromCache,
        CachedAt = cachedAt,
        Reference = reference,
        ReferenceOrigin = referenceOrigin,
        Messages = messages,
    };

    // ------------------------------------------------------------------ décisions de diffusion (console 4.0)

    /// <summary>Propositions au plus par demande (même limite que la console).</summary>
    public const int MaxDecisionCandidates = 1000;

    /// <summary>
    /// Décisions de diffusion pour les propositions d'une analyse (console en mode décisions : section « rollout » avec
    /// <c>decisions: true</c>) : POST {ServerUrl}/api/rollout/decide avec la source, le titre, la version et le type de chaque
    /// proposition (et de ses alternatives). Console injoignable, réponse invalide ou politique lue depuis le cache : dernières
    /// décisions connues (cache local), les propositions sans décision étant retenues si l'anneau retient les nouveautés.
    /// null si la politique n'est pas en mode décisions (rien à demander : retenues dans les règles, comportement précédent).
    /// Ne lève pas d'exception (hors annulation) ; les problèmes sont ajoutés à <see cref="LoadedPolicy.Messages"/>.
    /// </summary>
    public async Task<RolloutDecisions?> DecideAsync(LoadedPolicy policy, IEnumerable<DriverCandidate> candidates, CancellationToken ct = default)
    {
        if (policy.Organization?.Rollout is not { Decisions: true } || policy.OrganizationOrigin is not { } origin) return null;
        var all = new Dictionary<string, DriverCandidate>(StringComparer.Ordinal);
        foreach (var c in candidates)
            foreach (var x in c.Alternatives.Prepend(c))
                if (DriverIdentity.IsTrackable(x)) all.TryAdd(RolloutDecisions.Key(x), x);
        if (all.Count == 0) return new RolloutDecisions(new Dictionary<string, RolloutDecision>(), fromCache: false);

        string problem;
        if (policy.MachineCacheOnly) problem = "interface sans droits administrateur ; revérifiées au moment d'installer";
        else if (policy.FromCache) problem = "console injoignable lors du chargement de la politique";
        else
        {
            try
            {
                var decided = await RequestDecisionsAsync(origin, all.Values.Take(MaxDecisionCandidates).ToList(), ct).ConfigureAwait(false);
                RolloutDecisions.Save(DecisionCacheFile, origin, decided, DateTime.Now);
                Log.Info($"Diffusion par anneaux : {decided.Count} décision(s) reçue(s) de la console " +
                         $"({decided.Values.Count(d => d.IsHold)} retenue(s) ou suspension(s)).");
                return new RolloutDecisions(decided, fromCache: false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                problem = ex.Message;
            }
        }
        var cached = RolloutDecisions.FromCacheFile(DecisionCacheFile, origin, all.Values) ??
                     (MachineDecisionCacheFile is { } machine && File.Exists(machine) && DataProtection.IsWrittenByAdministrators(machine)
                         ? RolloutDecisions.FromCacheFile(machine, origin, all.Values)
                         : null) ??
                     new RolloutDecisions(new Dictionary<string, RolloutDecision>(), fromCache: true);
        var missing = all.Count - cached.Count;
        var r = policy.Organization.Rollout;
        policy.Messages.Add($"Diffusion par anneaux : décisions de la console indisponibles ({problem}). Dernières décisions connues appliquées " +
                            $"à {cached.Count} proposition(s){(cached.CachedAt is { } at ? $" (cache du {at:dd/MM/yyyy HH:mm})" : "")}" +
                            (missing > 0 ? r.HoldUnknown ? $" ; {missing} autre(s) retenue(s) par prudence jusqu'au retour de la console." : $" ; {missing} sans décision." : "."));
        Log.Info(policy.Messages[^1]);
        return cached;
    }

    private sealed record DecideCandidate(string Source, string Title, string? Version, string Kind, string? ItemId, string? HardwareVendor, string? DeviceClass);

    private static readonly JsonSerializerOptions DecideJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private async Task<Dictionary<string, RolloutDecision>> RequestDecisionsAsync(string policyUrl, IReadOnlyList<DriverCandidate> list, CancellationToken ct)
    {
        if (!Uri.TryCreate(policyUrl, UriKind.Absolute, out var policyUri) || !ShouldSendToken(policyUri))
            throw new InvalidOperationException("la politique ne vient pas de la console configurée (ServerUrl)");
        var uri = new Uri(_settings.ServerUrl!.Trim().TrimEnd('/') + "/api/rollout/decide");
        // 4.0.1 : identité d'une proposition sans version lisible (itemId) et matériel visé (hardwareVendor, deviceClass : même
        // pilote d'une autre source). Une console antérieure ignore ces champs (comportement précédent).
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            candidates = list.Select(c => new DecideCandidate(c.Source.ToString(), c.Title, c.Version, c.Kind.ToString(), DriverIdentity.ItemKey(c),
                c.HardwareVendor, c.HardwareVendor is null ? null : c.DeviceClass)),
        }, DecideJson);
        using var compressed = new MemoryStream();
        await using (var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            await gzip.WriteAsync(body, ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(compressed.ToArray()) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Content.Headers.ContentEncoding.Add("gzip");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ServerToken);
        var (machineId, machineName) = (Identity ?? DefaultIdentity)();
        if (machineId is not null) request.Headers.TryAddWithoutValidation(Online.ServerClient.MachineIdHeader, machineId);
        request.Headers.TryAddWithoutValidation(Online.ServerClient.MachineNameHeader, machineName);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException($"accès refusé (HTTP {(int)response.StatusCode}) : vérifiez le jeton ServerToken");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        if (text.Length > MaxPolicyBytes) throw new InvalidOperationException("réponse trop volumineuse");

        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("decisions", out var decisions) || decisions.ValueKind != JsonValueKind.Array || decisions.GetArrayLength() != list.Count)
            throw new InvalidOperationException("réponse de la console illisible (liste des décisions absente ou incomplète)");
        var now = DateTime.Now;
        var result = new Dictionary<string, RolloutDecision>(StringComparer.Ordinal);
        var i = 0;
        foreach (var d in decisions.EnumerateArray())
        {
            var c = list[i++];
            var status = d.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            // Statut inconnu (console plus récente) : traité comme une retenue (jamais comme une autorisation).
            if (status is not (RolloutDecision.Allowed or RolloutDecision.Held or RolloutDecision.Suspended or RolloutDecision.Unknown)) status = RolloutDecision.Held;
            result[RolloutDecisions.Key(c)] = new RolloutDecision
            {
                Status = status,
                Reason = d.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null,
                AvailableAt = d.TryGetProperty("availableAt", out var at) && at.ValueKind == JsonValueKind.String && at.TryGetDateTimeOffset(out var when) ? when : null,
                DecidedAt = now,
            };
        }
        return result;
    }

    // ------------------------------------------------------------------ récupération

    /// <summary>Lit la politique (fichier, partage ou URL). noPolicy = la console a répondu 204.</summary>
    public async Task<(string? Json, bool NoPolicy)> FetchAsync(string source, CancellationToken ct)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            if (!IsAllowedPolicyUrl(uri))
                throw new InvalidOperationException("HTTPS obligatoire (http n'est accepté que pour localhost / 127.0.0.1)");

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (ShouldSendToken(uri))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ServerToken);
                // Console 4.0 : le poste s'identifie pour recevoir la politique de SON anneau de déploiement, et demande la
                // section « rollout » (anneau, dates de disponibilité). Une console plus ancienne ignore ces en-têtes.
                var (machineId, machineName) = (Identity ?? DefaultIdentity)();
                if (machineId is not null) request.Headers.TryAddWithoutValidation(Online.ServerClient.MachineIdHeader, machineId);
                request.Headers.TryAddWithoutValidation(Online.ServerClient.MachineNameHeader, machineName);
                request.Headers.TryAddWithoutValidation(RolloutHeader, "1");
                // Console 4.0 récente : retenues décidées proposition par proposition (POST /api/rollout/decide) plutôt que la
                // liste de tout le parc dans la politique. Une console qui ne connaît pas cet en-tête sert la liste (comportement précédent).
                request.Headers.TryAddWithoutValidation(DecisionsHeader, "1");
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NoContent) return (null, true);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new InvalidOperationException($"accès refusé (HTTP {(int)response.StatusCode}) : vérifiez le jeton ServerToken");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            if (response.Content.Headers.ContentLength > MaxPolicyBytes)
                throw new InvalidOperationException("fichier trop volumineux (> 2 Mo)");
            var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (text.Length > MaxPolicyBytes) throw new InvalidOperationException("fichier trop volumineux (> 2 Mo)");
            return (text, false);
        }

        var path = uri is { IsFile: true } ? uri.LocalPath : source;
        // Un partage réseau injoignable peut bloquer longtemps : lecture bornée dans le temps.
        var read = Task.Run(() =>
        {
            if (!File.Exists(path)) throw new FileNotFoundException($"fichier introuvable : {path}");
            if (new FileInfo(path).Length > MaxPolicyBytes) throw new InvalidOperationException("fichier trop volumineux (> 2 Mo)");
            return File.ReadAllText(path);
        }, ct);
        var json = await read.WaitAsync(Timeout, ct).ConfigureAwait(false);
        return (json, false);
    }

    public static bool IsAllowedPolicyUrl(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps ||
        (uri.Scheme == Uri.UriSchemeHttp && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Le jeton de la console n'est envoyé qu'à la console elle-même (même hôte que ServerUrl).</summary>
    private bool ShouldSendToken(Uri uri) => !string.IsNullOrWhiteSpace(_settings.ServerToken) && IsConsoleUrl(uri);

    private bool IsConsoleUrl(Uri uri)
    {
        if (string.IsNullOrWhiteSpace(_settings.ServerUrl)) return false;
        return Uri.TryCreate(_settings.ServerUrl.Trim(), UriKind.Absolute, out var server) &&
               server.Host.Equals(uri.Host, StringComparison.OrdinalIgnoreCase) && server.Port == uri.Port &&
               server.Scheme == uri.Scheme;
    }

    /// <summary>Politique de la console demandée sans jeton par une interface non élevée (voir <see cref="MachineCacheFile"/>).</summary>
    private bool IsConsoleWithoutToken(string source) =>
        MachineCacheFile is not null && string.IsNullOrWhiteSpace(_settings.ServerToken) &&
        Uri.TryCreate(source, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && IsConsoleUrl(uri);

    /// <summary>
    /// Politique reçue par les exécutions SYSTEM du poste depuis la même source : lue seulement si le fichier (et son
    /// dossier) ont été écrits par SYSTEM ou un administrateur — jamais un fichier préparé par un utilisateur.
    /// </summary>
    private (DriverPolicy Policy, DateTime SavedAt)? ReadMachineCache(string source)
    {
        var path = MachineCacheFile;
        try
        {
            if (path is null || !File.Exists(path)) return null;
            if (!DataProtection.IsWrittenByAdministrators(path))
            {
                Log.Warn($"Cache de politique du poste ignoré : {path} n'a pas été écrit par un administrateur.");
                return null;
            }
            var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(path), CacheJson);
            if (entry is null || !string.Equals(entry.Origin, source, StringComparison.OrdinalIgnoreCase)) return null;
            var parsed = DriverPolicy.Parse(entry.Content);
            return parsed.IsValid ? (parsed.Policy!, entry.SavedAt) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Debug($"Cache de politique du poste illisible : {ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------ cache

    private sealed class CacheEntry
    {
        public string Origin { get; set; } = "";
        public DateTime SavedAt { get; set; }
        /// <summary>Contenu brut du fichier de politique (commentaires compris).</summary>
        public string Content { get; set; } = "";
    }

    private static readonly JsonSerializerOptions CacheJson = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private void SaveCache(string origin, string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            var tmp = CacheFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new CacheEntry { Origin = origin, SavedAt = DateTime.Now, Content = json }, CacheJson));
            File.Move(tmp, CacheFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Impossible d'enregistrer le cache de la politique ({CacheFile}) : {ex.Message}");
        }
    }

    private (DriverPolicy Policy, DateTime SavedAt)? ReadCache()
    {
        try
        {
            if (!File.Exists(CacheFile)) return null;
            var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(CacheFile), CacheJson);
            if (entry is null) return null;
            var parsed = DriverPolicy.Parse(entry.Content);
            return parsed.IsValid ? (parsed.Policy!, entry.SavedAt) : null;
        }
        catch (Exception ex)
        {
            Log.Debug($"Cache de politique illisible : {ex.Message}");
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* sans gravité */ }
    }

    // ------------------------------------------------------------------ liste de référence

    /// <summary>Liste noire de référence intégrée à SpokaPilote (ressource embarquée).</summary>
    public static PolicyParseResult LoadEmbeddedReference()
    {
        using var stream = typeof(PolicyLoader).Assembly.GetManifestResourceStream(EmbeddedResource);
        if (stream is null)
        {
            var r = new PolicyParseResult();
            r.Errors.Add(new PolicyIssue("Liste de référence intégrée absente."));
            return r;
        }
        using var reader = new StreamReader(stream);
        return DriverPolicy.Parse(reader.ReadToEnd(), PolicyLayer.Reference);
    }

    private async Task<(DriverPolicy? Policy, string Origin)> LoadReferenceAsync(List<string> messages, CancellationToken ct)
    {
        var embedded = LoadEmbeddedReference();
        if (!embedded.IsValid) Log.Warn($"Liste de référence intégrée invalide : {string.Join(" ; ", embedded.Errors)}");
        foreach (var w in embedded.Warnings) Log.Warn($"Liste de référence intégrée : {w}");
        var best = embedded.IsValid ? embedded.Policy : null;
        var origin = "intégrée";

        if (RefreshReferenceList) await RefreshReferenceAsync(ct).ConfigureAwait(false);

        // Copie rafraîchie depuis GitHub (fichier non signé) : retenue seulement si elle est au moins aussi récente que la liste
        // intégrée, et TOUJOURS complétée par les règles de la liste intégrée (elle peut ajouter des blocages, jamais en retirer).
        // Ses règles autres que « block » sont ignorées (voir DriverPolicy.RestrictReferenceLayer).
        try
        {
            if (File.Exists(ReferenceCacheFile))
            {
                var cached = DriverPolicy.Parse(await File.ReadAllTextAsync(ReferenceCacheFile, ct).ConfigureAwait(false), PolicyLayer.Reference);
                if (cached.IsValid && IsNewerOrSame(cached.Policy!, best))
                {
                    foreach (var w in cached.Warnings) Log.Warn($"Liste de référence en ligne : {w}");
                    best = DriverPolicy.UnionReference(cached.Policy!, embedded.IsValid ? embedded.Policy : null);
                    origin = $"{ReferenceListUrl} (copie du {File.GetLastWriteTime(ReferenceCacheFile):dd/MM/yyyy}, complétée par la liste intégrée)";
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Debug($"Copie locale de la liste de référence illisible : {ex.Message}");
        }
        return (best, origin);
    }

    private static bool IsNewerOrSame(DriverPolicy candidate, DriverPolicy? current)
    {
        if (current is null) return true;
        var a = DriverPolicy.ParseDate(candidate.UpdatedAt);
        var b = DriverPolicy.ParseDate(current.UpdatedAt);
        return a is not null && (b is null || a >= b);
    }

    private async Task RefreshReferenceAsync(CancellationToken ct)
    {
        var marker = ReferenceCacheFile + ".lastcheck";
        try
        {
            if (File.Exists(marker) && DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < ReferenceRefreshInterval) return;
            Directory.CreateDirectory(Path.GetDirectoryName(ReferenceCacheFile)!);
            File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            using var response = await _http.GetAsync(ReferenceListUrl, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Debug($"Liste de référence en ligne indisponible (HTTP {(int)response.StatusCode}) : la liste intégrée est utilisée.");
                return;
            }
            var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var parsed = DriverPolicy.Parse(json, PolicyLayer.Reference);
            if (!parsed.IsValid)
            {
                Log.Warn($"Liste de référence en ligne invalide, ignorée : {string.Join(" ; ", parsed.Errors.Take(3))}");
                return;
            }
            foreach (var w in parsed.Warnings) Log.Warn($"Liste de référence en ligne : {w}");
            File.WriteAllText(ReferenceCacheFile, json);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug($"Rafraîchissement de la liste de référence impossible : {ex.Message}");
        }
    }
}
