using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SpokaPilote.Server.Security;

/// <summary>
/// Empreintes de mots de passe : PBKDF2 (RFC 2898) HMAC-SHA-256, sel aléatoire de 128 bits propre à chaque compte,
/// 310 000 itérations, empreinte de 256 bits. Format : « pbkdf2-sha256$itérations$sel$empreinte » (base64), ce qui permet
/// d'augmenter le nombre d'itérations plus tard (l'empreinte est recalculée à la connexion suivante).
/// La comparaison est faite en temps constant.
/// </summary>
public static class PasswordHasher
{
    public const string Algorithm = "pbkdf2-sha256";
    public const int Iterations = 310_000;
    public const int MinimumIterations = 100_000;
    public const int MinimumLength = 10;
    public const int MaximumLength = 256;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>Empreinte factice : vérifiée quand le compte n'existe pas, pour que le temps de réponse ne le révèle pas.</summary>
    private static readonly Lazy<string> Dummy = new(() => Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))));

    public static string Hash(string password, int iterations = Iterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (iterations < MinimumIterations) throw new ArgumentOutOfRangeException(nameof(iterations));
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return string.Join('$', Algorithm, iterations.ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    /// <summary>
    /// Vérifie un mot de passe. <paramref name="needsUpgrade"/> : l'empreinte utilise moins d'itérations que la valeur
    /// actuelle et doit être recalculée. Une empreinte illisible ou trop faible est toujours refusée.
    /// </summary>
    public static bool Verify(string? password, string? stored, out bool needsUpgrade)
    {
        needsUpgrade = false;
        if (password is null || stored is null) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Algorithm ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) || iterations < MinimumIterations)
            return false;
        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }
        if (salt.Length < 8 || expected.Length < 16) return false;
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        var ok = CryptographicOperations.FixedTimeEquals(actual, expected);
        needsUpgrade = ok && iterations < Iterations;
        return ok;
    }

    /// <summary>Calcul équivalent à une vérification réelle (compte inconnu ou désactivé) : résultat toujours faux.</summary>
    public static void VerifyDummy(string? password) => Verify(password ?? "", Dummy.Value, out _);

    /// <summary>Règles de mot de passe ; renvoie le motif du refus (null si acceptable).</summary>
    public static string? CheckPolicy(string? password, string? login)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinimumLength)
            return $"Le mot de passe doit contenir au moins {MinimumLength} caractères.";
        if (password.Length > MaximumLength) return $"Le mot de passe ne peut pas dépasser {MaximumLength} caractères.";
        if (string.IsNullOrWhiteSpace(password)) return "Le mot de passe ne peut pas être composé uniquement d'espaces.";
        if (login is not null && password.Contains(login, StringComparison.OrdinalIgnoreCase))
            return "Le mot de passe ne doit pas contenir l'identifiant.";
        if (password.Distinct().Count() < 4) return "Le mot de passe est trop simple (trop peu de caractères différents).";
        return null;
    }
}
