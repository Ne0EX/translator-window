using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int code = 0;
        app.Startup += async (_, _) =>
        {
            try { await Run(args); }
            catch (Exception error) { Console.Error.WriteLine(error); code = 1; }
            finally { app.Shutdown(); }
        };
        app.Run();
        return code;
    }

    private static async Task Run(string[] args)
    {
        string root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
        string imagePath = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(root, "artifacts/manga-test/page12.png"));
        string output = Path.Combine(root, "artifacts/manga-test");
        Directory.CreateDirectory(output);
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(imagePath); bitmap.EndInit(); bitmap.Freeze();
        Require(bitmap.PixelWidth <= 1300 && bitmap.PixelHeight <= 1800, "The unscaled test page must fit the left-side desktop region.");
        var bounds = new Drawing.Rectangle(40, 60, bitmap.PixelWidth, bitmap.PixelHeight);
        Require(ScreenCapture.VirtualBounds.Contains(bounds), "The desktop must fit the complete unscaled page at (40,60).");
        var image = new Image { Source = bitmap, Stretch = Stretch.Fill };
        var page = new Grid { Background = Brushes.White, ClipToBounds = true };
        page.Children.Add(image);
        var fixture = new Window { Title = "Local Japanese manga acceptance check", WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, Background = Brushes.White, Content = page };
        var overlay = new SubtitleOverlay();
        string python = Path.Combine(root, ".venv/Scripts/python.exe");
        using var translator = new LocalTranslator(python, Path.Combine(root, "local-model/worker.py"), Path.Combine(root, "models/hy-mt2"));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        Task? running = null;
        try
        {
            fixture.Show();
            var handle = new WindowInteropHelper(fixture).Handle;
            Place(handle, bounds);
            await Paint(fixture);
            Require(ScreenCapture.GetWindowBounds(handle) == bounds, "Window capture must use the complete unscaled page bounds.");
            using (var original = ScreenCapture.Capture(bounds)) original.Save(Path.Combine(output, "live-original.png"), Drawing.Imaging.ImageFormat.Png);
            int frames = 0;
            var statuses = new List<string>();
            var ocr = new SpatialOcr(Path.Combine(root, "models/tessdata"), python,
                Path.Combine(root, "local-ocr/worker.py"), Path.Combine(root, "models/comic-text-detector/comictextdetector.onnx"));
            var session = new LiveTranslationSession(translator, overlay, message => {
                statuses.Add(message); Console.WriteLine(message);
                if (message.Contains("local model")) frames++;
            }, ocr);
            var latency = Stopwatch.StartNew();
            running = session.RunAsync(() => ScreenCapture.GetWindowBounds(handle), "ja-vert", "th",
                SubtitleStyle.Overwrite, 8, cancel.Token, hideOriginals: true);
            await Paint(overlay);
            Require(frames == 0 && !overlay.IsVisible, "Cold startup must leave the manga interactive while models load.");
            using (var cold = VisiblePixels(overlay, bounds))
            {
                cold.Save(Path.Combine(output, "live-cold-start-visible.png"), Drawing.Imaging.ImageFormat.Png);
                using var original = new Drawing.Bitmap(Path.Combine(output, "live-original.png"));
                Require(SamePixels(cold, original), "Cold startup must not replace the manga page.");
            }
            await Until(() => Captions(overlay).Count > 0, running, cancel.Token);
            long firstCaptionMs = latency.ElapsedMilliseconds;
            await Until(() => frames >= 1, running, cancel.Token);
            long completePageMs = latency.ElapsedMilliseconds;
            await Paint(overlay);
            var firstCaptions = Captions(overlay);
            Require(firstCaptions.Count == 8, $"Expected all eight real manga text regions; received {firstCaptions.Count}.");
            Require(firstCaptions.All(IsThai), "Every real manga caption must contain Thai text.");
            CheckTextFits(overlay);
            using var first = VisiblePixels(overlay, bounds);
            first.Save(Path.Combine(output, "live-thai-overwrite.png"), Drawing.Imaging.ImageFormat.Png);
            using (var original = new Drawing.Bitmap(Path.Combine(output, "live-original.png")))
                Require(!SamePixels(first, original), "The actual visible image must differ from original Japanese.");

            // A small scroll should move captions over the live page; newly exposed art remains visible.
            var transform = PresentationSource.FromVisual(fixture)!.CompositionTarget!.TransformFromDevice;
            latency.Restart();
            image.RenderTransform = new TranslateTransform(0, -120 * transform.M22);
            await Task.Delay(600, cancel.Token);
            await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            using (var held = VisiblePixels(overlay, bounds))
            {
                held.Save(Path.Combine(output, "live-scroll-held.png"), Drawing.Imaging.ImageFormat.Png);
                Require(!SamePixels(first, held)
                    && !((Canvas)overlay.Content).Children.OfType<Border>().Any(border => border.Uid == "new-content-cover")
                    && !((Canvas)overlay.Content).Children.OfType<Image>().Any()
                    && Captions(overlay).Count == 8 && Captions(overlay).All(IsThai),
                    "Scrolling must move captions without freezing or covering the page artwork.");
            }
            await Until(() => frames >= 2, running, cancel.Token);
            long completeScrollMs = latency.ElapsedMilliseconds;
            await Paint(overlay);
            using var updated = VisiblePixels(overlay, bounds);
            // Preserve diagnostics before asserting every region survives scrolling.
            updated.Save(Path.Combine(output, "live-scroll-translated.png"), Drawing.Imaging.ImageFormat.Png);
            Require(Captions(overlay).Count == 8 && Captions(overlay).All(IsThai), "The scrolled page must retain eight Thai captions.");
            Require(!SamePixels(first, updated), "The newly translated captions must follow the scrolled page.");

            // A no-text page should remain visible without stale captions.
            int beforeBlank = statuses.Count;
            image.Source = null;
            await Until(() => statuses.Skip(beforeBlank).Any(status => status.StartsWith("No text detected")), running, cancel.Token);
            await Paint(overlay);
            using (var blankPage = VisiblePixels(overlay, bounds))
            {
                blankPage.Save(Path.Combine(output, "live-empty-page-visible.png"), Drawing.Imaging.ImageFormat.Png);
                Require(!overlay.IsVisible && !SamePixels(updated, blankPage), "A no-text frame must show its own background.");
            }
            fixture.WindowState = WindowState.Minimized;
            await Until(() => !overlay.IsVisible, running, cancel.Token);
            Require(((Canvas)overlay.Content).Children.Count == 0, "Minimizing the selected window must clear every overlay visual.");
            image.Source = bitmap;
            latency.Restart();
            fixture.WindowState = WindowState.Normal; Place(handle, bounds);
            await Until(() => Captions(overlay).Count > 0, running, cancel.Token);
            long restoredFirstCaptionMs = latency.ElapsedMilliseconds;
            await Until(() => frames >= 3, running, cancel.Token);
            cancel.Cancel();
            try { await running; } catch (OperationCanceledException) { }
            Require(!overlay.IsVisible && ((Canvas)overlay.Content).Children.Count == 0, "Stopping must remove subtitles.");
            File.WriteAllText(Path.Combine(output, "live-manga-check.json"), JsonSerializer.Serialize(new {
                image = imagePath, bounds, firstCaptions, statuses,
                firstCaptionMs, completePageMs, completeScrollMs, restoredFirstCaptionMs,
                coldStartupVisible = true, eightThaiCaptions = true, pageInteractiveDuringScroll = true,
                newScrollFrameChanged = true, emptyPageVisible = true, minimizedCleared = true, stoppedCleared = true
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Latency: cold first caption {firstCaptionMs} ms; complete page {completePageMs} ms; scroll {completeScrollMs} ms; restored first caption {restoredFirstCaptionMs} ms.");
            Console.WriteLine("PASS: real manga -> eight Thai captions over live pixels; page stays visible through startup, scrolling and no-text; minimize and stop clear.");
        }
        finally
        {
            cancel.Cancel();
            if (running is not null) { try { await running; } catch (OperationCanceledException) { } }
            overlay.Close(); fixture.Close();
        }
    }

    private static bool IsThai(string text) => text.Any(character => character >= '\u0e00' && character <= '\u0e7f');
    private static List<string> Captions(SubtitleOverlay overlay) => ((Canvas)overlay.Content).Children.OfType<Border>()
        .Select(border => border.Child).OfType<TextBlock>().Select(text => text.Text).ToList();
    private static void CheckTextFits(SubtitleOverlay overlay)
    {
        foreach (var border in ((Canvas)overlay.Content).Children.OfType<Border>().Where(border => border.Child is TextBlock))
        {
            var text = (TextBlock)border.Child;
            text.Measure(new Size(text.ActualWidth, double.PositiveInfinity));
            Require(text.DesiredSize.Height <= text.ActualHeight + 0.1, "A Thai caption is clipped vertically.");
        }
    }
    private static async Task Until(Func<bool> predicate, Task running, CancellationToken token)
    {
        while (!predicate())
        {
            if (running.IsCompleted) { await running; throw new Exception("Live session ended before the required state."); }
            await Task.Delay(20, token);
        }
    }
    private static async Task Paint(Window window)
    {
        window.UpdateLayout();
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(80);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        DwmFlush();
    }
    private static Drawing.Bitmap VisiblePixels(SubtitleOverlay overlay, Drawing.Rectangle bounds)
    {
        // Synchronous on the UI thread: the session cannot capture diagnostic inclusion and feed it back to OCR.
        overlay.UpdateLayout();
        if (!overlay.IsVisible) { DwmFlush(); return ScreenCapture.Capture(bounds); }
        var handle = new WindowInteropHelper(overlay).Handle;
        Require(SetWindowDisplayAffinity(handle, 0), "Cannot include subtitles in the native diagnostic screenshot.");
        try { DwmFlush(); return ScreenCapture.Capture(bounds); }
        finally { Require(SetWindowDisplayAffinity(handle, 0x11), "Cannot restore subtitle capture exclusion."); }
    }
    private static bool SamePixels(Drawing.Bitmap left, Drawing.Bitmap right)
    {
        if (left.Size != right.Size) return false;
        var first = Pixels(left);
        var second = Pixels(right);
        // DWM may round subpixel text colors by a few levels between captures.
        return first.Length == second.Length && first.Where((value, i) => Math.Abs(value - second[i]) > 3).Any() == false;
    }
    private static byte[] Pixels(Drawing.Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
            Drawing.Imaging.ImageLockMode.ReadOnly, Drawing.Imaging.PixelFormat.Format32bppArgb);
        try { var pixels = new byte[Math.Abs(data.Stride) * data.Height]; Marshal.Copy(data.Scan0, pixels, 0, pixels.Length); return pixels; }
        finally { bitmap.UnlockBits(data); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Place(nint handle, Drawing.Rectangle bounds) =>
        Require(SetWindowPos(handle, new nint(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0040), "Could not position the page fixture.");
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint handle, uint affinity);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}

