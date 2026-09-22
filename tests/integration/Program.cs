using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Translumo.Local;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int exitCode = 0;
        app.Startup += async (_, _) =>
        {
            try { await Run(args); }
            catch (Exception error) { Console.Error.WriteLine(error); exitCode = 1; }
            finally { app.Shutdown(); }
        };
        app.Run();
        return exitCode;
    }

    private static async Task Run(string[] args)
    {
        string root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
        var screen = Forms.Screen.PrimaryScreen!.Bounds;
        var page = new Canvas { Background = Brushes.White };
        var first = Bubble("今日はいい天気ですね。", 160, 150);
        var second = Bubble("一緒に学校へ行こう。", 450, 390);
        page.Children.Add(first); page.Children.Add(second);
        var fixture = new Window {
            Title = "Japanese manga acceptance fixture", Content = page, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, Background = Brushes.White, Topmost = true
        };
        fixture.Show();
        var hwnd = new WindowInteropHelper(fixture).Handle;
        Place(hwnd, screen);
        await Task.Delay(500);
        var overlay = new SubtitleOverlay();
        using var translator = new LocalTranslator(Path.Combine(root, ".venv/Scripts/python.exe"),
            Path.Combine(root, "local-model/worker.py"), Path.Combine(root, "models/hy-mt2"));
        try
        {
            await translator.TranslateAsync(new[] { "今日はいい天気ですね。", "一緒に学校へ行こう。" }, "ja", "th", CancellationToken.None);
            foreach (var mode in Enum.GetValues<CaptureMode>())
            foreach (var style in Enum.GetValues<SubtitleStyle>())
            {
                Func<Drawing.Rectangle?> bounds = mode switch {
                    CaptureMode.SelectedArea => () => new Drawing.Rectangle(screen.X + 100, screen.Y + 90, screen.Width - 200, screen.Height - 180),
                    CaptureMode.Window => () => ScreenCapture.GetWindowBounds(hwnd),
                    _ => () => screen
                };
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var session = new LiveTranslationSession(translator, overlay, message => {
                    if (message.Contains("local model")) ready.TrySetResult(message);
                });
                var run = session.RunAsync(bounds, "ja", "th", style, 6, cts.Token);
                string result = await ReadyOrFailure(ready.Task, run, cts.Token);
                var captions = Captions(overlay);
                Require(captions.Count >= 2 && captions.All(text => text.Any(c => c >= '\u0e00' && c <= '\u0e7f')),
                    "Both Japanese speech bubbles must become Thai captions.");
                using (var capture = ScreenCapture.Capture(bounds()!.Value))
                using (var ocr = new SpatialOcr(Path.Combine(root, "models/tessdata")))
                {
                    var original = await ocr.RecognizeAsync(capture, "ja", cts.Token);
                    Require(original.Any(r => r.Text.Contains("天気")), "OCR must still see original Japanese under overlay.");
                }
                Console.WriteLine($"PASS {mode}/{style}: {result}; " + string.Join(" | ", captions));
                if (mode == CaptureMode.SelectedArea && style == SubtitleStyle.Overwrite)
                {
                    // The model-ready callback precedes WPF rendering; wait for the new visual frame.
                    await overlay.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    await Task.Delay(30);
                    string screenshotPath = Path.Combine(root, ".cache/verification/manga-ja-th.png");
                    SaveComposite(page, overlay, screen, screenshotPath);
                    using var visible = new Drawing.Bitmap(screenshotPath);
                    using var visibleOcr = new SpatialOcr(Path.Combine(root, "models/tessdata"));
                    var exposedText = await visibleOcr.RecognizeAsync(visible, "ja", cts.Token);
                    Require(!exposedText.Any(r => r.Text.Contains("天気") || r.Text.Contains("学校")),
                        "The actual overwrite screenshot must not expose the Japanese source dialogue.");
                }
                cts.Cancel();
                try { await run; } catch (OperationCanceledException) { }
                Require(!overlay.IsVisible, "Stopping must remove subtitles.");
            }

            using var scrollCancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            int frames = 0;
            var scrollSession = new LiveTranslationSession(translator, overlay, message => {
                if (message.Contains("local model")) frames++;
            });
            var scrollRun = scrollSession.RunAsync(() => ScreenCapture.GetWindowBounds(hwnd),
                "ja", "th", SubtitleStyle.Overwrite, 6, scrollCancel.Token);
            await Until(() => frames >= 1, scrollRun, scrollCancel.Token);
            // Simulate a scrolling page: move both bubbles and replace the dialogue.
            first.Text = "どこに行くの？";
            second.Text = "また明日会いましょう。";
            Canvas.SetTop(first, 240); Canvas.SetTop(second, 520);
            await Task.Delay(140);
            Require(!Captions(overlay).Any(t => t.Contains("อากาศ")), "Old page translations must clear on changed pixels.");
            await Until(() => frames >= 2, scrollRun, scrollCancel.Token);
            Require(Captions(overlay).All(t => t.Any(c => c >= '\u0e00' && c <= '\u0e7f')), "Scrolled page must receive Thai subtitles.");
            int beforeMove = frames;
            var movedBounds = new Drawing.Rectangle(screen.X + 120, screen.Y + 80, screen.Width - 240, screen.Height - 160);
            Place(hwnd, movedBounds);
            Require(ScreenCapture.GetWindowBounds(hwnd) == movedBounds, "Window capture must follow physical move and resize bounds.");
            await Until(() => frames > beforeMove, scrollRun, scrollCancel.Token);
            await overlay.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await Task.Delay(60);
            using (var movedCapture = ScreenCapture.Capture(movedBounds))
            using (var movedOcr = new SpatialOcr(Path.Combine(root, "models/tessdata")))
            {
                var movedRegions = await movedOcr.RecognizeAsync(movedCapture, "ja", scrollCancel.Token);
                var masks = ((Canvas)overlay.Content).Children.OfType<Border>().Where(b => b.Child is null).Select(b => {
                    var start = b.PointToScreen(new Point(0, 0));
                    var end = b.PointToScreen(new Point(b.ActualWidth, b.ActualHeight));
                    return Drawing.Rectangle.FromLTRB((int)Math.Floor(start.X), (int)Math.Floor(start.Y),
                        (int)Math.Ceiling(end.X), (int)Math.Ceiling(end.Y));
                }).ToArray();
                Require(movedRegions.Count >= 2 && movedRegions.All(r => {
                    var original = r.Bounds; original.Offset(movedBounds.Location);
                    return masks.Any(mask => mask.Contains(original));
                }), "Moved-window overwrite masks must cover original text at the new physical coordinates.");
            }
            SaveComposite(page, overlay, movedBounds, Path.Combine(root, ".cache/verification/moved-window-ja-th.png"));
            int beforeRestore = frames;
            fixture.WindowState = WindowState.Minimized;
            await Task.Delay(300);
            Require(!overlay.IsVisible, "Minimized captured window must clear subtitles.");
            fixture.WindowState = WindowState.Normal;
            Place(hwnd, screen);
            await Until(() => frames > beforeRestore, scrollRun, scrollCancel.Token);
            scrollCancel.Cancel();
            try { await scrollRun; } catch (OperationCanceledException) { }
            Console.WriteLine("PASS page change, physical window move/resize, window minimize/restore, and cancellation.");
        }
        finally { overlay.Close(); fixture.Close(); }
    }

    private static TextBlock Bubble(string text, double left, double top)
    {
        var block = new TextBlock { Text = text, FontFamily = new FontFamily("Yu Gothic"),
            FontSize = 30, Foreground = Brushes.Black, Background = Brushes.White,
            Width = 420, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(10) };
        Canvas.SetLeft(block, left); Canvas.SetTop(block, top); return block;
    }

    private static List<string> Captions(SubtitleOverlay overlay) => ((Canvas)overlay.Content).Children
        .OfType<Border>().Select(border => border.Child).OfType<TextBlock>().Select(block => block.Text).ToList();

    private static async Task<string> ReadyOrFailure(Task<string> ready, Task run, CancellationToken token)
    {
        var completed = await Task.WhenAny(ready, run).WaitAsync(token);
        if (completed == run) { await run; throw new Exception("Session stopped before a translation."); }
        return await ready;
    }

    private static async Task Until(Func<bool> predicate, Task run, CancellationToken token)
    {
        while (!predicate()) { if (run.IsCompleted) await run; await Task.Delay(30, token); }
    }

    private static void SaveComposite(Canvas page, SubtitleOverlay overlay, Drawing.Rectangle screen, string path)
    {
        page.UpdateLayout(); overlay.UpdateLayout();
        var handle = new WindowInteropHelper(overlay).Handle;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Test-only screenshot: pause on the UI thread, include the overlay, then restore exclusion before processing resumes.
        Require(SetWindowDisplayAffinity(handle, 0), "Could not enable the diagnostic screenshot.");
        try
        {
            DwmFlush();
            using var screenshot = ScreenCapture.Capture(screen);
            screenshot.Save(path, Drawing.Imaging.ImageFormat.Png);
        }
        finally { Require(SetWindowDisplayAffinity(handle, 0x11), "Could not restore subtitle capture exclusion."); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(nint handle, uint affinity);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Place(nint handle, Drawing.Rectangle bounds) =>
        SetWindowPos(handle, new nint(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0040);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
}







