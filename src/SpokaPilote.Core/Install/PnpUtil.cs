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

    public static Task<ProcessResult> ScanDevicesAsync(CancellationToken ct = default) =>
        ProcessRunner.RunAsync(Exe, "/scan-devices", timeout: TimeSpan.FromMinutes(5), ct: ct);

    public static bool IsSuccess(int exitCode) => exitCode is 0 or ErrorSuccessRebootRequired or ErrorNoMoreItems;
}
