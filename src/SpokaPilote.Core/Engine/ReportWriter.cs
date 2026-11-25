using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Models;

namespace SpokaPilote.Core.Engine;

/// <summary>Rapports d'inventaire et d'intervention (JSON pour l'outillage, HTML pour les humains / le ticket).</summary>
public static class ReportWriter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ToJson(ScanResult scan, IReadOnlyList<InstallOutcome>? outcomes = null) =>
        JsonSerializer.Serialize(new
        {
            generatedBy = "SpokaPilote",
            scan.Timestamp,
            durationSeconds = Math.Round(scan.Duration.TotalSeconds),
            system = scan.System,
            sources = scan.SourcesUsed,
            sourceErrors = scan.SourceErrors,
            problemDevices = scan.ProblemDevices.Select(d => new { d.Name, d.InstanceId, d.ErrorCode, d.StatusText, hardwareIds = d.HardwareIds }),
            unresolvedDevices = scan.UnresolvedDevices.Select(d => new { d.Name, d.InstanceId, hardwareIds = d.HardwareIds }),
            candidates = scan.Candidates.Select(c => new
            {
                c.Title, c.Version, c.Date, source = c.SourceLabel, kind = c.KindLabel, c.Category, c.SizeBytes,
                devices = c.TargetDeviceNames, c.MatchedHardwareId, c.DownloadUrl, c.InfoUrl,
            }),
            installation = outcomes?.Select(o => new { o.Candidate.Title, o.Candidate.Version, status = o.Status.ToString(), o.Message, o.RebootRequired }),
            devices = scan.Devices.Where(d => d.IsPresent).Select(d => new
            {
                d.Name, d.ClassName, d.Manufacturer, d.InstanceId, d.DriverVersion, d.DriverDate, d.DriverProvider, d.ErrorCode,
            }),
        }, Json);

    public static string ToHtml(ScanResult scan, IReadOnlyList<InstallOutcome>? outcomes = null)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var s = scan.System;
        var sb = new StringBuilder();
        sb.Append("""
            <!doctype html><html lang="fr"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Rapport SpokaPilote</title><style>
            :root{--bg:#f6f7f9;--card:#fff;--text:#1b1f24;--muted:#5b6470;--line:#e3e6ea;--ok:#1a7f37;--warn:#9a6700;--bad:#cf222e;--accent:#0b63ce}
            @media (prefers-color-scheme:dark){:root{--bg:#0f1216;--card:#171b21;--text:#e6e9ed;--muted:#9aa4b1;--line:#2a313a;--ok:#3fb950;--warn:#d29922;--bad:#f85149;--accent:#58a6ff}}
            body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 "Segoe UI",system-ui,sans-serif}
            main{max-width:1100px;margin:0 auto;padding:24px 16px}h1{font-size:22px;margin:0 0 4px}h2{font-size:16px;margin:28px 0 8px}
            .muted{color:var(--muted)}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(200px,1fr));gap:12px;margin-top:16px}
            .card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:12px 14px}.k{font-size:12px;color:var(--muted)}.v{font-size:15px;font-weight:600;overflow-wrap:anywhere}
            table{width:100%;border-collapse:collapse;background:var(--card);border:1px solid var(--line);border-radius:10px;overflow:hidden}
            th,td{text-align:left;padding:8px 10px;border-bottom:1px solid var(--line);vertical-align:top;overflow-wrap:anywhere}th{font-size:12px;color:var(--muted);font-weight:600}
            .ok{color:var(--ok)}.warn{color:var(--warn)}.bad{color:var(--bad)}.scroll{overflow-x:auto}code{font-size:12px}
            </style></head><body><main>
            """);
        sb.Append($"<h1>Rapport SpokaPilote — {E(s.ComputerName)}</h1><div class=\"muted\">{scan.Timestamp:dd/MM/yyyy HH:mm} · analyse en {scan.Duration.TotalSeconds:0} s · sources : {E(string.Join(", ", scan.SourcesUsed))}</div>");
        sb.Append("<div class=\"grid\">");
        void Card(string k, string? v) => sb.Append($"<div class=\"card\"><div class=\"k\">{E(k)}</div><div class=\"v\">{E(v)}</div></div>");
        Card("Fabricant", s.Manufacturer);
        Card("Modèle", s.DisplayModel);
        Card("N° de série", s.SerialNumber);
        Card("Système", $"{s.OsCaption} {s.OsDisplayVersion} ({s.OsVersion})");
        Card("BIOS", s.BiosVersion);
        Card("Périphériques en erreur", scan.ProblemDevices.Count().ToString());
        sb.Append("</div>");

        if (scan.SourceErrors.Count > 0)
        {
            sb.Append("<h2 class=\"warn\">Sources indisponibles</h2><ul>");
            foreach (var (k, v) in scan.SourceErrors) sb.Append($"<li><b>{E(k)}</b> : {E(v)}</li>");
            sb.Append("</ul>");
        }

        if (outcomes is { Count: > 0 })
        {
            sb.Append("<h2>Installation</h2><div class=\"scroll\"><table><tr><th>Pilote</th><th>Résultat</th><th>Détail</th></tr>");
            foreach (var o in outcomes)
            {
                var cls = o.Succeeded ? "ok" : o.Status == InstallStatus.Skipped ? "muted" : "bad";
                sb.Append($"<tr><td>{E(o.Candidate.Title)} <span class=\"muted\">{E(o.Candidate.Version)}</span></td><td class=\"{cls}\">{E(o.Status.ToString())}</td><td>{E(o.Message)}</td></tr>");
            }
            sb.Append("</table></div>");
        }

        sb.Append("<h2>Pilotes proposés</h2>");
        if (scan.Candidates.Count == 0) sb.Append("<p class=\"ok\">Aucun pilote manquant ou à mettre à jour.</p>");
        else
        {
            sb.Append("<div class=\"scroll\"><table><tr><th>Type</th><th>Pilote</th><th>Version</th><th>Source</th><th>Périphérique(s)</th></tr>");
            foreach (var c in scan.Candidates)
                sb.Append($"<tr><td>{E(c.KindLabel)}</td><td>{E(c.Title)}</td><td>{E(c.Version)}</td><td>{E(c.SourceLabel)}</td><td>{E(string.Join(", ", c.TargetDeviceNames.Distinct()))}</td></tr>");
            sb.Append("</table></div>");
        }

        var unresolved = scan.UnresolvedDevices.ToList();
        if (unresolved.Count > 0)
        {
            sb.Append("<h2 class=\"bad\">Périphériques sans pilote trouvé</h2><div class=\"scroll\"><table><tr><th>Périphérique</th><th>État</th><th>ID matériel</th></tr>");
            foreach (var d in unresolved)
                sb.Append($"<tr><td>{E(d.Name)}{(d.Hint is null ? "" : $"<br><span class=\"muted\">{E(d.Hint)}</span>")}</td><td>{E(d.StatusText)}</td><td><code>{E(d.HardwareIds.FirstOrDefault())}</code></td></tr>");
            sb.Append("</table></div>");
        }

        sb.Append("<h2>Inventaire</h2><div class=\"scroll\"><table><tr><th>Classe</th><th>Périphérique</th><th>Pilote</th><th>Fournisseur</th><th>État</th></tr>");
        foreach (var d in scan.Devices.Where(d => d.IsPresent && (d.IsHardware || d.HasProblem)).OrderBy(d => d.ClassName).ThenBy(d => d.Name))
        {
            var cls = d.HasProblem ? "bad" : "ok";
            sb.Append($"<tr><td>{E(d.ClassName)}</td><td>{E(d.Name)}</td><td>{E(d.DriverVersion)} <span class=\"muted\">{d.DriverDate:dd/MM/yyyy}</span></td><td>{E(d.DriverProvider)}</td><td class=\"{cls}\">{E(d.StatusText)}</td></tr>");
        }
        sb.Append("</table></div><p class=\"muted\">Généré par SpokaPilote.</p></main></body></html>");
        return sb.ToString();
    }
}
