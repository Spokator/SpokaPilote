using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SpokaPilote.Server.Api;

namespace SpokaPilote.Server.Alerts;

/// <summary>
/// Corps JSON des webhooks, selon la plateforme : Microsoft Teams (carte adaptative, flux « Workflows » ou webhook
/// entrant), Discord (embed), Slack (blocs Block Kit) ou JSON générique (automatisation, outil de supervision).
/// Les limites de taille de chaque plateforme sont respectées (textes tronqués).
/// </summary>
public static class WebhookFormatter
{
    public static string Format(string format, Alert alert) => format switch
    {
        "teams" => Teams(alert),
        "discord" => Discord(alert),
        "slack" => Slack(alert),
        _ => Generic(alert),
    };

    private static string Serialize(JsonNode node) => node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    public static string Teams(Alert a)
    {
        var body = new JsonArray
        {
            new JsonObject { ["type"] = "TextBlock", ["text"] = "SpokaPilote", ["size"] = "Small", ["isSubtle"] = true, ["spacing"] = "None" },
            new JsonObject
            {
                ["type"] = "TextBlock", ["text"] = a.Title, ["weight"] = "Bolder", ["size"] = "Medium", ["wrap"] = true,
                ["color"] = a.Severity switch { "critical" => "Attention", "warning" => "Warning", _ => "Default" },
            },
            new JsonObject { ["type"] = "TextBlock", ["text"] = Cut(a.Message, 4000), ["wrap"] = true },
        };
        if (a.Facts.Count > 0)
            body.Add(new JsonObject
            {
                ["type"] = "FactSet",
                ["facts"] = new JsonArray(a.Facts.Select(f => (JsonNode)new JsonObject { ["title"] = f.Label, ["value"] = Cut(f.Value, 1000) }).ToArray()),
            });
        var card = new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.4",
            ["body"] = body,
        };
        if (a.Link is not null)
            card["actions"] = new JsonArray(new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = "Ouvrir la console", ["url"] = a.Link });
        return Serialize(new JsonObject
        {
            ["type"] = "message",
            ["attachments"] = new JsonArray(new JsonObject
            {
                ["contentType"] = "application/vnd.microsoft.card.adaptive",
                ["contentUrl"] = null,
                ["content"] = card,
            }),
        });
    }

    public static string Discord(Alert a)
    {
        var embed = new JsonObject
        {
            ["title"] = Cut(a.Title, 256),
            ["description"] = Cut(a.Message, 4000),
            ["color"] = a.Severity switch { "critical" => 0xCF222E, "warning" => 0xD29922, _ => 0x0B63CE },
            ["timestamp"] = a.Timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            ["footer"] = new JsonObject { ["text"] = "SpokaPilote Server" },
            ["fields"] = new JsonArray(a.Facts.Take(25).Select(f => (JsonNode)new JsonObject
            {
                ["name"] = Cut(f.Label, 256),
                ["value"] = Cut(string.IsNullOrWhiteSpace(f.Value) ? "—" : f.Value, 1024),
                ["inline"] = f.Value.Length <= 40,
            }).ToArray()),
        };
        if (a.Link is not null) embed["url"] = a.Link;
        return Serialize(new JsonObject
        {
            ["username"] = "SpokaPilote",
            // Aucune mention (@everyone, @here, rôles) ne doit être déclenchée par le contenu d'un rapport.
            ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
            ["embeds"] = new JsonArray(embed),
        });
    }

    /// <summary>Échappement mrkdwn de Slack (&amp;, &lt;, &gt;) : un nom de périphérique ne peut pas créer de lien ou de mention.</summary>
    public static string SlackEscape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public static string Slack(Alert a)
    {
        var blocks = new JsonArray
        {
            new JsonObject { ["type"] = "header", ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = Cut(a.Title, 150), ["emoji"] = false } },
            new JsonObject { ["type"] = "section", ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = Cut(SlackEscape(a.Message), 2900) } },
        };
        if (a.Facts.Count > 0)
            blocks.Add(new JsonObject
            {
                ["type"] = "section",
                ["fields"] = new JsonArray(a.Facts.Take(10).Select(f => (JsonNode)new JsonObject
                {
                    ["type"] = "mrkdwn",
                    ["text"] = Cut($"*{SlackEscape(f.Label)}*\n{SlackEscape(string.IsNullOrWhiteSpace(f.Value) ? "—" : f.Value)}", 1900),
                }).ToArray()),
            });
        if (a.Link is not null)
            blocks.Add(new JsonObject
            {
                ["type"] = "actions",
                ["elements"] = new JsonArray(new JsonObject
                {
                    ["type"] = "button",
                    ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = "Ouvrir la console" },
                    ["url"] = a.Link,
                }),
            });
        return Serialize(new JsonObject { ["text"] = Cut($"{a.Title} — {a.Message}", 3000), ["blocks"] = blocks });
    }

    public static string Generic(Alert a) => Serialize(new JsonObject
    {
        ["source"] = "SpokaPilote Server",
        ["version"] = ApiEndpoints.Version,
        ["event"] = a.Reason,
        ["eventLabel"] = AlertReasons.Label(a.Reason),
        ["severity"] = a.Severity,
        ["title"] = a.Title,
        ["message"] = a.Message,
        ["machine"] = a.Machine,
        ["timestamp"] = a.Timestamp.ToUniversalTime().ToString("O"),
        ["url"] = a.Link,
        ["details"] = new JsonArray(a.Facts.Select(f => (JsonNode)new JsonObject { ["label"] = f.Label, ["value"] = f.Value }).ToArray()),
    });

    /// <summary>Texte brut de l'alerte (e-mail).</summary>
    public static string PlainText(Alert a)
    {
        var sb = new StringBuilder();
        sb.AppendLine(a.Title).AppendLine(new string('=', Math.Min(a.Title.Length, 70))).AppendLine().AppendLine(a.Message).AppendLine();
        foreach (var f in a.Facts) sb.Append(f.Label).Append(" : ").AppendLine(f.Value);
        if (a.Link is not null) sb.AppendLine().Append("Console : ").AppendLine(a.Link);
        sb.AppendLine().Append("-- ").AppendLine("SpokaPilote Server — alerte automatique.");
        return sb.ToString();
    }

    /// <summary>HTML simple de l'alerte (e-mail), entièrement échappé.</summary>
    public static string Html(Alert a)
    {
        static string E(string s) => System.Net.WebUtility.HtmlEncode(s);
        var color = a.Severity switch { "critical" => "#cf222e", "warning" => "#9a6700", _ => "#0b63ce" };
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"fr\"><body style=\"font-family:Segoe UI,Arial,sans-serif;color:#1b1f24;\">");
        sb.Append("<p style=\"color:#5b6470;font-size:12px;margin:0\">SpokaPilote</p>");
        sb.Append($"<h2 style=\"color:{color};margin:4px 0 12px\">{E(a.Title)}</h2>");
        sb.Append($"<p style=\"white-space:pre-line\">{E(a.Message)}</p>");
        if (a.Facts.Count > 0)
        {
            sb.Append("<table style=\"border-collapse:collapse;font-size:14px\">");
            foreach (var f in a.Facts)
                sb.Append($"<tr><td style=\"padding:3px 12px 3px 0;color:#5b6470;vertical-align:top\">{E(f.Label)}</td><td style=\"padding:3px 0\">{E(f.Value)}</td></tr>");
            sb.Append("</table>");
        }
        if (a.Link is not null) sb.Append($"<p><a href=\"{E(a.Link)}\">Ouvrir la console</a></p>");
        sb.Append("<p style=\"color:#5b6470;font-size:12px\">SpokaPilote Server — alerte automatique.</p></body></html>");
        return sb.ToString();
    }
}
