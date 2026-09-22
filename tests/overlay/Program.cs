using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Translumo.Local;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

internal static class Program
{
    private static void CheckThaiWrapping()
    {
        var text = new TextBlock { FontFamily = new FontFamily("Leelawadee UI"), FontSize = 20,
            Language = System.Windows.Markup.XmlLanguage.GetLanguage("th-TH"), TextWrapping = TextWrapping.NoWrap };
        const string caption = "ดังนั้นซาตโต";
        var words = SubtitleOverlay.ThaiWords(caption);
        Require(string.Concat(words) == caption, "Thai segmentation must preserve the original characters.");
        Require(SubtitleOverlay.WrapWords(text, words, 70), "A short Thai caption must fit on two lines.");
        Require(text.Text.Replace("\r", "") == "ดังนั้น\nซาตโต", "Balanced Thai wrapping must not strand half of a name.");
        text.FontSize = 14;
        const string university = "มหาวิทยาลัยแพทย์ของอาวเวอร์ซู";
        Require(SubtitleOverlay.WrapWords(text, SubtitleOverlay.ThaiWords(university), 70),
            "A long Thai caption must wrap without splitting dictionary words.");
        Require(text.Text.Contains("มหาวิทยาลัย"), "Thai university word must stay intact.");
        Require(text.Text.Replace("\r", "").Replace("\n", "") == university, "Wrapping must not lose text.");
        foreach (string line in text.Text.Split('\n'))
        {
            var measure = new TextBlock { Text = line.TrimEnd('\r'), FontFamily = text.FontFamily,
                FontSize = text.FontSize, Language = text.Language, TextWrapping = TextWrapping.NoWrap };
            measure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Require(measure.DesiredSize.Width <= 70, "Every Thai line must fit without clipping.");
        }
        Require(!SubtitleOverlay.WrapWords(text, new[] { "มหาวิทยาลัย" }, 10),
            "A word wider than the caption must request another width or font size, never split characters.");
        const string decomposed = "ทํางานอย่างจริงจัง";
        Require(string.Concat(SubtitleOverlay.ThaiWords(decomposed)) == decomposed,
            "Segmentation must preserve decomposed Thai marks instead of silently normalizing the caption.");
        var hyCaptions = new[] {
            "วันนี้ขอร้องให้คุณมาทำงานพาร์ทไทม์ในตอนค่ำที่โรงพยาบาลของเราด้วย.",
            "แล้วก็ ซาโต้คุง", "ไซโตะ เอ็นจิโร่ อายุ 25 ปี", "นั่นแหละคือ “เอลิเต้” คนนั้น...",
            "ยงไตจบการศึกษาเหรอ...", "นี่เป็นครั้งแรกที่ฉันมาประจำการในโรงพยาบาลนี้...",
            "จบการศึกษาจากคณะแพทยศาสตร์ มหาวิทยาลัยเอ็นโรกุ...", "โรงพยาบาลเชิงดุล"
        };
        foreach (string hyCaption in hyCaptions)
        {
            var thaiWords = SubtitleOverlay.ThaiWords(hyCaption);
            Require(string.Concat(thaiWords) == hyCaption, "HY caption segmentation must preserve every original character.");
            var elements = StringInfo.ParseCombiningCharacters(hyCaption).ToHashSet();
            int offset = 0;
            foreach (string word in thaiWords)
            {
                Require(elements.Contains(offset) && !IsMark(word), "Thai dictionary boundaries must not separate a tone mark from its base.");
                offset += word.Length;
            }
            foreach (double font in new[] { 12d, 14d, 18d, 20d })
                foreach (double width in new[] { 60d, 70d, 80d, 100d, 120d, 140d })
                {
                    text.FontSize = font;
                    if (!SubtitleOverlay.WrapWords(text, thaiWords, width)) continue;
                    Require(text.Text.Split('\n').All(line => !IsMark(line.TrimStart())),
                        "No narrow HY caption line may start with an unattached combining mark.");
                }
        }
        Console.WriteLine("All eight HY captions retain grapheme clusters at 12-20 DIP and six narrow caption widths.");

        static bool IsMark(string value) => value.Length > 0 && CharUnicodeInfo.GetUnicodeCategory(value, 0)
            is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
        Console.WriteLine("Thai word boundaries, balanced lines, preserved text and no-clipping checks passed.");
    }

    [STAThread]
    private static void Main(string[] args)
    {
        SetProcessDpiAwarenessContext(new nint(-4));
        CheckThaiWrapping();
        SubtitleLayout.SelfCheck();
        Console.WriteLine("Subtitle geometry checks passed.");
        if (!args.Contains("--visual")) return;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var screen = Forms.Screen.PrimaryScreen!.Bounds;
        var capture = new Drawing.Rectangle(screen.Left + 80, screen.Top + 60, 720, Math.Min(800, screen.Height - 100));
        var regions = new[] {
            new TextRegion("ここはどこ？", new Drawing.Rectangle(80, 80, 180, 55)),
            new TextRegion("一緒に行こう！", new Drawing.Rectangle(360, 260, 220, 55)),
            new TextRegion("君を待っていた。", new Drawing.Rectangle(100, 480, 240, 60))
        };
        var translations = new[] { "Where am I?", "Let's go together!", "I have been waiting for you." };
        var sourceVisual = new DrawingVisual();
        using (var drawing = sourceVisual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.LightSteelBlue, null, new Rect(0, 0, capture.Width, capture.Height));
            foreach (var region in regions)
            {
                var bounds = region.Bounds;
                drawing.DrawRoundedRectangle(Brushes.White, new Pen(Brushes.Black, 2),
                    new Rect(bounds.X - 25, bounds.Y - 25, bounds.Width + 50, bounds.Height + 50), 35, 35);
                drawing.DrawText(new FormattedText(region.Text, CultureInfo.GetCultureInfo("ja-JP"),
                    FlowDirection.LeftToRight, new Typeface("Yu Gothic"), 24, Brushes.DarkGreen, 1),
                    new Point(bounds.X, bounds.Y + 10));
            }
        }
        var source = new RenderTargetBitmap(capture.Width, capture.Height, 96, 96, PixelFormats.Pbgra32);
        source.Render(sourceVisual);
        var page = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Topmost = true, Content = new Image { Source = source, Stretch = Stretch.Fill } };
        page.Show();
        SetWindowPos(new WindowInteropHelper(page).Handle, new nint(-1), capture.X, capture.Y,
            capture.Width, capture.Height, 0x0010);
        Pump(app);
        using var baseline = Screenshot(capture);
        var overlay = new SubtitleOverlay();
        try
        {
            foreach (var style in new[] { SubtitleStyle.Overwrite, SubtitleStyle.Overlay })
            {
                overlay.Render(capture, regions, translations, style);
                Pump(app);
                overlay.UpdateLayout();
                var hwnd = new WindowInteropHelper(overlay).Handle;
                Require(GetWindowDisplayAffinity(hwnd, out uint affinity) && affinity == 0x11,
                    "Subtitle window must be excluded from capture.");
                Require((GetWindowLong(hwnd, -20) & 0x080000A0) == 0x080000A0,
                    "Subtitle window must be click-through, no-activate, and absent from the taskbar.");
                var canvas = (Canvas)overlay.Content;
                foreach (var border in canvas.Children.OfType<Border>().Where(b => b.Child is TextBlock))
                {
                    var text = (TextBlock)border.Child;
                    text.Measure(new Size(text.ActualWidth, double.PositiveInfinity));
                    Require(text.DesiredSize.Height <= text.ActualHeight + 0.1,
                        "Translation text must fit its allocated caption height.");
                    Require(border.Background == Brushes.White, "Overwrite captions must remain opaque.");
                }
                using var captured = Screenshot(capture);
                Require(Identical(baseline, captured), "Screen capture must contain the original page without subtitle feedback.");
                Require(SetWindowDisplayAffinity(hwnd, 0), "Cannot enable the native visual check.");
                try
                {
                    DwmFlush();
                    using var actual = Screenshot(capture);
                    int visibleSourcePixels = CountGreen(actual);
                    Require(CountGreen(baseline) > 100, "The fixture must have visible original-language pixels.");
                    Require(style == SubtitleStyle.Overwrite ? visibleSourcePixels == 0 : visibleSourcePixels == CountGreen(baseline),
                        "Native subtitle pixels must hide all originals in overwrite and retain all originals beside.");
                }
                finally { Require(SetWindowDisplayAffinity(hwnd, 0x11), "Cannot restore capture exclusion."); }

                var desktop = Forms.SystemInformation.VirtualScreen;
                var device = PresentationSource.FromVisual(overlay)!.CompositionTarget!.TransformFromDevice;
                var overlayBitmap = new RenderTargetBitmap(desktop.Width, desktop.Height,
                    96 / device.M11, 96 / device.M22, PixelFormats.Pbgra32);
                overlayBitmap.Render(overlay);
                var combined = new DrawingVisual();
                using (var drawing = combined.RenderOpen())
                {
                    drawing.DrawImage(source, new Rect(0, 0, capture.Width, capture.Height));
                    drawing.DrawImage(overlayBitmap,
                        new Rect(desktop.X - capture.X, desktop.Y - capture.Y, desktop.Width, desktop.Height));
                }
                var result = new RenderTargetBitmap(capture.Width, capture.Height, 96, 96, PixelFormats.Pbgra32);
                result.Render(combined);
                Directory.CreateDirectory(".cache/verification");
                var output = Path.GetFullPath($".cache/verification/subtitle-{style.ToString().ToLowerInvariant()}.png");
                using var stream = File.Create(output);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(result));
                encoder.Save(stream);
                Console.WriteLine($"{style}: text fits; native behavior and actual capture exclusion passed. {output}");
                overlay.Clear();
                Pump(app);
            }
            overlay.Preparing(capture);
            Pump(app);
            using (var preparing = VisibleScreenshot(overlay, capture))
                Require(CountGreen(preparing) == 0, "Preparing must hide all original Japanese glyphs.");
            overlay.Render(capture, regions, translations, SubtitleStyle.Overwrite, frame: source);
            Pump(app);
            page.Content = new Border { Background = Brushes.LightPink };
            Pump(app);
            using (var held = VisibleScreenshot(overlay, capture))
            {
                Require(held.GetPixel(10, 10).ToArgb() == baseline.GetPixel(10, 10).ToArgb(),
                    "Holding must preserve artwork while underlying content changes.");
                Require(CountGreen(held) == 0, "Held pages must retain source-text masks.");
            }
            page.Topmost = false;
            var controls = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, Background = Brushes.LimeGreen };
            try
            {
                controls.Show();
                var controlsHandle = new WindowInteropHelper(controls).Handle;
                overlay.ControlsHandle = controlsHandle;
                SetWindowPos(controlsHandle, nint.Zero, capture.X + 20, capture.Y + 20, 120, 90, 0x0010 | 0x0004);
                overlay.RefreshControlExclusion(); Pump(app);
                using (var hole = VisibleScreenshot(overlay, capture))
                    Require(hole.GetPixel(40, 40).ToArgb() == Drawing.Color.LimeGreen.ToArgb(),
                        "Live controls must remain visible through the held page.");
                SetWindowPos(controlsHandle, nint.Zero, capture.X + 150, capture.Y + 30, 120, 90, 0x0010 | 0x0004);
                overlay.RefreshControlExclusion(); Pump(app);
                using (var moved = VisibleScreenshot(overlay, capture))
                {
                    Require(moved.GetPixel(170, 50).ToArgb() == Drawing.Color.LimeGreen.ToArgb(), "Control cutout must follow movement.");
                    Require(moved.GetPixel(40, 40).ToArgb() == baseline.GetPixel(40, 40).ToArgb(), "Old control location must be covered again.");
                }
                controls.WindowState = WindowState.Minimized;
                overlay.RefreshControlExclusion(); Pump(app);
                using (var minimized = VisibleScreenshot(overlay, capture))
                    Require(minimized.GetPixel(170, 50).ToArgb() == baseline.GetPixel(170, 50).ToArgb(), "Minimized controls must leave no hole.");
            }
            finally { controls.Close(); }
            Console.WriteLine("Preparing, held artwork, hidden originals, and moving/minimized control cutouts passed.");
        }
        finally { overlay.Close(); page.Close(); app.Shutdown(); }
    }

    private static void Pump(Application app)
    {
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        // Allow WPF's render thread to submit the new visual frame before native pixel assertions.
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static Drawing.Bitmap Screenshot(Drawing.Rectangle bounds)
    {
        var bitmap = new Drawing.Bitmap(bounds.Width, bounds.Height);
        using var graphics = Drawing.Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Location, Drawing.Point.Empty, bounds.Size);
        return bitmap;
    }
    private static bool Identical(Drawing.Bitmap left, Drawing.Bitmap right)
    {
        // Sample every caption and margin; this also detects capture APIs that return black exclusion holes.
        for (int y = 2; y < left.Height; y += 4)
            for (int x = 2; x < left.Width; x += 4)
                if (left.GetPixel(x, y) != right.GetPixel(x, y)) return false;
        return true;
    }
    private static Drawing.Bitmap VisibleScreenshot(SubtitleOverlay overlay, Drawing.Rectangle bounds)
    {
        var hwnd = new WindowInteropHelper(overlay).Handle;
        Require(SetWindowDisplayAffinity(hwnd, 0), "Cannot enable diagnostic capture.");
        try { DwmFlush(); return Screenshot(bounds); }
        finally { Require(SetWindowDisplayAffinity(hwnd, 0x11), "Cannot restore capture exclusion."); }
    }
    private static int CountGreen(Drawing.Bitmap image)
    {
        int count = 0;
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                var pixel = image.GetPixel(x, y);
                if (pixel.G > pixel.R + 15 && pixel.G > pixel.B + 15) count++;
            }
        return count;
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
}



