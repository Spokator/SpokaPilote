using System.Globalization;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SpokaPilote.Core.Infrastructure;

/// <summary>settings.json ou jeton mis en quarantaine par une reprise complète, avec son propriétaire d'origine.</summary>
public sealed record QuarantinedConfig(string Name, string RelativePath, string? OwnerSid, DateTime QuarantinedAt);

/// <summary>Configuration en attente dans la quarantaine (marqueur <see cref="ConfigQuarantine.MarkerFileName"/>).</summary>
public sealed class QuarantineEntry
{
    /// <summary>settings.json ou server-token.dat.</summary>
    public string Name { get; set; } = "";
    /// <summary>Chemin relatif au dossier de données (« Quarantaine\20261128-100000\settings.json »).</summary>
    public string Path { get; set; } = "";
    public string? OwnerSid { get; set; }
    /// <summary>« PC\bob (S-1-5-21-…) » : propriétaire d'origine lisible.</summary>
    public string? Owner { get; set; }
    public DateTime QuarantinedAt { get; set; }
    /// <summary>Mis en quarantaine par la version 4.0.1, qui ne notait pas le propriétaire d'origine (repris avant le déplacement).</summary>
    public bool Legacy { get; set; }
    /// <summary>Réglages qui étendent la confiance (éditeurs, domaines, dépôts, politique...) : jamais restaurés sans un administrateur.</summary>
    public List<string> TrustExtendingSettings { get; set; } = new();

    private bool IsSettings => Name.Equals(ConfigQuarantine.SettingsFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Message affiché par la CLI (chaque commande) et l'interface (bandeau de l'accueil et des paramètres).</summary>
    [JsonIgnore]
    public string Notice
    {
        get
        {
            var date = QuarantinedAt.ToString("dd/MM/yyyy à HH:mm", CultureInfo.GetCultureInfo("fr-FR"));
            var what = IsSettings ? $"Configuration ({Name}) mise" : $"Jeton de la console ({Name}) mis";
            var why = Legacy
                ? "par la version 4.0.1 (propriétaire d'origine inconnu" +
                  (TrustExtendingSettings.Count > 0 ? $" ; réglages qui étendent la confiance : {string.Join(", ", TrustExtendingSettings)}" : "") + ")"
                : $"(fichier créé par un compte non administrateur : {Owner ?? OwnerSid ?? "propriétaire illisible"})";
            return $"{what} en quarantaine le {date} {why} : vérifiez-{(IsSettings ? "la" : "le")} puis « spokapilote config restore ».";
        }
    }
}

/// <summary>Contenu d'un fichier mis en quarantaine, lu une seule fois : ce qui est affiché est exactement ce qui est restauré.</summary>
public sealed class QuarantineCandidate
{
    public required string FullPath { get; init; }
    public required string Name { get; init; }
    public QuarantineEntry? Entry { get; init; }
    /// <summary>Configuration à afficher (jeton masqué), null pour un fichier de jeton.</summary>
    public JsonObject? Display { get; init; }
    public bool ContainsToken { get; init; }
    public IReadOnlyList<string> TrustExtendingSettings { get; init; } = [];
    internal Settings? Settings { get; init; }
    internal string? Token { get; init; }
    public bool IsSettings => Settings is not null;
}

/// <summary>
/// Configuration mise en quarantaine : jamais perdue en silence. Un settings.json (ou server-token.dat) que la protection
/// du dossier de données (<see cref="DataRootProtector"/>) a mis en quarantaine parce que son propriétaire n'est pas un
/// administrateur reconnu est :
/// <list type="bullet">
/// <item>signalé par une ERREUR du journal et un marqueur (<see cref="MarkerFileName"/>, sans secret, lisible par tous)
/// que la CLI affiche au début de chaque commande et l'interface dans un bandeau (accueil, paramètres) ;</item>
/// <item>restauré par « spokapilote config restore » (administrateur) : contenu affiché (jeton masqué), confirmation
/// (--yes pour un script), jeton en clair déplacé vers server-token.dat, marqueur effacé ;</item>
/// <item>restauré automatiquement au démarrage élevé si aucune configuration n'existe et que son propriétaire d'origine
/// est désormais reconnu comme administrateur (ajouté au groupe depuis, domaine de nouveau joignable).</item>
/// </list>
/// Postes passés par la version 4.0.1 : elle mettait en quarantaine les fichiers des comptes administrateurs (PC\Pierre)
/// et ramenait leur propriétaire aux Administrateurs AVANT le déplacement : le propriétaire d'origine est perdu. Le
/// settings.json le plus récent de la quarantaine est alors restauré automatiquement (si aucun settings.json n'existe)
/// seulement s'il ne contient aucun réglage qui étend la confiance (<see cref="TrustExtendingKeys"/> : éditeurs ou
/// domaines supplémentaires, dépôts, source de politique, catalogue d'imprimantes) ; sinon il est signalé comme ci-dessus.
/// La quarantaine est purgée après <see cref="Retention"/> (sauf un élément encore en attente).
/// </summary>
public static class ConfigQuarantine
{
    public const string MarkerFileName = "quarantine.json";
    public const string SettingsFileName = "settings.json";
    /// <summary>Fichier restauré, conservé dans la quarantaine jusqu'à la purge.</summary>
    internal const string RestoredSuffix = ".restaure";
    /// <summary>Fichier écarté par un administrateur (« config restore --discard »), conservé jusqu'à la purge.</summary>
    internal const string DiscardedSuffix = ".ecarte";
    /// <summary>settings.json remplacé par une restauration explicite, gardé à côté.</summary>
    public const string PreviousSettingsSuffix = ".avant-restauration";
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private const string StampFormat = "yyyyMMdd-HHmmss";

    /// <summary>Réglages qui étendent ce à quoi les exécutions SYSTEM font confiance (clé JSON, nom affiché).</summary>
    internal static readonly (string Key, string Display)[] TrustExtendingKeys =
    [
        ("extraTrustedPublishers", nameof(Settings.ExtraTrustedPublishers)),
        ("extraTrustedHosts", nameof(Settings.ExtraTrustedHosts)),
        ("repositoryPaths", nameof(Settings.RepositoryPaths)),
        ("policySource", nameof(Settings.PolicySource)),
        ("printerCatalogUrl", nameof(Settings.PrinterCatalogUrl)),
    ];

    private static readonly JsonSerializerOptions MarkerJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions LenientJson = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static string MarkerFile => Path.Combine(AppPaths.Root, MarkerFileName);
    private static string QuarantineRoot => Path.Combine(AppPaths.Root, DataRootProtector.QuarantineFolderName);

    internal static bool IsConfigFile(string name) =>
        name.Equals(SettingsFileName, StringComparison.OrdinalIgnoreCase) || name.Equals(SecretStore.TokenFileName, StringComparison.OrdinalIgnoreCase);

    private sealed class Marker
    {
        public int Version { get; set; } = 1;
        public List<QuarantineEntry> Entries { get; set; } = new();
    }

    // ------------------------------------------------------------------ lecture du marqueur (tout processus)

    /// <summary>Configurations en attente dans la quarantaine (vide s'il n'y en a pas, ou si le marqueur est illisible).</summary>
    public static IReadOnlyList<QuarantineEntry> Pending()
    {
        try
        {
            return ReadMarker()?.Entries ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Messages à afficher (un par configuration en attente).</summary>
    public static IReadOnlyList<string> Notices() => Pending().Select(e => e.Notice).ToList();

    private static Marker? ReadMarker()
    {
        if (AppPaths.Layout.Mode == DataLocationMode.Portable) return null;
        var path = MarkerFile;
        if (!File.Exists(path)) return null;
        // Exécution élevée : un marqueur qui n'a pas été écrit par un administrateur n'est jamais suivi.
        if (!DataProtection.IsTrustedForElevatedUse(path)) return null;
        return JsonSerializer.Deserialize<Marker>(File.ReadAllText(path), MarkerJson);
    }

    private static void WriteMarker(Marker marker)
    {
        if (marker.Entries.Count == 0)
        {
            if (File.Exists(MarkerFile)) File.Delete(MarkerFile);
            return;
        }
        DataProtection.WriteTextAtomically(MarkerFile, JsonSerializer.Serialize(marker, MarkerJson));
    }

    // ------------------------------------------------------------------ démarrage élevé

    /// <summary>
    /// Après la protection du dossier de données (démarrage élevé) : configurations mises en quarantaine signalées
    /// (ERREUR + marqueur), quarantaine de la version 4.0.1 reconnue, restauration automatique quand elle est sûre.
    /// Ne lève jamais d'exception.
    /// </summary>
    internal static void AfterProtection(ProtectionReport report, DateTime now)
    {
        try
        {
            var marker = ReadMarker() ?? new Marker();
            var changed = false;
            var announced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in report.QuarantinedConfig)
            {
                var owner = item.OwnerSid is { } sid ? new SecurityIdentifier(sid) : null;
                var entry = new QuarantineEntry
                {
                    Name = item.Name, Path = item.RelativePath, OwnerSid = item.OwnerSid,
                    Owner = AdministratorOwners.Describe(owner), QuarantinedAt = item.QuarantinedAt,
                };
                marker.Entries.RemoveAll(e => SamePath(e.Path, entry.Path));
                marker.Entries.Add(entry);
                announced.Add(entry.Path);
                changed = true;
                Log.Error($"{entry.Notice} Fichier : {Path.Combine(AppPaths.Root, entry.Path)}.");
            }

            // Quarantaine de la version 4.0.1 : propriétaire d'origine perdu, configuration du poste absente.
            foreach (var name in new[] { SettingsFileName, SecretStore.TokenFileName })
            {
                if (CurrentExists(name) || marker.Entries.Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                if (NewestQuarantined(name) is not { } legacy) continue;
                var entry = new QuarantineEntry { Name = name, Path = Path.GetRelativePath(AppPaths.Root, legacy.Path), QuarantinedAt = legacy.Stamp, Legacy = true };
                if (name == SettingsFileName)
                {
                    try { entry.TrustExtendingSettings = TrustExtending(Encoding.UTF8.GetString(File.ReadAllBytes(legacy.Path))).ToList(); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    {
                        Log.Debug($"{legacy.Path} illisible : {ex.Message}");
                    }
                }
                marker.Entries.Add(entry);
                changed = true;
            }

            // Restauration automatique : jamais par-dessus une configuration présente.
            foreach (var entry in marker.Entries.OrderByDescending(e => e.QuarantinedAt).ToList())
            {
                if (CurrentExists(entry.Name) || !AutoRestorable(entry)) continue;
                try
                {
                    var full = ResolveInQuarantine(entry.Path) ?? throw new IOException("chemin hors de la quarantaine");
                    var candidate = Inspect(full, entry, requireTrustedChain: true);
                    if (entry.Legacy && candidate.TrustExtendingSettings.Count > 0) continue; // contenu relu : même règle
                    Apply(candidate, marker);
                    changed = true;
                    announced.Add(entry.Path);
                    var token = candidate.ContainsToken ? $" ; jeton enregistré chiffré dans {SecretStore.TokenFileName}" : "";
                    Log.Warn(entry.Legacy
                        ? $"{entry.Name} mis en quarantaine par la version 4.0.1 le {entry.QuarantinedAt:dd/MM/yyyy HH:mm} restauré automatiquement " +
                          $"(aucun réglage qui étend la confiance, aucune configuration présente){token}."
                        : $"{entry.Name} mis en quarantaine le {entry.QuarantinedAt:dd/MM/yyyy HH:mm} restauré automatiquement : son propriétaire " +
                          $"({entry.Owner}) est désormais reconnu comme administrateur{token}.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    Log.Error($"Restauration automatique de {entry.Path} impossible : {ex.Message}");
                }
            }

            foreach (var entry in marker.Entries.Where(e => !announced.Contains(e.Path))) Log.Error(entry.Notice);
            if (changed) WriteMarker(marker);
        }
        catch (Exception ex)
        {
            Log.Error($"Contrôle de la configuration mise en quarantaine impossible : {ex.Message}");
        }
    }

    /// <summary>Restauration automatique permise : propriétaire d'origine désormais administrateur, ou (4.0.1) contenu sans réglage qui étend la confiance.</summary>
    internal static bool AutoRestorable(QuarantineEntry entry)
    {
        if (entry.Legacy) return entry.TrustExtendingSettings.Count == 0;
        if (entry.OwnerSid is null) return false;
        try { return DataProtection.IsTrustedOwner(new SecurityIdentifier(entry.OwnerSid)); }
        catch (ArgumentException) { return false; }
    }

    private static bool CurrentExists(string name) =>
        name.Equals(SettingsFileName, StringComparison.OrdinalIgnoreCase) ? File.Exists(AppPaths.SettingsFile) : File.Exists(SecretStore.TokenFile);

    /// <summary>Le plus récent « Quarantaine\&lt;date&gt;\&lt;nom&gt; » (fichier de premier niveau du dossier de données).</summary>
    private static (string Path, DateTime Stamp)? NewestQuarantined(string name)
    {
        if (!Directory.Exists(QuarantineRoot)) return null;
        (string Path, DateTime Stamp)? best = null;
        foreach (var dir in Directory.EnumerateDirectories(QuarantineRoot))
        {
            if (!DateTime.TryParseExact(Path.GetFileName(dir), StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp)) continue;
            var file = Path.Combine(dir, name);
            if (File.Exists(file) && (best is null || stamp > best.Value.Stamp)) best = (file, stamp);
        }
        return best;
    }

    private static string Full(string relative) => Path.GetFullPath(Path.Combine(AppPaths.Root, relative));

    private static bool SamePath(string a, string b) => string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Chemin complet d'une entrée du marqueur, null s'il sort du dossier Quarantaine.</summary>
    private static string? ResolveInQuarantine(string relative)
    {
        var full = Full(relative);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(QuarantineRoot)) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>Réglages non vides qui étendent la confiance dans ce settings.json (clés de toute casse).</summary>
    internal static IEnumerable<string> TrustExtending(string json)
    {
        if (JsonNode.Parse(json.TrimStart('﻿'), documentOptions: LenientJson) is not JsonObject raw) return [];
        var found = new List<string>();
        foreach (var (key, display) in TrustExtendingKeys)
        {
            var value = raw.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
            var set = value switch
            {
                null => false,
                JsonArray array => array.Count > 0,
                JsonValue v when v.TryGetValue<string>(out var text) => !string.IsNullOrWhiteSpace(text),
                _ => true,
            };
            if (set) found.Add(display);
        }
        return found;
    }

    // ------------------------------------------------------------------ restauration (« config restore », interface)

    /// <summary>
    /// Fichier à restaurer : <paramref name="path"/> (fichier, ou dossier de la quarantaine qui contient settings.json ;
    /// relatif au dossier de données), sinon la configuration en attente la plus récente (settings.json d'abord), sinon le
    /// settings.json le plus récent de la quarantaine. FileNotFoundException s'il n'y en a pas.
    /// </summary>
    public static QuarantineCandidate Prepare(string? path = null)
    {
        var pending = Pending();
        string full;
        QuarantineEntry? entry;
        if (path is not null)
        {
            full = Path.GetFullPath(File.Exists(path) || Directory.Exists(path) ? path : Path.Combine(AppPaths.Root, path));
            if (Directory.Exists(full)) full = Path.Combine(full, SettingsFileName);
            entry = pending.FirstOrDefault(e => string.Equals(Full(e.Path), full, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            entry = pending.OrderByDescending(e => e.Name.Equals(SettingsFileName, StringComparison.OrdinalIgnoreCase)).ThenByDescending(e => e.QuarantinedAt).FirstOrDefault();
            full = entry is not null
                ? ResolveInQuarantine(entry.Path) ?? throw new IOException($"{entry.Path} n'est pas dans la quarantaine.")
                : NewestQuarantined(SettingsFileName)?.Path ?? throw new FileNotFoundException("Aucune configuration dans la quarantaine.");
        }
        if (!File.Exists(full)) throw new FileNotFoundException($"Fichier introuvable : {full}");
        return Inspect(full, entry, requireTrustedChain: false);
    }

    /// <summary>Lit le fichier une fois (jamais un lien) et prépare ce qui sera restauré.</summary>
    internal static QuarantineCandidate Inspect(string full, QuarantineEntry? entry, bool requireTrustedChain)
    {
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new IOException($"{full} est un lien : refusé.");
        if (requireTrustedChain && !DataProtection.IsWrittenByAdministrators(full))
            throw new IOException($"{full} n'est pas sûr (lien ou propriétaire inattendu sur le chemin).");
        var bytes = File.ReadAllBytes(full);
        var name = Path.GetFileName(full);
        if (name.Equals(SecretStore.TokenFileName, StringComparison.OrdinalIgnoreCase))
        {
            var token = SecretStore.Decode(bytes) ?? throw new InvalidOperationException($"{full} ne contient pas de jeton lisible.");
            return new QuarantineCandidate { FullPath = full, Name = name, Entry = entry, ContainsToken = true, Token = token };
        }
        var json = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
        var raw = JsonNode.Parse(json, documentOptions: LenientJson) as JsonObject ?? throw new JsonException($"{full} n'est pas une configuration.");
        var hasToken = raw.Any(kv => kv.Key.Equals("serverToken", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value?.ToString()));
        var settings = Settings.FromJson(json);
        var display = settings.ToDisplayJson();
        display[nameof(Settings.ServerToken)] = hasToken
            ? $"(défini, en clair dans ce fichier : enregistré chiffré dans {SecretStore.TokenFileName} à la restauration)"
            : string.IsNullOrWhiteSpace(settings.ServerToken) ? null : "(absent de ce fichier : le jeton actuel est conservé)";
        return new QuarantineCandidate
        {
            FullPath = full, Name = SettingsFileName, Entry = entry, Display = display, ContainsToken = hasToken,
            TrustExtendingSettings = TrustExtending(json).ToList(), Settings = settings,
        };
    }

    /// <summary>
    /// Restaure la configuration préparée par <see cref="Prepare"/> (à appeler depuis un processus élevé, version
    /// installée) : l'ancien settings.json éventuel est gardé à côté (<see cref="PreviousSettingsSuffix"/>), le jeton en
    /// clair va dans server-token.dat, le fichier restauré reste dans la quarantaine (renommé) et le marqueur est effacé.
    /// </summary>
    public static void Restore(QuarantineCandidate candidate)
    {
        var marker = ReadMarker() ?? new Marker();
        if (candidate.IsSettings && File.Exists(AppPaths.SettingsFile))
            DataProtection.WriteFileAtomically(AppPaths.SettingsFile + PreviousSettingsSuffix, File.ReadAllBytes(AppPaths.SettingsFile));
        Apply(candidate, marker);
        WriteMarker(marker);
        Log.Info($"Configuration restaurée depuis {candidate.FullPath}" + (candidate.ContainsToken ? $" ; jeton enregistré chiffré dans {SecretStore.TokenFile}." : "."));
    }

    private static void Apply(QuarantineCandidate candidate, Marker marker)
    {
        if (candidate.Settings is { } settings)
        {
            if (!settings.Save()) throw new IOException("configuration non enregistrée (voir le journal)");
        }
        else
        {
            SecretStore.WriteToken(candidate.Token);
        }
        MarkHandled(candidate.FullPath, RestoredSuffix);
        marker.Entries.RemoveAll(e => string.Equals(Full(e.Path), candidate.FullPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// « config restore --discard » : configurations en attente écartées (fichier jugé illégitime), conservées dans la
    /// quarantaine jusqu'à la purge ; le bandeau disparaît. Renvoie le nombre d'éléments écartés.
    /// </summary>
    public static int DiscardPending()
    {
        var marker = ReadMarker();
        if (marker is null || marker.Entries.Count == 0) return 0;
        var count = marker.Entries.Count;
        foreach (var entry in marker.Entries)
            if (ResolveInQuarantine(entry.Path) is { } full) MarkHandled(full, DiscardedSuffix);
        marker.Entries.Clear();
        WriteMarker(marker);
        Log.Warn($"{count} configuration(s) mise(s) en quarantaine écartée(s) par un administrateur (conservée(s) jusqu'à la purge de la quarantaine).");
        return count;
    }

    /// <summary>Fichier traité (restauré, écarté) renommé dans la quarantaine : plus jamais proposé, purgé avec son dossier.</summary>
    private static void MarkHandled(string full, string suffix)
    {
        if (ResolveInQuarantine(full) is null || !File.Exists(full)) return;
        try
        {
            var target = full + suffix;
            for (var i = 2; File.Exists(target); i++) target = $"{full}{suffix}-{i}";
            File.Move(full, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"{full} non renommé : {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ purge

    /// <summary>Dossiers de la quarantaine de plus de <see cref="Retention"/> supprimés (sauf s'ils contiennent un élément en attente).</summary>
    internal static int Prune(DateTime now)
    {
        try
        {
            if (!Directory.Exists(QuarantineRoot)) return 0;
            var pending = Pending().Select(e => Full(e.Path)).ToList();
            var removed = 0;
            foreach (var dir in Directory.EnumerateDirectories(QuarantineRoot).ToList())
            {
                if (!DateTime.TryParseExact(Path.GetFileName(dir), StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp)) continue;
                if (now - stamp < Retention) continue;
                var prefix = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
                if (pending.Any(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) continue;
                if (DataProtection.DeleteTree(dir))
                {
                    removed++;
                    Log.Info($"Quarantaine du {stamp:dd/MM/yyyy} supprimée (plus de {Retention.Days} jours) : {dir}");
                }
            }
            return removed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"Purge de la quarantaine impossible : {ex.Message}");
            return 0;
        }
    }
}
