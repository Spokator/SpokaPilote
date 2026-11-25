using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Matching;
using SpokaPilote.Core.Sources;

namespace SpokaPilote.Core.Printers;

public sealed record PrinterInstallResult(bool Success, string Message, string? PrinterName = null, string? DriverName = null);

public sealed class PrinterInstallRequest
{
    public required DiscoveredPrinter Printer { get; init; }
    public required PrinterDriverOption Driver { get; init; }
    public string? PrinterName { get; init; }
    public string? Location { get; init; }
    public bool SetAsDefault { get; init; }
    public bool Shared { get; init; }
}

/// <summary>Installe le pilote (si nécessaire), crée le port TCP/IP et la file d'impression Windows.</summary>
public sealed class PrinterInstaller
{
    private readonly Downloader _downloader;

    public PrinterInstaller(Settings settings) => _downloader = new Downloader(new TrustPolicy(settings));

    public async Task<PrinterInstallResult> InstallAsync(PrinterInstallRequest request, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!Elevation.IsAdministrator())
            return new PrinterInstallResult(false, "L'ajout d'une imprimante nécessite les droits administrateur.");

        var printer = request.Printer;
        var option = request.Driver;
        var name = string.IsNullOrWhiteSpace(request.PrinterName) ? DefaultName(printer) : request.PrinterName!.Trim();

        try
        {
            if (option.Kind == PrinterDriverOptionKind.VendorPage)
                return new PrinterInstallResult(false, $"Téléchargez le pilote sur {option.Url}, puis importez-le dans le dépôt SpokaPilote.");

            if (option.Kind == PrinterDriverOptionKind.IppClass)
            {
                var ippUrl = option.Url ?? printer.IppUri?.ToString() ?? $"http://{printer.Address}:631/ipp/print";
                progress?.Report("Création de l'imprimante IPP...");
                var script = $"Add-Printer -Name {ProcessRunner.PsQuote(name)} -IppURL {ProcessRunner.PsQuote(ippUrl)}";
                var r = await ProcessRunner.RunPowerShellAsync(script, TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
                if (r.ExitCode != 0) return new PrinterInstallResult(false, $"Échec de l'ajout IPP : {Clean(r.StdErr)}");
                await FinalizeAsync(name, request, ct).ConfigureAwait(false);
                return new PrinterInstallResult(true, $"Imprimante « {name} » ajoutée avec le pilote IPP Windows.", name, PrinterDriverResolver.IppClassDriver);
            }

            var driverName = option.DriverName;
            if (option.Kind is PrinterDriverOptionKind.MicrosoftCatalog or PrinterDriverOptionKind.LocalRepository)
            {
                var (inf, resolvedName, error) = await PrepareInfAsync(option, printer, progress, ct).ConfigureAwait(false);
                if (inf is null) return new PrinterInstallResult(false, error ?? "Paquet de pilote inutilisable.");
                driverName = resolvedName;

                progress?.Report($"Ajout du paquet {Path.GetFileName(inf)} au magasin de pilotes...");
                var add = await PnpUtil.AddDriverAsync(inf, install: false, ct).ConfigureAwait(false);
                if (!PnpUtil.IsSuccess(add.ExitCode))
                    return new PrinterInstallResult(false, $"pnputil a refusé le paquet (code {add.ExitCode}).");
            }

            if (string.IsNullOrWhiteSpace(driverName)) return new PrinterInstallResult(false, "Nom de pilote inconnu.");

            progress?.Report($"Enregistrement du pilote « {driverName} »...");
            var portName = $"IP_{printer.Address}";
            var raw = printer.OpenPorts.Contains(9100) || !printer.OpenPorts.Contains(515);
            var scriptMain = string.Join("; ", new[]
            {
                $"if (-not (Get-PrinterDriver -Name {ProcessRunner.PsQuote(driverName)} -ErrorAction SilentlyContinue)) {{ Add-PrinterDriver -Name {ProcessRunner.PsQuote(driverName)} }}",
                raw
                    ? $"if (-not (Get-PrinterPort -Name {ProcessRunner.PsQuote(portName)} -ErrorAction SilentlyContinue)) {{ Add-PrinterPort -Name {ProcessRunner.PsQuote(portName)} -PrinterHostAddress {ProcessRunner.PsQuote(printer.Address)} -PortNumber 9100 }}"
                    : $"if (-not (Get-PrinterPort -Name {ProcessRunner.PsQuote(portName)} -ErrorAction SilentlyContinue)) {{ Add-PrinterPort -Name {ProcessRunner.PsQuote(portName)} -LprHostAddress {ProcessRunner.PsQuote(printer.Address)} -LprQueueName 'lp' }}",
                $"if (Get-Printer -Name {ProcessRunner.PsQuote(name)} -ErrorAction SilentlyContinue) {{ Set-Printer -Name {ProcessRunner.PsQuote(name)} -DriverName {ProcessRunner.PsQuote(driverName)} -PortName {ProcessRunner.PsQuote(portName)} }} " +
                $"else {{ Add-Printer -Name {ProcessRunner.PsQuote(name)} -DriverName {ProcessRunner.PsQuote(driverName)} -PortName {ProcessRunner.PsQuote(portName)} }}",
            });
            progress?.Report("Création du port et de la file d'impression...");
            var result = await ProcessRunner.RunPowerShellAsync(scriptMain, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            if (result.ExitCode != 0)
                return new PrinterInstallResult(false, $"Échec de la création de l'imprimante : {Clean(result.StdErr)}");

            await FinalizeAsync(name, request, ct).ConfigureAwait(false);
            Log.Info($"Imprimante « {name} » installée ({printer.Address}, pilote « {driverName} »).");
            return new PrinterInstallResult(true, $"Imprimante « {name} » installée avec le pilote « {driverName} ».", name, driverName);
        }
        catch (SecurityException ex)
        {
            return new PrinterInstallResult(false, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Installation d'imprimante échouée", ex);
            return new PrinterInstallResult(false, ex.Message);
        }
    }

    public static string DefaultName(DiscoveredPrinter p)
    {
        var baseName = p.DisplayName;
        return $"{baseName} ({p.Address})";
    }

    private static async Task FinalizeAsync(string name, PrinterInstallRequest request, CancellationToken ct)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Location))
            parts.Add($"Set-Printer -Name {ProcessRunner.PsQuote(name)} -Location {ProcessRunner.PsQuote(request.Location)}");
        if (request.Shared)
            parts.Add($"Set-Printer -Name {ProcessRunner.PsQuote(name)} -Shared $true -ShareName {ProcessRunner.PsQuote(AppPaths.SafeName(name, 60))}");
        if (request.SetAsDefault)
            parts.Add($"Get-CimInstance -ClassName Win32_Printer | Where-Object Name -eq {ProcessRunner.PsQuote(name)} | Invoke-CimMethod -MethodName SetDefaultPrinter | Out-Null");
        if (parts.Count == 0) return;
        var r = await ProcessRunner.RunPowerShellAsync(string.Join("; ", parts), TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
        if (r.ExitCode != 0) Log.Warn($"Réglages complémentaires non appliqués : {Clean(r.StdErr)}");
    }

    /// <summary>
    /// Télécharge/extrait le paquet, choisit l'INF et le nom de pilote correspondant au modèle, vérifie la signature.
    /// Ne modifie pas le système : utilisable sans droits administrateur pour préparer une intervention.
    /// </summary>
    public async Task<(string? Inf, string? DriverName, string? Error)> PrepareInfAsync(
        PrinterDriverOption option, DiscoveredPrinter printer, IProgress<string>? progress, CancellationToken ct)
    {
        if (option.Kind == PrinterDriverOptionKind.LocalRepository && option.InfPath is not null)
            return SelectDriver(Path.GetDirectoryName(option.InfPath)!, option, printer);

        var variants = option.CatalogVariantIds.Count > 0 ? option.CatalogVariantIds : new List<string> { option.CatalogUpdateId! };
        string? lastError = null;
        for (var i = 0; i < variants.Count; i++)
        {
            var updateId = variants[i];
            progress?.Report(variants.Count > 1 ? $"Variante {i + 1}/{variants.Count} : résolution du lien officiel Microsoft..." : "Résolution du lien officiel Microsoft...");
            var urls = await MicrosoftCatalogClient.GetDownloadUrlsAsync(updateId, ct).ConfigureAwait(false);
            var url = urls.FirstOrDefault();
            if (url is null) { lastError = "Aucun fichier téléchargeable pour ce pilote."; continue; }
            var file = await _downloader.DownloadAsync(url, sha1: MicrosoftCatalogClient.Sha1FromUrl(url),
                progress: new Progress<DownloadProgress>(p => progress?.Report($"Téléchargement {p.Received / 1048576.0:0.0} Mo")), ct: ct).ConfigureAwait(false);
            var folder = Path.Combine(AppPaths.Extracted, AppPaths.SafeName(updateId));
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            progress?.Report("Extraction...");
            await Archive.ExtractAsync(file, folder, ct).ConfigureAwait(false);

            var result = SelectDriver(folder, option, printer);
            if (result.Inf is not null) return result;
            lastError = result.Error;
            Log.Info($"Variante {updateId} écartée : {result.Error}");
        }
        return (null, null, lastError);
    }

    private static (string? Inf, string? DriverName, string? Error) SelectDriver(string folder, PrinterDriverOption option, DiscoveredPrinter printer)
    {
        var model = printer.Model ?? "";
        var vendor = PrinterVendorCatalog.FindVendor(printer.Manufacturer) ?? PrinterVendorCatalog.FindVendor(model);
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => "amd64",
        };

        (string Inf, string Name, int Score)? best = null;
        var infs = option.InfPath is not null ? new[] { option.InfPath } : Directory.GetFiles(folder, "*.inf", SearchOption.AllDirectories);
        foreach (var infPath in infs)
        {
            InfInfo inf;
            try { inf = InfParser.Parse(infPath); } catch { continue; }
            if (!string.Equals(inf.Class, "Printer", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var m in inf.Models.Where(m => InfParser.DecorationApplies(m.Decoration, arch, Environment.OSVersion.Version)))
            {
                var score = option.DriverName is not null && m.Description.Equals(option.DriverName, StringComparison.OrdinalIgnoreCase)
                    ? 1000
                    : PrinterVendorCatalog.ModelScore(m.Description, model);
                if (score < 70 && vendor is not null)
                {
                    var u = vendor.UniversalDrivers.FindIndex(x => m.Description.StartsWith(x, StringComparison.OrdinalIgnoreCase));
                    if (u >= 0) score = 60 - u; // pilote universel : préférer l'ordre déclaré (PCL6 avant PS)
                }
                if (best is null || score > best.Value.Score) best = (infPath, m.Description, score);
            }
        }

        if (best is null || best.Value.Score < 40)
            return (null, null, $"Aucun pilote de ce paquet ne correspond au modèle « {model} » pour cette architecture ({arch}).");

        var infInfo = InfParser.Parse(best.Value.Inf);
        if (string.IsNullOrEmpty(infInfo.CatalogFile))
            return (null, null, "Paquet non signé (aucun catalogue .cat) : refusé.");
        var cat = Path.Combine(Path.GetDirectoryName(best.Value.Inf)!, infInfo.CatalogFile);
        var sig = File.Exists(cat) ? SignatureVerifier.Verify(cat) : new SignatureResult(false, null, "catalogue manquant");
        if (!sig.IsValid) return (null, null, $"Signature du pilote refusée : {sig.Detail}.");

        Log.Info($"Pilote d'impression retenu : « {best.Value.Name} » ({Path.GetFileName(best.Value.Inf)}, {sig}).");
        return (best.Value.Inf, best.Value.Name, null);
    }

    private static string Clean(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(l => !l.StartsWith('+') && !l.StartsWith("Au caractère") && !l.StartsWith("At line")) ?? s.Trim();
}
