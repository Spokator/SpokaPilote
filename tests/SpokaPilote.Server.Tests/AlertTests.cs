using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SpokaPilote.Server.Alerts;
using SpokaPilote.Server.Fleet;
using SpokaPilote.Server.Jobs;
using SpokaPilote.Server.Storage;

namespace SpokaPilote.Server.Tests;

/// <summary>Déclencheurs, anti-spam et réglages des alertes (sans envoi réel : la file est inspectée).</summary>
public sealed class AlertServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-server-alerts-" + Guid.NewGuid().ToString("N"));
    private readonly ServerStore _store;
    private readonly AlertQueue _queue = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 26, 9, 0, 0, TimeSpan.Zero));
    private readonly Dictionary<string, string?> _env = new();
    private AlertSettingsService Settings => new(_store, new ConfigurationBuilder().AddInMemoryCollection(_env).Build(), new ServerOptions { DataDirectory = _dir });
    private AlertService Alerts => new(_store, Settings, _queue, _clock, NullLogger<AlertService>.Instance);

    public AlertServiceTests()
    {
        _store = new ServerStore(new ServerOptions { DataDirectory = _dir });
        // Un canal configuré (webhook JSON) : sans canal, aucune alerte n'est générée.
        Settings.Update(new AlertSettingsUpdate
        {
            Webhooks = [new AlertSettingsUpdate.WebhookUpdate { Name = "Tests", Format = "json", Url = "https://hooks.exemple.fr/secret-123" }],
            ConsoleUrl = "https://spokapilote.exemple.fr",
            StaleDays = 7,
        }, _clock.GetUtcNow());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* temporaire */ }
    }

    private List<Alert> Drain()
    {
        var list = new List<Alert>();
        while (_queue.Reader.TryRead(out var a)) list.Add(a);
        return list;
    }

    private void Report(ReportFixture fixture, DateTimeOffset? at = null)
    {
        var json = fixture.Json();
        var report = ReportParser.Parse(json);
        Alerts.OnReportSaved(report, _store.SaveReport(report, json, at ?? _clock.GetUtcNow()));
    }

    private static ReportFixture Failing(string name) =>
        ReportFixture.Typical(name).Outcome("Pilote Realtek", "Failed");

    [Fact]
    public void Machine_becoming_to_process_alerts_once_per_24_hours()
    {
        Report(new ReportFixture("PC-01"));                 // sain : rien
        Assert.Empty(Drain());

        Report(ReportFixture.Typical("PC-01", timestamp: _clock.Now.AddMinutes(1)));
        var alert = Assert.Single(Drain());
        Assert.Equal(AlertReasons.MachineToProcess, alert.Reason);
        Assert.Equal("PC-01", alert.Machine);
        Assert.Equal("https://spokapilote.exemple.fr/#/postes/PC-01", alert.Link);

        // Redevient sain puis à traiter dans les 24 h : pas de deuxième alerte.
        Report(new ReportFixture("PC-01", timestamp: _clock.Now.AddMinutes(2)));
        Report(ReportFixture.Typical("PC-01", timestamp: _clock.Now.AddMinutes(3)));
        Assert.Empty(Drain());

        _clock.Advance(TimeSpan.FromHours(25));
        Report(new ReportFixture("PC-01", timestamp: _clock.Now));
        Report(ReportFixture.Typical("PC-01", timestamp: _clock.Now.AddMinutes(1)));
        Assert.Single(Drain());
    }

    [Fact]
    public void Install_failure_and_dangerous_driver_are_separate_reasons()
    {
        Report(Failing("PC-02").WithHealth("""
            { "score": 31, "grade": "E", "findings": [
              { "severity": "critical", "code": "known-bad-driver", "title": "Pilote audio XYZ 1.2 (écrans bleus)", "deviceId": "HDAUDIO\\FUNC_01" } ] }
            """));
        var reasons = Drain().Select(a => a.Reason).Order().ToList();
        Assert.Equal(new[] { AlertReasons.DangerousDriver, AlertReasons.InstallFailed, AlertReasons.MachineToProcess }.Order(), reasons);

        // Même poste, mêmes motifs, une heure plus tard : anti-spam.
        _clock.Advance(TimeSpan.FromHours(1));
        Report(Failing("PC-02").WithHealth("""{ "findings": [ { "severity": "critical", "code": "known-bad-driver", "title": "x" } ] }"""));
        Assert.Empty(Drain());

        // Un autre poste n'est pas concerné par l'anti-spam du premier.
        Report(Failing("PC-03"));
        Assert.Equal(2, Drain().Count);
    }

    [Fact]
    public void Warning_or_other_codes_do_not_trigger_the_dangerous_driver_alert()
    {
        Report(new ReportFixture("PC-04").WithHealth("""
            { "score": 70, "grade": "C", "findings": [
              { "severity": "warning", "code": "known-bad-driver", "title": "a" },
              { "severity": "critical", "code": "disk-full", "title": "b" } ] }
            """));
        Assert.Empty(Drain());
    }

    [Fact]
    public void Old_reports_and_missing_channels_never_alert()
    {
        Report(new ReportFixture("PC-05", timestamp: _clock.Now));
        Report(Failing("PC-05"), _clock.Now.AddDays(-30)); // rapport ancien (horodatage fixe du gabarit, antérieur)
        Assert.Empty(Drain());

        Settings.Update(new AlertSettingsUpdate { Webhooks = [] }, _clock.GetUtcNow());
        Report(Failing("PC-06"));
        Assert.Empty(Drain());
    }

    [Fact]
    public void Failed_jobs_alert_but_never_picked_jobs_do_not()
    {
        var json = ReportFixture.Typical("PC-07").Json();
        var m = _store.SaveReport(ReportParser.Parse(json), json, _clock.Now).MachineRef;
        var job = _store.CreateJob(m, JobTypes.InstallMissing, new JobParameters(), "Alice", _clock.Now).Job;
        _store.ClaimPendingJobs(m, _clock.Now);
        var (_, failed) = _store.CompleteJob(job.Id, m, false, 2, "échec partiel", null, null, _clock.Now);
        Alerts.OnJobFinished(failed!);
        var alert = Assert.Single(Drain());
        Assert.Equal(AlertReasons.JobFailed, alert.Reason);
        Assert.Contains("échec partiel", alert.Message);

        var never = _store.CreateJob(m, JobTypes.Scan, new JobParameters(), "x", _clock.Now).Job;
        var expired = Assert.Single(_store.ExpireJobs(_clock.Now.AddDays(8), 7, 6));
        Assert.Equal(never.Id, expired.Id);
        Alerts.OnJobFinished(expired);
        Assert.Empty(Drain());
    }

    [Fact]
    public void Stale_machines_are_grouped_and_signalled_once_per_absence()
    {
        foreach (var name in new[] { "PC-A", "PC-B" })
        {
            var json = new ReportFixture(name, timestamp: _clock.Now).Json();
            _store.SaveReport(ReportParser.Parse(json), json, _clock.Now);
        }
        Assert.Equal(0, Alerts.CheckStaleMachines(_clock.Now.AddDays(3)));
        Assert.Equal(2, Alerts.CheckStaleMachines(_clock.Now.AddDays(8)));
        var alert = Assert.Single(Drain());
        Assert.Equal("2 postes non vus depuis 7 jours", alert.Title);
        Assert.Contains("PC-A", alert.Message);
        Assert.Equal(0, Alerts.CheckStaleMachines(_clock.Now.AddDays(9)));   // même absence : pas de répétition
        Assert.Empty(Drain());
    }

    [Fact]
    public void Anti_spam_window_is_per_subject_and_reason()
    {
        var t = _clock.Now;
        Assert.True(_store.TryRegisterAlert("poste:1", "to-process", t, TimeSpan.FromHours(24)));
        Assert.False(_store.TryRegisterAlert("poste:1", "to-process", t.AddHours(23), TimeSpan.FromHours(24)));
        Assert.True(_store.TryRegisterAlert("poste:1", "install-failed", t.AddHours(1), TimeSpan.FromHours(24)));
        Assert.True(_store.TryRegisterAlert("poste:2", "to-process", t.AddHours(1), TimeSpan.FromHours(24)));
        Assert.True(_store.TryRegisterAlert("poste:1", "to-process", t.AddHours(24), TimeSpan.FromHours(24)));
    }

    [Fact]
    public void Daily_summary_counts_the_fleet()
    {
        Report(Failing("PC-08"));
        Report(new ReportFixture("PC-09").WithHealth("""{ "score": 90, "grade": "A" }"""));
        Drain();
        var summary = Alerts.BuildDailySummary(_clock.Now);
        Assert.Contains("1 poste(s) à traiter sur 2", summary.Title);
        Assert.Contains(summary.Facts, f => f.Label == "Santé moyenne" && f.Value.StartsWith("90"));
    }

    // ------------------------------------------------------------------ réglages

    [Fact]
    public void Secrets_are_never_returned_and_kept_when_omitted()
    {
        var s = Settings;
        s.Update(new AlertSettingsUpdate
        {
            Smtp = new AlertSettingsUpdate.SmtpUpdate { Host = "smtp.exemple.fr", Port = 587, Security = "starttls", Username = "alertes", Password = "MotDePasse!42", From = "spoka@exemple.fr", To = ["it@exemple.fr", "chef@exemple.fr"] },
            Webhooks = [new AlertSettingsUpdate.WebhookUpdate { Name = "Discord", Format = "discord", Url = "https://discord.com/api/webhooks/1/jeton-secret" }],
        }, _clock.Now);
        var view = JsonSerializer.Serialize(s.View(), ServerStore.Json);
        Assert.DoesNotContain("MotDePasse", view);
        Assert.DoesNotContain("jeton-secret", view);
        Assert.Contains("\"passwordSet\":true", view);
        Assert.Contains("https://discord.com/\\u2026", view); // « … » échappé par le sérialiseur

        // Nouvelle modification sans mot de passe ni adresse : les secrets sont conservés.
        var id = s.Current().Webhooks.Single().Id;
        s.Update(new AlertSettingsUpdate
        {
            Smtp = new AlertSettingsUpdate.SmtpUpdate { Host = "smtp.exemple.fr", Port = 465, Security = "ssl", Username = "alertes", Password = null, From = "spoka@exemple.fr", To = ["it@exemple.fr"] },
            Webhooks = [new AlertSettingsUpdate.WebhookUpdate { Id = id, Name = "Discord IT", Format = "discord", Url = null }],
        }, _clock.Now);
        var current = s.Current();
        Assert.Equal("MotDePasse!42", current.Smtp.Password);
        Assert.Equal(465, current.Smtp.Port);
        Assert.Equal("https://discord.com/api/webhooks/1/jeton-secret", current.Webhooks.Single().Url);
        Assert.Equal("Discord IT", current.Webhooks.Single().Name);

        // Chaîne vide : effacé.
        s.Update(new AlertSettingsUpdate { Smtp = new AlertSettingsUpdate.SmtpUpdate { Host = "smtp.exemple.fr", Password = "", From = "spoka@exemple.fr", To = ["it@exemple.fr"] } }, _clock.Now);
        Assert.Null(s.Current().Smtp.Password);
    }

    [Theory]
    [InlineData("ftp://hooks.exemple.fr")]
    [InlineData("https://user:pass@hooks.exemple.fr/x")]
    [InlineData("pas une adresse")]
    public void Invalid_webhook_urls_are_rejected(string url) =>
        Assert.Throws<AlertSettingsException>(() => Settings.Update(new AlertSettingsUpdate
        {
            Webhooks = [new AlertSettingsUpdate.WebhookUpdate { Name = "x", Format = "json", Url = url }],
        }, _clock.Now));

    [Fact]
    public void Invalid_smtp_settings_are_rejected()
    {
        Assert.Throws<AlertSettingsException>(() => Settings.Update(new AlertSettingsUpdate
            { Smtp = new AlertSettingsUpdate.SmtpUpdate { Host = "smtp.exemple.fr", From = "pas-une-adresse", To = ["it@exemple.fr"] } }, _clock.Now));
        Assert.Throws<AlertSettingsException>(() => Settings.Update(new AlertSettingsUpdate
            { Smtp = new AlertSettingsUpdate.SmtpUpdate { Host = "smtp.exemple.fr", From = "a@exemple.fr", To = [] } }, _clock.Now));
        Assert.Throws<AlertSettingsException>(() => Settings.Update(new AlertSettingsUpdate { DailySummaryTime = "25:99" }, _clock.Now));
    }

    [Fact]
    public void Environment_variables_take_precedence_and_lock_fields()
    {
        _env["SPOKAPILOTE_SMTP_HOST"] = "relais.interne";
        _env["SPOKAPILOTE_SMTP_SECURITY"] = "none";
        _env["SPOKAPILOTE_SMTP_PORT"] = "25";
        _env["SPOKAPILOTE_SMTP_FROM"] = "spoka@interne";
        _env["SPOKAPILOTE_SMTP_TO"] = "a@interne; b@interne";
        _env["SPOKAPILOTE_WEBHOOK_URL"] = "https://exemple.webhook.office.com/abc";
        _env["SPOKAPILOTE_WEBHOOK_FORMAT"] = "teams";
        var s = Settings;
        var current = s.Current();
        Assert.Equal("relais.interne", current.Smtp.Host);
        Assert.Equal(["a@interne", "b@interne"], current.Smtp.To);
        Assert.True(current.Smtp.IsConfigured);
        Assert.Equal("teams", current.Webhooks[0].Format);
        Assert.Equal(AlertSettingsService.EnvWebhookId, current.Webhooks[0].Id);
        Assert.Contains("smtp.host", s.LockedFields());

        // La console ne peut pas écraser une valeur fixée par variable d'environnement.
        s.Update(new AlertSettingsUpdate { Smtp = new AlertSettingsUpdate.SmtpUpdate { Host = "autre.serveur" } }, _clock.Now);
        Assert.Equal("relais.interne", s.Current().Smtp.Host);
        Assert.Equal(2, s.Current().Webhooks.Count); // webhook d'environnement + webhook enregistré
    }
}

/// <summary>Corps envoyés aux webhooks : JSON valide, structure attendue par chaque plateforme, contenu échappé.</summary>
public sealed class WebhookFormatTests
{
    private static readonly Alert Sample = new(AlertReasons.InstallFailed, "critical", "Échec d'installation sur PC-01",
        "2 pilote(s) n'ont pas pu être installés :\n<Realtek> & @everyone", "PC-01",
        [new AlertFact("Poste", "PC-01"), new AlertFact("Modèle", "Dell Latitude 7420"), new AlertFact("Vide", "")],
        "https://spokapilote.exemple.fr/#/postes/PC-01", new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero));

    private static JsonElement Parse(string format) => JsonDocument.Parse(WebhookFormatter.Format(format, Sample)).RootElement;

    [Fact]
    public void Teams_sends_an_adaptive_card()
    {
        var root = Parse("teams");
        Assert.Equal("message", root.GetProperty("type").GetString());
        var attachment = root.GetProperty("attachments")[0];
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.GetProperty("contentType").GetString());
        var card = attachment.GetProperty("content");
        Assert.Equal("AdaptiveCard", card.GetProperty("type").GetString());
        Assert.Equal("Attention", card.GetProperty("body")[1].GetProperty("color").GetString());
        Assert.Equal("FactSet", card.GetProperty("body")[3].GetProperty("type").GetString());
        Assert.Equal(Sample.Link, card.GetProperty("actions")[0].GetProperty("url").GetString());
    }

    [Fact]
    public void Discord_sends_an_embed_without_mentions()
    {
        var root = Parse("discord");
        Assert.Equal(0, root.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength());
        var embed = root.GetProperty("embeds")[0];
        Assert.Equal(Sample.Title, embed.GetProperty("title").GetString());
        Assert.Equal(0xCF222E, embed.GetProperty("color").GetInt32());
        Assert.Equal("—", embed.GetProperty("fields")[2].GetProperty("value").GetString()); // Discord refuse les valeurs vides
        Assert.Equal(Sample.Link, embed.GetProperty("url").GetString());
    }

    [Fact]
    public void Slack_sends_blocks_with_escaped_mrkdwn()
    {
        var root = Parse("slack");
        Assert.Contains(Sample.Title, root.GetProperty("text").GetString());
        var blocks = root.GetProperty("blocks");
        Assert.Equal("header", blocks[0].GetProperty("type").GetString());
        var text = blocks[1].GetProperty("text").GetProperty("text").GetString()!;
        Assert.Contains("&lt;Realtek&gt; &amp;", text);
        Assert.DoesNotContain("<Realtek>", text);
        Assert.Equal("actions", blocks[3].GetProperty("type").GetString());
    }

    [Fact]
    public void Generic_json_is_stable_for_automation()
    {
        var root = Parse("json");
        Assert.Equal("install-failed", root.GetProperty("event").GetString());
        Assert.Equal("critical", root.GetProperty("severity").GetString());
        Assert.Equal("PC-01", root.GetProperty("machine").GetString());
        Assert.Equal("2026-11-26T10:00:00.0000000+00:00", root.GetProperty("timestamp").GetString());
        Assert.Equal("Poste", root.GetProperty("details")[0].GetProperty("label").GetString());
    }

    [Fact]
    public void Email_html_is_escaped()
    {
        var html = WebhookFormatter.Html(Sample);
        Assert.Contains("&lt;Realtek&gt; &amp; @everyone", html);
        Assert.DoesNotContain("<Realtek>", html);
        Assert.Contains("Dell Latitude 7420", WebhookFormatter.PlainText(Sample));
    }
}

/// <summary>Envoi SMTP en TLS implicite : dialogue vérifié avec un faux serveur (sans TLS pour le test).</summary>
public sealed class SmtpDialogTests
{
    [Fact]
    public async Task Dialog_authenticates_and_sends_mime()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = new List<string>();
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 faux-serveur ESMTP");
            while (await reader.ReadLineAsync() is { } line)
            {
                received.Add(line);
                if (line.StartsWith("EHLO")) { await writer.WriteLineAsync("250-faux-serveur"); await writer.WriteLineAsync("250 AUTH LOGIN PLAIN"); }
                else if (line.StartsWith("AUTH PLAIN")) await writer.WriteLineAsync("235 ok");
                else if (line.StartsWith("MAIL") || line.StartsWith("RCPT")) await writer.WriteLineAsync("250 ok");
                else if (line == "DATA")
                {
                    await writer.WriteLineAsync("354 go");
                    while (await reader.ReadLineAsync() is { } data && data != ".") received.Add(data);
                    await writer.WriteLineAsync("250 queued");
                }
                else if (line == "QUIT") { await writer.WriteLineAsync("221 bye"); break; }
            }
        });

        var smtp = new SmtpSettings { Host = "localhost", Port = port, Security = "ssl", Username = "alertes", Password = "secret", From = "spoka@exemple.fr", To = ["it@exemple.fr", "chef@exemple.fr"] };
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var mime = EmailSender.BuildMime(smtp, "[SpokaPilote] Échec d'installation", "texte", "<p>html</p>", DateTimeOffset.UtcNow);
            await SmtpDialog.SendAsync(tcp.GetStream(), smtp, mime, CancellationToken.None);
        }
        await server;
        listener.Stop();

        Assert.Contains("AUTH PLAIN " + Convert.ToBase64String(Encoding.UTF8.GetBytes("\0alertes\0secret")), received);
        Assert.Contains("MAIL FROM:<spoka@exemple.fr>", received);
        Assert.Contains("RCPT TO:<chef@exemple.fr>", received);
        var subject = received.Single(l => l.StartsWith("Subject: "));
        Assert.Equal("[SpokaPilote] Échec d'installation",
            Encoding.UTF8.GetString(Convert.FromBase64String(subject["Subject: =?utf-8?B?".Length..^2])));
        Assert.Contains(received, l => l.StartsWith("Content-Type: multipart/alternative"));
    }
}
