using SpokaPilote.Core.Infrastructure;
using SpokaPilote.Core.Matching;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Sources;

namespace SpokaPilote.Core.Install;

public enum InstallStatus { Installed, NotApplicable, Failed, Blocked, Skipped }

public sealed record InstallOutcome(DriverCandidate Candidate, InstallStatus Status, string Message, bool RebootRequired = false)
{
    public bool Succeeded => Status is InstallStatus.Installed or InstallStatus.NotApplicable;
}

public sealed record InstallProgress(int Index, int Total, string Title, string Step, double? Percent = null);

public sealed class InstallOptions
{
    public bool CreateRestorePoint { get; init; } = true;
    public bool BackupDrivers { get; init; } = true;
    /// <summary>Simulation : télécharge et vérifie tout, mais n'installe rien.</summary>
    public bool DryRun { get; init; }
}

/// <summary>
/// Moteur d'installation. Chaque paquet passe par : source officielle → empreinte → signature Authenticode
/// d'un éditeur approuvé → (INF) l'ID matériel est réellement déclaré → installation → contrôle du résultat.
/// </summary>
public sealed class DriverInstaller
{
    private readonly TrustPolicy _trust;
    private readonly Downloader _downloader;
    private readonly SystemInfo _system;
    private readonly BackupRetention _retention;

    /// <summary>Dossier de travail de l'exécution en cours (téléchargements, extractions), supprimé à la fin.</summary>
    private WorkDirectory? _work;
    /// <summary>Noms publiés renvoyés par pnputil /add-driver pendant l'étape en cours.</summary>
    private readonly List<string> _reportedNames = new();

    public DriverInstaller(Settings settings, SystemInfo system)
    {
        _trust = new TrustPolicy(settings);
        _downloader = new Downloader(_trust);
        _system = system;
        _retention = BackupRetention.From(settings);
    }

    private WorkDirectory Work => _work ?? throw new InvalidOperationException("Aucune installation en cours.");

    /// <summary>
    /// Installe les propositions. Tout est téléchargé, vérifié et exécuté dans un dossier de travail neuf et privé
    /// (<see cref="WorkDirectory"/>), supprimé à la fin, que l'installation réussisse ou non.
    /// </summary>
    public async Task<List<InstallOutcome>> InstallAsync(
        IReadOnlyList<DriverCandidate> candidates,
        IReadOnlyList<DeviceInfo> devices,
        InstallOptions options,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!options.DryRun && candidates.Any(c => c.BlockedReason is null) && !Elevation.IsAdministrator())
            throw new UnauthorizedAccessException("L'installation de pilotes nécessite les droits administrateur.");
        if (candidates.Count == 0) return new List<InstallOutcome>();

        using var work = WorkDirectory.Create("install");
        _work = work;
        try
        {
            return await InstallCoreAsync(candidates, devices, options, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            _work = null;
        }
    }

    private async Task<List<InstallOutcome>> InstallCoreAsync(
        IReadOnlyList<DriverCandidate> candidates,
        IReadOnlyList<DeviceInfo> devices,
        InstallOptions options,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        var outcomes = new List<InstallOutcome>();
        var added = new List<AddedDriverPackage>();

        // Un candidat bloqué par la politique de pilotes n'est jamais installé, même si l'appelant l'a sélectionné.
        var installable = candidates.Count(c => c.BlockedReason is null);
        var intervention = new Intervention();
        HashSet<string>? storeBefore = null;
        if (!options.DryRun && installable > 0)
        {
            if (options.CreateRestorePoint)
            {
                progress?.Report(new InstallProgress(0, candidates.Count, "Préparation", "Création d'un point de restauration..."));
                intervention.RestorePointCreated = await SystemProtection.CreateRestorePointAsync($"SpokaPilote - {candidates.Count} pilote(s)", ct).ConfigureAwait(false);
            }
            if (options.BackupDrivers)
            {
                progress?.Report(new InstallProgress(0, candidates.Count, "Préparation", "Sauvegarde des pilotes actuels..."));
                intervention.BackupFolder = await SystemProtection.BackupDriversAsync(_retention, ct).ConfigureAwait(false);
            }
            storeBefore = InterventionHistory.SnapshotDriverStore();
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var c = candidates[i];
            var report = new Progress<string>(step => progress?.Report(new InstallProgress(i + 1, candidates.Count, c.Title, step)));
            InstallOutcome outcome;
            if (c.BlockedReason is not null)
            {
                outcome = new InstallOutcome(c, InstallStatus.Blocked, c.BlockedReason);
                Log.Warn($"[{outcome.Status}] {c.Title} {c.Version} — refusé par la politique de pilotes : {c.BlockedReason}");
                outcomes.Add(outcome);
                continue;
            }
            // Paquets attribués à cette seule étape : jamais ceux qu'un autre outil (Windows Update, Intune...) ajoute
            // entre deux étapes ou pendant la préparation.
            var stepBefore = storeBefore is null ? null : InterventionHistory.SnapshotDriverStore();
            _reportedNames.Clear();
            try
            {
                outcome = c.IsInformational
                    ? new InstallOutcome(c, InstallStatus.Skipped, $"Candidat informatif, non installable automatiquement : voir la page officielle {c.InfoUrl}")
                    : c.PackageKind switch
                    {
                        DriverPackageKind.WindowsUpdate => await InstallWindowsUpdateAsync(c, options, report, ct).ConfigureAwait(false),
                        DriverPackageKind.InfPackage => await InstallInfCandidateAsync(c, devices, options, report, ct).ConfigureAwait(false),
                        DriverPackageKind.VendorInstaller => await InstallVendorAsync(c, options, report, ct).ConfigureAwait(false),
                        _ => new InstallOutcome(c, InstallStatus.Skipped, "Type de paquet inconnu"),
                    };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SecurityException ex)
            {
                outcome = new InstallOutcome(c, InstallStatus.Blocked, ex.Message);
            }
            catch (Exception ex)
            {
                outcome = new InstallOutcome(c, InstallStatus.Failed, ex.Message);
            }

            if (stepBefore is not null)
                added.AddRange(AttributeStep(c, stepBefore, InterventionHistory.SnapshotDriverStore(), _reportedNames));

            var level = outcome.Succeeded || outcome.Status == InstallStatus.Skipped ? LogLevel.Info : LogLevel.Warning;
            Log.Write(level, $"[{outcome.Status}] {c.Title} {c.Version} — {outcome.Message}");
            outcomes.Add(outcome);
        }

        if (!options.DryRun && outcomes.Any(o => o.Status == InstallStatus.Installed))
        {
            // Demande à Windows de réévaluer les périphériques avec les pilotes fraîchement ajoutés.
            await PnpUtil.ScanDevicesAsync(ct).ConfigureAwait(false);
        }

        if (storeBefore is not null)
        {
            // Identité de chaque paquet ajouté (nom d'origine, éditeur, classe, version, dossier du magasin) : revérifiée
            // avant toute suppression par l'annulation.
            var described = InterventionHistory.DescribeStore(added.Select(a => a.PublishedName));
            foreach (var a in added)
            {
                if (!described.TryGetValue(a.PublishedName, out var d)) continue;
                a.OriginalName = d.OriginalName;
                a.PackageId = d.PackageId;
                a.Provider = d.Provider;
                a.ClassName = d.ClassName;
                a.Version = d.Version;
            }
            intervention.AddedPackages = added.Where(a => described.ContainsKey(a.PublishedName)).OrderBy(a => a.PublishedName, StringComparer.OrdinalIgnoreCase).ToList();
            intervention.AddedDriverPackages = intervention.AddedPackages.Select(a => a.PublishedName).ToList();
            intervention.CompletedAt = DateTime.Now;
            intervention.Items = outcomes.Select(o => new InterventionItem
            {
                Title = o.Candidate.Title,
                Version = o.Candidate.Version,
                Source = o.Candidate.SourceLabel,
                Status = o.Status.ToString(),
                Message = o.Message,
            }).ToList();
            InterventionHistory.Save(intervention);
            LastIntervention = intervention;
        }
        return outcomes;
    }

    /// <summary>Intervention enregistrée par le dernier appel à <see cref="InstallAsync"/> (null en simulation).</summary>
    public Intervention? LastIntervention { get; private set; }

    /// <summary>
    /// Paquets à attribuer à une étape (une proposition) : ceux dont pnputil a renvoyé le nom publié et qui n'existaient
    /// pas avant l'étape (un paquet « déjà présent » n'est pas à nous) ; à défaut de nom renvoyé (installeur constructeur,
    /// Windows Update), les paquets apparus pendant cette seule étape.
    /// </summary>
    internal static List<AddedDriverPackage> AttributeStep(DriverCandidate c, IReadOnlySet<string> before, IReadOnlySet<string> after, IEnumerable<string> reported)
    {
        var appeared = after.Where(n => !before.Contains(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var named = reported.Where(appeared.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var (names, origin) = named.Count > 0 ? (named, AddedPackageOrigins.PnpUtil) : (appeared.ToList(), AddedPackageOrigins.Step);
        return names.Where(PnpUtil.IsPublishedOemName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new AddedDriverPackage { PublishedName = n.ToLowerInvariant(), Origin = origin, Candidate = $"{c.Title} {c.Version}".Trim() })
            .ToList();
    }

    // ------------------------------------------------------------------ Windows Update

    private Task<InstallOutcome> InstallWindowsUpdateAsync(DriverCandidate c, InstallOptions options, IProgress<string> progress, CancellationToken ct)
    {
        if (c.Payload is not WindowsUpdatePayload payload)
            return Task.FromResult(new InstallOutcome(c, InstallStatus.Failed, "Mise à jour Windows Update introuvable (relancez l'analyse)."));
        if (options.DryRun)
            return Task.FromResult(new InstallOutcome(c, InstallStatus.Skipped, "Simulation : serait installé via Windows Update."));

        return Task.Run(() =>
        {
            dynamic session = payload.Session;
            dynamic update = payload.Update;
            var collType = Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!;
            dynamic coll = Activator.CreateInstance(collType)!;
            if (!(bool)update.EulaAccepted) update.AcceptEula();
            coll.Add(update);

            progress.Report("Téléchargement via Windows Update...");
            dynamic downloader = session.CreateUpdateDownloader();
            downloader.Updates = coll;
            dynamic downloadResult = downloader.Download();
            int dlCode = downloadResult.ResultCode;
            if (dlCode is not (2 or 3))
                return new InstallOutcome(c, InstallStatus.Failed, $"Échec du téléchargement Windows Update (code {dlCode}).");

            progress.Report("Installation via Windows Update...");
            dynamic installer = session.CreateUpdateInstaller();
            installer.Updates = coll;
            dynamic installResult = installer.Install();
            int code = installResult.GetUpdateResult(0).ResultCode;
            bool reboot = installResult.RebootRequired;
            return code is 2 or 3
                ? new InstallOutcome(c, InstallStatus.Installed, "Installé via Windows Update.", reboot)
                : new InstallOutcome(c, InstallStatus.Failed, $"Windows Update a renvoyé le code {code} (HRESULT 0x{(int)installResult.GetUpdateResult(0).HResult:X8}).");
        }, ct);
    }

    // ------------------------------------------------------------------ Paquets INF

    private async Task<InstallOutcome> InstallInfCandidateAsync(DriverCandidate c, IReadOnlyList<DeviceInfo> devices, InstallOptions options, IProgress<string> progress, CancellationToken ct)
    {
        var targets = devices.Where(d => c.TargetDeviceIds.Contains(d.InstanceId, StringComparer.OrdinalIgnoreCase)).ToList();
        var attempts = new[] { c }.Concat(c.Alternatives.Where(alt => alt.BlockedReason is null)).ToList();
        var reasons = new List<string>();
        // Toutes les tentatives écartées pour une raison de sécurité (entrée du catalogue contredite) : « bloqué », pas
        // « échec » (un échec compterait contre la version annoncée dans le disjoncteur des anneaux).
        var securityRefusals = 0;

        foreach (var attempt in attempts)
        {
            string folder;
            var fromCatalog = false;
            CatalogEntryDetails? details = null;
            if (attempt.LocalPath is not null)
            {
                folder = File.Exists(attempt.LocalPath) ? Path.GetDirectoryName(attempt.LocalPath)! : attempt.LocalPath;
            }
            else if (attempt.Source == DriverSourceKind.MicrosoftCatalog && attempt.SourceItemId is not null)
            {
                fromCatalog = true;
                // Titre et version viennent de la page de recherche (peut-être relayée par la console) : confirmés
                // directement chez Microsoft avant d'aller plus loin.
                progress.Report("Confirmation de l'entrée auprès de Microsoft...");
                var confirmation = await CatalogPackageCheck.ConfirmEntryAsync(attempt.SourceItemId, attempt.Title, attempt.Version, ct).ConfigureAwait(false);
                if (confirmation.Refusal is { } refusal)
                {
                    Log.Warn($"⚠ Entrée du catalogue Microsoft refusée ({attempt.Title} {attempt.Version}) : {refusal}.");
                    reasons.Add($"{attempt.Title} : {refusal}");
                    securityRefusals++;
                    continue;
                }
                details = confirmation.Details;
                progress.Report("Résolution du lien officiel Microsoft...");
                var urls = await MicrosoftCatalogClient.GetDownloadUrlsAsync(attempt.SourceItemId, ct).ConfigureAwait(false);
                var url = urls.FirstOrDefault(u => u.EndsWith(".cab", StringComparison.OrdinalIgnoreCase)) ?? urls.FirstOrDefault();
                if (url is null)
                {
                    reasons.Add($"{attempt.Title} : aucun fichier téléchargeable");
                    continue;
                }
                progress.Report("Téléchargement depuis Microsoft...");
                var file = await _downloader.DownloadAsync(url, sha1: MicrosoftCatalogClient.Sha1FromUrl(url),
                    progress: new Progress<DownloadProgress>(p => progress.Report(FormatDownload(p))), ct: ct, directory: Work.Path).ConfigureAwait(false);
                folder = Work.NewSubdirectory("x-" + attempt.SourceItemId);
                progress.Report("Extraction...");
                await Archive.ExtractAsync(file, folder, ct).ConfigureAwait(false);
            }
            else if (attempt.DownloadUrl is not null)
            {
                var file = await _downloader.DownloadAsync(attempt.DownloadUrl, attempt.FileName, attempt.Sha256, attempt.Sha1, attempt.Md5, ct: ct, directory: Work.Path).ConfigureAwait(false);
                folder = Work.NewSubdirectory("x-" + attempt.Id);
                await Archive.ExtractAsync(file, folder, ct).ConfigureAwait(false);
            }
            else
            {
                reasons.Add($"{attempt.Title} : aucune source");
                continue;
            }

            progress.Report("Vérification de la signature et de la compatibilité...");
            var verified = PackageVerifier.SelectVerifiedInfs(folder, attempt.LocalPath, targets, _system.Architecture, _system.OsVersion);
            var (infs, why) = (verified.Infs, verified.Reason);
            if (infs.Count == 0)
            {
                reasons.Add($"{attempt.Title} : {why}");
                continue;
            }
            if (fromCatalog)
            {
                // L'INF signé fait foi : il doit porter la version et l'éditeur annoncés (ceux qu'ont vus la politique et les anneaux).
                var (matching, mismatch) = CatalogPackageCheck.CheckInfs(infs, attempt.Title, attempt.Version, details);
                if (matching.Count == 0)
                {
                    reasons.Add($"{attempt.Title} : paquet refusé, {mismatch}");
                    securityRefusals++;
                    continue;
                }
                infs = matching;
            }

            if (options.DryRun)
                return new InstallOutcome(c, InstallStatus.Skipped, $"Simulation : {infs.Count} INF vérifié(s) ({string.Join(", ", infs.Select(Path.GetFileName))}).");

            var reboot = false;
            var messages = new List<string>();
            var anyOk = false;
            foreach (var inf in infs)
            {
                progress.Report($"Installation de {Path.GetFileName(inf)}...");
                var result = await PnpUtil.AddDriverAsync(inf, install: true, ct).ConfigureAwait(false);
                reboot |= result.ExitCode == PnpUtil.ErrorSuccessRebootRequired;
                if (PnpUtil.IsSuccess(result.ExitCode))
                {
                    _reportedNames.AddRange(PnpUtil.ParsePublishedNames(result.Combined));
                    anyOk = true;
                    messages.Add(result.ExitCode == PnpUtil.ErrorNoMoreItems
                        ? $"{Path.GetFileName(inf)} ajouté au magasin (Windows conserve le pilote actuel, jugé équivalent ou meilleur)"
                        : $"{Path.GetFileName(inf)} installé");
                }
                else
                {
                    messages.Add($"{Path.GetFileName(inf)} : pnputil code {result.ExitCode} {LastLine(result.Combined)}");
                }
            }
            if (anyOk) return new InstallOutcome(c, InstallStatus.Installed, string.Join(" ; ", messages), reboot);
            reasons.Add(string.Join(" ; ", messages));
        }

        var status = securityRefusals > 0 && securityRefusals == reasons.Count ? InstallStatus.Blocked : InstallStatus.Failed;
        return new InstallOutcome(c, status, string.Join(" | ", reasons));
    }

    // ------------------------------------------------------------------ Installeurs constructeurs

    private async Task<InstallOutcome> InstallVendorAsync(DriverCandidate c, InstallOptions options, IProgress<string> progress, CancellationToken ct)
    {
        if (c.DownloadUrl is null) return new InstallOutcome(c, InstallStatus.Failed, "Aucune URL de téléchargement.");
        progress.Report("Téléchargement depuis le site officiel du constructeur...");
        var file = await _downloader.DownloadAsync(c.DownloadUrl, c.FileName, c.Sha256, c.Sha1, c.Md5,
            new Progress<DownloadProgress>(p => progress.Report(FormatDownload(p))), ct, directory: Work.Path).ConfigureAwait(false);

        progress.Report("Vérification de la signature numérique...");
        var sig = SignatureVerifier.Verify(file);
        if (!sig.IsValid)
            throw new SecurityException($"Installeur refusé : {sig.Detail}.");
        if (!_trust.IsTrustedPublisher(sig.Signer))
            throw new SecurityException($"Installeur refusé : éditeur non approuvé ({sig.Signer}).");

        if (options.DryRun)
            return new InstallOutcome(c, InstallStatus.Skipped, $"Simulation : téléchargé et vérifié ({sig.Signer}).");

        var packageName = AppPaths.SafeName(c.Id.Replace(':', '_'));
        // Auto-extracteur NVIDIA en mode silencieux : destination obligatoirement directement sous %TEMP%
        // et inexistante (sinon codes 8 / 12) ; elle n'est donc pas créée à l'avance. Nom aléatoire : impossible à
        // préparer d'avance par un autre compte. Sinon : sous-dossier neuf du dossier de travail privé.
        var packagePath = c.ExtractToTempFolder
            ? Path.Combine(Path.GetTempPath(), $"SpokaPilote-{AppPaths.SafeName(packageName, 40)}-{DataProtection.RandomName(6)}")
            : Work.NewSubdirectory("pkg-" + packageName);
        try
        {
            return await RunVendorCommandsAsync(c, file, packagePath, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            if (c.ExtractToTempFolder)
            {
                try { if (Directory.Exists(packagePath)) Directory.Delete(packagePath, recursive: true); }
                catch (Exception ex) { Log.Debug($"Dossier temporaire non supprimé ({packagePath}) : {ex.Message}"); }
            }
        }
    }

    private static async Task<InstallOutcome> RunVendorCommandsAsync(DriverCandidate c, string file, string packagePath, IProgress<string> progress, CancellationToken ct)
    {
        var downloadDir = Path.GetDirectoryName(file)!;

        // %FILE% = installeur téléchargé, %PACKAGEPATH% = dossier d'extraction, %LOGPATH% = journaux de SpokaPilote.
        // Les commandes viennent des catalogues : forme contrôlée (VendorCommand) AVANT toute exécution, programme lancé
        // directement (jamais « cmd /c <ligne du catalogue> »).
        var silentTemplate = c.SilentCommand ?? $"\"{VendorCommand.FilePlaceholder}\"";
        foreach (var template in new[] { c.ExtractCommand, c.SilentCommand == VendorCommand.InfMarker ? null : silentTemplate })
        {
            if (!string.IsNullOrWhiteSpace(template) && VendorCommand.Validate(template) is { } problem)
                throw new SecurityException($"Commande d'installation refusée ({problem}) : {template}");
        }

        if (!string.IsNullOrWhiteSpace(c.ExtractCommand))
        {
            progress.Report("Extraction du paquet...");
            var extract = await RunVendorAsync(VendorCommand.Resolve(c.ExtractCommand, file, packagePath, downloadDir, packageAllowed: false), downloadDir, ct)
                .ConfigureAwait(false);
            if (!Directory.Exists(packagePath) || (extract.ExitCode != 0 && !Directory.EnumerateFileSystemEntries(packagePath).Any()))
                return new InstallOutcome(c, InstallStatus.Failed, $"Extraction échouée (code {extract.ExitCode}).");
        }

        progress.Report("Installation silencieuse...");
        if (c.SilentCommand == VendorCommand.InfMarker)
        {
            var r = await PnpUtil.AddDriversRecursiveAsync(packagePath, install: true, ct).ConfigureAwait(false);
            return PnpUtil.IsSuccess(r.ExitCode)
                ? new InstallOutcome(c, InstallStatus.Installed, "Pilotes INF installés.", r.ExitCode == PnpUtil.ErrorSuccessRebootRequired)
                : new InstallOutcome(c, InstallStatus.Failed, $"pnputil code {r.ExitCode} {LastLine(r.Combined)}");
        }

        var workDir = string.IsNullOrWhiteSpace(c.ExtractCommand) ? downloadDir : packagePath;
        var result = await RunVendorAsync(VendorCommand.Resolve(silentTemplate, file, packagePath, workDir, packageAllowed: true), workDir, ct)
            .ConfigureAwait(false);
        if (result.TimedOut) return new InstallOutcome(c, InstallStatus.Failed, "Délai d'installation dépassé.");
        if (c.NotApplicableExitCodes.Contains(result.ExitCode))
            return new InstallOutcome(c, InstallStatus.NotApplicable, $"Déjà à jour ou non applicable (code {result.ExitCode}).");
        if (c.SuccessExitCodes.Contains(result.ExitCode) || c.RebootExitCodes.Contains(result.ExitCode))
            return new InstallOutcome(c, InstallStatus.Installed, $"Installé (code {result.ExitCode}).",
                c.RebootRequired || c.RebootExitCodes.Contains(result.ExitCode));
        return new InstallOutcome(c, InstallStatus.Failed, $"L'installeur a renvoyé le code {result.ExitCode}.");
    }

    private static Task<ProcessResult> RunVendorAsync(VendorInvocation invocation, string workingDirectory, CancellationToken ct)
    {
        Log.Info($"Exécution : \"{invocation.FileName}\" {invocation.Arguments}");
        return ProcessRunner.RunAsync(invocation.FileName, invocation.Arguments, workingDirectory, TimeSpan.FromMinutes(45), ct);
    }

    private static string FormatDownload(DownloadProgress p) =>
        p.Total is > 0
            ? $"Téléchargement {p.Received * 100 / p.Total.Value} % ({p.Received / 1048576.0:0.0} / {p.Total.Value / 1048576.0:0.0} Mo)"
            : $"Téléchargement {p.Received / 1048576.0:0.0} Mo";

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
}
