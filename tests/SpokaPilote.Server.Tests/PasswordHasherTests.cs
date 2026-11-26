using System.Globalization;
using SpokaPilote.Server.Security;

namespace SpokaPilote.Server.Tests;

/// <summary>Empreintes de mots de passe PBKDF2-SHA256.</summary>
public sealed class PasswordHasherTests
{
    [Fact]
    public void Hash_then_verify_round_trips_and_rejects_wrong_passwords()
    {
        var hash = PasswordHasher.Hash("Correct-Horse-Battery");
        Assert.True(PasswordHasher.Verify("Correct-Horse-Battery", hash, out var upgrade));
        Assert.False(upgrade);
        Assert.False(PasswordHasher.Verify("correct-horse-battery", hash, out _)); // sensible à la casse
        Assert.False(PasswordHasher.Verify("Correct-Horse-Battery ", hash, out _));
        Assert.False(PasswordHasher.Verify("", hash, out _));
        Assert.False(PasswordHasher.Verify(null, hash, out _));
    }

    [Fact]
    public void Format_uses_sha256_enough_iterations_and_a_random_salt_per_hash()
    {
        var a = PasswordHasher.Hash("Meme-Mot-De-Passe");
        var b = PasswordHasher.Hash("Meme-Mot-De-Passe");
        Assert.NotEqual(a, b); // sel propre à chaque empreinte
        var parts = a.Split('$');
        Assert.Equal(4, parts.Length);
        Assert.Equal("pbkdf2-sha256", parts[0]);
        Assert.True(int.Parse(parts[1], CultureInfo.InvariantCulture) >= 100_000);
        Assert.Equal(16, Convert.FromBase64String(parts[2]).Length);
        Assert.Equal(32, Convert.FromBase64String(parts[3]).Length);
        Assert.NotEqual(a.Split('$')[2], b.Split('$')[2]);
    }

    [Fact]
    public void Older_hashes_with_fewer_iterations_verify_and_ask_for_an_upgrade()
    {
        var old = PasswordHasher.Hash("Ancien-Mot-De-Passe", 120_000);
        Assert.True(PasswordHasher.Verify("Ancien-Mot-De-Passe", old, out var upgrade));
        Assert.True(upgrade);
    }

    [Theory]
    [InlineData("")]
    [InlineData("md5$1$abc$def")]
    [InlineData("pbkdf2-sha256$abc$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("pbkdf2-sha256$1000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")] // trop peu d'itérations
    [InlineData("pbkdf2-sha256$310000$pas-du-base64$AAAA")]
    public void Malformed_or_weak_hashes_are_always_rejected(string stored) =>
        Assert.False(PasswordHasher.Verify("nimporte-quoi", stored, out _));

    [Fact]
    public void Tampered_hash_is_rejected()
    {
        var parts = PasswordHasher.Hash("Mot-De-Passe-Solide").Split('$');
        var bytes = Convert.FromBase64String(parts[3]);
        bytes[0] ^= 1;
        parts[3] = Convert.ToBase64String(bytes);
        Assert.False(PasswordHasher.Verify("Mot-De-Passe-Solide", string.Join('$', parts), out _));
    }

    [Theory]
    [InlineData("court", "jdupont", false)]
    [InlineData("aaaaaaaaaaaa", "jdupont", false)]       // trop simple
    [InlineData("jdupont-2026!", "jdupont", false)]      // contient l'identifiant
    [InlineData("Un-Mot-De-Passe-Correct", "jdupont", true)]
    public void Password_policy(string password, string login, bool accepted) =>
        Assert.Equal(accepted, PasswordHasher.CheckPolicy(password, login) is null);

    [Fact]
    public void Dummy_verification_never_succeeds() => PasswordHasher.VerifyDummy("quoi-que-ce-soit");
}
