using System.Security.Cryptography;
using System.Text;

namespace SpokaPilote.Server.Security;

[Flags]
public enum Access
{
    None = 0,
    /// <summary>Poste enrôlé (jeton d'enrôlement SPOKAPILOTE_AGENT_TOKEN).</summary>
    Agent = 1,
    /// <summary>Console et API d'administration (jeton SPOKAPILOTE_ADMIN_TOKEN).</summary>
    Admin = 2,
}

/// <summary>
/// Jetons d'accès : administrateur (console) et enrôlement (postes). Fournis par variable d'environnement, sinon
/// générés au premier démarrage et conservés dans le dossier de données. Seules leurs empreintes SHA-256 restent
/// en mémoire pour la comparaison, faite en temps constant.
/// </summary>
public sealed class TokenService
{
    public const string AdminVariable = "SPOKAPILOTE_ADMIN_TOKEN";
    public const string AgentVariable = "SPOKAPILOTE_AGENT_TOKEN";
    public const string AdminFile = "admin-token.txt";
    public const string AgentFile = "agent-token.txt";
    private const int MinimumLength = 24;

    private readonly byte[] _adminHash;
    private readonly byte[] _agentHash;

    /// <summary>Jeton d'enrôlement en clair : affiché aux administrateurs dans la page « Enrôlement ».</summary>
    public string AgentToken { get; }

    public TokenService(string adminToken, string agentToken)
    {
        if (string.IsNullOrWhiteSpace(adminToken) || string.IsNullOrWhiteSpace(agentToken))
            throw new ArgumentException("Les jetons ne peuvent pas être vides.");
        if (string.Equals(adminToken, agentToken, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{AdminVariable} et {AgentVariable} doivent être différents : le jeton d'enrôlement est copié sur chaque poste.");
        _adminHash = Hash(adminToken);
        _agentHash = Hash(agentToken);
        AgentToken = agentToken;
    }

    public static TokenService Initialize(IConfiguration config, ServerOptions options, ILogger logger) => new(
        LoadOrCreate(config, AdminVariable, AdminFile, "administrateur", options, logger),
        LoadOrCreate(config, AgentVariable, AgentFile, "d'enrôlement des postes", options, logger));

    /// <summary>Droits accordés par l'en-tête « Authorization: Bearer &lt;jeton&gt; » (Access.None si absent ou inconnu).</summary>
    public Access Authenticate(string? authorizationHeader)
    {
        var token = ExtractBearer(authorizationHeader);
        if (token is null) return Access.None;
        var hash = Hash(token);
        // Les deux comparaisons sont toujours effectuées : le temps de réponse ne révèle rien.
        var isAdmin = CryptographicOperations.FixedTimeEquals(hash, _adminHash);
        var isAgent = CryptographicOperations.FixedTimeEquals(hash, _agentHash);
        return isAdmin ? Access.Admin : isAgent ? Access.Agent : Access.None;
    }

    internal static string? ExtractBearer(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var parts = header.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 && parts[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase) && parts[1].Length > 0 ? parts[1] : null;
    }

    /// <summary>Jeton aléatoire de 256 bits, encodé en base64url (43 caractères, sans caractère à échapper).</summary>
    public static string Generate() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string LoadOrCreate(IConfiguration config, string variable, string fileName, string label, ServerOptions options, ILogger logger)
    {
        var fromEnv = config[variable]?.Trim();
        if (!string.IsNullOrEmpty(fromEnv))
        {
            if (fromEnv.Length < MinimumLength)
                logger.LogWarning("Le jeton {Label} fourni par {Variable} est court ({Length} caractères) : {Minimum} au moins sont recommandés.",
                    label, variable, fromEnv.Length, MinimumLength);
            else
                logger.LogInformation("Jeton {Label} fourni par la variable {Variable}.", label, variable);
            return fromEnv;
        }

        var path = Path.Combine(options.DataDirectory, fileName);
        if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } stored)
        {
            logger.LogInformation("Jeton {Label} lu dans {Path}.", label, path);
            return stored;
        }

        var token = Generate();
        Directory.CreateDirectory(options.DataDirectory);
        File.WriteAllText(path, token + Environment.NewLine);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        logger.LogWarning("Jeton {Label} généré (aucune variable {Variable}) : {Token} — conservé dans {Path}.", label, variable, token, path);
        return token;
    }
}
