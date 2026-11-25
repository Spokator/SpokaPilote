using System.Windows;
using Microsoft.Win32;

namespace SpokaPilote.App;

/// <summary>Assistant de premier lancement : présentation, type d'usage (poste / parc), première analyse.</summary>
public partial class OnboardingWindow : Window
{
    private int _step = 1;

    public OnboardingWindow()
    {
        InitializeComponent();
        Localization.Loc.Translate(this);
    }

    /// <summary>Choix de l'utilisateur, appliqués par la fenêtre principale.</summary>
    public bool FleetMode => UseFleet.IsChecked == true;
    public string Repository => RepoBox.Text.Trim();
    public string ServerUrl => ServerBox.Text.Trim();
    public string ServerToken => TokenBox.Text.Trim();
    public bool StartScan => ScanNow.IsChecked == true;

    /// <summary>Affiche directement une étape (mode capture d'écran).</summary>
    public void GoToStep(int step, bool fleet = false)
    {
        _step = Math.Clamp(step, 1, 3);
        UseFleet.IsChecked = fleet;
        UseSingle.IsChecked = !fleet;
        ShowStep();
    }

    private void ShowStep()
    {
        Step1.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        StepLabel.Text = Localization.Loc.T($"Étape {_step} sur 3");
        BackButton.IsEnabled = _step > 1;
        NextButton.Content = Localization.Loc.T(_step == 3 ? "Commencer" : "Suivant");
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_step == 2 && FleetMode && ServerUrl.Length > 0 &&
            !ServerUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !ServerUrl.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, Localization.Loc.T("L'adresse de la console doit commencer par https:// (connexion chiffrée)."),
                "SpokaPilote", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_step == 3)
        {
            DialogResult = true;
            return;
        }
        _step++;
        ShowStep();
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_step <= 1) return;
        _step--;
        ShowStep();
    }

    private void OnSkip(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnUseChanged(object sender, RoutedEventArgs e) =>
        FleetOptions.Visibility = UseFleet.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void OnBrowseRepo(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Dossier ou partage contenant vos pilotes" };
        if (dialog.ShowDialog(this) == true) RepoBox.Text = dialog.FolderName;
    }
}
