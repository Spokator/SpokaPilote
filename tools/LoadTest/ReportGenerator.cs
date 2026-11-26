using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpokaPilote.LoadTest;

/// <summary>
/// Génère des rapports « spokapilote scan --json » réalistes (forme de ScanReport, JSON indenté comme le client) :
/// ~200 périphériques, quelques périphériques en erreur tirés d'un catalogue à distribution de Zipf (certains matériels
/// sans pilote sont très répandus, d'autres rares), pilotes proposés, santé, parfois une installation. Déterministe :
/// le même poste et le même numéro de rapport donnent toujours le même contenu.
/// </summary>
public sealed class ReportGenerator
{
    public const int Sites = 20;

    private static readonly (string Manufacturer, string Model, string? Friendly, int Weight)[] Models =
    [
        ("Dell Inc.", "Latitude 5440", null, 18), ("Dell Inc.", "Latitude 7420", null, 9), ("Dell Inc.", "OptiPlex 7010", null, 12),
        ("Dell Inc.", "Precision 3581", null, 3), ("LENOVO", "21HD", "ThinkPad T14 Gen 4", 11), ("LENOVO", "20W0", "ThinkPad T14 Gen 2", 6),
        ("LENOVO", "11T3", "ThinkCentre M70q Gen 3", 8), ("LENOVO", "21K5", "ThinkPad X1 Carbon Gen 11", 3),
        ("HP", "HP EliteBook 840 G10", null, 10), ("HP", "HP EliteBook 840 G8", null, 5), ("HP", "HP ProDesk 400 G7", null, 7),
        ("HP", "HP EliteDesk 800 G6", null, 4), ("Microsoft Corporation", "Surface Laptop 5", null, 2),
        ("Microsoft Corporation", "Surface Pro 9", null, 2), ("ASUSTeK COMPUTER INC.", "ExpertBook B1500CEAE", null, 2),
        ("Acer", "TravelMate P214-54", null, 1), ("Fujitsu", "LIFEBOOK U7411", null, 1), ("Dynabook Inc.", "Portégé X30L-K", null, 1),
    ];

    private static readonly (string Caption, string Display, string Version, int Weight)[] Systems =
    [
        ("Microsoft Windows 11 Professionnel", "24H2", "10.0.26100", 50), ("Microsoft Windows 11 Professionnel", "23H2", "10.0.22631", 25),
        ("Microsoft Windows 11 Entreprise", "24H2", "10.0.26100", 10), ("Microsoft Windows 10 Professionnel", "22H2", "10.0.19045", 15),
    ];

    private static readonly string[] Classes =
    [
        "System", "USB", "HIDClass", "Net", "Display", "MEDIA", "AudioEndpoint", "Bluetooth", "Camera", "DiskDrive", "Keyboard", "Mouse",
        "Monitor", "Processor", "SoftwareDevice", "Volume", "SCSIAdapter", "SmartCardReader", "Biometric", "Firmware", "Ports", "Battery",
    ];

    private static readonly string[] Vendors = ["Intel", "Realtek", "Microsoft", "Synaptics", "Qualcomm", "NVIDIA", "AMD", "Broadcom", "Goodix", "Logitech"];

    /// <summary>Matériels susceptibles d'être en erreur, du plus répandu au plus rare.</summary>
    private static readonly ProblemTemplate[] Problems = BuildProblems();

    private readonly int _machines;
    private readonly double[] _modelCumulative;
    private readonly double[] _systemCumulative;
    private readonly double[] _problemCumulative;

    /// <summary>Parc réaliste (familles de pilotes partagées par les modèles), null = propositions synthétiques très diverses.</summary>
    private readonly RealisticFleet? _realistic;

    /// <param name="fleet">« synthetic » (défaut : propositions tirées au hasard, versions toutes différentes — cas extrême
    /// pour la diffusion par anneaux) ou « realistic » (voir <see cref="RealisticFleet"/>).</param>
    public ReportGenerator(int machines, string fleet = "synthetic")
    {
        _machines = machines;
        _realistic = fleet switch
        {
            "realistic" => new RealisticFleet(),
            "synthetic" => null,
            _ => throw new ArgumentException($"Parc inconnu « {fleet} » (synthetic ou realistic)."),
        };
        _modelCumulative = Cumulative(Models.Select(m => (double)m.Weight));
        _systemCumulative = Cumulative(Systems.Select(s => (double)s.Weight));
        _problemCumulative = Cumulative(Problems.Select((_, i) => 1.0 / Math.Pow(i + 1, 1.1)));
    }

    public int MachineCount => _machines;

    public static string MachineName(int index) => $"PC-S{index % Sites:D2}-{index:D5}";

    public static string MachineId(int index)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("spokapilote-loadtest-" + index));
        return new Guid(hash).ToString("D");
    }

    /// <summary>Adresse IP simulée du poste (10.site.x.y), transmise par X-Forwarded-For.</summary>
    public static string MachineIp(int index) => $"10.{index % Sites}.{index / 250 % 250 + 1}.{index % 250 + 1}";

    /// <summary>Rapport JSON (UTF-8) du poste <paramref name="index"/>, numéro <paramref name="round"/>.</summary>
    public byte[] Generate(int index, int round, DateTimeOffset timestamp)
    {
        var plan = PlanReport(index, round, timestamp);
        var (model, os, deviceCount, rng, problems, install) = (plan.Model, plan.Os, plan.DeviceCount, plan.Rng, plan.Problems, plan.Install);
        var dangerous = index % 97 == 0;

        var buffer = new ArrayBufferWriter<byte>(256 * 1024);
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", 2);
            w.WriteString("generatedBy", "SpokaPilote");
            w.WriteString("appVersion", index % 10 == 0 ? "3.0.2" : "3.1.0");
            w.WriteString("timestamp", timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz"));
            w.WriteNumber("durationSeconds", plan.Duration);

            w.WriteStartObject("system");
            w.WriteString("computerName", MachineName(index));
            w.WriteString("machineId", MachineId(index));
            w.WriteString("manufacturer", model.Manufacturer);
            w.WriteString("model", model.Model);
            if (model.Friendly is not null) w.WriteString("friendlyModel", model.Friendly);
            w.WriteString("serialNumber", $"SN{index:D7}{(char)('A' + index % 26)}");
            w.WriteString("biosVersion", $"1.{index % 30}.0");
            w.WriteString("osCaption", os.Caption);
            w.WriteString("osVersion", os.Version);
            w.WriteString("osDisplayVersion", os.Display);
            w.WriteString("architecture", "amd64");
            w.WriteEndObject();

            w.WriteStartArray("sources");
            w.WriteStringValue("Windows Update");
            w.WriteStringValue($"{model.Manufacturer.Split(' ')[0]} (catalogue officiel)");
            w.WriteStringValue("Dépôt interne");
            w.WriteEndArray();
            w.WriteStartObject("sourceErrors");
            if (plan.SourceError) w.WriteString("Dépôt interne", "Partage \\\\srv\\pilotes injoignable (délai dépassé).");
            w.WriteEndObject();

            // Pilotes proposés (calculés par PlanReport : les mêmes que ceux que le banc « rollout » soumet à la console).
            w.WriteStartArray("candidates");
            foreach (var c in plan.Candidates) WriteCandidate(w, c);
            w.WriteEndArray();
            var candidates = plan.Candidates.Count;

            w.WriteStartObject("policy");
            w.WriteString("name", "Politique du parc");
            w.WriteString("version", "2026.11");
            w.WriteNumber("organizationRules", 12);
            w.WriteNumber("blockedCandidates", problems.Count(p => p.HasDriver && p.BlockedByPolicy));
            w.WriteStartArray("hiddenCandidates");
            w.WriteEndArray();
            w.WriteStartArray("messages");
            w.WriteEndArray();
            w.WriteEndObject();

            // Santé : score, note, constats.
            var score = Math.Clamp(100 - problems.Count * 12 - rng.Next(25) - (dangerous ? 30 : 0), 5, 100);
            w.WriteStartObject("health");
            w.WriteNumber("score", score);
            w.WriteString("grade", score >= 90 ? "A" : score >= 75 ? "B" : score >= 60 ? "C" : score >= 40 ? "D" : "E");
            w.WriteStartArray("findings");
            if (dangerous)
                WriteFinding(w, "critical", "known-bad-driver", "Pilote audio connu pour provoquer des écrans bleus", "HDAUDIO\\FUNC_01&VEN_10EC&DEV_0257");
            foreach (var p in problems) WriteFinding(w, "warning", "device-error", $"{p.Name} en erreur", p.HardwareId);
            for (var i = rng.Next(4); i > 0; i--) WriteFinding(w, "info", "old-driver", "Pilote de plus de 3 ans", $"PCI\\VEN_8086&DEV_{rng.Next(0x10000):X4}");
            w.WriteEndArray();
            w.WriteEndObject();

            if (install)
            {
                w.WriteStartArray("installation");
                for (var i = 0; i < Math.Max(1, candidates); i++)
                {
                    w.WriteStartObject();
                    // Parc réaliste : installation des pilotes proposés (alimente la validation des anneaux et le disjoncteur).
                    var installed = _realistic is not null && i < plan.Candidates.Count ? plan.Candidates[i] : null;
                    w.WriteString("title", installed?.Title ?? $"Pilote {i}");
                    w.WriteString("version", installed?.Version ?? "1.2.3.4");
                    var failed = rng.NextDouble() < (installed is null ? 0.15 : 0.03);
                    w.WriteString("status", failed ? "Failed" : "Installed");
                    w.WriteString("message", failed ? "Code de sortie 1603" : "Installé");
                    w.WriteBoolean("rebootRequired", !failed && rng.NextDouble() < 0.3);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            w.WriteStartArray("devices");
            var healthyCount = deviceCount - problems.Count;
            var deviceRng = new Random(index * 31 + 1);
            for (var d = 0; d < healthyCount; d++) WriteDevice(w, deviceRng, d, index);
            foreach (var p in problems) WriteProblemDevice(w, p, index);
            w.WriteEndArray();

            w.WriteStartArray("problemDevices");
            foreach (var p in problems) w.WriteStringValue(p.Name);
            w.WriteEndArray();
            w.WriteStartArray("unresolvedDevices");
            foreach (var p in problems.Where(p => !p.HasDriver)) w.WriteStringValue(p.Name);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] Gzip(byte[] json)
    {
        using var output = new MemoryStream(json.Length / 8);
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(json);
        return output.ToArray();
    }

    // ------------------------------------------------------------------ éléments

    /// <summary>Proposition synthétique (tirages dans le même ordre que les versions précédentes de l'outil).</summary>
    private static GenCandidate Synthetic(string title, string kind, string device, string deviceId, string? blocked, Random rng)
    {
        var version = $"{rng.Next(1, 40)}.{rng.Next(100)}.{rng.Next(1000)}.{rng.Next(10000)}";
        var source = rng.Next(2) == 0 ? "Windows Update" : "Catalogue constructeur";
        var size = rng.Next(200_000, 900_000_000);
        var url = $"https://catalog.update.microsoft.com/download/{rng.Next():x8}/{rng.Next():x8}.cab";
        return new GenCandidate(title, version, source, kind, "Périphérique système", device, deviceId, blocked, size, url);
    }

    private static void WriteCandidate(Utf8JsonWriter w, GenCandidate c)
    {
        w.WriteStartObject();
        w.WriteString("title", c.Title);
        w.WriteString("version", c.Version);
        w.WriteString("date", "2026-10-14T00:00:00");
        w.WriteString("source", c.Source);
        w.WriteString("kind", c.Kind);
        w.WriteString("category", c.Category);
        w.WriteNumber("sizeBytes", c.SizeBytes);
        w.WriteStartArray("devices");
        w.WriteStringValue(c.Device);
        w.WriteEndArray();
        w.WriteStartArray("deviceIds");
        w.WriteStringValue(c.DeviceId);
        w.WriteEndArray();
        w.WriteString("matchedHardwareId", c.DeviceId.Split('\\')[0] + "\\" + c.DeviceId.Split('\\')[1]);
        w.WriteString("downloadUrl", c.DownloadUrl);
        w.WriteString("infoUrl", "https://www.catalog.update.microsoft.com/");
        if (c.BlockedReason is not null) w.WriteString("blockedReason", c.BlockedReason);
        w.WriteEndObject();
    }

    /// <summary>Ce qu'un rapport contient, tiré au sort (déterministe) : modèle, périphériques en erreur, propositions.</summary>
    private sealed record ReportPlan((string Manufacturer, string Model, string? Friendly, int Weight) Model,
        (string Caption, string Display, string Version, int Weight) Os, int DeviceCount, Random Rng, List<ProblemTemplate> Problems, bool Install,
        int Duration, bool SourceError, List<GenCandidate> Candidates);

    private ReportPlan PlanReport(int index, int round, DateTimeOffset timestamp)
    {
        var machineRng = new Random(index * 7919 + 17);
        var model = Models[Pick(_modelCumulative, machineRng)];
        var os = Systems[Pick(_systemCumulative, machineRng)];
        var deviceCount = 205 + machineRng.Next(30);
        var rng = new Random(index * 7919 + round * 104729 + 3);
        var realisticModel = _realistic is null ? -1 : _realistic.ModelOf(index);
        if (_realistic is not null)
        {
            var m = _realistic.Models[realisticModel];
            model = (m.Manufacturer, m.Name, m.Friendly, m.Weight);
        }

        // Périphériques en erreur de ce rapport : 65 % des postes n'en ont aucun.
        var problems = new List<ProblemTemplate>();
        if (rng.NextDouble() >= 0.65)
        {
            var count = 1 + rng.Next(3);
            for (var i = 0; i < count; i++)
            {
                var p = Problems[Pick(_problemCumulative, rng)];
                if (!problems.Contains(p)) problems.Add(p);
            }
        }
        var install = rng.NextDouble() < 0.10;
        var duration = 20 + rng.Next(60);
        var sourceError = rng.NextDouble() < 0.03;

        // Pilotes proposés : une solution pour environ la moitié des matériels en erreur, plus des mises à jour.
        var candidates = new List<GenCandidate>();
        foreach (var p in problems.Where(p => p.HasDriver))
        {
            var blocked = p.BlockedByPolicy ? "Source non approuvée par la politique" : null;
            var c = Synthetic($"{p.Vendor} {p.Name}", "Pilote manquant", p.Name, InstanceId(p, index), blocked, rng);
            if (_realistic is not null)
            {
                // Pilote manquant d'un matériel connu : version stable (nouvelle version tous les 90 jours).
                var release = (int)((timestamp - new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalDays / 90);
                var number = Array.IndexOf(Problems, p);
                c = c with { Version = $"1.{number}.0.{100 + release}", Source = "Windows Update", Category = p.Class };
            }
            candidates.Add(c);
        }
        if (_realistic is null)
        {
            for (var i = rng.Next(6); i > 0; i--)
                candidates.Add(Synthetic($"{Vendors[rng.Next(Vendors.Length)]} pilote {Classes[rng.Next(Classes.Length)]}", "Mise à jour",
                    $"Périphérique {rng.Next(200)}", $"PCI\\VEN_{rng.Next(0x10000):X4}&DEV_{rng.Next(0x10000):X4}\\{rng.Next():X8}", null, rng));
        }
        else candidates.AddRange(_realistic.Candidates(index, realisticModel, timestamp));
        return new ReportPlan(model, os, deviceCount, rng, problems, install, duration, sourceError, candidates);
    }

    /// <summary>Propositions qu'un rapport du poste contient (celles qu'un poste 4.0 soumet à la console avant d'installer).</summary>
    public IReadOnlyList<GenCandidate> Candidates(int index, int round, DateTimeOffset timestamp) => PlanReport(index, round, timestamp).Candidates;


    private static void WriteFinding(Utf8JsonWriter w, string severity, string code, string title, string deviceId)
    {
        w.WriteStartObject();
        w.WriteString("severity", severity);
        w.WriteString("code", code);
        w.WriteString("title", title);
        w.WriteString("detail", "Constat généré par le test de charge.");
        w.WriteString("deviceId", deviceId);
        w.WriteEndObject();
    }

    private static void WriteDevice(Utf8JsonWriter w, Random rng, int d, int machine)
    {
        var cls = Classes[d % Classes.Length];
        var vendor = Vendors[(d * 7 + machine % 3) % Vendors.Length];
        var ven = (0x1000 + d * 37) & 0xFFFF;
        var dev = (0x2000 + d * 101 + machine % 5) & 0xFFFF;
        var enumerator = cls switch { "USB" or "HIDClass" or "Camera" or "Biometric" => "USB", "SoftwareDevice" or "AudioEndpoint" => "SWD", "System" => "ACPI", _ => "PCI" };
        var hwid = enumerator == "USB" ? $"USB\\VID_{ven:X4}&PID_{dev:X4}&REV_{d % 16:X4}" : $"{enumerator}\\VEN_{ven:X4}&DEV_{dev:X4}&SUBSYS_{rng.Next():X8}";
        w.WriteStartObject();
        w.WriteString("instanceId", $"{hwid}\\{d}&{machine:X}&0&{rng.Next(1000)}");
        w.WriteString("name", $"{vendor} {cls} {d}");
        w.WriteString("className", cls);
        w.WriteString("classGuid", "{4d36e97d-e325-11ce-bfc1-08002be10318}");
        w.WriteString("manufacturer", vendor);
        w.WriteStartArray("hardwareIds");
        w.WriteStringValue(hwid);
        w.WriteStringValue(hwid[..hwid.LastIndexOf('&')]);
        if (d % 2 == 0) w.WriteStringValue($"{enumerator}\\VEN_{ven:X4}");
        w.WriteEndArray();
        w.WriteStartArray("compatibleIds");
        w.WriteStringValue($"{enumerator}\\CC_{d % 255:X2}0000");
        w.WriteStringValue($"{enumerator}\\CC_{d % 255:X2}");
        w.WriteEndArray();
        w.WriteNumber("errorCode", d % 41 == 0 ? 22 : 0);
        w.WriteBoolean("isPresent", d % 29 != 0);
        w.WriteString("driverVersion", $"10.0.{26100 + d % 7}.{1000 + d}");
        w.WriteString("driverDate", "2006-06-21T00:00:00+02:00");
        w.WriteString("driverProvider", vendor);
        w.WriteString("infName", $"oem{d}.inf");
        w.WriteBoolean("isSigned", true);
        w.WriteString("enumerator", enumerator);
        w.WriteString("health", d % 41 == 0 ? "Disabled" : "Ok");
        w.WriteBoolean("hasProblem", false);
        w.WriteBoolean("isHardware", enumerator is "PCI" or "USB" or "ACPI");
        w.WriteString("statusText", "Fonctionne correctement");
        w.WriteBoolean("isHardwareFault", false);
        w.WriteEndObject();
    }

    private static void WriteProblemDevice(Utf8JsonWriter w, ProblemTemplate p, int machine)
    {
        w.WriteStartObject();
        w.WriteString("instanceId", InstanceId(p, machine));
        w.WriteString("name", p.Name);
        w.WriteString("className", p.Class);
        w.WriteString("manufacturer", p.Vendor);
        w.WriteStartArray("hardwareIds");
        w.WriteStringValue(p.HardwareId);
        w.WriteStringValue(p.HardwareId.Split('&')[0]);
        w.WriteEndArray();
        w.WriteStartArray("compatibleIds");
        w.WriteEndArray();
        w.WriteNumber("errorCode", p.ErrorCode);
        w.WriteBoolean("isPresent", true);
        w.WriteString("enumerator", p.HardwareId.Split('\\')[0]);
        w.WriteString("health", p.ErrorCode == 28 ? "MissingDriver" : "Error");
        w.WriteBoolean("hasProblem", true);
        w.WriteBoolean("isHardware", true);
        w.WriteString("statusText", p.ErrorCode == 28 ? "Aucun pilote installé" : "Arrêté suite à un problème signalé");
        w.WriteBoolean("isHardwareFault", p.HardwareFault);
        if (p.HardwareFault) w.WriteString("hint", "Problème matériel USB (câble, port, alimentation ou périphérique défectueux).");
        w.WriteEndObject();
    }

    private static string InstanceId(ProblemTemplate p, int machine) => $"{p.HardwareId}\\{machine:X}&0&1";

    private sealed record ProblemTemplate(string Name, string Vendor, string Class, string HardwareId, int ErrorCode, bool HasDriver,
        bool BlockedByPolicy, bool HardwareFault);

    private static ProblemTemplate[] BuildProblems()
    {
        var list = new List<ProblemTemplate>
        {
            new("Périphérique USB inconnu (échec de demande de descripteur)", "Microsoft", "USB", "USB\\DEVICE_DESCRIPTOR_FAILURE", 43, false, false, true),
        };
        var rng = new Random(42);
        for (var i = 0; i < 160; i++)
        {
            var cls = Classes[rng.Next(Classes.Length)];
            var vendor = Vendors[rng.Next(Vendors.Length)];
            var hwid = i % 3 == 0
                ? $"USB\\VID_{rng.Next(0x10000):X4}&PID_{rng.Next(0x10000):X4}&REV_0100"
                : $"ACPI\\VEN_{vendor[..3].ToUpperInvariant()}&DEV_{rng.Next(0x10000):X4}";
            list.Add(new ProblemTemplate($"{cls} {vendor} n° {i}", vendor, cls, hwid, rng.Next(5) == 0 ? 10 : 28, i % 2 == 1, i % 7 == 1, false));
        }
        return list.ToArray();
    }

    private static double[] Cumulative(IEnumerable<double> weights)
    {
        var w = weights.ToArray();
        var total = w.Sum();
        var acc = 0.0;
        return w.Select(x => acc += x / total).ToArray();
    }

    private static int Pick(double[] cumulative, Random rng)
    {
        var x = rng.NextDouble();
        var i = Array.BinarySearch(cumulative, x);
        return Math.Min(i < 0 ? ~i : i, cumulative.Length - 1);
    }
}
