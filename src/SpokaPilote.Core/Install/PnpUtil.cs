using SpokaPilote.Core.Infrastructure;

namespace SpokaPilote.Core.Install;

/// <summary>Enveloppe de pnputil.exe, l'outil Microsoft officiel de gestion du magasin de pilotes.</summary>
public static class PnpUtil
{
    private static string Exe => ProcessRunner.SystemTool("pnputil.exe");

    // Codes de sortie pnputil documentés.
    public const int ErrorSuccessRebootRequired = 3010;
    public const int ErrorNoMoreItems = 259; // aucun périphérique mis à jour (pilote ajouté au magasin mais pas meilleur que l'actuel)

    public static Task<ProcessResult> AddDriverAsync(string infPath, bool install, CancellationToken ct = default) =>
        ProcessRunner.RunAsync(Exe, $"/add-driver \"{infPath}\"{(install ? " /install" : "")}", timeout: TimeSpan.FromMinutes(15), ct: ct);

    public static Task<ProcessResult> AddDriversRecursiveAsync(string folder, bool install, CancellationToken ct = default) =>
        ProcessRunner.RunAsync(Exe, $"/add-driver \"{Path.Combine(folder, "*.inf")}\" /subdirs{(install ? " /install" : "")}", timeout: TimeSpan.FromMinutes(30), ct: ct);

    public static Task<ProcessResult> ExportAllAsync(string destination, CancellationToken ct = default) =>
        ProcessRunner.RunAsync(Exe, $"/export-driver * \"{destination}\"", timeout: TimeSpan.FromMinutes(30), ct: ct);

    /// <summary>Retire un paquet du magasin et le désinstalle des périphériques (ils reviennent au meilleur pilote restant).</summary>
    public static Task<ProcessResult> DeleteDriverAsync(string publishedName, CancellationToken ct = default)
    {
        if (!IsPublishedOemName(publishedName))
            throw new ArgumentException($"Nom de paquet inattendu : {publishedName}");
        return ProcessRunner.RunAsync(Exe, $"/delete-driver {publishedName} /uninstall /force", timeout: TimeSpan.FromMinutes(10), ct: ct);
    }

    /// <summary>Seuls les paquets tiers publiés (oemNN.inf) peuvent être retirés : jamais un pilote Windows intégré.</summary>
    internal static bool IsPublishedOemName(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, @"^oem\d+\.inf$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static Task<ProcessResult> ScanDevicesAsync(CancellationToken ct = default) =>
        ProcessRunner.RunAsync(Exe, "/scan-devices", timeout: TimeSpan.FromMinutes(5), ct: ct);

    public static bool IsSuccess(int exitCode) => exitCode is 0 or ErrorSuccessRebootRequired or ErrorNoMoreItems;
}
