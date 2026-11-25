using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Matching;
using SpokaPilote.Core.Models;

namespace SpokaPilote.Core.Install;

/// <summary>
/// Garde-fou essentiel, partagé par l'installation et la préparation des packs hors ligne : un INF n'est retenu
/// que s'il déclare réellement un ID d'un périphérique ciblé (pour l'architecture et la version de Windows visées)
/// et si son catalogue (.cat) porte une signature valide.
/// </summary>
public static class PackageVerifier
{
    public sealed record Result(List<string> Infs, string Reason, List<DeviceMatch> Matches);

    public static Result SelectVerifiedInfs(string folder, string? preferredInf, IReadOnlyList<DeviceInfo> targets, string architecture, Version osVersion)
    {
        var infFiles = preferredInf is not null && File.Exists(preferredInf)
            ? new List<string> { preferredInf }
            : Directory.Exists(folder) ? Directory.GetFiles(folder, "*.inf", SearchOption.AllDirectories).ToList() : new List<string>();
        if (infFiles.Count == 0) return new Result(new(), "aucun fichier INF dans le paquet", new());

        var selected = new List<string>();
        var allMatches = new List<DeviceMatch>();
        var reason = "aucun INF ne correspond au matériel (ou pas pour cette architecture / version de Windows)";
        foreach (var infPath in infFiles)
        {
            InfInfo inf;
            try { inf = InfParser.Parse(infPath); }
            catch (Exception ex) { reason = $"INF illisible : {ex.Message}"; continue; }

            var matches = targets.Select(t => HardwareIdMatcher.BestMatch(t, inf, architecture, osVersion)).OfType<DeviceMatch>().ToList();
            if (targets.Count > 0 && matches.Count == 0) continue;
            if (matches.Count > 0 && matches.All(m => m.Rank >= HardwareIdMatcher.CompatibleIdOffset && HardwareIdMatcher.IsGenericCompatibleId(m.MatchedId)))
            {
                reason = "correspondance uniquement générique (refusée)";
                continue;
            }

            var (signed, signatureReason) = VerifyInfSignature(infPath, inf, architecture);
            if (!signed)
            {
                reason = signatureReason;
                continue;
            }
            Log.Info($"{Path.GetFileName(infPath)} : {signatureReason} ; correspondance {string.Join(", ", matches.Select(m => m.MatchedId).Distinct())}");
            selected.Add(infPath);
            allMatches.AddRange(matches);
        }
        return new Result(selected, reason, allMatches);
    }

    /// <summary>
    /// L'INF déclare-t-il un catalogue (.cat) présent à côté de lui et dont la signature est valide ?
    /// Renvoie le motif du refus, ou la description de la signature si elle est valide.
    /// </summary>
    public static (bool Signed, string Reason) VerifyInfSignature(string infPath, InfInfo inf, string? architecture = null)
    {
        // Le catalogue propre à l'architecture (CatalogFile.NTamd64...) prime sur le catalogue non décoré.
        var catalog = architecture is null ? inf.CatalogFile : inf.CatalogFileFor(architecture);
        if (string.IsNullOrEmpty(catalog))
            return (false, $"{Path.GetFileName(infPath)} n'est pas signé (pas de fichier .cat)");
        var catPath = Path.Combine(Path.GetDirectoryName(infPath)!, catalog);
        if (!File.Exists(catPath))
            return (false, $"catalogue {catalog} manquant");
        var sig = SignatureVerifier.Verify(catPath);
        return sig.IsValid ? (true, sig.ToString()) : (false, $"{catalog} : {sig.Detail}");
    }
}
