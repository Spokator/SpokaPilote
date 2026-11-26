// Copie figée de src/SpokaPilote.Core/Models/DriverCandidate.cs à l'étiquette v3.1.0 (git show v3.1.0:...), espace de noms renommé.
// Sert à prouver que la politique servie par la console 4.0 est comprise par un poste 3.1 : NE PAS MODIFIER.
namespace SpokaPilote.Server.Tests.Legacy31.Models;

public enum DriverSourceKind { WindowsUpdate, MicrosoftCatalog, Dell, HP, Lenovo, LocalRepository, Nvidia, Intel, Amd }

public enum DriverPackageKind
{
    /// <summary>Mise à jour installée via l'agent Windows Update.</summary>
    WindowsUpdate,
    /// <summary>Paquet INF (.cab du catalogue Microsoft, dossier du dépôt) installé via pnputil.</summary>
    InfPackage,
    /// <summary>Installeur constructeur signé (.exe / .msi) exécuté en mode silencieux.</summary>
    VendorInstaller,
}

public enum CandidateKind { MissingDriver, Update, Optional, Firmware }

public sealed class DriverCandidate
{
    public required string Id { get; init; }
    public required DriverSourceKind Source { get; init; }
    public required string Title { get; init; }
    public DriverPackageKind PackageKind { get; init; }
    public CandidateKind Kind { get; set; } = CandidateKind.Update;

    public string? Manufacturer { get; init; }
    public string? Category { get; init; }
    public string? Version { get; init; }
    public DateTime? Date { get; init; }
    public long? SizeBytes { get; init; }

    /// <summary>Identifiant matériel qui a permis l'association (le plus spécifique trouvé).</summary>
    public string? MatchedHardwareId { get; set; }
    /// <summary>Rang de correspondance façon Windows : plus petit = meilleur (0 = ID matériel le plus spécifique).</summary>
    public int MatchRank { get; set; } = int.MaxValue;
    public List<string> TargetDeviceIds { get; } = new();
    public List<string> TargetDeviceNames { get; } = new();

    public string? DownloadUrl { get; set; }
    public string? Sha256 { get; init; }
    public string? Sha1 { get; init; }
    public string? Md5 { get; init; }
    /// <summary>Dossier ou fichier local (dépôt) — pas de téléchargement nécessaire.</summary>
    public string? LocalPath { get; init; }
    /// <summary>Nom du fichier à télécharger (si différent de la fin de l'URL).</summary>
    public string? FileName { get; init; }
    /// <summary>Commande silencieuse (installeurs constructeurs). %PACKAGEPATH% = dossier d'extraction.</summary>
    public string? SilentCommand { get; init; }
    /// <summary>Commande d'extraction préalable (Lenovo/HP). %PACKAGEPATH% = dossier d'extraction.</summary>
    public string? ExtractCommand { get; init; }
    /// <summary>
    /// Extraire dans un dossier placé directement sous %TEMP% (%TEMP%\SpokaPilote-&lt;id&gt;, non créé à l'avance) plutôt que
    /// dans %ProgramData% : l'auto-extracteur NVIDIA refuse toute autre destination en mode silencieux.
    /// Dossier supprimé après l'installation.
    /// </summary>
    public bool ExtractToTempFolder { get; init; }
    public int[] SuccessExitCodes { get; init; } = { 0, 3010, 1641 };
    public int[] RebootExitCodes { get; init; } = { 3010, 1641 };
    /// <summary>Codes signifiant « déjà installé / non applicable » (ex. Dell DUP 3 et 5).</summary>
    public int[] NotApplicableExitCodes { get; init; } = Array.Empty<int>();
    public bool RebootRequired { get; init; }
    public string? InfoUrl { get; init; }
    /// <summary>Identifiant propre à la source (UpdateID Windows Update, GUID du catalogue, packageID Dell, numéro SoftPaq...).</summary>
    public string? SourceItemId { get; init; }

    /// <summary>Alternatives de la même source, essayées si la première ne convient pas (ex. résultats du catalogue Microsoft).</summary>
    public List<DriverCandidate> Alternatives { get; } = new();

    /// <summary>Objet natif propre à la source (ex. IUpdate de Windows Update).</summary>
    internal object? Payload { get; init; }

    public string SourceLabel => Source switch
    {
        DriverSourceKind.WindowsUpdate => "Windows Update",
        DriverSourceKind.MicrosoftCatalog => "Microsoft Update Catalog",
        DriverSourceKind.Dell => "Dell (catalogue officiel)",
        DriverSourceKind.HP => "HP (catalogue officiel)",
        DriverSourceKind.Lenovo => "Lenovo (catalogue officiel)",
        DriverSourceKind.LocalRepository => "Dépôt local",
        DriverSourceKind.Nvidia => "NVIDIA (site officiel)",
        DriverSourceKind.Intel => "Intel (site officiel)",
        DriverSourceKind.Amd => "AMD (site officiel)",
        _ => Source.ToString(),
    };

    /// <summary>
    /// Candidat informatif : lien vers la page officielle du fabricant (<see cref="InfoUrl"/>), sans paquet
    /// téléchargeable automatiquement. Jamais coché ni installé (ex. pilotes graphiques Intel et AMD).
    /// </summary>
    public bool IsInformational { get; init; }

    /// <summary>Motif de blocage par la politique de pilotes (null = autorisé). Un candidat bloqué n'est jamais installé.</summary>
    public string? BlockedReason { get; set; }

    public string KindLabel => Kind switch
    {
        CandidateKind.MissingDriver => "Pilote manquant",
        CandidateKind.Update => "Mise à jour",
        CandidateKind.Optional => "Optionnel",
        CandidateKind.Firmware => "BIOS / Firmware",
        _ => Kind.ToString(),
    };

    public string SizeLabel => SizeBytes is null or <= 0 ? "" :
        SizeBytes < 1024 * 1024 ? $"{SizeBytes / 1024.0:0} Ko" :
        SizeBytes < 1024L * 1024 * 1024 ? $"{SizeBytes / 1024.0 / 1024:0.0} Mo" :
        $"{SizeBytes / 1024.0 / 1024 / 1024:0.00} Go";

    public override string ToString() => $"{Title} {Version} ({SourceLabel})";
}
