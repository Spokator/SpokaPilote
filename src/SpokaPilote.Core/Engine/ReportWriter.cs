using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpokaPilote.Core.Install;
using SpokaPilote.Core.Models;
using SpokaPilote.Core.Policy;

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
        ScanReport.From(scan, outcomes).ToJson();

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
            .health{display:flex;gap:20px;align-items:flex-start;flex-wrap:wrap;margin-top:16px}.health h2{margin:0 0 6px}
            .grade{flex:0 0 auto;width:96px;height:96px;border-radius:14px;display:flex;flex-direction:column;align-items:center;justify-content:center;color:#fff;font-weight:700}
            .grade b{font-size:52px;line-height:1}.grade span{font-size:13px;opacity:.95}
            .g-A{background:#1a7f37}.g-B{background:#2f8f4e}.g-C{background:#9a6700}.g-D{background:#bc4c00}.g-E{background:#cf222e}
            .findings{flex:1 1 320px;min-width:0}.findings ul{list-style:none;margin:8px 0 0;padding:0}
            .findings li{padding:8px 0;border-top:1px solid var(--line)}.findings li:first-child{border-top:0}
            .sev{display:inline-block;font-size:11px;font-weight:700;text-transform:uppercase;border-radius:4px;padding:1px 6px;margin-right:6px;color:#fff}
            .sev-critical{background:var(--bad)}.sev-warning{background:var(--warn)}.sev-info{background:var(--muted)}
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
        if (scan.Policy is { } policy)
            Card("Politique de pilotes", policy.Organization is null
                ? "Aucune (liste de référence seule)"
                : $"{policy.Organization.Name ?? "sans nom"}{(policy.Organization.Version is null ? "" : " v" + policy.Organization.Version)}{(policy.FromCache ? " (cache)" : "")}");
        sb.Append("</div>");

        if (scan.Health is { } health)
        {
            // Carte « Santé des pilotes » : note en grand, constats triés (critiques d'abord).
            sb.Append($"<div class=\"card health\"><div class=\"grade g-{E(health.Grade)}\" role=\"img\" aria-label=\"Note {E(health.Grade)}, {health.Score} sur 100\"><b>{E(health.Grade)}</b><span>{health.Score}/100</span></div>");
            sb.Append($"<div class=\"findings\"><h2>Santé des pilotes — {E(health.GradeLabel)}</h2><div class=\"muted\">{E(health.Summary)}</div>");
            if (health.Findings.Count == 0) sb.Append("<p class=\"ok\">Aucun constat : pilotes à jour, signés et sans erreur.</p>");
            else
            {
                sb.Append("<ul>");
                foreach (var f in health.Findings)
                {
                    var sev = f.Severity.ToString().ToLowerInvariant();
                    var detail = E(f.Detail);
                    if (f.AdvisoryUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps)
                        detail = detail.Replace(E(url), $"<a href=\"{E(url)}\" rel=\"noopener noreferrer\">{E(url)}</a>");
                    sb.Append($"<li><span class=\"sev sev-{sev}\">{E(f.SeverityLabel)}</span><b>{E(f.Title)}</b><br><span class=\"muted\">{detail}</span></li>");
                }
                sb.Append("</ul>");
            }
            sb.Append("<p class=\"muted\">Pondération : pilote dangereux −30 (note D au mieux), sans pilote / en erreur / non signé −15, pilote ancien −3 (−15 max), mise à jour −2 (−10 max) ; pannes matérielles et périphériques désactivés non comptés.</p></div></div>");
        }

        if (scan.Policy is { } applied)
        {
            var hidden = scan.PolicyDecisions.Where(d => d.Decision.Verdict == PolicyVerdict.Hidden).ToList();
            sb.Append($"<p class=\"muted\">Politique : {E(applied.Summary)}{(applied.OrganizationOrigin is null ? "" : $" — origine : <code>{E(applied.OrganizationOrigin)}</code>")}</p>");
            foreach (var m in applied.Messages) sb.Append($"<p class=\"warn\">{E(m)}</p>");
            if (hidden.Count > 0)
            {
                sb.Append($"<details><summary class=\"muted\">{hidden.Count} pilote(s) masqué(s) par la politique</summary><ul>");
                foreach (var d in hidden) sb.Append($"<li>{E(d.Candidate.Title)} {E(d.Candidate.Version)} — {E(d.Decision.Reason)}</li>");
                sb.Append("</ul></details>");
            }
        }

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
                sb.Append($"<tr><td>{E(c.KindLabel)}</td><td>{E(c.Title)}{(c.BlockedReason is null ? "" : $"<br><span class=\"bad\">⛔ Bloqué par la politique : {E(c.BlockedReason)}</span>")}</td><td>{E(c.Version)}</td><td>{E(c.SourceLabel)}</td><td>{E(string.Join(", ", c.TargetDeviceNames.Distinct()))}</td></tr>");
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
