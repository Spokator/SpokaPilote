using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Inventory;

namespace SpokaPilote.Core.Repository;

/// <summary>
/// « Poste de référence » : exporte tous les pilotes tiers d'une machine qui fonctionne parfaitement vers
/// &lt;dépôt&gt;\&lt;Fabricant&gt;\&lt;Modèle&gt;. Les autres postes du même modèle s'installent ensuite depuis ce dépôt,
/// sans Internet, en quelques minutes.
/// </summary>
public static class DriverExporter
{
    public static async Task<(bool Success, string Folder, string Message)> ExportAsync(string repositoryRoot, CancellationToken ct = default)
    {
        if (!Elevation.IsAdministrator())
            return (false, "", "L'export des pilotes nécessite les droits administrateur.");

        var sys = SystemInfoProvider.Collect();
        var model = string.IsNullOrWhiteSpace(sys.FriendlyModel) ? sys.Model : $"{sys.FriendlyModel} {sys.Model}";
        var folder = Path.Combine(repositoryRoot,
            AppPaths.SafeName(string.IsNullOrWhiteSpace(sys.Manufacturer) ? "Inconnu" : sys.Manufacturer, 40),
            AppPaths.SafeName(string.IsNullOrWhiteSpace(model) ? "Inconnu" : model, 60),
            $"Win{(sys.IsWindows11 ? "11" : "10")}-{sys.Architecture}");
        Directory.CreateDirectory(folder);

        var result = await PnpUtil.ExportAllAsync(folder, ct).ConfigureAwait(false);
        var count = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.inf", SearchOption.AllDirectories).Length : 0;
        await File.WriteAllTextAsync(Path.Combine(folder, "spokapilote-source.txt"),
            $"Exporté par SpokaPilote le {DateTime.Now:dd/MM/yyyy HH:mm}\r\nPoste : {sys.ComputerName}\r\nModèle : {sys.Manufacturer} {sys.DisplayModel}\r\n" +
            $"Système : {sys.OsCaption} {sys.OsDisplayVersion} ({sys.OsVersion})\r\nBIOS : {sys.BiosVersion}\r\nPaquets INF : {count}\r\n", ct).ConfigureAwait(false);

        return result.ExitCode == 0 || count > 0
            ? (true, folder, $"{count} paquet(s) de pilotes exporté(s) vers {folder}")
            : (false, folder, $"Échec de l'export (pnputil code {result.ExitCode}).");
    }

    /// <summary>Importe une archive (.zip/.cab) ou un dossier de pilotes téléchargé manuellement dans le dépôt.</summary>
    public static async Task<(bool Success, string Message)> ImportAsync(string source, string repositoryRoot, CancellationToken ct = default)
    {
        var name = AppPaths.SafeName(Path.GetFileNameWithoutExtension(source.TrimEnd('\\', '/')), 60);
        var target = Path.Combine(repositoryRoot, "_Imports", name);
        Directory.CreateDirectory(target);

        if (Directory.Exists(source))
        {
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(source, file);
                var dest = Path.Combine(target, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }
        }
        else if (source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || source.EndsWith(".cab", StringComparison.OrdinalIgnoreCase))
        {
            await Archive.ExtractAsync(source, target, ct).ConfigureAwait(false);
        }
        else
        {
            return (false, "Format non pris en charge : fournissez un dossier, un .zip ou un .cab contenant des fichiers .inf " +
                           "(pour un .exe constructeur, extrayez-le d'abord, ex. avec 7-Zip).");
        }

        var infs = Directory.GetFiles(target, "*.inf", SearchOption.AllDirectories);
        var unsigned = infs.Count(i =>
        {
            try { return string.IsNullOrEmpty(Matching.InfParser.Parse(i).CatalogFile); }
            catch { return true; }
        });
        var msg = $"{infs.Length} fichier(s) INF importé(s) dans {target}";
        if (unsigned > 0) msg += $" — attention : {unsigned} sans catalogue de signature (ils seront refusés à l'installation)";
        return (infs.Length > 0, msg);
    }
}
