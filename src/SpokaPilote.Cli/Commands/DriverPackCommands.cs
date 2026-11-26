using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Inventory;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Repository;

namespace SpokaPilote.Cli;

/// <summary>
/// spokapilote driverpack list|download : packs complets de pilotes officiels Dell / HP / Lenovo (déploiement SCCM/MDT)
/// et MSI « pilotes et firmwares » Microsoft Surface, téléchargés dans le dépôt pour installer hors ligne tous les postes
/// d'un même modèle.
/// </summary>
internal static partial class Program
{
    private static async Task<int> DriverPackAsync(Args a, CancellationToken ct)
    {
        var sub = a.Positional(0) ?? "list";
        if (sub is not ("list" or "download")) return Help();
        var settings = LoadSettings(a);
        Banner();

        DriverPackQuery query;
        try
        {
            query = BuildPackQuery(a, forDownload: sub == "download");
        }
        catch (ArgumentException ex)
        {
            WriteLine(ex.Message, ConsoleColor.Yellow);
            return ExitUsage;
        }
        if (query is { SystemId: null, Text: null })
        {
            WriteLine("Ce poste n'est pas un Dell, HP, Lenovo ou Surface identifiable : précisez le modèle avec --model \"Latitude 7420\".", ConsoleColor.Yellow);
            return ExitUsage;
        }

        WriteLine($"Recherche des packs de pilotes officiels ({query.Describe()}" +
                  $"{(query.Vendor is { } v ? ", " + OemDriverPacks.VendorName(v) : "")}" +
                  $"{(query.Os is null ? "" : ", " + query.Os)}{(query.Architecture is null ? "" : ", " + query.Architecture)})...", ConsoleColor.Gray);
        var matches = await OemDriverPacks.SearchAsync(query, settings, new Progress<string>(m => WriteLine($"› {m}", ConsoleColor.DarkGray)), ct);
        if (matches.Count == 0)
        {
            WriteLine("Aucun pack officiel trouvé pour ces critères.", ConsoleColor.Yellow);
            if (query.Os is not null) WriteLine("  Essayez sans --os, ou avec l'autre version de Windows (certains modèles n'ont qu'un pack Windows 10).", ConsoleColor.DarkYellow);
            return ExitError;
        }

        if (sub == "list")
        {
            PrintPacks(matches);
            return ExitOk;
        }
        return await DownloadPackAsync(a, settings, matches, ct);
    }

    private static DriverPackQuery BuildPackQuery(Args a, bool forDownload)
    {
        var model = a.Value("model");
        DriverPackQuery query;
        SystemInfo? system = null;
        if (string.IsNullOrWhiteSpace(model))
        {
            system = SystemInfoProvider.Collect();
            WriteLine($"Poste courant : {system.Manufacturer} {system.DisplayModel}" +
                      $"{(system.SystemSku is null ? "" : $" · SKU {system.SystemSku}")}{(system.BaseBoardProduct is null ? "" : $" · carte {system.BaseBoardProduct}")}", ConsoleColor.White);
            if (system.Vendor == OemVendor.Microsoft && SurfaceDriverPacks.Catalog.Models.Count > 0 &&
                !SurfaceDriverPacks.Catalog.Models.Any(m => m.SystemSkus.Contains(system.SystemSku ?? "", StringComparer.OrdinalIgnoreCase)))
                WriteLine($"  SKU absent de la table Surface (version {SurfaceDriverPacks.Catalog.Updated}) : recherche par nom de modèle « {system.Model} ».", ConsoleColor.DarkYellow);
            query = DriverPackQuery.ForSystem(system);
        }
        else
        {
            query = new DriverPackQuery { Text = model.Trim() };
        }

        query.Vendor = a.Value("vendor")?.Trim().ToLowerInvariant() switch
        {
            null => query.Vendor,
            "dell" => OemVendor.Dell,
            "hp" => OemVendor.HP,
            "lenovo" => OemVendor.Lenovo,
            "surface" or "microsoft" => OemVendor.Microsoft,
            var other => throw new ArgumentException($"Marque inconnue : {other} (valeurs : dell, hp, lenovo, surface)"),
        };
        query.Os = a.Value("os")?.Trim().ToLowerInvariant() switch
        {
            null => forDownload ? (system ?? SystemInfoProvider.Collect()).IsWindows11 ? "win11" : "win10" : null,
            "win11" or "11" or "windows11" => "win11",
            "win10" or "10" or "windows10" => "win10",
            var other => throw new ArgumentException($"Windows inconnu : {other} (valeurs : win11, win10)"),
        };
        query.Architecture = a.Value("arch")?.Trim().ToLowerInvariant() switch
        {
            null => forDownload || system is not null ? (system ?? SystemInfoProvider.Collect()).Architecture == "arm64" ? "arm64" : "amd64" : null,
            "x64" or "amd64" => "amd64",
            "arm64" => "arm64",
            var other => throw new ArgumentException($"Architecture inconnue : {other} (valeurs : x64, arm64)"),
        };
        query.AllVersions = a.Flag("all");
        return query;
    }

    private static void PrintPacks(IReadOnlyList<DriverPackMatch> matches)
    {
        WriteLine($"\n{matches.Count} pack(s) officiel(s) :", ConsoleColor.White);
        foreach (var vendorGroup in matches.GroupBy(m => m.Pack.Vendor))
        {
            WriteLine($"  {OemDriverPacks.VendorName(vendorGroup.Key)}", ConsoleColor.Cyan);
            foreach (var m in vendorGroup)
            {
                var p = m.Pack;
                var releases = p.OsReleases.Count > 0 ? $" ({string.Join(", ", p.OsReleases.OrderBy(OemDriverPacks.ReleaseRank))})" : "";
                WriteLine($"    • {string.Join(" / ", m.MatchedModels.Take(3))}{(m.MatchedModels.Count > 3 ? $" (+{m.MatchedModels.Count - 3})" : "")}", ConsoleColor.White);
                var id = p.Vendor == OemVendor.Microsoft ? p.FileName : p.Id; // Surface : le nom du MSI porte produit, build et version
                var build = p.OsBuild is { } b ? $" build {b}" : "";
                WriteLine($"        {p.OsLabel}{releases}{build} · {id}{(p.Version is null || p.Vendor == OemVendor.Microsoft ? "" : " version " + p.Version)} du {p.Date:dd/MM/yyyy} · {p.SizeLabel} · {p.Format}", ConsoleColor.Gray);
                var ids = p.Systems.Where(s => m.MatchedModels.Contains(s.Name, StringComparer.OrdinalIgnoreCase)).SelectMany(s => s.Ids).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (ids.Count > 0) WriteLine($"        identifiants : {string.Join(", ", ids)}", ConsoleColor.DarkGray);
                WriteLine($"        {p.Url}", ConsoleColor.DarkGray);
                if (p.Vendor == OemVendor.Microsoft && p.InfoUrl is not null) WriteLine($"        page officielle : {p.InfoUrl}", ConsoleColor.DarkGray);
            }
        }
        if (matches.Any(m => m.Pack.Vendor == OemVendor.Microsoft))
            WriteLine("\nSurface : le MSI contient pilotes ET firmwares (UEFI, SAM, EC...) ; Microsoft ne publie pas d'empreinte : " +
                      "la signature « Microsoft Corporation » et la taille sont vérifiées.", ConsoleColor.DarkGray);
        WriteLine("\nTélécharger dans le dépôt : spokapilote driverpack download <dépôt> --model \"<modèle>\" [--os win11|win10]", ConsoleColor.Gray);
    }

    private static async Task<int> DownloadPackAsync(Args a, Settings settings, List<DriverPackMatch> matches, CancellationToken ct)
    {
        var repo = a.Positional(1) ?? settings.RepositoryPaths.FirstOrDefault();
        if (repo is null)
        {
            WriteLine("Usage : spokapilote driverpack download <dépôt> [--model \"Latitude 7420\"] [--os win11|win10] [--arch x64|arm64] [--yes]", ConsoleColor.Yellow);
            return ExitUsage;
        }

        if (matches.Count > 1 && a.Value("model") is { } typed)
        {
            // Nom saisi en entier (« HP Engage One Pro AIO System ») : on retient ce modèle exact.
            static string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            var exact = matches.Where(m => m.MatchedModels.Any(n => Norm(n) == Norm(typed))).ToList();
            if (exact.Count == 1) matches = exact;
        }
        if (matches.Count > 1)
        {
            WriteLine($"{matches.Count} packs correspondent : précisez le modèle (nom complet ou identifiant), la marque (--vendor) ou Windows (--os).", ConsoleColor.Yellow);
            PrintPacks(matches);
            return ExitUsage;
        }

        var match = matches[0];
        var pack = match.Pack;
        var folder = OemDriverPackDownloader.TargetFolder(repo, match);
        var label = match.ModelLabel.StartsWith(pack.VendorName + " ", StringComparison.OrdinalIgnoreCase) ? match.ModelLabel : $"{pack.VendorName} {match.ModelLabel}";
        WriteLine($"\nPack : {label} — {pack.OsLabel} — {pack.Id}{(pack.Version is null ? "" : " " + pack.Version)} du {pack.Date:dd/MM/yyyy}", ConsoleColor.White);
        WriteLine($"Fichier : {pack.FileName} ({pack.SizeLabel}) depuis {new Uri(pack.Url).Host}", ConsoleColor.Gray);
        WriteLine($"Destination : {folder}", ConsoleColor.Gray);
        if (!a.Flag("yes") && !Confirm("Télécharger et extraire ce pack dans le dépôt ?")) return ExitOk;

        var result = await OemDriverPackDownloader.DownloadAsync(match, repo, settings,
            new DriverPackDownloadOptions { Force = a.Flag("force"), KeepDownload = a.Flag("keep-download") },
            new Progress<string>(m => WriteLine($"› {m}", ConsoleColor.DarkGray)), ct);

        WriteLine(result.Message, result.Success ? ConsoleColor.Green : ConsoleColor.Red);
        if (result.Rejected is { Count: > 0 } rejected)
        {
            foreach (var r in rejected.Take(10)) WriteLine($"  ⚠ {r}", ConsoleColor.Yellow);
            if (rejected.Count > 10) WriteLine($"  ... et {rejected.Count - 10} autre(s) (voir {OemDriverPackDownloader.ManifestName})", ConsoleColor.Yellow);
        }
        if (result.Success)
        {
            WriteLine($"\nSur chaque {match.ModelLabel} : spokapilote install --repo \"{repo}\" --yes", ConsoleColor.Cyan);
            WriteLine($"Ou déclarez le dépôt une fois pour toutes : spokapilote config add-repo \"{repo}\"", ConsoleColor.Gray);
        }
        return result.Success ? ExitOk : ExitError;
    }
}
