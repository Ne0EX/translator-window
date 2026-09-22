using System;

using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using Rectangle = System.Drawing.Rectangle;

namespace Translumo.Local;

public sealed class LocalWindow : Window
{
    private readonly ComboBox mode = new() { ItemsSource = new[] { "Selected area", "Window", "Screen" }, SelectedIndex = 0 };
    private readonly ComboBox style = new() { ItemsSource = new[] { "Subtitle overlay — beside text", "Subtitle overwrite — cover text" }, SelectedIndex = 1 };
    private readonly ComboBox source = new() { ItemsSource = Languages, DisplayMemberPath = "Name", SelectedValuePath = "Code", SelectedValue = "ja" };
    private readonly ComboBox target = new() { ItemsSource = Languages, DisplayMemberPath = "Name", SelectedValuePath = "Code", SelectedValue = "th" };
    private readonly ComboBox windows = new() { DisplayMemberPath = "Title", MinWidth = 240 };
    private readonly ComboBox screens = new() { DisplayMemberPath = "DeviceName", ItemsSource = Forms.Screen.AllScreens, SelectedIndex = 0 };
    private readonly CheckBox comics = new() { Content = "Manga / webtoon text detection", IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Slider padding = new() { Minimum = 0, Maximum = 30, Value = 6, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly TextBox python = new(), model = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Text = "Choose what to capture, then start." };
    private readonly TextBlock areaDescription = new() { Text = "No area selected", Margin = new Thickness(10, 5, 0, 0) };
    private readonly Button start = new() { Content = "Start translation", Padding = new Thickness(20, 9, 20, 9) };
    private readonly Button stop = new() { Content = "Stop", IsEnabled = false, Padding = new Thickness(20, 9, 20, 9), Margin = new Thickness(10, 0, 0, 0) };
    private readonly StackPanel settings = new();
    private readonly SubtitleOverlay overlay = new();
    private readonly string root;
    private Rectangle area;
    private CancellationTokenSource? cancellation;
    private Task? running;
    private LocalTranslator? translator;
    private bool closing;
    private static readonly LanguageChoice[] Languages =
    {
        new("ja", "Japanese"), new("ko", "Korean"), new("en", "English"), new("th", "Thai")
    };

    public LocalWindow()
    {
        root = FindRoot();
        Title = "Translumo Local";
        Width = 610; Height = 790; MinWidth = 490; MinHeight = 580;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14; Background = Brushes.WhiteSmoke;
        var panel = new StackPanel { Margin = new Thickness(26) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = "Read in your language", FontSize = 28, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "On-device recognition and translation", Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 20) });
        panel.Children.Add(settings);
        AddField("Capture", mode);
        var areaRow = new StackPanel { Orientation = Orientation.Horizontal };
        var selectArea = new Button { Content = "Select area…", Padding = new Thickness(12, 6, 12, 6) };
        selectArea.Click += (_, _) => SelectArea();
        areaRow.Children.Add(selectArea); areaRow.Children.Add(areaDescription); settings.Children.Add(areaRow);
        var windowRow = new DockPanel();
        var refresh = new Button { Content = "Refresh", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(8, 0, 0, 0) };
        refresh.Click += (_, _) => RefreshWindows();
        DockPanel.SetDock(refresh, Dock.Right); windowRow.Children.Add(refresh); windowRow.Children.Add(windows);
        var windowLabel = AddField("Application window (keep visible)", windowRow);
        var screenLabel = AddField("Screen", screens);
        AddField("Source language", source); settings.Children.Add(comics);
        AddField("Translate to", target);
        AddField("Subtitle style", style);
        AddField("Cover padding (0–30 pixels)", padding);
        var modelSettings = new StackPanel();
        modelSettings.Children.Add(new TextBlock { Text = "Python runtime" }); modelSettings.Children.Add(python);
        modelSettings.Children.Add(new TextBlock { Text = "Local translation model folder", Margin = new Thickness(0, 8, 0, 0) }); modelSettings.Children.Add(model);
        settings.Children.Add(new Expander { Header = "Local model setup", Content = modelSettings, Margin = new Thickness(0, 14, 0, 12) });
        python.Text = Path.Combine(root, ".venv", "Scripts", "python.exe");
        model.Text = Path.Combine(root, "models", "hy-mt2");
        LoadSettings();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 16) };
        buttons.Children.Add(start); buttons.Children.Add(stop); panel.Children.Add(buttons); panel.Children.Add(status);
        panel.Children.Add(new TextBlock { Text = "Manga overwrite covers detected text while keeping the page visible.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 10, 0, 0) });
        start.Click += async (_, _) => { running = StartAsync(); await running; };
        stop.Click += (_, _) => cancellation?.Cancel();
        mode.SelectionChanged += (_, _) => { UpdateCaptureControls(); status.Text = "Choose the capture target, then start translation."; };
        void UpdateCaptureControls()
        {
            areaRow.Visibility = mode.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            windowRow.Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            windowLabel.Visibility = windowRow.Visibility;
            screens.Visibility = screenLabel.Visibility = mode.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateCaptureControls(); RefreshWindows();
        SourceInitialized += (_, _) =>
        {
            overlay.ControlsHandle = new WindowInteropHelper(this).Handle;
            if (!ScreenCapture.ExcludeFromCapture(overlay.ControlsHandle))
                status.Text = "This Windows version cannot exclude controls from capture. Keep this window outside the capture area.";
        };
        LocationChanged += (_, _) => overlay.RefreshControlExclusion();
        SizeChanged += (_, _) => overlay.RefreshControlExclusion();
        StateChanged += (_, _) => overlay.RefreshControlExclusion();
        Closing += async (_, args) =>
        {
            if (closing) return;
            args.Cancel = true; closing = true;
            cancellation?.Cancel();
            if (running is not null) await running;
            translator?.Dispose(); overlay.Close(); SaveSettings(); Close();
        };
    }

    private TextBlock AddField(string label, UIElement control)
    {
        var caption = new TextBlock { Text = label, Margin = new Thickness(0, 10, 0, 4) };
        settings.Children.Add(caption);
        System.Windows.Automation.AutomationProperties.SetName(control, label);
        settings.Children.Add(control);
        return caption;
    }

    private void RefreshWindows()
    {
        var old = windows.SelectedItem as WindowTarget;
        windows.ItemsSource = ScreenCapture.ListWindows();
        windows.SelectedItem = windows.Items.Cast<WindowTarget>().FirstOrDefault(w => w.Handle == old?.Handle);
        if (windows.SelectedItem is null && windows.Items.Count > 0) windows.SelectedIndex = 0;
    }

    private void SelectArea()
    {
        Hide();
        try
        {
            var selector = new SelectionAreaWindow { WindowState = WindowState.Normal, Topmost = true };
            selector.SourceInitialized += (_, _) =>
            {
                var rect = ScreenCapture.VirtualBounds;
                NativePosition.Place(new WindowInteropHelper(selector).Handle, rect);
            };
            if (selector.ShowDialog() == true)
            {
                area = Rectangle.Round(selector.SelectedArea);
                areaDescription.Text = $"{area.Width} × {area.Height} at {area.X}, {area.Y}";
            }
        }
        finally { Show(); Activate(); }
    }

    private async Task StartAsync()
    {
        try
        {
            Func<Rectangle?> getBounds;
            if (mode.SelectedIndex == 0)
            {
                if (area.Width < 4 || area.Height < 4) throw new InvalidOperationException("Select an area first.");
                getBounds = () => Rectangle.Intersect(area, ScreenCapture.VirtualBounds);
            }
            else if (mode.SelectedIndex == 1)
            {
                if (windows.SelectedItem is not WindowTarget selected) throw new InvalidOperationException("Select a visible application window.");
                getBounds = () => ScreenCapture.GetWindowBounds(selected.Handle);
            }
            else
            {
                if (screens.SelectedItem is not Forms.Screen selected) throw new InvalidOperationException("Select a screen.");
                getBounds = () => Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == selected.DeviceName)?.Bounds;
            }
            if (!File.Exists(python.Text) || !Directory.Exists(model.Text) || Directory.EnumerateFiles(model.Text, "*.gguf").Count() != 1)
                throw new InvalidOperationException("Install the local model first: run local-model/setup.ps1 in the project folder, then verify the paths under Local model setup.");
            SaveSettings();
            translator?.Dispose();
            translator = new LocalTranslator(python.Text, Path.Combine(root, "local-model", "worker.py"), model.Text);
            cancellation = new CancellationTokenSource();
            settings.IsEnabled = false; start.IsEnabled = false; stop.IsEnabled = true;
            var sourceCode = (string)source.SelectedValue;
            if (comics.IsChecked == true) sourceCode += "-comic";
            bool hideOriginals = comics.IsChecked == true && style.SelectedIndex == 1;
            status.Text = "Loading the local model…";
            var ocr = new SpatialOcr(Path.Combine(root, "models", "tessdata"), python.Text,
                Path.Combine(root, "local-ocr", "worker.py"), Path.Combine(root, "models", "comic-text-detector", "comictextdetector.onnx"));
            var session = new LiveTranslationSession(translator, overlay, text => status.Text = text, ocr);
            await session.RunAsync(getBounds, sourceCode, (string)target.SelectedValue,
                style.SelectedIndex == 0 ? SubtitleStyle.Overlay : SubtitleStyle.Overwrite, (int)padding.Value, cancellation.Token, hideOriginals);
        }
        catch (OperationCanceledException) { status.Text = "Translation stopped."; }
        catch (Exception error) { status.Text = error.Message; }
        finally
        {
            overlay.Clear(); cancellation?.Dispose(); cancellation = null;
            translator?.Dispose(); translator = null;
            settings.IsEnabled = true; start.IsEnabled = true; stop.IsEnabled = false;
        }
    }

    private static string FindRoot()
    {
        foreach (var startPath in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var path = new DirectoryInfo(startPath); path is not null; path = path.Parent)
                if (File.Exists(Path.Combine(path.FullName, "local-model", "worker.py")) && Directory.Exists(Path.Combine(path.FullName, ".venv"))) return path.FullName;
        return AppContext.BaseDirectory;
    }

    private string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TranslumoLocal", "settings.json");

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var saved = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(SettingsPath));
            if (saved is null) return;
            if (Languages.Any(l => l.Code == saved.Source)) source.SelectedValue = saved.Source;
            if (Languages.Any(l => l.Code == saved.Target)) target.SelectedValue = saved.Target;
            if (!string.IsNullOrWhiteSpace(saved.Python)) python.Text = saved.Python;
            if (!string.IsNullOrWhiteSpace(saved.Model)) model.Text = saved.Model;
            style.SelectedIndex = saved.Overwrite ? 1 : 0; comics.IsChecked = saved.Comics;
            padding.Value = Math.Clamp(saved.Padding, 0, 30);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        { status.Text = "Saved settings could not be read. Using defaults."; }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var text = JsonSerializer.Serialize(new SavedSettings((string)source.SelectedValue, (string)target.SelectedValue,
                python.Text, model.Text, style.SelectedIndex == 1, (int)padding.Value, comics.IsChecked == true));
            File.WriteAllText(SettingsPath + ".tmp", text);
            File.Move(SettingsPath + ".tmp", SettingsPath, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { status.Text = "Settings could not be saved: " + error.Message; }
    }

    private sealed record LanguageChoice(string Code, string Name);
    private sealed record SavedSettings(string Source, string Target, string Python, string Model, bool Overwrite, int Padding, bool Comics = true);
}

internal static class NativePosition
{
    public static void Place(nint handle, Rectangle bounds)
    {
        if (!SetWindowPos(handle, new nint(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0040))
            throw new System.ComponentModel.Win32Exception();
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
}
