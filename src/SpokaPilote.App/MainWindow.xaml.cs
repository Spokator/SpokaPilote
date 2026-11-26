using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpokaPilote.App.ViewModels;

namespace SpokaPilote.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly UiPreferences _prefs = UiPreferences.Load();
    private readonly string[] _args = Environment.GetCommandLineArgs();

    /// <summary>Mode capture : SpokaPilote.App.exe --screenshot dossier (voir <see cref="RunScreenshotModeAsync"/>).</summary>
    private bool IsScreenshotMode => Array.IndexOf(_args, "--screenshot") is var i && i >= 0 && i + 1 < _args.Length;

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
        // Mode portable (clé USB) : visible dès la barre de titre (détail dans la barre d'état).
        if (Core.Infrastructure.AppPaths.IsPortable) Title += " · " + Localization.Loc.T("mode portable");
        _vm = new MainViewModel(IsScreenshotMode ? Argument("--alias") : null);
        DataContext = _vm;
        if (IsScreenshotMode && Argument("--size") is { } size && TryParseSize(size, out var w, out var h))
        {
            Width = w;
            Height = h;
        }
        ContentRendered += OnContentRendered;
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += (_, _) =>
        {
            if (IsScreenshotMode) return; // les captures ne modifient pas les préférences de l'utilisateur
            // Relecture : la langue a pu être changée dans les Paramètres pendant la session.
            var prefs = UiPreferences.Load();
            prefs.LastPage = MainViewModel.PageKeys[_vm.PageIndex];
            prefs.LastTab = null;
            prefs.OnboardingCompleted |= _prefs.OnboardingCompleted;
            prefs.Save();
        };
    }

    private string? Argument(string name)
    {
        var i = Array.IndexOf(_args, name);
        return i >= 0 && i + 1 < _args.Length ? _args[i + 1] : null;
    }

    private static bool TryParseSize(string text, out double width, out double height)
    {
        width = height = 0;
        var parts = text.Split('x', 'X');
        return parts.Length == 2 && double.TryParse(parts[0], out width) && double.TryParse(parts[1], out height) && width >= 600 && height >= 400;
    }

    /// <summary>Raccourcis : F5 analyser, Ctrl+1..6 pages, F1 aide en ligne.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.F5 && _vm.ScanCommand.CanExecute(null))
        {
            _vm.ScanCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F1)
        {
            _vm.OpenUrlCommand.Execute("https://github.com/Spokator/SpokaPilote#readme");
            e.Handled = true;
        }
        else if (ctrl && (e.Key is >= Key.D1 and <= Key.D9 || e.Key is >= Key.NumPad1 and <= Key.NumPad9))
        {
            var index = e.Key >= Key.NumPad1 ? e.Key - Key.NumPad1 : e.Key - Key.D1;
            if (index < MainViewModel.PageKeys.Length)
            {
                _vm.PageIndex = index;
                FocusNavigation();
                e.Handled = true;
            }
        }
    }

    /// <summary>Après un changement de page au clavier, le focus suit la page choisie dans la navigation.</summary>
    private void FocusNavigation()
    {
        if (Nav.ItemContainerGenerator.ContainerFromIndex(Nav.SelectedIndex) is ListBoxItem item) item.Focus();
    }

    /// <summary>Premier lancement : assistant de bienvenue (une seule fois par utilisateur), sinon dernière page.</summary>
    private void RunOnboardingIfNeeded()
    {
        if (_prefs.OnboardingCompleted)
        {
            if (_prefs.StartPage is { } page) _vm.Navigate(page);
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

    private async void OnContentRendered(object? sender, EventArgs e)
    {
        if (!IsScreenshotMode)
        {
            RunOnboardingIfNeeded();
            await _vm.CheckForUpdateAsync();
            return;
        }
        await RunScreenshotModeAsync(Argument("--screenshot")!);
    }

    /// <summary>
    /// Mode capture (contrôle de l'interface par scripts\publish.ps1, visuels du Store et du site) :
    /// <c>SpokaPilote.App.exe --screenshot dossier [--scan] [--lang en] [--theme dark|light] [--size 1000x660] [--alias PC-DEMO] [--expand]</c>.
    /// Écrit page-N-nom.png pour chaque page de la navigation (page entière en plus dans complet-N-nom.png quand elle
    /// défile), puis assistant-N.png pour chaque étape de l'assistant, et quitte. Toute erreur d'interface : code 3.
    /// --alias remplace le nom de l'ordinateur (et masque le numéro de série) ; --expand ouvre les volets de détail.
    /// Les préférences de l'utilisateur (dernière page, premier lancement) ne sont pas modifiées.
    /// </summary>
    private async Task RunScreenshotModeAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var expand = _args.Contains("--expand");

        if (_args.Contains("--scan"))
        {
            _vm.ScanCommand.Execute(null);
            while (_vm.IsBusy || !_vm.HasScan)
            {
                await Task.Delay(500);
                if (!_vm.IsBusy && _vm.LastFailure is not null) break; // analyse en échec : capturer quand même l'écran d'erreur
            }
        }
        await Task.Delay(500);

        for (var i = 0; i < MainViewModel.PageKeys.Length; i++)
        {
            _vm.PageIndex = i;
            await Task.Delay(400);
            var page = PageHost.Child is Grid pages ? pages.Children[i] as FrameworkElement : null;
            if (expand && page is not null) ExpandDetails(page);
            await Task.Delay(500);
            var name = $"{i + 1}-{MainViewModel.PageKeys[i]}";
            Capture(Path.Combine(folder, $"page-{name}.png"));
            // Page qui défile : capture complète en plus (vérification visuelle de tout le contenu).
            if (page is not null && FindScrollViewer(page) is { Content: FrameworkElement content } scroller
                && content.ActualHeight > scroller.ViewportHeight + 1)
                CaptureElement(content, this, Path.Combine(folder, $"complet-{name}.png"));
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

    /// <summary>Ouvre les volets repliés et sélectionne la première ligne des listes (détail visible dans les captures).</summary>
    private static void ExpandDetails(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is Expander expander) expander.IsExpanded = true;
            if (child is DataGrid grid && grid.Items.Count > 0 && grid.SelectedIndex < 0) grid.SelectedIndex = 0;
            ExpandDetails(child);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is ScrollViewer sv) return sv;
            if (child is Panel or UserControl or Border && FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    private void Capture(string path) => CaptureElement((FrameworkElement)Content, this, path);

    private static void CaptureElement(FrameworkElement root, Window owner, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(owner);
        // Fond de la fenêtre : l'effet Mica est transparent dans une capture ; on le remplace par la couleur de base du thème.
        var background = owner.TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? owner.Background ?? SystemColors.WindowBrush;
        var bmp = new RenderTargetBitmap(
            (int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        bmp.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
