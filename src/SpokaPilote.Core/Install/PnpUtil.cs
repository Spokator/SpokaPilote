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

    /// <summary>
    /// Retire un paquet du magasin et le désinstalle des périphériques (ils reviennent au meilleur pilote restant). Jamais
    /// /force : si Windows ne peut pas libérer un périphérique qui l'utilise, pnputil refuse et le paquet reste en place
    /// (signalé à l'appelant) plutôt que d'être arraché à un périphérique en service.
    /// </summary>
    public static Task<ProcessResult> DeleteDriverAsync(string publishedName, CancellationToken ct = default)
    {
        if (!IsPublishedOemName(publishedName))
            throw new ArgumentException($"Nom de paquet inattendu : {publishedName}");
        return ProcessRunner.RunAsync(Exe, DeleteDriverArguments(publishedName), timeout: TimeSpan.FromMinutes(10), ct: ct);
    }

    internal static string DeleteDriverArguments(string publishedName) => $"/delete-driver {publishedName} /uninstall";

    /// <summary>
    /// Noms publiés (oemNN.inf) cités par la sortie de pnputil /add-driver (« Published Name: oem42.inf », « Nom publié :
    /// oem42.inf » selon la langue de Windows), dans l'ordre et sans doublon. Le nom est cité aussi pour un paquet « déjà
    /// présent » : l'appelant écarte ceux qui existaient avant l'intervention.
    /// </summary>
    internal static List<string> ParsePublishedNames(string output) =>
        System.Text.RegularExpressions.Regex.Matches(output ?? "", @"(?<![\w.])oem\d+\.inf\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(m => m.Value.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Seuls les paquets tiers publiés (oemNN.inf) peuvent être retirés : jamais un pilote Windows intégré.</summary>
    internal static bool IsPublishedOemName(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, @"^oem\d+\.inf$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Retire un paquet INUTILISÉ du magasin (nettoyage) : ni /uninstall ni /force, pnputil refuse donc la suppression
    /// si un périphérique utilise encore le paquet.
    /// </summary>
    public static Task<ProcessResult> DeleteUnusedDriverAsync(string publishedName, CancellationToken ct = default)
    {
        if (!IsPublishedOemName(publishedName))
            throw new ArgumentException($"Nom de paquet inattendu : {publishedName}");
        return ProcessRunner.RunAsync(Exe, $"/delete-driver {publishedName}", timeout: TimeSpan.FromMinutes(10), ct: ct);
    }

    /// <summary>Copie un paquet du magasin dans un dossier (sauvegarde réinstallable avec « spokapilote restore »).</summary>
    public static Task<ProcessResult> ExportDriverAsync(string publishedName, string destination, CancellationToken ct = default)
    {
        if (!IsPublishedOemName(publishedName))
            throw new ArgumentException($"Nom de paquet inattendu : {publishedName}");
        return ProcessRunner.RunAsync(Exe, $"/export-driver {publishedName} \"{destination}\"", timeout: TimeSpan.FromMinutes(10), ct: ct);
    }

    public static Task<ProcessResult> ScanDevicesAsync(CancellationToken ct = default) =>
        ProcessRunner.RunAsync(Exe, "/scan-devices", timeout: TimeSpan.FromMinutes(5), ct: ct);

    // ------------------------------------------------------------------ actions sur un périphérique

    /// <summary>
    /// /restart-device, /enable-device, /disable-device et /remove-device existent depuis Windows 10 version 2004
    /// (build 19041) ; /scan-devices depuis la version 1903.
    /// </summary>
    public static bool SupportsDeviceCommands => Environment.OSVersion.Version.Build >= 19041;

    /// <summary>Redémarre un périphérique (arrêt puis redémarrage de sa pile de pilotes).</summary>
    public static Task<ProcessResult> RestartDeviceAsync(string instanceId, CancellationToken ct = default) =>
        DeviceCommandAsync("/restart-device", instanceId, ct);

    public static Task<ProcessResult> EnableDeviceAsync(string instanceId, CancellationToken ct = default) =>
        DeviceCommandAsync("/enable-device", instanceId, ct);

    public static Task<ProcessResult> DisableDeviceAsync(string instanceId, CancellationToken ct = default) =>
        DeviceCommandAsync("/disable-device", instanceId, ct);

    /// <summary>Retire le périphérique (sans supprimer son pilote du magasin) : « /scan-devices » le redétecte ensuite.</summary>
    public static Task<ProcessResult> RemoveDeviceAsync(string instanceId, CancellationToken ct = default) =>
        DeviceCommandAsync("/remove-device", instanceId, ct);

    private static Task<ProcessResult> DeviceCommandAsync(string command, string instanceId, CancellationToken ct)
    {
        if (!IsValidInstanceId(instanceId))
            throw new ArgumentException($"Identifiant de périphérique inattendu : {instanceId}");
        // Jamais /force : pnputil refuse alors d'agir sur un périphérique qui fournit une fonction système critique.
        return ProcessRunner.RunAsync(Exe, $"{command} \"{instanceId}\"", timeout: TimeSpan.FromMinutes(3), ct: ct);
    }

    /// <summary>Identifiant d'instance plausible (ex. USB\VID_0000&amp;PID_0002\5&amp;2E786FD2&amp;0&amp;4) : pas de guillemet ni de joker.</summary>
    internal static bool IsValidInstanceId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 512 && id.Contains('\\') &&
        id.IndexOfAny(new[] { '"', '*', '?', '\r', '\n', '\0' }) < 0 && !id.StartsWith('/');

    public static bool IsSuccess(int exitCode) => exitCode is 0 or ErrorSuccessRebootRequired or ErrorNoMoreItems;
}
