using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SpokaPilote.Server.Security;

namespace SpokaPilote.Server.Tests;

public sealed class TokenServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-server-tokens-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* dossier temporaire */ }
    }

    private TokenService Init(Dictionary<string, string?>? values = null) => TokenService.Initialize(
        new ConfigurationBuilder().AddInMemoryCollection(values ?? new()).Build(),
        new ServerOptions { DataDirectory = _dir },
        NullLogger.Instance);

    [Fact]
    public void Authenticates_bearer_tokens_by_role()
    {
        var t = new TokenService("jeton-admin-aaaaaaaaaaaaaaaaaaaa", "jeton-agent-bbbbbbbbbbbbbbbbbbbb");
        Assert.Equal(Access.Admin, t.Authenticate("Bearer jeton-admin-aaaaaaaaaaaaaaaaaaaa"));
        Assert.Equal(Access.Agent, t.Authenticate("bearer   jeton-agent-bbbbbbbbbbbbbbbbbbbb "));
        Assert.Equal(Access.None, t.Authenticate("Bearer jeton-admin-aaaaaaaaaaaaaaaaaaab"));
        Assert.Equal(Access.None, t.Authenticate("Basic jeton-admin-aaaaaaaaaaaaaaaaaaaa"));
        Assert.Equal(Access.None, t.Authenticate("jeton-admin-aaaaaaaaaaaaaaaaaaaa"));
        Assert.Equal(Access.None, t.Authenticate(null));
        Assert.Equal(Access.None, t.Authenticate("Bearer "));
    }

    [Fact]
    public void Admin_and_agent_tokens_must_differ() =>
        Assert.Throws<InvalidOperationException>(() => new TokenService("meme-jeton-123456789012345", "meme-jeton-123456789012345"));

    [Fact]
    public void Generates_tokens_once_and_reuses_them_after_restart()
    {
        var first = Init();
        var adminFile = Path.Combine(_dir, TokenService.AdminFile);
        var admin = File.ReadAllText(adminFile).Trim();
        Assert.True(admin.Length >= 43);
        Assert.Equal(Access.Admin, first.Authenticate($"Bearer {admin}"));
        Assert.Equal(Access.Agent, first.Authenticate($"Bearer {first.AgentToken}"));

        var second = Init();
        Assert.Equal(first.AgentToken, second.AgentToken);
        Assert.Equal(Access.Admin, second.Authenticate($"Bearer {admin}"));
    }

    [Fact]
    public void Environment_variables_take_precedence_over_stored_tokens()
    {
        Init(); // génère les fichiers
        var t = Init(new()
        {
            [TokenService.AdminVariable] = "admin-depuis-variable-0123456789",
            [TokenService.AgentVariable] = "agent-depuis-variable-0123456789",
        });
        Assert.Equal(Access.Admin, t.Authenticate("Bearer admin-depuis-variable-0123456789"));
        Assert.Equal("agent-depuis-variable-0123456789", t.AgentToken);
        Assert.Equal(Access.None, t.Authenticate($"Bearer {File.ReadAllText(Path.Combine(_dir, TokenService.AdminFile)).Trim()}"));
    }

    [Fact]
    public void Generated_tokens_are_url_safe_and_unique()
    {
        var a = TokenService.Generate();
        var b = TokenService.Generate();
        Assert.NotEqual(a, b);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", a);
    }
}
