using System.Runtime.CompilerServices;
using SpokaPilote.Core.Infrastructure;

namespace SpokaPilote.Core.Tests;

/// <summary>
/// Isole les tests des données réelles du poste : sans SPOKAPILOTE_HOME, les journaux, l'historique ou les réglages
/// écrits pendant les tests iraient dans %ProgramData%\SpokaPilote. Exécuté au chargement de l'assembly, avant tout test
/// et avant le premier calcul (mis en cache) des emplacements par <see cref="AppPaths"/>.
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void IsolateDataFolder()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.HomeVariable))) return;
        var home = Path.Combine(Path.GetTempPath(), "SpokaPilote.Tests", Environment.ProcessId.ToString());
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable(AppPaths.HomeVariable, home);
    }
}
