using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Matching;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Online;
using SpokaPilote.Core.Sources;

namespace SpokaPilote.Core.Install;

/// <summary>Résultat de la confirmation d'une entrée du Microsoft Update Catalog : fiche Microsoft, ou motif du refus.</summary>
public sealed record CatalogEntryConfirmation(CatalogEntryDetails? Details, string? Refusal);

/// <summary>
/// Paquets du Microsoft Update Catalog : le titre et la version d'une proposition viennent de la page de RECHERCHE, qui
/// peut avoir été relayée par la console (donc altérée par une console compromise) ; or ils décident du classement, de
/// « plus récent que l'installé », des règles de politique et de l'identité de diffusion (anneaux, disjoncteur). Avant
/// d'installer — que la recherche soit passée par la console ou non (défense en profondeur) :
/// <list type="number">
/// <item>la fiche de l'entrée est lue directement chez Microsoft : titre et version doivent être ceux annoncés ;</item>
/// <item>après extraction, chaque INF retenu doit porter la version annoncée (DriverVer) et un fournisseur (Provider ou
///   fabricant de ses modèles) en rapport avec l'éditeur annoncé.</item>
/// </list>
/// Une entrée légitime satisfait toujours ces deux conditions (la version du catalogue EST le DriverVer de l'INF signé) :
/// tout écart est refusé avec un message clair, et les valeurs ayant servi à la politique et aux anneaux sont ainsi
/// celles du paquet réellement installé.
/// </summary>
public static class CatalogPackageCheck
{
    /// <summary>
    /// Confirme l'entrée chez Microsoft. <paramref name="detailsRequired"/> (défaut : la console relaie les recherches) :
    /// fiche injoignable ou illisible → refus ; sinon elle est seulement journalisée (le contrôle de l'INF demeure).
    /// </summary>
    public static async Task<CatalogEntryConfirmation> ConfirmEntryAsync(string updateId, string title, string? version, CancellationToken ct,
        Func<string, CancellationToken, Task<CatalogEntryDetails?>>? fetch = null, bool? detailsRequired = null)
    {
        var details = await (fetch ?? MicrosoftCatalogClient.GetEntryDetailsAsync)(updateId, ct).ConfigureAwait(false);
        if (details is null)
        {
            if (detailsRequired ?? ConsoleCache.Current is { Catalogs: true })
                return new(null, "fiche de l'entrée injoignable chez Microsoft : titre et version annoncés par la recherche relayée invérifiables");
            Log.Info($"Catalogue Microsoft : fiche de « {title} » invérifiable ; contrôle de la version et de l'éditeur sur l'INF seulement.");
            return new(null, null);
        }
        return new(details, CompareWithDetails(title, version, details));
    }

    /// <summary>Motif du refus si la fiche Microsoft contredit le titre ou la version annoncés par la recherche, sinon null.</summary>
    public static string? CompareWithDetails(string title, string? version, CatalogEntryDetails details)
    {
        if (!SameText(title, details.Title))
            return $"titre annoncé par la recherche « {title} », fiche Microsoft « {details.Title} » (page de recherche altérée ?)";
        if (!SameVersion(version, details.Version))
            return $"version annoncée par la recherche {version ?? "(aucune)"}, fiche Microsoft {details.Version ?? "(aucune)"} (page de recherche altérée ?)";
        return null;
    }

    /// <summary>
    /// INF du paquet extrait qui portent la version annoncée et un fournisseur en rapport avec l'éditeur annoncé ; motif
    /// du refus si aucun.
    /// </summary>
    public static (List<string> Infs, string? Reason) CheckInfs(IEnumerable<string> infs, string title, string? version, CatalogEntryDetails? details)
    {
        var kept = new List<string>();
        string? reason = null;
        var announcedVendors = Vendors(new[] { title, details?.Provider, details?.Manufacturer });
        foreach (var path in infs)
        {
            InfInfo inf;
            try { inf = InfParser.Parse(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                reason = $"{Path.GetFileName(path)} illisible ({ex.Message})";
                continue;
            }
            if (version is not null && DriverVersion.Compare(inf.DriverVersion, version) != 0)
            {
                reason = $"{Path.GetFileName(path)} : version de l'INF {inf.DriverVersion ?? "(aucune)"} différente de la version annoncée {version}";
                continue;
            }
            var infVendors = Vendors(inf.Models.Select(m => m.Manufacturer).Prepend(inf.Provider));
            if (!infVendors.Overlaps(announcedVendors))
            {
                reason = $"{Path.GetFileName(path)} : fournisseur de l'INF « {inf.Provider ?? "(aucun)"} » sans rapport avec l'éditeur annoncé « {details?.Provider ?? title} »";
                continue;
            }
            kept.Add(path);
        }
        if (kept.Count == 0 && reason is not null) Log.Warn($"⚠ Paquet du catalogue Microsoft refusé (« {title} » {version}) : {reason}.");
        return (kept, kept.Count > 0 ? null : reason ?? "aucun INF");
    }

    private static bool SameText(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool SameVersion(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b);
        return DriverVersion.Compare(a, b) is 0 || string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Même éditeur écrit autrement dans les titres du catalogue et les INF.</summary>
    private static readonly Dictionary<string, string> VendorAliases = new(StringComparer.Ordinal)
    {
        ["amd"] = "advanced",
        ["ati"] = "advanced",
        ["hewlett"] = "hp",
    };

    /// <summary>Premiers mots (lettres et chiffres, en minuscules) : « Realtek Semiconductor Corp. » → « realtek ».</summary>
    private static HashSet<string> Vendors(IEnumerable<string?> names)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var word = new string(name.SkipWhile(c => !char.IsLetterOrDigit(c)).TakeWhile(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (word.Length < 2) continue;
            set.Add(VendorAliases.GetValueOrDefault(word, word));
        }
        return set;
    }
}
