using SpokaPilote.App.Localization;
using SpokaPilote.Core.Engine;
using SpokaPilote.Core.Models;

namespace SpokaPilote.App.ViewModels;

/// <summary>
/// Accueil : « Analyser ce poste → Installer les bons pilotes ». Identité du poste, note de santé et ses principaux
/// constats, état du BIOS, et une seule action principale dont le libellé suit l'état (Analyser / Installer N pilotes /
/// Tout est à jour). Rien de technique ici : le détail est dans les autres pages.
/// </summary>
public sealed class HomeViewModel : ObservableObject
{
    public HomeViewModel(MainViewModel shell)
    {
        Shell = shell;
        PrimaryCommand = new RelayCommand(OnPrimary, () => !Shell.IsBusy && State is "scan" or "install");
        ShowDetailsCommand = new RelayCommand(ShowDetails);
        DismissMessageCommand = new RelayCommand(ClearMessage);
    }

    public MainViewModel Shell { get; }
    private ScanResult? Scan => Shell.LastScan;
    private SystemInfo? Machine => Shell.Machine;

    /// <summary>Met à jour tout l'écran (appelé à chaque changement d'analyse, de sélection ou d'opération).</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    // ------------------------------------------------------------------ identité du poste

    public string MachineModel
    {
        get
        {
            if (Machine is not { } m) return Shell.DisplayComputerName;
            var vendor = m.Vendor switch
            {
                OemVendor.Dell => "Dell",
                OemVendor.HP => "HP",
                OemVendor.Lenovo => "Lenovo",
                OemVendor.Microsoft => "Microsoft",
                _ => m.Manufacturer.Trim(),
            };
            var model = (string.IsNullOrWhiteSpace(m.FriendlyModel) ? m.Model : m.FriendlyModel).Trim();
            if (model.Length == 0) return vendor.Length == 0 ? Shell.DisplayComputerName : vendor;
            return model.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) || vendor.Length == 0 ? model : $"{vendor} {model}";
        }
    }

    /// <summary>« PC-COMPTA · Windows 11 Professionnel 24H2 · 64 bits (x64) ».</summary>
    public string MachineLine
    {
        get
        {
            var parts = new List<string> { Shell.DisplayComputerName };
            if (Machine is { } m)
            {
                var os = m.OsCaption.Replace("Microsoft ", "", StringComparison.OrdinalIgnoreCase).Trim();
                if (os.Length > 0) parts.Add(string.IsNullOrWhiteSpace(m.OsDisplayVersion) ? os : $"{os} {m.OsDisplayVersion}");
                parts.Add(m.Architecture.ToLowerInvariant() switch
                {
                    "arm64" => Loc.T("ARM 64 bits"),
                    "x86" => Loc.T("32 bits (x86)"),
                    _ => Loc.T("64 bits (x64)"),
                });
            }
            return string.Join("   ·   ", parts);
        }
    }

    /// <summary>Détails à la demande : numéro de série, BIOS, version exacte de Windows.</summary>
    public string MachineDetails
    {
        get
        {
            if (Machine is not { } m) return Loc.T("Identité du poste en cours de lecture...");
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(m.Manufacturer)) lines.Add(Loc.F("Fabricant : {0}", m.Manufacturer));
            if (!string.IsNullOrWhiteSpace(m.Model)) lines.Add(Loc.F("Modèle : {0}", m.DisplayModel));
            if (m.SerialNumber is not null) lines.Add(Loc.F("N° de série : {0}", Shell.DisplayAlias is null ? m.SerialNumber : "—"));
            if (m.BiosVersion is not null)
                lines.Add(Loc.F("BIOS : {0}", m.BiosVersion) + (m.BiosReleaseDate is { } d ? $" ({d:dd/MM/yyyy})" : ""));
            lines.Add(Loc.F("Windows : version {0}", m.OsVersion));
            return string.Join("\n", lines);
        }
    }

    // ------------------------------------------------------------------ note de santé

    public bool HasScan => Scan is not null;
    public bool HasHealth => Scan?.Health is not null;
    public string Grade => Scan?.Health?.Grade ?? "?";
    /// <summary>Couleur de la note : good (A, B), warn (C), bad (D, E), none (pas d'analyse).</summary>
    public string GradeKind => Scan?.Health?.Grade switch
    {
        null => "none",
        "A" or "B" => "good",
        "C" => "warn",
        _ => "bad",
    };
    public string GradeLabel => Scan?.Health is { } h ? Loc.T(h.GradeLabel) : Scan is null ? Loc.T("Non analysé") : Loc.T("Note indisponible");
    public string ScoreText => Scan?.Health is { } h ? Loc.F("{0}/100", h.Score) : "";
    public string GradeAccessibleName => Scan?.Health is { } h
        ? Loc.F("Note de santé des pilotes : {0}, {1}, {2} sur 100", h.Grade, Loc.T(h.GradeLabel), h.Score)
        : Loc.T("Note de santé des pilotes : poste non analysé");

    private List<FindingItem> Findings =>
        Scan?.Health?.Findings.OrderBy(f => f.Severity).ThenByDescending(f => f.Penalty).Select(f => new FindingItem(f)).ToList() ?? new();
    /// <summary>Les 3 constats les plus importants, en langage simple.</summary>
    public List<FindingItem> TopFindings => Findings.Take(3).ToList();
    public List<FindingItem> AllFindings => Findings;
    public bool HasMoreFindings => Findings.Count > 3;
    public string AllFindingsHeader => Loc.F("Voir les {0} constats", Findings.Count);
    public bool HasNoFindings => HasHealth && Findings.Count == 0;

    // ------------------------------------------------------------------ BIOS et firmwares (information)

    private Core.Firmware.BiosStatus? Bios => Scan?.Firmware?.Bios;

    /// <summary>« BIOS : 1.12.0 installé, 1.18.0 disponible (critique) » — rien n'est installé sans l'option BIOS / firmware.</summary>
    public string BiosStatusText
    {
        get
        {
            if (Scan is null) return "";
            var installed = Scan.System.BiosVersion ?? Loc.T("version inconnue");
            if (Bios is not { } b)
            {
                return Scan.System.Vendor is OemVendor.Dell or OemVendor.HP or OemVendor.Lenovo
                    ? Loc.F("BIOS : {0} installé — non vérifié (catalogue {1} non interrogé ou indisponible)", installed, Scan.System.Vendor)
                    : Loc.F("BIOS : {0} installé — non vérifié (comparaison disponible pour Dell, HP et Lenovo)", installed);
            }
            return b.State switch
            {
                Core.Firmware.BiosState.Outdated => Loc.F("BIOS : {0} installé, {1} disponible ({2})", installed, b.LatestVersion, Loc.T(b.ImportanceText)),
                Core.Firmware.BiosState.UpToDate => Loc.F("BIOS : {0} installé, à jour (dernière version {1} : {2})", installed, b.Vendor, b.LatestVersion),
                Core.Firmware.BiosState.InstalledNewer => Loc.F("BIOS : {0} installé, plus récent que le catalogue {1} ({2})", installed, b.Vendor, b.LatestVersion),
                Core.Firmware.BiosState.NotInCatalog => Loc.F("BIOS : {0} installé — aucun BIOS pour ce modèle dans le catalogue {1}", installed, b.Vendor),
                _ => Loc.F("BIOS : {0} installé — impossible de comparer avec le catalogue {1}", installed, b.Vendor)
                     + (b.Reason is null ? "" : $" ({b.Reason})"),
            };
        }
    }
    /// <summary>Icône de la ligne BIOS : success (à jour), warning (mise à jour disponible), info (non vérifié).</summary>
    public string BiosKind => Bios?.State switch
    {
        Core.Firmware.BiosState.Outdated => "warning",
        Core.Firmware.BiosState.UpToDate or Core.Firmware.BiosState.InstalledNewer => "success",
        _ => "info",
    };
    public bool IsBiosOutdated => Bios?.State == Core.Firmware.BiosState.Outdated;
    public string BiosWarningText => IsBiosOutdated ? Loc.T(Core.Firmware.BiosStatus.UpdateWarning) : "";
    public string? BiosLink => Bios?.Link;
    public bool HasBiosLink => IsBiosOutdated && BiosLink is not null;
    /// <summary>Firmwares de stations d'accueil / Thunderbolt détectés (information, jamais installés ici).</summary>
    public string FirmwareComponentsText => Scan?.Firmware is not { } fw ? "" :
        string.Join("\n", fw.Components.Select(c => Loc.F("{0} : {1} {2} disponible (information, non installé)", Loc.T(c.KindLabel), c.Title, c.Version)));

    // ------------------------------------------------------------------ sources indisponibles

    public bool HasSourceWarnings => Scan?.SourceErrors.Count > 0;
    public string SourceWarningText => Scan is null || Scan.SourceErrors.Count == 0 ? "" :
        Loc.F("Certaines sources n'ont pas répondu ({0}) : la liste des pilotes peut être incomplète. Relancez l'analyse plus tard.",
            string.Join(", ", Scan.SourceErrors.Keys));

    // ------------------------------------------------------------------ action principale

    /// <summary>busy, scan (à analyser), install (pilotes cochés à installer), uptodate (rien à installer).</summary>
    public string State =>
        Shell.IsBusy && Shell.BusyKind is BusyKind.Scan or BusyKind.Install ? "busy"
        : Scan is null ? "scan"
        : Shell.Drivers.SelectedCount > 0 ? "install"
        : "uptodate";

    public bool IsUpToDate => State == "uptodate";
    public bool ShowPrimaryButton => !IsUpToDate;

    public string PrimaryLabel => Shell.BusyKind switch
    {
        BusyKind.Scan => Loc.T("Analyse en cours..."),
        BusyKind.Install => Loc.T("Installation en cours..."),
        _ => State switch
        {
            "scan" => Loc.T("Analyser ce poste"),
            "install" => Shell.Drivers.SelectedCount == 1 ? Loc.T("Installer 1 pilote") : Loc.F("Installer {0} pilotes", Shell.Drivers.SelectedCount),
            _ => Loc.T("Tout est à jour"),
        },
    };

    public string PrimaryHelp => State switch
    {
        "install" => Loc.T("Installer les pilotes cochés dans la page Pilotes"),
        _ => Loc.T("Analyser ce poste (F5)"),
    };

    public string Headline
    {
        get
        {
            if (Scan is null) return Loc.T("Analysez ce poste pour trouver les pilotes qui manquent.");
            var missing = Scan.Candidates.Count(c => c.Kind == CandidateKind.MissingDriver && c.BlockedReason is null);
            var updates = Scan.Candidates.Count(c => c.Kind == CandidateKind.Update && c.BlockedReason is null);
            var unresolved = Scan.UnresolvedDevices.Count();
            if (missing == 0 && updates == 0)
            {
                return unresolved == 0
                    ? Loc.T("Tout est en ordre : aucun pilote manquant.")
                    : Loc.F("Aucun pilote à installer, mais {0} périphérique(s) en erreur sans solution automatique.", unresolved);
            }
            var parts = new List<string>();
            if (missing > 0) parts.Add(missing == 1 ? Loc.T("1 pilote manquant") : Loc.F("{0} pilotes manquants", missing));
            if (updates > 0) parts.Add(updates == 1 ? Loc.T("1 mise à jour disponible") : Loc.F("{0} mises à jour disponibles", updates));
            return string.Join(Loc.T(" et "), parts) + ".";
        }
    }

    public string SubHeadline => State switch
    {
        "scan" or "busy" when Scan is null => Loc.T("L'analyse ne modifie rien. Les pilotes viennent uniquement de sources officielles et sont vérifiés avant toute installation."),
        "install" => Loc.T("Chaque pilote est téléchargé depuis sa source officielle et vérifié (signature, compatibilité) avant d'être installé.")
                     + (Shell.IsAdmin ? "" : " " + Loc.T("Les droits administrateur seront demandés.")),
        _ when Scan is not null && Scan.UnresolvedDevices.Any() => Loc.T("La page Périphériques propose une réparation guidée pour chacun."),
        _ => Loc.F("Dernière analyse : {0:HH:mm}. Relancez-la après un changement de matériel (F5).", Scan?.Timestamp ?? DateTime.Now),
    };

    public RelayCommand PrimaryCommand { get; }
    public RelayCommand ShowDetailsCommand { get; }

    private Task OnPrimary()
    {
        if (State == "scan") Shell.ScanCommand.Execute(null);
        else if (State == "install") Shell.Drivers.InstallCommand.Execute(null);
        return Task.CompletedTask;
    }

    /// <summary>« Voir le détail » : la liste des pilotes, ou les périphériques s'il n'y a rien à installer mais des erreurs.</summary>
    private Task ShowDetails()
    {
        var goDevices = Scan is not null && Scan.Candidates.Count == 0 && Scan.ProblemDevices.Any();
        Shell.Navigate(goDevices ? "peripheriques" : "pilotes");
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ résultat de la dernière opération

    private string _message = "";
    private string _messageKind = "info";
    public string Message { get => _message; private set => Set(ref _message, value); }
    /// <summary>info, success, warning, error.</summary>
    public string MessageKind { get => _messageKind; private set => Set(ref _messageKind, value); }
    public bool HasMessage => Message.Length > 0;
    public RelayCommand DismissMessageCommand { get; }

    public void ShowMessage(string kind, string text)
    {
        MessageKind = kind;
        Message = text;
        OnPropertyChanged(nameof(HasMessage));
    }

    public void ClearMessage() => ShowMessage("info", "");
}
