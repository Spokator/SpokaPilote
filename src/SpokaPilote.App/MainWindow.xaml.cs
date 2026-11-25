using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpokaPilote.App.ViewModels;

namespace SpokaPilote.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        ContentRendered += OnContentRendered;
    }

    /// <summary>
    /// Mode capture (vérification visuelle / visuels pour le Store) :
    /// SpokaPilote.exe --screenshot dossier [--scan] — exporte une image PNG de chaque onglet puis quitte.
    /// </summary>
    private async void OnContentRendered(object? sender, EventArgs e)
    {
        var args = Environment.GetCommandLineArgs();
        var idx = Array.IndexOf(args, "--screenshot");
        if (idx < 0 || idx + 1 >= args.Length) return;
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
        Application.Current.Shutdown();
    }

    private void Capture(string path)
    {
        var root = (FrameworkElement)Content;
        var dpi = VisualTreeHelper.GetDpi(this);
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
