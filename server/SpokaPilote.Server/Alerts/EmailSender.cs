using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace SpokaPilote.Server.Alerts;

/// <summary>
/// Envoi des alertes par e-mail, sans dépendance tierce.
/// - « starttls » (587) et « none » (25, relais interne) : System.Net.Mail.SmtpClient (STARTTLS via EnableSsl).
/// - « ssl » (465, TLS implicite) : SmtpClient ne sait pas ouvrir une connexion directement en TLS ; un envoi minimal
///   (EHLO, AUTH PLAIN/LOGIN, MAIL, RCPT, DATA) est fait sur un SslStream, avec vérification normale du certificat.
/// </summary>
public sealed class EmailSender
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task SendAsync(SmtpSettings smtp, Alert alert, CancellationToken ct)
    {
        if (!smtp.IsConfigured) throw new InvalidOperationException("E-mail non configuré (serveur, expéditeur et destinataires).");
        var subject = $"[SpokaPilote] {alert.Title}";
        var text = WebhookFormatter.PlainText(alert);
        var html = WebhookFormatter.Html(alert);

        if (smtp.Security == "ssl")
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            await tcp.ConnectAsync(smtp.Host!, smtp.Port, cts.Token).ConfigureAwait(false);
            await using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = smtp.Host }, cts.Token).ConfigureAwait(false);
            await SmtpDialog.SendAsync(tls, smtp, BuildMime(smtp, subject, text, html, DateTimeOffset.UtcNow), cts.Token).ConfigureAwait(false);
            return;
        }

        using var message = new MailMessage
        {
            From = new MailAddress(smtp.From!, "SpokaPilote"),
            Subject = subject,
            SubjectEncoding = Encoding.UTF8,
            Body = text,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = false,
        };
        foreach (var to in smtp.To) message.To.Add(to);
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(html, Encoding.UTF8, "text/html"));
        using var client = new SmtpClient(smtp.Host!, smtp.Port)
        {
            EnableSsl = smtp.Security == "starttls",
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = (int)Timeout.TotalMilliseconds,
            UseDefaultCredentials = false,
            Credentials = string.IsNullOrEmpty(smtp.Username) ? null : new NetworkCredential(smtp.Username, smtp.Password ?? ""),
        };
        await client.SendMailAsync(message, ct).ConfigureAwait(false);
    }

    /// <summary>Message MIME (texte + HTML, UTF-8, base64) pour l'envoi en TLS implicite.</summary>
    internal static string BuildMime(SmtpSettings smtp, string subject, string text, string html, DateTimeOffset date)
    {
        static string Encoded(string s) => $"=?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(s))}?=";
        static string Base64Lines(string s)
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
            var sb = new StringBuilder();
            for (var i = 0; i < b64.Length; i += 76) sb.Append(b64, i, Math.Min(76, b64.Length - i)).Append("\r\n");
            return sb.ToString();
        }
        var boundary = "sp-" + Guid.NewGuid().ToString("N");
        var sb = new StringBuilder();
        sb.Append($"From: {Encoded("SpokaPilote")} <{smtp.From}>\r\n");
        sb.Append($"To: {string.Join(", ", smtp.To)}\r\n");
        sb.Append($"Subject: {Encoded(subject)}\r\n");
        sb.Append($"Date: {date.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", CultureInfo.InvariantCulture)}\r\n");
        sb.Append($"Message-ID: <{Guid.NewGuid():N}@spokapilote>\r\n");
        sb.Append("MIME-Version: 1.0\r\n");
        sb.Append($"Content-Type: multipart/alternative; boundary=\"{boundary}\"\r\n\r\n");
        sb.Append($"--{boundary}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n{Base64Lines(text)}");
        sb.Append($"--{boundary}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n{Base64Lines(html)}");
        sb.Append($"--{boundary}--\r\n");
        return sb.ToString();
    }
}

/// <summary>Dialogue SMTP minimal (RFC 5321) sur un flux déjà établi (TLS implicite). Testé avec un faux serveur.</summary>
internal static class SmtpDialog
{
    public static async Task SendAsync(Stream stream, SmtpSettings smtp, string mime, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { NewLine = "\r\n", AutoFlush = false };

        async Task<(int Code, List<string> Lines)> Read()
        {
            var lines = new List<string>();
            while (true)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false) ?? throw new IOException("Connexion SMTP fermée par le serveur.");
                lines.Add(line);
                if (line.Length < 4 || line[3] != '-') break;
            }
            return (int.TryParse(lines[^1].AsSpan(0, Math.Min(3, lines[^1].Length)), out var code) ? code : 0, lines);
        }
        async Task<List<string>> Command(string? command, params int[] expected)
        {
            if (command is not null)
            {
                await writer.WriteAsync(command + "\r\n").ConfigureAwait(false);
                await writer.FlushAsync(ct).ConfigureAwait(false);
            }
            var (code, lines) = await Read().ConfigureAwait(false);
            if (!expected.Contains(code))
            {
                var shown = command is not null && command.StartsWith("AUTH", StringComparison.Ordinal) ? "AUTH" : command?.Split(':')[0] ?? "connexion";
                throw new SmtpException($"Le serveur SMTP a refusé « {shown} » : {string.Join(" ", lines)}");
            }
            return lines;
        }

        await Command(null, 220).ConfigureAwait(false);
        var ehlo = await Command("EHLO spokapilote", 250).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(smtp.Username))
        {
            var auth = ehlo.FirstOrDefault(l => l.Length > 4 && l[4..].StartsWith("AUTH", StringComparison.OrdinalIgnoreCase))?[4..].ToUpperInvariant() ?? "";
            static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
            if (auth.Contains("PLAIN"))
                await Command($"AUTH PLAIN {B64($"\0{smtp.Username}\0{smtp.Password}")}", 235).ConfigureAwait(false);
            else
            {
                await Command("AUTH LOGIN", 334).ConfigureAwait(false);
                await Command(B64(smtp.Username), 334).ConfigureAwait(false);
                await Command(B64(smtp.Password ?? ""), 235).ConfigureAwait(false);
            }
        }
        await Command($"MAIL FROM:<{smtp.From}>", 250).ConfigureAwait(false);
        foreach (var to in smtp.To) await Command($"RCPT TO:<{to}>", 250, 251).ConfigureAwait(false);
        await Command("DATA", 354).ConfigureAwait(false);
        // Lignes commençant par « . » doublées (transparence) ; le base64 n'en produit pas, les en-têtes non plus.
        var body = string.Join("\r\n", mime.Replace("\r\n", "\n").Split('\n').Select(l => l.StartsWith('.') ? "." + l : l));
        await writer.WriteAsync(body.TrimEnd('\r', '\n') + "\r\n.\r\n").ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);
        await Command(null, 250).ConfigureAwait(false);
        try { await Command("QUIT", 221).ConfigureAwait(false); } catch (Exception ex) when (ex is IOException or SmtpException) { /* message déjà accepté */ }
    }
}
