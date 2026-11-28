namespace SpokaPilote.Core.Printers;

public sealed class DiscoveredPrinter
{
    public required string Address { get; init; }
    public string? HostName { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? DeviceId1284 { get; set; }
    public string? SerialNumber { get; set; }
    public string? Location { get; set; }
    public string? Description { get; set; }
    public List<int> OpenPorts { get; } = new();
    public Uri? IppUri { get; set; }
    public bool SupportsIpp => IppUri is not null;
    public List<string> IdentifiedBy { get; } = new();
    /// <summary>Nom de l'imprimante Windows déjà configurée vers cette adresse, s'il y en a une.</summary>
    public string? InstalledAs { get; set; }

    public string DisplayName => !string.IsNullOrWhiteSpace(Model)
        ? (Manufacturer is not null && !Model.StartsWith(Manufacturer, StringComparison.OrdinalIgnoreCase) ? $"{Manufacturer} {Model}" : Model)
        : Description ?? $"Imprimante {Address}";

    public string Ports => string.Join(", ", OpenPorts.Select(p => p switch { 9100 => "RAW 9100", 631 => "IPP 631", 515 => "LPR 515", _ => p.ToString() }));
}

public enum PrinterDriverOptionKind
{
    /// <summary>Pilote déjà présent sur ce poste.</summary>
    Installed,
    /// <summary>Paquet INF du dépôt interne de l'entreprise.</summary>
    LocalRepository,
    /// <summary>Paquet signé WHQL du Microsoft Update Catalog.</summary>
    MicrosoftCatalog,
    /// <summary>Pilote de classe IPP de Windows : universel, intégré, sans téléchargement.</summary>
    IppClass,
    /// <summary>Lien vers la page officielle du constructeur (action manuelle).</summary>
    VendorPage,
}

public sealed class PrinterDriverOption
{
    public required PrinterDriverOptionKind Kind { get; init; }
    /// <summary>Nom exact du pilote Windows (connu ou à déterminer après extraction du paquet).</summary>
    public string? DriverName { get; init; }
    public required string Label { get; init; }
    public int Score { get; init; }
    public string? InfPath { get; init; }
    public string? CatalogUpdateId { get; init; }
    /// <summary>
    /// Variantes du même pilote dans le catalogue (une par version de Windows / architecture, souvent sans que le titre
    /// le précise) : essayées dans l'ordre jusqu'à trouver celle qui convient à ce poste.
    /// </summary>
    public List<string> CatalogVariantIds { get; init; } = new();
    /// <summary>Titre et version annoncés par la recherche du catalogue (confirmés chez Microsoft et sur l'INF avant installation).</summary>
    public string? CatalogTitle { get; init; }
    public string? CatalogVersion { get; init; }
    public string? Url { get; init; }
    public bool IsUniversal { get; init; }

    public string KindLabel => Kind switch
    {
        PrinterDriverOptionKind.Installed => "Déjà installé",
        PrinterDriverOptionKind.LocalRepository => "Dépôt interne",
        PrinterDriverOptionKind.MicrosoftCatalog => "Microsoft (WHQL)",
        PrinterDriverOptionKind.IppClass => "Pilote IPP Windows",
        PrinterDriverOptionKind.VendorPage => "Site constructeur",
        _ => Kind.ToString(),
    };

    public override string ToString() => $"[{KindLabel}] {Label}";
}

public sealed class PrinterVendor
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Match { get; set; } = new();
    public List<string> UniversalDrivers { get; set; } = new();
    public List<string> CatalogQueries { get; set; } = new();
    public string? SupportUrl { get; set; }
}

public sealed class PrinterVendorCatalogFile
{
    public int Version { get; set; }
    public List<PrinterVendor> Vendors { get; set; } = new();
}
