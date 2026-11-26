using System.Globalization;

namespace SpokaPilote.LoadTest;

/// <summary>Pilote proposé dans un rapport (forme de DriverCandidate : libellés de source et de type du poste).</summary>
public sealed record GenCandidate(string Title, string Version, string Source, string Kind, string Category, string Device, string DeviceId,
    string? BlockedReason, long SizeBytes, string DownloadUrl);

/// <summary>
/// Parc « réaliste » pour la diffusion par anneaux : 50 modèles, 1 500 familles de pilotes partagées par les modèles
/// (300 familles Windows Update communes à plusieurs modèles — chipset, graphique, Wi-Fi... — et 24 familles propres à
/// chaque modèle — BIOS, firmwares, pilotes du catalogue constructeur), chaque famille publiant une nouvelle version tous
/// les 3 semaines à 6 mois (≈ 100 nouvelles versions par semaine sur le catalogue). Un poste se voit proposer la dernière
/// version des familles de son modèle sur lesquelles il est en retard (≈ 50 %), soit 15 à 30 propositions.
/// Tout est déterministe (mêmes poste, jour et graine → mêmes propositions).
/// </summary>
public sealed class RealisticFleet
{
    public sealed record Model(string Manufacturer, string Name, string? Friendly, string? OemSource, int Weight);

    /// <summary>Famille : format du titre ({0} = version), source, type, rythme de publication.</summary>
    public sealed record Family(int Id, string TitleFormat, string Source, string Kind, string Category, int Major, int Minor, int Build,
        int RevisionBase, int RevisionStep, int PeriodDays, int Phase, int? AltMajor);

    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public IReadOnlyList<Model> Models { get; }
    public IReadOnlyList<Family> Families { get; }
    /// <summary>Familles de chaque modèle (identifiants), et branche (version majeure secondaire) utilisée par ce modèle.</summary>
    private readonly List<(int Family, bool AltBranch)>[] _byModel;

    public RealisticFleet()
    {
        Models = BuildModels();
        var rng = new Random(2026);
        var families = new List<Family>();
        _byModel = Models.Select(_ => new List<(int, bool)>()).ToArray();

        // 300 familles Windows Update communes : « Intel - Display - 31.0.101.5590 », partagées par 1 à 40 modèles.
        string[] vendors =
        [
            "Intel", "Intel Corporation", "Realtek", "Realtek Semiconductor Corp.", "Synaptics", "Goodix", "Qualcomm", "NVIDIA", "Advanced Micro Devices, Inc.",
            "Broadcom", "ELAN", "Cirrus Logic", "MediaTek", "Logitech", "Microsoft", "Lenovo", "HP Inc.", "Dell Inc.", "Thunderbolt(TM)", "Bosch",
            "STMicroelectronics", "Alps Alpine", "Conexant", "Waves", "Dolby",
        ];
        string[] classes =
        [
            "Display", "System", "Net", "Bluetooth", "MEDIA", "Extension", "SoftwareComponent", "HIDClass", "Camera", "Biometric", "USB",
            "Firmware", "SCSIAdapter", "Ports", "Sensor", "AudioProcessingObject", "SmartCardReader", "Monitor", "Keyboard", "Mouse", "Image", "Printer",
        ];
        var pairs = vendors.SelectMany(v => classes.Select(c => (v, c))).OrderBy(_ => rng.Next()).Take(300).ToList();
        for (var i = 0; i < pairs.Count; i++)
        {
            var (vendor, cls) = pairs[i];
            var period = 21 + rng.Next(100);
            var f = new Family(families.Count, $"{vendor} - {cls} - {{0}}", "Windows Update", rng.Next(10) == 0 ? "Optionnel" : "Mise à jour", cls,
                rng.Next(1, 32), rng.Next(10), rng.Next(100, 999), rng.Next(1000, 6000), 1 + rng.Next(9), period, rng.Next(period),
                rng.Next(10) == 0 ? rng.Next(1, 32) : null);
            families.Add(f);
            var links = Math.Clamp((int)(40 / Math.Pow(1 + i, 0.6)), 1, 40);
            foreach (var m in Enumerable.Range(0, Models.Count).OrderBy(_ => rng.Next()).Take(links))
                _byModel[m].Add((f.Id, f.AltMajor is not null && m % 2 == 1));
        }

        // 24 familles propres à chaque modèle (catalogue constructeur ou Windows Update) : BIOS, firmwares, pilotes.
        (string Name, string Kind, string Category)[] components =
        [
            ("System BIOS", "BIOS / Firmware", "BIOS"), ("Thunderbolt Firmware", "BIOS / Firmware", "Firmware"), ("Embedded Controller", "BIOS / Firmware", "Firmware"),
            ("Chipset Driver", "Mise à jour", "Chipset"), ("Graphics Driver", "Mise à jour", "Video"), ("Audio Driver", "Mise à jour", "Audio"),
            ("Wireless LAN Driver", "Mise à jour", "Network"), ("Bluetooth Driver", "Mise à jour", "Network"), ("Ethernet Driver", "Mise à jour", "Network"),
            ("Card Reader Driver", "Mise à jour", "Storage"), ("Touchpad Driver", "Mise à jour", "Input"), ("Camera Driver", "Mise à jour", "Camera"),
            ("Fingerprint Driver", "Mise à jour", "Security"), ("Serial IO Driver", "Mise à jour", "Chipset"), ("Management Engine", "Mise à jour", "Chipset"),
            ("Power Manager", "Optionnel", "Application"), ("Dock Firmware", "BIOS / Firmware", "Firmware"), ("Rapid Storage Driver", "Mise à jour", "Storage"),
            ("Sensor Driver", "Mise à jour", "Chipset"), ("NFC Driver", "Mise à jour", "Security"), ("Hotkey Driver", "Mise à jour", "Input"),
            ("Optimizer", "Optionnel", "Application"), ("Monitor Driver", "Mise à jour", "Monitor"), ("Smart Card Driver", "Mise à jour", "Security"),
        ];
        for (var m = 0; m < Models.Count; m++)
        {
            var model = Models[m];
            foreach (var (name, kind, category) in components)
            {
                var firmware = kind == "BIOS / Firmware";
                var period = firmware ? 60 + rng.Next(120) : 30 + rng.Next(90);
                var f = new Family(families.Count, $"{model.Friendly ?? model.Name} - {name} - {{0}}", model.OemSource ?? "Windows Update", kind, category,
                    firmware ? 1 : rng.Next(1, 30), rng.Next(firmware ? 40 : 10), firmware ? 0 : rng.Next(100, 999), firmware ? 0 : rng.Next(100, 900),
                    1 + rng.Next(5), period, rng.Next(period), null);
                families.Add(f);
                _byModel[m].Add((f.Id, false));
            }
        }
        Families = families;
    }

    private static List<Model> BuildModels()
    {
        var list = new List<Model>();
        void Add(string manufacturer, string? oem, int weight, params string[] names)
        {
            foreach (var n in names)
            {
                var parts = n.Split('|');
                list.Add(new Model(manufacturer, parts[0], parts.Length > 1 ? parts[1] : null, oem, weight));
            }
        }
        Add("Dell Inc.", "Dell (catalogue officiel)", 12, "Latitude 5440", "Latitude 5540", "Latitude 7440", "OptiPlex 7010");
        Add("Dell Inc.", "Dell (catalogue officiel)", 4, "Latitude 3440", "Latitude 3540", "Latitude 7640", "Latitude 9440", "OptiPlex 3000", "OptiPlex 5000",
            "OptiPlex 7020", "Precision 3581", "Precision 3680", "Precision 5690");
        Add("HP", "HP (catalogue officiel)", 10, "HP EliteBook 840 G10", "HP EliteBook 840 G9", "HP ProDesk 400 G9");
        Add("HP", "HP (catalogue officiel)", 3, "HP EliteBook 640 G10", "HP EliteBook 840 G8", "HP EliteBook 860 G10", "HP EliteBook 1040 G10",
            "HP ProBook 450 G9", "HP ProBook 450 G10", "HP ProDesk 400 G7", "HP EliteDesk 800 G6", "HP EliteDesk 800 G8", "HP ZBook Firefly 14 G10");
        Add("LENOVO", "Lenovo (catalogue officiel)", 10, "21HD|ThinkPad T14 Gen 4", "21AH|ThinkPad T14 Gen 3", "11T3|ThinkCentre M70q Gen 3");
        Add("LENOVO", "Lenovo (catalogue officiel)", 3, "20W0|ThinkPad T14 Gen 2", "21HF|ThinkPad T16 Gen 2", "21CB|ThinkPad X1 Carbon Gen 10",
            "21HM|ThinkPad X1 Carbon Gen 11", "21C1|ThinkPad L14 Gen 3", "21H1|ThinkPad L14 Gen 4", "21JK|ThinkPad E14 Gen 5", "12E3|ThinkCentre M70q Gen 4",
            "11U5|ThinkCentre M90q Gen 3", "30FN|ThinkStation P360");
        Add("Microsoft Corporation", null, 2, "Surface Laptop 5", "Surface Pro 9", "Surface Laptop 6");
        Add("ASUSTeK COMPUTER INC.", null, 1, "ExpertBook B1500CEAE", "ExpertBook B5402CVA");
        Add("Acer", null, 1, "TravelMate P214-54", "TravelMate P216-51");
        Add("Fujitsu", null, 1, "LIFEBOOK U7411", "LIFEBOOK U7412");
        Add("Dynabook Inc.", null, 1, "Portégé X30L-K");
        return list;
    }

    /// <summary>Numéro de la dernière version publiée d'une famille à une date.</summary>
    public static int Release(Family f, DateTimeOffset at) => (int)Math.Floor(((at - Epoch).TotalDays + f.Phase) / f.PeriodDays);

    public static string VersionText(Family f, int release, bool alt)
    {
        var major = alt ? f.AltMajor!.Value : f.Major;
        return f.Kind == "BIOS / Firmware" && f.Build == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{major}.{f.Minor + release}.0")
            : string.Create(CultureInfo.InvariantCulture, $"{major}.{f.Minor}.{f.Build}.{f.RevisionBase + release * f.RevisionStep}");
    }

    private static uint Hash(int a, int b, int c)
    {
        unchecked
        {
            var h = 2166136261u;
            foreach (var x in new[] { a, b, c }) { h ^= (uint)x; h *= 16777619u; h ^= h >> 13; }
            return h;
        }
    }

    /// <summary>Propositions du poste <paramref name="machine"/> (modèle <paramref name="modelIndex"/>) à la date du rapport.</summary>
    public List<GenCandidate> Candidates(int machine, int modelIndex, DateTimeOffset at)
    {
        var list = new List<GenCandidate>();
        foreach (var (id, alt) in _byModel[modelIndex])
        {
            var f = Families[id];
            var release = Release(f, at);
            var h = Hash(machine, id, release);
            if (h % 100 >= 50) continue; // à jour sur cette famille
            if (h / 100 % 20 == 0) release--; // catalogue en retard : version précédente proposée
            var version = VersionText(f, release, alt);
            var title = string.Format(CultureInfo.InvariantCulture, f.TitleFormat, version);
            var device = $"{f.Category} {id}";
            list.Add(new GenCandidate(title, version, f.Source, f.Kind, f.Category, device, $"PCI\\VEN_{id % 0x10000:X4}&DEV_{(id * 7) % 0x10000:X4}\\{machine:X}&{id:X}",
                null, 200_000 + (long)(h % 400_000_000), $"https://catalog.update.microsoft.com/download/{id:x8}/{release:x8}.cab"));
        }
        return list;
    }

    /// <summary>Indice de modèle d'un poste (tirage pondéré déterministe).</summary>
    public int ModelOf(int machine)
    {
        var total = Models.Sum(m => m.Weight);
        var x = (int)(Hash(machine, 77, 1) % (uint)total);
        for (var i = 0; i < Models.Count; i++)
        {
            x -= Models[i].Weight;
            if (x < 0) return i;
        }
        return Models.Count - 1;
    }
}
