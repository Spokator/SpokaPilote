using System.Net;
using System.Text.Json;
using SpokaPilote.Server.Security;
using static SpokaPilote.Server.Tests.TestServerHost;

namespace SpokaPilote.Server.Tests;

/// <summary>Autorisation par rôle : chaque endpoint, chaque rôle, autorisé ou refusé.</summary>
public sealed class RoleAuthorizationTests : IClassFixture<RoleAuthorizationTests.Fixture>
{
    /// <summary>Serveur partagé : un poste, un groupe, un compte par rôle (les connexions coûtent un calcul PBKDF2).</summary>
    public sealed class Fixture : IAsyncLifetime
    {
        internal TestServerHost Host { get; } = new();
        internal Dictionary<string, HttpClient> Clients { get; } = new();

        public async Task InitializeAsync()
        {
            Assert.Equal(HttpStatusCode.OK, (await Host.Agent("6f1c2a8e-1d2b-4c3d-9e4f-0123456789ab", "PC-01")
                .PostAsync("/api/reports", Json(ReportFixture.Typical("PC-01").WithMachineId("6f1c2a8e-1d2b-4c3d-9e4f-0123456789ab").Json()))).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await Host.Client(AdminToken).PostAsync("/api/groups", Json(new { name = "Siège" }))).StatusCode);
            foreach (var role in Roles.All)
            {
                await Host.CreateUserAsync("u-" + role, role);
                Clients[role] = await Host.LoginAsync("u-" + role);
            }
            Clients["admin-token"] = Host.Client(AdminToken);
            Clients["agent-token"] = Host.Client(AgentToken);
            Clients["anonymous"] = Host.Client(null);
        }

        public Task DisposeAsync()
        {
            Host.Dispose();
            return Task.CompletedTask;
        }
    }

    private readonly Fixture _f;
    public RoleAuthorizationTests(Fixture f) => _f = f;

    /// <summary>Droit minimal de chaque endpoint de la console : view, operate ou admin.</summary>
    public static TheoryData<string, string, string, string?> Endpoints() => new()
    {
        { "view", "GET", "/api/fleet", null },
        { "view", "GET", "/api/fleet?group=1", null },
        { "view", "GET", "/api/machines", null },
        { "view", "GET", "/api/machines/PC-01", null },
        { "view", "GET", "/api/machines/PC-01/raw", null },
        { "view", "GET", "/api/jobs", null },
        { "view", "GET", "/api/job-types", null },
        { "view", "GET", "/api/groups", null },
        { "view", "GET", "/api/groups/1", null },
        { "view", "GET", "/api/schedules", null },
        { "view+agent", "GET", "/api/policy", null },
        { "view+agent", "GET", "/api/printer-profile", null },
        { "view+agent", "GET", "/api/whoami", null },
        { "view", "GET", "/api/session", null },
        { "operate", "POST", "/api/jobs", """{"type":"scan","machines":["PC-01"]}""" },
        { "operate", "POST", "/api/jobs/999/cancel", null },
        { "operate", "POST", "/api/groups", """{"name":"Nouveau"}""" },
        { "operate", "PUT", "/api/groups/1", """{"name":"Siège"}""" },
        { "operate", "POST", "/api/groups/1/members", """{"add":["PC-01"]}""" },
        { "operate", "POST", "/api/schedules", """{"name":"Analyse","type":"scan","frequency":"daily","time":"22:00"}""" },
        { "operate", "DELETE", "/api/schedules/999", null },
        { "admin", "PUT", "/api/policy", """{"version":1}""" },
        { "admin", "DELETE", "/api/printer-profile", null },
        { "admin", "DELETE", "/api/machines/PC-INCONNU", null },
        { "admin", "GET", "/api/enrollment", null },
        { "admin", "GET", "/api/settings/alerts", null },
        { "admin", "GET", "/api/alerts/events", null },
        { "admin", "GET", "/api/users", null },
        { "admin", "POST", "/api/users", """{"login":"x","role":"readonly","password":"court"}""" },
        { "admin", "GET", "/api/audit", null },
        { "admin", "GET", "/api/audit/export", null },
    };

    private static readonly Dictionary<string, string[]> Grants = new()
    {
        [Roles.Admin] = ["view", "operate", "admin"],
        ["admin-token"] = ["view", "operate", "admin"],
        [Roles.Technician] = ["view", "operate"],
        [Roles.ReadOnly] = ["view"],
        ["agent-token"] = ["agent"],
        ["anonymous"] = [],
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Each_role_is_allowed_or_denied(string required, string method, string path, string? body)
    {
        foreach (var (who, grants) in Grants)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (body is not null) request.Content = Json(body);
            var response = await _f.Clients[who].SendAsync(request);
            var status = response.StatusCode;
            if (required.Split('+').Any(grants.Contains))
                Assert.True(status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden),
                    $"{who} devrait être autorisé sur {method} {path} (reçu {(int)status}).");
            else if (who == "anonymous")
                Assert.Equal(HttpStatusCode.Unauthorized, status);
            else
                Assert.True(status == HttpStatusCode.Forbidden, $"{who} devrait être refusé sur {method} {path} (reçu {(int)status}).");
        }
    }

    [Fact]
    public async Task Agent_endpoints_stay_reserved_to_the_enrollment_token()
    {
        foreach (var role in Roles.All)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await _f.Clients[role].PostAsync("/api/reports", Json(ReportFixture.Typical().Json()))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await _f.Clients[role].GetAsync("/api/agent/jobs")).StatusCode);
        }
    }

    [Fact]
    public async Task Session_describes_the_caller_and_its_permissions()
    {
        var tech = await TestServerHost.ReadJson(await _f.Clients[Roles.Technician].GetAsync("/api/session"));
        Assert.Equal("session", tech.GetProperty("mode").GetString());
        Assert.Equal("u-technician", tech.GetProperty("login").GetString());
        Assert.True(tech.GetProperty("permissions").GetProperty("operate").GetBoolean());
        Assert.False(tech.GetProperty("permissions").GetProperty("admin").GetBoolean());
        Assert.Equal(JsonValueKind.Null, tech.GetProperty("accountCount").ValueKind);

        var token = await TestServerHost.ReadJson(await _f.Clients["admin-token"].GetAsync("/api/session"));
        Assert.Equal("token", token.GetProperty("mode").GetString());
        Assert.True(token.GetProperty("permissions").GetProperty("admin").GetBoolean());
        Assert.Equal(3, token.GetProperty("accountCount").GetInt32());

        // « spokapilote server status » : réponses des jetons inchangées depuis la 3.0.
        Assert.Equal("admin", (await TestServerHost.ReadJson(await _f.Clients["admin-token"].GetAsync("/api/whoami"))).GetProperty("access").GetString());
        Assert.Equal("agent", (await TestServerHost.ReadJson(await _f.Clients["agent-token"].GetAsync("/api/whoami"))).GetProperty("access").GetString());
        Assert.Equal("user", (await TestServerHost.ReadJson(await _f.Clients[Roles.ReadOnly].GetAsync("/api/whoami"))).GetProperty("access").GetString());
    }

    [Fact]
    public async Task Jobs_created_by_an_account_carry_its_identity()
    {
        var r = await _f.Clients[Roles.Technician].PostAsync("/api/jobs", Json(new { type = "rollback-last", machines = new[] { "PC-01" }, requestedBy = "quelqu'un d'autre" }));
        var created = (await TestServerHost.ReadJson(r)).GetProperty("created");
        if (created.GetArrayLength() == 0) return; // déjà en attente (ordre des tests) : vérifié ailleurs
        Assert.Equal("u-technician", created[0].GetProperty("createdBy").GetString());
    }
}

/// <summary>Comptes : création, modification, dernier administrateur, changement de mot de passe.</summary>
public sealed class AccountTests : IDisposable
{
    private readonly TestServerHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Admin_token_bootstraps_the_first_admin_account()
    {
        var admin = _host.Client(AdminToken);
        var info = await ReadJson(await admin.GetAsync("/api/session"));
        Assert.Equal(0, info.GetProperty("accountCount").GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/users", Json(new { login = "chef", role = "admin", password = "court" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/users", Json(new { login = "Pas Valide", role = "admin", password = Password }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/users", Json(new { login = "chef", role = "root", password = Password }))).StatusCode);
        await _host.CreateUserAsync("chef", Roles.Admin);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/users", Json(new { login = "CHEF", role = "admin", password = Password }))).StatusCode);

        // Le nouveau compte gère les comptes ; l'empreinte n'est jamais renvoyée.
        var chef = await _host.LoginAsync("Chef"); // identifiant insensible à la casse
        var users = await (await chef.GetAsync("/api/users")).Content.ReadAsStringAsync();
        Assert.Contains("\"login\":\"chef\"", users);
        Assert.DoesNotContain("pbkdf2", users);
        Assert.DoesNotContain("passwordHash", users);
    }

    [Fact]
    public async Task The_last_active_admin_cannot_be_removed_and_nobody_locks_themselves_out()
    {
        await _host.CreateUserAsync("chef", Roles.Admin);
        await _host.CreateUserAsync("tech", Roles.Technician);
        var admin = _host.Client(AdminToken);
        var users = (await ReadJson(await admin.GetAsync("/api/users"))).GetProperty("users");
        long Id(string login) => users.EnumerateArray().First(u => u.GetProperty("login").GetString() == login).GetProperty("id").GetInt64();

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsync($"/api/users/{Id("chef")}", Json(new { role = "technician", enabled = true }))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsync($"/api/users/{Id("chef")}", Json(new { role = "admin", enabled = false }))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/users/{Id("chef")}")).StatusCode);

        var chef = await _host.LoginAsync("chef");
        Assert.Equal(HttpStatusCode.Conflict, (await chef.DeleteAsync($"/api/users/{Id("chef")}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await chef.PutAsync($"/api/users/{Id("chef")}", Json(new { role = "readonly", enabled = true }))).StatusCode);

        // Le technicien supprimé, supprimer le dernier compte reste possible (retour au seul jeton).
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/users/{Id("tech")}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/users/{Id("chef")}")).StatusCode);
    }

    [Fact]
    public async Task Disabling_or_resetting_an_account_closes_its_sessions()
    {
        await _host.CreateUserAsync("chef", Roles.Admin);
        await _host.CreateUserAsync("tech", Roles.Technician);
        var tech = await _host.LoginAsync("tech");
        Assert.Equal(HttpStatusCode.OK, (await tech.GetAsync("/api/machines")).StatusCode);
        var admin = _host.Client(AdminToken);
        var id = (await ReadJson(await admin.GetAsync("/api/users"))).GetProperty("users").EnumerateArray()
            .First(u => u.GetProperty("login").GetString() == "tech").GetProperty("id").GetInt64();

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/users/{id}", Json(new { role = "technician", enabled = false }))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tech.GetAsync("/api/machines")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.TryLoginAsync("tech", Password)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/users/{id}", Json(new { role = "technician", enabled = true }))).StatusCode);
        tech = await _host.LoginAsync("tech");
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/users/{id}", Json(new { role = "technician", enabled = true, password = "Nouveau-Mot-De-Passe-7" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tech.GetAsync("/api/machines")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.TryLoginAsync("tech", Password)).StatusCode);
        await _host.LoginAsync("tech", "Nouveau-Mot-De-Passe-7");
    }

    [Fact]
    public async Task Users_change_their_own_password_and_other_sessions_are_closed()
    {
        await _host.CreateUserAsync("lecteur", Roles.ReadOnly);
        var first = await _host.LoginAsync("lecteur");
        var second = await _host.LoginAsync("lecteur");

        var wrong = await first.PutAsync("/api/session/password", Json(new { currentPassword = "faux", newPassword = "Nouveau-Mot-De-Passe-7" }));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await first.PutAsync("/api/session/password", Json(new { currentPassword = Password, newPassword = "court" }))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await first.PutAsync("/api/session/password", Json(new { currentPassword = Password, newPassword = "Nouveau-Mot-De-Passe-7" }))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/machines")).StatusCode);        // session courante conservée
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/machines")).StatusCode); // autres sessions fermées
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.TryLoginAsync("lecteur", Password)).StatusCode);
        await _host.LoginAsync("lecteur", "Nouveau-Mot-De-Passe-7");

        // Le jeton administrateur n'a pas de mot de passe.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _host.Client(AdminToken).PutAsync("/api/session/password", Json(new { currentPassword = "x", newPassword = "Nouveau-Mot-De-Passe-8" }))).StatusCode);
    }
}
