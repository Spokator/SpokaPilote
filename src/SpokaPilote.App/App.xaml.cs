using System.Windows;
using System.Windows.Threading;
using SpokaPilote.Core.Infrastructure;

namespace SpokaPilote.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Erreur inattendue de l'interface", e.Exception);
        if (Environment.GetCommandLineArgs().Contains("--screenshot"))
        {
            // Mode capture / contrôle automatique : pas de fenêtre bloquante, code d'erreur pour le script.
            e.Handled = true;
            Current.Shutdown(3);
            return;
        }
        MessageBox.Show($"Une erreur inattendue est survenue :\n\n{e.Exception.Message}\n\nLe détail est dans le journal.",
            "SpokaPilote", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
