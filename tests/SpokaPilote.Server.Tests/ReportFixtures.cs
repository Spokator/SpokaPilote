using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpokaPilote.Server.Tests;

/// <summary>Fabrique de rapports JSON au format « spokapilote scan --json » (ScanReport, camelCase).</summary>
internal sealed class ReportFixture
{
    private readonly JsonObject _root;
    private readonly JsonArray _devices = new();
    private readonly JsonArray _candidates = new();
    private JsonArray? _installation;

    public ReportFixture(string computerName = "PC-COMPTA-01", string manufacturer = "Dell Inc.", string model = "Latitude 7420",
        DateTimeOffset? timestamp = null)
    {
        _root = new JsonObject
        {
            ["schema"] = 2,
            ["generatedBy"] = "SpokaPilote",
            ["appVersion"] = "1.0.1",
            ["timestamp"] = (timestamp ?? new DateTimeOffset(2026, 11, 25, 9, 30, 0, TimeSpan.FromHours(1))).ToString("O"),
            ["durationSeconds"] = 42,
            ["system"] = new JsonObject
            {
                ["computerName"] = computerName,
                ["manufacturer"] = manufacturer,
                ["model"] = model,
                ["serialNumber"] = "SN-" + computerName,
                ["osCaption"] = "Microsoft Windows 11 Professionnel",
                ["osVersion"] = "10.0.26100",
                ["osDisplayVersion"] = "24H2",
                ["architecture"] = "amd64",
            },
            ["sources"] = new JsonArray("Windows Update", "Dell (catalogue officiel)"),
            ["sourceErrors"] = new JsonObject(),
        };
        // Un périphérique sain, toujours présent.
        Device("Carte réseau Intel", "PCI\\VEN_8086&DEV_15F3\\1", ["PCI\\VEN_8086&DEV_15F3&SUBSYS_0A6B1028"], 0);
    }

    public ReportFixture Device(string name, string instanceId, string[] hardwareIds, int errorCode, bool present = true)
    {
        _devices.Add(new JsonObject
        {
            ["instanceId"] = instanceId,
            ["name"] = name,
            ["hardwareIds"] = new JsonArray(hardwareIds.Select(h => (JsonNode)JsonValue.Create(h)!).ToArray()),
            ["errorCode"] = errorCode,
            ["isPresent"] = present,
            ["driverVersion"] = "1.0.0.0",
            ["driverProvider"] = "Microsoft",
        });
        return this;
    }

    public ReportFixture Candidate(string title, string[] deviceIds, string? blockedReason = null, string kind = "Pilote manquant")
    {
        var c = new JsonObject
        {
            ["title"] = title,
            ["version"] = "2.0.0.0",
            ["source"] = "Windows Update",
            ["kind"] = kind,
            ["devices"] = new JsonArray(title),
            ["deviceIds"] = new JsonArray(deviceIds.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray()),
        };
        if (blockedReason is not null) c["blockedReason"] = blockedReason;
        _candidates.Add(c);
        return this;
    }

    public ReportFixture Outcome(string title, string status, bool reboot = false)
    {
        _installation ??= new JsonArray();
        _installation.Add(new JsonObject { ["title"] = title, ["version"] = "2.0.0.0", ["status"] = status, ["message"] = status, ["rebootRequired"] = reboot });
        return this;
    }

    public ReportFixture SourceError(string source, string error)
    {
        _root["sourceErrors"]!.AsObject()[source] = error;
        return this;
    }

    public string Json()
    {
        var root = _root.DeepClone().AsObject();
        root["candidates"] = _candidates.DeepClone();
        root["devices"] = _devices.DeepClone();
        if (_installation is not null) root["installation"] = _installation.DeepClone();
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Poste type : un pilote manquant résolu, un sans solution, une panne USB.</summary>
    public static ReportFixture Typical(string name = "PC-COMPTA-01", string model = "Latitude 7420", DateTimeOffset? timestamp = null) =>
        new ReportFixture(name, model: model, timestamp: timestamp)
            .Device("Contrôleur audio", "HDAUDIO\\FUNC_01&VEN_10EC\\1", ["HDAUDIO\\FUNC_01&VEN_10EC&DEV_0256"], 28)
            .Device("Lecteur d'empreintes", "USB\\VID_27C6&PID_538C\\2", ["USB\\VID_27C6&PID_538C&REV_0100", "USB\\VID_27C6&PID_538C"], 28)
            .Device("Périphérique USB inconnu", "USB\\VID_0000&PID_0002\\3", ["USB\\DEVICE_DESCRIPTOR_FAILURE"], 43)
            .Candidate("Realtek Audio", ["HDAUDIO\\FUNC_01&VEN_10EC\\1"]);
}
