using System.Windows;
using System.Windows.Controls;
using SpokaPilote.App.ViewModels;

namespace SpokaPilote.App.Views;

/// <summary>Page « Paramètres » de la fenêtre principale (contexte de données : sa vue-modèle).</summary>
public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SettingsViewModel old) old.TokenSaved -= ClearToken;
            if (e.NewValue is SettingsViewModel vm) vm.TokenSaved += ClearToken;
        };
    }

    /// <summary>PasswordBox (non liable) : le nouveau jeton saisi est transmis à la vue-modèle, jamais réaffiché.</summary>
    private void OnTokenChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.TokenInput = TokenBox.Password;
    }

    private void ClearToken() => TokenBox.Clear();
}
