using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpokaPilote.App.ViewModels;

namespace SpokaPilote.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    private readonly UiPreferences _prefs = UiPreferences.Load();

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            Localization.Loc.Translate(this);
        }
        catch (Exception ex)
        {
            // La traduction ne doit jamais empêcher l'application de s'ouvrir.
            Core.Infrastructure.Log.Error("Traduction de l'interface", ex);
            Core.Infrastructure.Log.Debug(ex.ToString());
        }
        _vm = new MainViewModel();
        DataContext = _vm;
        ContentRendered += OnContentRendered;
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += (_, _) =>
        {
            // Relecture : la langue a pu être changée dans les Paramètres pendant la session.
            var prefs = UiPreferences.Load();
            prefs.LastTab = Tabs.SelectedIndex;
            prefs.OnboardingCompleted |= _prefs.OnboardingCompleted;
            prefs.Save();
        };
    }

    /// <summary>Raccourcis : F5 analyser, Ctrl+1..9 onglets, F1 aide en ligne.</summary>
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        if (e.Key == System.Windows.Input.Key.F5 && _vm.ScanCommand.CanExecute(null))
        {
            _vm.ScanCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.F1)
        {
            _vm.OpenUrlCommand.Execute("https://github.com/Spokator/SpokaPilote#readme");
            e.Handled = true;
        }
        else if (ctrl && e.Key >= System.Windows.Input.Key.D1 && e.Key <= System.Windows.Input.Key.D9)
        {
            var index = e.Key - System.Windows.Input.Key.D1;
            if (index < Tabs.Items.Count)
            {
                Tabs.SelectedIndex = index;
                e.Handled = true;
            }
        }
    }

    /// <summary>Premier lancement : assistant de bienvenue (une seule fois par utilisateur).</summary>
    private void RunOnboardingIfNeeded()
    {
        if (_prefs.OnboardingCompleted)
        {
            if (_prefs.LastTab > 0 && _prefs.LastTab < Tabs.Items.Count) Tabs.SelectedIndex = _prefs.LastTab;
            return;
        }
        var wizard = new OnboardingWindow { Owner = this };
        var accepted = wizard.ShowDialog() == true;
        _prefs.OnboardingCompleted = true;
        _prefs.Save();
        if (!accepted) return;
        if (wizard.FleetMode) _vm.ApplyFleetSetup(wizard.Repository, wizard.ServerUrl, wizard.ServerToken);
        if (wizard.StartScan && _vm.ScanCommand.CanExecute(null)) _vm.ScanCommand.Execute(null);
    }

    /// <summary>
    /// Mode capture (vérification visuelle / visuels pour le Store) :
    /// SpokaPilote.App.exe --screenshot dossier [--scan] — exporte une image PNG de chaque onglet puis quitte.
    /// </summary>
    private async void OnContentRendered(object? sender, EventArgs e)
    {
        var args = Environment.GetCommandLineArgs();
        var idx = Array.IndexOf(args, "--screenshot");
        if (idx < 0 || idx + 1 >= args.Length)
        {
            RunOnboardingIfNeeded();
            await _vm.CheckForUpdateAsync();
            return;
        }
        var folder = args[idx + 1];
        Directory.CreateDirectory(folder);

        if (args.Contains("--scan"))
        {
            _vm.ScanCommand.Execute(null);
            while (_vm.IsBusy || !_vm.HasScan) await Task.Delay(500);
        }

        for (var i = 0; i < Tabs.Items.Count; i++)
        {
            Tabs.SelectedIndex = i;
            await Task.Delay(700);
            Capture(Path.Combine(folder, $"onglet-{i + 1}.png"));
        }

        // Assistant de premier lancement (non modal pour la capture).
        var wizard = new OnboardingWindow { Owner = this, ShowInTaskbar = false };
        wizard.Show();
        foreach (var (step, fleet) in new[] { (1, false), (2, true), (3, false) })
        {
            wizard.GoToStep(step, fleet);
            await Task.Delay(500);
            CaptureElement((FrameworkElement)wizard.Content, wizard, Path.Combine(folder, $"assistant-{step}.png"));
        }
        wizard.Close();
        Application.Current.Shutdown();
    }

    private void Capture(string path) => CaptureElement((FrameworkElement)Content, this, path);

    private static void CaptureElement(FrameworkElement root, Window owner, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(owner);
        var Background = owner.Background;
        var bmp = new RenderTargetBitmap(
            (int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Background ?? SystemColors.WindowBrush, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        bmp.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
