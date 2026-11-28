using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Install;

namespace SpokaPilote.Core.Printers;

public enum PrinterDeployStatus { Created, UpToDate, Updated, Removed, Skipped, Failed }

public sealed class PrinterDeployItem
{
    public required string Name { get; init; }
    public PrinterDeployStatus Status { get; set; }
    public string Message { get; set; } = "";
    public List<string> Changes { get; } = new();
    public bool DryRun { get; init; }

    public string StatusLabel => (Status, DryRun) switch
    {
        (PrinterDeployStatus.Created, true) => "à créer",
        (PrinterDeployStatus.Updated, true) => "à mettre à jour",
        (PrinterDeployStatus.Removed, true) => "à retirer",
        (PrinterDeployStatus.Created, _) => "créée",
        (PrinterDeployStatus.Updated, _) => "mise à jour",
        (PrinterDeployStatus.Removed, _) => "retirée",
        (PrinterDeployStatus.UpToDate, _) => "à jour",
        (PrinterDeployStatus.Skipped, _) => "ignorée",
        _ => "échec",
    };
}

public sealed class PrinterDeployReport
{
    public required PrinterProfileContext Context { get; init; }
    public bool DryRun { get; init; }
    public int Applicable { get; set; }
    public int NotApplicable { get; set; }
    public List<string> Warnings { get; } = new();
    public List<PrinterDeployItem> Items { get; } = new();
    public bool HasFailures => Items.Any(i => i.Status == PrinterDeployStatus.Failed);
    public bool AllFailed => Items.Count > 0 && Items.All(i => i.Status == PrinterDeployStatus.Failed);
}

/// <summary>
/// Applique un profil d'imprimantes au poste, de façon idempotente : une imprimante conforme n'est pas touchée,
/// une imprimante dont l'adresse ou le pilote diffère est mise à jour, une imprimante absente est créée.
/// Le pilote vient du profil s'il est présent, sinon de la logique existante (PrinterDiscovery + PrinterDriverResolver).
/// </summary>
public sealed class PrinterProfileDeployer
{
    private readonly Settings _settings;

    public PrinterProfileDeployer(Settings settings) => _settings = settings;

    private static bool IsSystemAccount()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem;
    }

    public async Task<PrinterDeployReport> DeployAsync(PrinterProfile profile, PrinterProfileContext context, bool dryRun,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var validation = profile.Validate();
        if (!validation.IsValid) throw new FormatException("Profil invalide :\n  " + string.Join("\n  ", validation.Errors));
        if (!dryRun && !Elevation.IsAdministrator())
            throw new InvalidOperationException("Le déploiement d'imprimantes nécessite les droits administrateur (ou --dry-run).");

        var report = new PrinterDeployReport { Context = context, DryRun = dryRun };
        report.Warnings.AddRange(validation.Warnings);
        var all = profile.AllEntries().Count();
        var applicable = new List<PrinterProfileEntry>();
        foreach (var entry in profile.ApplicableEntries(context))
        {
            // Deux entrées de même nom applicables (conditions qui se recouvrent) : la première l'emporte.
            if (applicable.Any(e => e.EffectiveName!.Equals(entry.EffectiveName, StringComparison.OrdinalIgnoreCase)))
            {
                report.Warnings.Add($"« {entry.EffectiveName} » est applicable plusieurs fois : seule la première définition est utilisée.");
                continue;
            }
            applicable.Add(entry);
        }
        report.Applicable = applicable.Count;
        report.NotApplicable = all - applicable.Count;

        var inventory = PrinterInventory.Read();
        foreach (var entry in applicable)
        {
            ct.ThrowIfCancellationRequested();
            var item = await DeployEntryAsync(entry, inventory, dryRun, progress, ct).ConfigureAwait(false);
            report.Items.Add(item);
            if (!dryRun && item.Status is PrinterDeployStatus.Created or PrinterDeployStatus.Updated)
                inventory = PrinterInventory.Read(); // ports et files viennent de changer
        }

        if (profile.RemoveUnlisted)
        {
            var keep = new HashSet<string>(applicable.Select(e => e.EffectiveName!), StringComparer.OrdinalIgnoreCase);
            foreach (var p in inventory.Printers.Where(p => p.IsNetwork && !keep.Contains(p.Name)).ToList())
            {
                var item = new PrinterDeployItem { Name = p.Name, DryRun = dryRun, Status = PrinterDeployStatus.Removed };
                item.Changes.Add($"non listée dans le profil ({PrinterDeployPlanner.DescribeExisting(p)})");
                if (!dryRun)
                {
                    progress?.Report($"Retrait de « {p.Name} » (non listée)...");
                    var (ok, message) = await RemoveAsync(p.Name, ct).ConfigureAwait(false);
                    item.Status = ok ? PrinterDeployStatus.Removed : PrinterDeployStatus.Failed;
                    item.Message = message;
                }
                report.Items.Add(item);
            }
        }
        return report;
    }

    private async Task<PrinterDeployItem> DeployEntryAsync(PrinterProfileEntry entry, PrinterInventory inventory, bool dryRun,
        IProgress<string>? progress, CancellationToken ct)
    {
        var name = entry.EffectiveName!;
        var item = new PrinterDeployItem { Name = name, DryRun = dryRun };
        var existing = inventory.Find(name);
        var changes = PrinterDeployPlanner.Compare(entry, existing);

        var system = IsSystemAccount();
        if (system && changes.RemoveAll(c => c.Kind == PrinterChangeKind.Default) > 0 && changes.Count == 0)
        {
            item.Status = PrinterDeployStatus.UpToDate;
            item.Message = "conforme (imprimante par défaut : réglage propre à chaque utilisateur, ignoré pour le compte SYSTEM)";
            return item;
        }
        item.Changes.AddRange(changes.Select(c => c.Description));
        if (changes.Count == 0)
        {
            item.Status = PrinterDeployStatus.UpToDate;
            item.Message = existing is null ? "" : $"{PrinterDeployPlanner.DescribeExisting(existing)}, pilote « {existing.DriverName} »";
            return item;
        }
        item.Status = existing is null ? PrinterDeployStatus.Created : PrinterDeployStatus.Updated;

        try
        {
            if (entry.IsConnection) return await DeployConnectionAsync(entry, existing, changes, item, system, dryRun, ct).ConfigureAwait(false);

            if (changes.Any(c => c.Kind is PrinterChangeKind.Create or PrinterChangeKind.Address or PrinterChangeKind.Driver))
            {
                progress?.Report($"« {name} » : choix du pilote...");
                var plan = await ChooseDriverAsync(entry, existing, progress, ct).ConfigureAwait(false);
                if (plan.Error is not null) return Fail(item, plan.Error);
                if (dryRun)
                {
                    item.Message = $"pilote : {plan.Label}";
                    return item;
                }
                progress?.Report($"« {name} » : création du port et de la file d'impression...");
                var (ok, driverUsed, error) = await ApplyQueueAsync(entry, existing, plan, inventory, progress, ct).ConfigureAwait(false);
                if (!ok && plan.Fallback is not null)
                {
                    Log.Warn($"« {name} » : pilote « {plan.DriverName} » indisponible ({error}), choix automatique : {plan.Fallback.Label}.");
                    item.Changes.Add($"pilote préféré « {plan.DriverName} » indisponible : {plan.Fallback.Label}");
                    (ok, driverUsed, error) = await ApplyQueueAsync(entry, existing, plan.Fallback, inventory, progress, ct).ConfigureAwait(false);
                }
                if (!ok) return Fail(item, error ?? "échec de la création de la file d'impression");
                item.Message = $"pilote « {driverUsed} »";
            }
            else if (dryRun)
            {
                return item;
            }

            // Réglages complémentaires, puis contrôle : l'imprimante doit maintenant être conforme.
            var after = PrinterInventory.Read().Find(name);
            if (after is null) return Fail(item, "la file d'impression n'apparaît pas après sa création");
            var remaining = PrinterDeployPlanner.Compare(entry, after);
            if (system) remaining.RemoveAll(c => c.Kind == PrinterChangeKind.Default);
            remaining.RemoveAll(c => c.Kind == PrinterChangeKind.Driver); // pilote préféré indisponible : remplacement déjà signalé
            if (remaining.Any(c => c.Kind is PrinterChangeKind.Address))
                return Fail(item, $"contrôle après déploiement : {string.Join(", ", remaining.Select(c => c.Description))}");
            if (remaining.Count > 0)
            {
                var error = await ApplySettingsAsync(entry, name, remaining, ct).ConfigureAwait(false);
                if (error is not null) return Fail(item, $"réglages non appliqués : {error}");
            }
            Log.Info($"Profil d'imprimantes : « {name} » {item.StatusLabel} ({string.Join(", ", item.Changes)}).");
            return item;
        }
        catch (SecurityException ex)
        {
            return Fail(item, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"Déploiement de l'imprimante « {name} » échoué", ex);
            return Fail(item, ex.Message);
        }
    }

    private static PrinterDeployItem Fail(PrinterDeployItem item, string message)
    {
        item.Status = PrinterDeployStatus.Failed;
        item.Message = message;
        Log.Info($"Profil d'imprimantes : « {item.Name} » en échec : {message}"); // affiché par le rapport : journal seulement
        return item;
    }

    // ------------------------------------------------------------------ connexions partagées (\\serveur\file)

    private static async Task<PrinterDeployItem> DeployConnectionAsync(PrinterProfileEntry entry, InstalledPrinter? existing,
        List<PrinterChange> changes, PrinterDeployItem item, bool system, bool dryRun, CancellationToken ct)
    {
        if (system)
        {
            item.Status = PrinterDeployStatus.Skipped;
            item.Message = "connexion partagée : propre à chaque utilisateur, sans effet pour le compte SYSTEM (déployez-la à l'ouverture de session ou par GPO)";
            return item;
        }
        if (dryRun) return item;

        var name = entry.EffectiveName!;
        var parts = new List<string>();
        if (existing is null) parts.Add($"Add-Printer -ConnectionName {ProcessRunner.PsQuote(name)}");
        if (changes.Any(c => c.Kind == PrinterChangeKind.Default) || (existing is null && entry.SetDefault)) parts.Add(SetDefaultScript(name));
        var r = await ProcessRunner.RunPowerShellAsync(string.Join("; ", parts), TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        if (r.ExitCode != 0) return Fail(item, $"connexion impossible : {Clean(r.StdErr)}");
        item.Message = "connexion ajoutée pour l'utilisateur courant";
        return item;
    }

    // ------------------------------------------------------------------ choix du pilote

    private sealed class DriverPlan
    {
        public string? DriverName { get; init; }
        public PrinterDriverOption? Package { get; init; }
        public DiscoveredPrinter? Printer { get; init; }
        public string? IppUrl { get; init; }
        public string Label { get; init; } = "";
        public string? Error { get; init; }
        /// <summary>Choix automatique si le pilote préféré n'est pas dans le magasin de pilotes Windows.</summary>
        public DriverPlan? Fallback { get; init; }
    }

    private async Task<DriverPlan> ChooseDriverAsync(PrinterProfileEntry entry, InstalledPrinter? existing, IProgress<string>? progress, CancellationToken ct)
    {
        if (entry.EffectiveProtocol == "ipp")
            return new DriverPlan { DriverName = PrinterDriverResolver.IppClassDriver, IppUrl = entry.EffectiveIppUrl, Label = $"« {PrinterDriverResolver.IppClassDriver} » ({entry.EffectiveIppUrl})" };

        var installed = PrinterDriverResolver.InstalledPrinterDrivers();
        bool IsInstalled(string? d) => d is not null && installed.Contains(d.Trim(), StringComparer.OrdinalIgnoreCase);

        var wanted = entry.DriverName?.Trim();
        if (IsInstalled(wanted))
            return new DriverPlan { DriverName = wanted, Label = $"« {wanted} » (déjà présent)" };
        if (wanted is null && existing is not null && IsInstalled(existing.DriverName))
            return new DriverPlan { DriverName = existing.DriverName, Label = $"« {existing.DriverName} » (pilote actuel conservé)" };

        progress?.Report($"Identification de l'imprimante {entry.Address}...");
        var printer = await IdentifyAsync(entry, ct).ConfigureAwait(false);
        var options = await PrinterDriverResolver.ResolveAsync(printer, _settings, ct).ConfigureAwait(false);
        var protocolForced = entry.Protocol is not null; // raw/lpr imposé : le pilote IPP (file IPP) ne convient pas
        var usable = options.Where(o => o.Kind != PrinterDriverOptionKind.VendorPage && !(protocolForced && o.Kind == PrinterDriverOptionKind.IppClass)).ToList();

        DriverPlan? auto = usable.FirstOrDefault() is { } best ? FromOption(best, printer, entry) : null;
        if (wanted is not null)
        {
            var match = usable.FirstOrDefault(o => string.Equals(o.DriverName, wanted, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return FromOption(match, printer, entry);
            // Pilote intégré à Windows (magasin de pilotes) : Add-PrinterDriver le rend disponible sans téléchargement.
            return new DriverPlan { DriverName = wanted, Label = $"« {wanted} » (magasin de pilotes Windows){(auto is null ? "" : $", sinon {auto.Label}")}", Fallback = auto };
        }

        if (auto is not null) return auto;
        var vendorPage = options.FirstOrDefault(o => o.Kind == PrinterDriverOptionKind.VendorPage);
        var model = printer.Model is null ? "modèle inconnu" : $"« {printer.Model} »";
        var unreachable = printer.OpenPorts.Count == 0 ? ", imprimante injoignable" : "";
        return new DriverPlan
        {
            Error = vendorPage is not null
                ? $"aucun pilote officiel téléchargeable ({model}) : {vendorPage.Url} (puis import dans le dépôt, ou « driverName » dans le profil)"
                : $"aucun pilote trouvé ({model}{unreachable}) : renseignez « model » ou « driverName » dans le profil",
        };
    }

    private static DriverPlan FromOption(PrinterDriverOption o, DiscoveredPrinter printer, PrinterProfileEntry entry) => o.Kind switch
    {
        PrinterDriverOptionKind.IppClass => new DriverPlan
        {
            DriverName = PrinterDriverResolver.IppClassDriver,
            IppUrl = entry.IppUrl ?? o.Url ?? entry.EffectiveIppUrl,
            Label = $"« {PrinterDriverResolver.IppClassDriver} » ({entry.IppUrl ?? o.Url ?? entry.EffectiveIppUrl})",
        },
        PrinterDriverOptionKind.Installed => new DriverPlan { DriverName = o.DriverName, Label = $"« {o.DriverName} » (déjà présent)" },
        _ => new DriverPlan { DriverName = o.DriverName, Package = o, Printer = printer, Label = o.ToString() },
    };

    /// <summary>Identification par la logique existante (ports, SNMP, IPP) ; à défaut, le modèle indiqué dans le profil.</summary>
    private static async Task<DiscoveredPrinter> IdentifyAsync(PrinterProfileEntry entry, CancellationToken ct)
    {
        var host = entry.Address!.Trim();
        IPAddress? ip = null;
        if (IPAddress.TryParse(host, out var parsed)) ip = parsed;
        else
        {
            try { ip = (await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false)).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork); }
            catch (SocketException) { Log.Warn($"Nom d'hôte introuvable : {host}"); }
        }
        var found = ip is null ? null : (await PrinterDiscovery.DiscoverAsync(new[] { ip }, ct: ct).ConfigureAwait(false)).FirstOrDefault();

        var printer = new DiscoveredPrinter
        {
            Address = host,
            Model = found?.Model ?? entry.Model,
            Manufacturer = found?.Manufacturer,
            Description = found?.Description,
            DeviceId1284 = found?.DeviceId1284,
            IppUri = found?.IppUri,
            HostName = found?.HostName,
        };
        if (found is not null) printer.OpenPorts.AddRange(found.OpenPorts);
        printer.Manufacturer ??= PrinterVendorCatalog.FindVendor(printer.Model)?.Name;
        return printer;
    }

    // ------------------------------------------------------------------ application

    private async Task<(bool Ok, string? Driver, string? Error)> ApplyQueueAsync(PrinterProfileEntry entry, InstalledPrinter? existing,
        DriverPlan plan, PrinterInventory inventory, IProgress<string>? progress, CancellationToken ct)
    {
        var name = entry.EffectiveName!;
        var q = ProcessRunner.PsQuote;
        var driverName = plan.DriverName;

        if (plan.Package is not null)
        {
            using var installer = new PrinterInstaller(_settings);
            var (inf, resolved, error) = await installer.PrepareInfAsync(plan.Package, plan.Printer!, progress, ct).ConfigureAwait(false);
            if (inf is null) return (false, null, error ?? "paquet de pilote inutilisable");
            progress?.Report($"Ajout du paquet {Path.GetFileName(inf)} au magasin de pilotes...");
            var add = await PnpUtil.AddDriverAsync(inf, install: false, ct).ConfigureAwait(false);
            if (!PnpUtil.IsSuccess(add.ExitCode)) return (false, null, $"pnputil a refusé le paquet (code {add.ExitCode})");
            driverName = resolved;
        }
        if (string.IsNullOrWhiteSpace(driverName)) return (false, null, "nom de pilote inconnu");

        string script;
        string? newPort = null;
        if (plan.IppUrl is not null)
        {
            // Une file existante ne peut pas basculer en IPP : elle est recréée.
            script = $"if (Get-Printer -Name {q(name)} -ErrorAction SilentlyContinue) {{ Remove-Printer -Name {q(name)} }}; " +
                     $"Add-Printer -Name {q(name)} -IppURL {q(plan.IppUrl)}";
        }
        else
        {
            var reuse = PrinterDeployPlanner.FindReusablePort(entry, inventory.TcpPorts);
            newPort = reuse?.Name ?? UniquePortName(PrinterDeployPlanner.PortNameFor(entry), inventory);
            var host = entry.Address!.Trim();
            var portCommand = reuse is not null ? null
                : entry.EffectiveProtocol == "lpr"
                    ? $"Add-PrinterPort -Name {q(newPort)} -LprHostAddress {q(host)} -LprQueueName {q(entry.EffectiveLprQueue)}"
                    : $"Add-PrinterPort -Name {q(newPort)} -PrinterHostAddress {q(host)} -PortNumber {entry.EffectivePortNumber}";
            script = string.Join("; ", new[]
            {
                $"if (-not (Get-PrinterDriver -Name {q(driverName)} -ErrorAction SilentlyContinue)) {{ Add-PrinterDriver -Name {q(driverName)} }}",
                portCommand ?? "",
                $"if (Get-Printer -Name {q(name)} -ErrorAction SilentlyContinue) {{ Set-Printer -Name {q(name)} -DriverName {q(driverName)} -PortName {q(newPort)} }} " +
                $"else {{ Add-Printer -Name {q(name)} -DriverName {q(driverName)} -PortName {q(newPort)} }}",
            }.Where(s => s.Length > 0));
        }

        var r = await ProcessRunner.RunPowerShellAsync(script, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        if (r.ExitCode != 0) return (false, null, Clean(r.StdErr));

        // Ancien port TCP/IP devenu inutile (changement d'adresse) : retiré s'il ne sert à aucune autre imprimante.
        if (existing is { PortKind: InstalledPortKind.TcpIp } && !existing.PortName.Equals(newPort, StringComparison.OrdinalIgnoreCase)
            && inventory.IsPortUnused(existing.PortName, exceptPrinter: name))
        {
            var cleanup = await ProcessRunner.RunPowerShellAsync(RemovePortScript(existing.PortName), TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
            if (cleanup.StdOut.Contains("PORT_KEPT")) Log.Warn($"Ancien port « {existing.PortName} » conservé (encore utilisé par le spouleur).");
        }
        return (true, driverName, null);
    }

    private static string UniquePortName(string baseName, PrinterInventory inventory)
    {
        var name = baseName;
        for (var i = 2; inventory.TcpPorts.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); i++) name = $"{baseName}_{i}";
        return name;
    }

    private static async Task<string?> ApplySettingsAsync(PrinterProfileEntry entry, string name, List<PrinterChange> changes, CancellationToken ct)
    {
        var q = ProcessRunner.PsQuote;
        var parts = new List<string>();
        foreach (var c in changes)
        {
            switch (c.Kind)
            {
                case PrinterChangeKind.Location: parts.Add($"Set-Printer -Name {q(name)} -Location {q(entry.Location!.Trim())}"); break;
                case PrinterChangeKind.Comment: parts.Add($"Set-Printer -Name {q(name)} -Comment {q(entry.Comment!.Trim())}"); break;
                case PrinterChangeKind.Shared when entry.Shared == true:
                    parts.Add($"Set-Printer -Name {q(name)} -Shared $true -ShareName {q(AppPaths.SafeName(name, 60))}"); break;
                case PrinterChangeKind.Shared: parts.Add($"Set-Printer -Name {q(name)} -Shared $false"); break;
                case PrinterChangeKind.Default: parts.Add(SetDefaultScript(name)); break;
            }
        }
        if (parts.Count == 0) return null;
        var r = await ProcessRunner.RunPowerShellAsync(string.Join("; ", parts), TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        return r.ExitCode == 0 ? null : Clean(r.StdErr);
    }

    private static string SetDefaultScript(string name) =>
        $"Get-CimInstance -ClassName Win32_Printer | Where-Object Name -eq {ProcessRunner.PsQuote(name)} | Invoke-CimMethod -MethodName SetDefaultPrinter | Out-Null";

    private static string RemovePortScript(string port)
    {
        var q = ProcessRunner.PsQuote(port);
        // Le spouleur libère parfois le port avec un léger délai après la suppression de la file.
        return $"for ($i = 0; $i -lt 10; $i++) {{ if (-not (Get-PrinterPort -Name {q} -ErrorAction SilentlyContinue)) {{ break }}; " +
               $"try {{ Remove-PrinterPort -Name {q} -ErrorAction Stop }} catch {{ Start-Sleep -Milliseconds 500 }} }}; " +
               $"if (Get-PrinterPort -Name {q} -ErrorAction SilentlyContinue) {{ 'PORT_KEPT' }}";
    }

    // ------------------------------------------------------------------ retrait

    /// <summary>
    /// Retire une imprimante réseau ou une connexion partagée, ainsi que son port TCP/IP s'il ne sert plus.
    /// Les imprimantes locales/virtuelles (PDF, XPS, OneNote, Fax...) sont refusées ; aucun pilote n'est désinstallé.
    /// </summary>
    public static async Task<(bool Ok, string Message)> RemoveAsync(string name, CancellationToken ct = default)
    {
        if (!Elevation.IsAdministrator()) return (false, "Le retrait d'une imprimante nécessite les droits administrateur.");
        var inventory = PrinterInventory.Read();
        var printer = inventory.Find(name);
        if (printer is null) return (false, $"Imprimante « {name} » introuvable sur ce poste.");
        if (!printer.IsNetwork && !printer.IsConnection)
            return (false, $"« {printer.Name} » est une imprimante locale ou virtuelle (port {printer.PortName}) : non gérée par {AppIdentity.Current.ProductName}.");

        var removePort = printer.PortKind == InstalledPortKind.TcpIp && inventory.IsPortUnused(printer.PortName, exceptPrinter: printer.Name);
        var script = $"Remove-Printer -Name {ProcessRunner.PsQuote(printer.Name)}" + (removePort ? "; " + RemovePortScript(printer.PortName) : "");
        var r = await ProcessRunner.RunPowerShellAsync(script, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        if (r.ExitCode != 0) return (false, $"Retrait de « {printer.Name} » impossible : {Clean(r.StdErr)}");

        var after = PrinterInventory.Read();
        if (after.Find(printer.Name) is not null) return (false, $"« {printer.Name} » est toujours présente après le retrait.");
        var portKept = removePort && after.TcpPorts.Any(p => p.Name.Equals(printer.PortName, StringComparison.OrdinalIgnoreCase));
        Log.Info($"Imprimante « {printer.Name} » retirée{(removePort && !portKept ? $", port « {printer.PortName} » retiré" : "")}.");
        var detail = printer.PortKind != InstalledPortKind.TcpIp ? ""
            : !removePort ? $" (port « {printer.PortName} » conservé : utilisé par une autre imprimante)"
            : portKept ? $" (port « {printer.PortName} » encore retenu par le spouleur : il sera libéré à son redémarrage)"
            : $" ainsi que son port « {printer.PortName} »";
        return (true, $"Imprimante « {printer.Name} » retirée{detail}. Le pilote reste disponible sur le poste.");
    }

    // ------------------------------------------------------------------ export

    /// <summary>
    /// Construit un profil à partir des imprimantes du poste : imprimantes réseau (port TCP/IP ou IPP) et connexions
    /// partagées. Les imprimantes locales/virtuelles et les ports WSD sont écartés, avec leur motif.
    /// </summary>
    public static (PrinterProfile Profile, List<(string Name, string Reason)> Skipped) Export(PrinterInventory inventory, string computerName)
    {
        var profile = new PrinterProfile
        {
            Name = $"Imprimantes de {computerName}",
            ExportedFrom = computerName,
            ExportedAt = DateTime.Now,
        };
        var skipped = new List<(string, string)>();
        foreach (var p in inventory.Printers.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            switch (p.PortKind)
            {
                case InstalledPortKind.TcpIp:
                    profile.Printers.Add(new PrinterProfileEntry
                    {
                        Name = p.Name,
                        Address = p.Host,
                        DriverName = string.IsNullOrWhiteSpace(p.DriverName) ? null : p.DriverName,
                        Location = p.Location,
                        Comment = p.Comment,
                        SetDefault = p.IsDefault,
                        Shared = p.Shared ? true : null,
                        Protocol = p.Protocol == "lpr" ? "lpr" : null,
                        PortNumber = p.Protocol == "raw" && p.PortNumber is { } n && n != 9100 ? n : null,
                        LprQueue = p.Protocol == "lpr" && !string.Equals(p.LprQueue, "lp", StringComparison.OrdinalIgnoreCase) ? p.LprQueue : null,
                    });
                    break;
                case InstalledPortKind.Ipp:
                    profile.Printers.Add(new PrinterProfileEntry
                    {
                        Name = p.Name,
                        Address = p.Host,
                        IppOnly = true,
                        IppUrl = p.IppUrl,
                        Location = p.Location,
                        Comment = p.Comment,
                        SetDefault = p.IsDefault,
                        Shared = p.Shared ? true : null,
                    });
                    break;
                case InstalledPortKind.Connection:
                    profile.Printers.Add(new PrinterProfileEntry { Connection = p.Name, SetDefault = p.IsDefault });
                    break;
                case InstalledPortKind.Wsd:
                    skipped.Add((p.Name, "port WSD (découverte automatique) : adresse non exportable, recréez-la en TCP/IP"));
                    break;
                default:
                    skipped.Add((p.Name, $"imprimante locale ou virtuelle (port {p.PortName})"));
                    break;
            }
        }
        return (profile, skipped);
    }

    private static string Clean(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => !l.StartsWith('+') && !l.StartsWith("Au caractère") && !l.StartsWith("At line")) ?? s.Trim();
}
