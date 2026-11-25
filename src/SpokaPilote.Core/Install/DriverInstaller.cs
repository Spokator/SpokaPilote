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

    public DriverInstaller(Settings settings, SystemInfo system)
    {
        _trust = new TrustPolicy(settings);
        _downloader = new Downloader(_trust);
        _system = system;
    }

    public async Task<List<InstallOutcome>> InstallAsync(
        IReadOnlyList<DriverCandidate> candidates,
        IReadOnlyList<DeviceInfo> devices,
        InstallOptions options,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!options.DryRun && candidates.Any(c => c.BlockedReason is null) && !Elevation.IsAdministrator())
            throw new UnauthorizedAccessException("L'installation de pilotes nécessite les droits administrateur.");

        var outcomes = new List<InstallOutcome>();
        if (candidates.Count == 0) return outcomes;

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
                intervention.BackupFolder = await SystemProtection.BackupDriversAsync(ct).ConfigureAwait(false);
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
            try
            {
                outcome = c.PackageKind switch
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
            intervention.AddedDriverPackages = InterventionHistory.SnapshotDriverStore().Except(storeBefore, StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n).ToList();
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

        foreach (var attempt in attempts)
        {
            string folder;
            if (attempt.LocalPath is not null)
            {
                folder = File.Exists(attempt.LocalPath) ? Path.GetDirectoryName(attempt.LocalPath)! : attempt.LocalPath;
            }
            else if (attempt.Source == DriverSourceKind.MicrosoftCatalog && attempt.SourceItemId is not null)
            {
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
                    progress: new Progress<DownloadProgress>(p => progress.Report(FormatDownload(p))), ct: ct).ConfigureAwait(false);
                folder = Path.Combine(AppPaths.Extracted, AppPaths.SafeName(attempt.SourceItemId));
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                progress.Report("Extraction...");
                await Archive.ExtractAsync(file, folder, ct).ConfigureAwait(false);
            }
            else if (attempt.DownloadUrl is not null)
            {
                var file = await _downloader.DownloadAsync(attempt.DownloadUrl, attempt.FileName, attempt.Sha256, attempt.Sha1, attempt.Md5, ct: ct).ConfigureAwait(false);
                folder = Path.Combine(AppPaths.Extracted, AppPaths.SafeName(attempt.Id));
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
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

        return new InstallOutcome(c, InstallStatus.Failed, string.Join(" | ", reasons));
    }

    // ------------------------------------------------------------------ Installeurs constructeurs

    private async Task<InstallOutcome> InstallVendorAsync(DriverCandidate c, InstallOptions options, IProgress<string> progress, CancellationToken ct)
    {
        if (c.DownloadUrl is null) return new InstallOutcome(c, InstallStatus.Failed, "Aucune URL de téléchargement.");
        progress.Report("Téléchargement depuis le site officiel du constructeur...");
        var file = await _downloader.DownloadAsync(c.DownloadUrl, c.FileName, c.Sha256, c.Sha1, c.Md5,
            new Progress<DownloadProgress>(p => progress.Report(FormatDownload(p))), ct).ConfigureAwait(false);

        progress.Report("Vérification de la signature numérique...");
        var sig = SignatureVerifier.Verify(file);
        if (!sig.IsValid)
            throw new SecurityException($"Installeur refusé : {sig.Detail}.");
        if (!_trust.IsTrustedPublisher(sig.Signer))
            throw new SecurityException($"Installeur refusé : éditeur non approuvé ({sig.Signer}).");

        if (options.DryRun)
            return new InstallOutcome(c, InstallStatus.Skipped, $"Simulation : téléchargé et vérifié ({sig.Signer}).");

        var packagePath = Path.Combine(AppPaths.Extracted, AppPaths.SafeName(c.Id.Replace(':', '_')));
        if (Directory.Exists(packagePath)) Directory.Delete(packagePath, recursive: true);
        Directory.CreateDirectory(packagePath);
        var downloadDir = Path.GetDirectoryName(file)!;

        string Expand(string command) => command
            .Replace("%FILE%", file, StringComparison.OrdinalIgnoreCase)
            .Replace("%PACKAGEPATH%", packagePath, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(c.ExtractCommand))
        {
            progress.Report("Extraction du paquet...");
            var extract = await RunShellAsync(Expand(c.ExtractCommand), downloadDir, ct).ConfigureAwait(false);
            if (extract.ExitCode != 0 && !Directory.EnumerateFileSystemEntries(packagePath).Any())
                return new InstallOutcome(c, InstallStatus.Failed, $"Extraction échouée (code {extract.ExitCode}).");
        }

        progress.Report("Installation silencieuse...");
        if (c.SilentCommand == "@INF")
        {
            var r = await PnpUtil.AddDriversRecursiveAsync(packagePath, install: true, ct).ConfigureAwait(false);
            return PnpUtil.IsSuccess(r.ExitCode)
                ? new InstallOutcome(c, InstallStatus.Installed, "Pilotes INF installés.", r.ExitCode == PnpUtil.ErrorSuccessRebootRequired)
                : new InstallOutcome(c, InstallStatus.Failed, $"pnputil code {r.ExitCode} {LastLine(r.Combined)}");
        }

        var workDir = string.IsNullOrWhiteSpace(c.ExtractCommand) ? downloadDir : packagePath;
        var result = await RunShellAsync(Expand(c.SilentCommand ?? $"\"{file}\""), workDir, ct).ConfigureAwait(false);
        if (result.TimedOut) return new InstallOutcome(c, InstallStatus.Failed, "Délai d'installation dépassé.");
        if (c.NotApplicableExitCodes.Contains(result.ExitCode))
            return new InstallOutcome(c, InstallStatus.NotApplicable, $"Déjà à jour ou non applicable (code {result.ExitCode}).");
        if (c.SuccessExitCodes.Contains(result.ExitCode) || c.RebootExitCodes.Contains(result.ExitCode))
            return new InstallOutcome(c, InstallStatus.Installed, $"Installé (code {result.ExitCode}).",
                c.RebootRequired || c.RebootExitCodes.Contains(result.ExitCode));
        return new InstallOutcome(c, InstallStatus.Failed, $"L'installeur a renvoyé le code {result.ExitCode}.");
    }

    private static Task<ProcessResult> RunShellAsync(string command, string workingDirectory, CancellationToken ct) =>
        ProcessRunner.RunAsync(ProcessRunner.SystemTool("cmd.exe"), $"/d /s /c \"{command}\"", workingDirectory, TimeSpan.FromMinutes(45), ct);

    private static string FormatDownload(DownloadProgress p) =>
        p.Total is > 0
            ? $"Téléchargement {p.Received * 100 / p.Total.Value} % ({p.Received / 1048576.0:0.0} / {p.Total.Value / 1048576.0:0.0} Mo)"
            : $"Téléchargement {p.Received / 1048576.0:0.0} Mo";

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
}
