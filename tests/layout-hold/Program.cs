using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Translumo.Local;

internal static class Program
{
    [STAThread] static void Main()
    {
        SetProcessDpiAwarenessContext(new nint(-4));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) => {
            try { await Check(); }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally { app.Shutdown(); }
        };
        app.Run();
    }
    static async Task Check()
    {
        var overlay = new SubtitleOverlay();
        var statuses = new List<string>();
        var bounds = new Rectangle(80, 60, 600, 300);
        using var cancellation = new CancellationTokenSource();
        var session = new LiveTranslationSession(new LocalTranslator(), overlay, message => { statuses.Add(message); Console.WriteLine(message); });
        ScreenCapture.Marker = 1;
        var running = session.RunAsync(() => bounds, "ja", "th", SubtitleStyle.Overwrite, 6,
            cancellation.Token, hideOriginals: true);
        try
        {
            await Until(() => statuses.Any(s => s.Contains("translated blocks")), running);
            await overlay.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var originalVisuals = ((Canvas)overlay.Content).Children.Cast<object>().ToArray();
            Require(originalVisuals.Length == 2 && overlay.IsVisible
                && originalVisuals.All(visual => visual is Border)
                && originalVisuals.All(visual => visual is not System.Windows.Controls.Image),
                "The first page must show only a local source mask and caption, without a full-page image layer.");
            ScreenCapture.Marker = 2;
            await Until(() => statuses.Any(s => s.Contains("not enough space")), running);
            await overlay.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(!running.IsCompleted, "A layout failure must leave the live session running.");
            Require(!overlay.IsVisible && ((Canvas)overlay.Content).Children.Count == 0,
                "A changed text box with an unreadable fit must clear stale artwork, mask and caption visuals.");
            Require(statuses.Last().Contains("Watching for changes"), "A layout failure must explain that the stale caption was cleared.");
            int completed = statuses.Count(s => s.Contains("translated blocks"));
            ScreenCapture.Marker = 3;
            await Until(() => statuses.Count(s => s.Contains("translated blocks")) > completed, running);
            Require(((Canvas)overlay.Content).Children.OfType<Border>().Any(b => b.Child is TextBlock t && t.Text == "Recovered caption"),
                "The next changed readable frame must replace the held page.");
            ScreenCapture.Marker = 4;
            try { await running.WaitAsync(TimeSpan.FromSeconds(10)); throw new Exception("Invalid model output was swallowed."); }
            catch (InvalidOperationException error) when (error is not SubtitleLayoutException && error.Message.Contains("empty translation")) { }
            Require(!overlay.IsVisible, "Fatal model errors must still end and clean up the session.");
            Console.WriteLine("PASS: layout failure clears stale visuals, session stays alive, next frame recovers, invalid model reply remains fatal.");
        }
        finally
        {
            cancellation.Cancel();
            try { await running; } catch (OperationCanceledException) { } catch (InvalidOperationException) { }
            overlay.Close();
        }

        var fallbackOverlay = new SubtitleOverlay();
        var fallbackStatuses = new List<string>();
        using var fallbackCancellation = new CancellationTokenSource();
        ScreenCapture.Marker = 5;
        var fallbackSession = new LiveTranslationSession(new LocalTranslator(), fallbackOverlay, fallbackStatuses.Add);
        var fallbackRun = fallbackSession.RunAsync(() => bounds, "ja", "th", SubtitleStyle.Overwrite, 6,
            fallbackCancellation.Token, hideOriginals: true);
        try
        {
            await Until(() => fallbackStatuses.Any(s => s.Contains("translated blocks")), fallbackRun);
            Require(((Canvas)fallbackOverlay.Content).Children.OfType<Border>().Any(b => b.Child is TextBlock t && t.Text == "Colored caption"),
                "A textured overwrite page must keep the translated caption visible while covering the source.");
            Console.WriteLine("PASS: textured overwrite keeps captions opaque.");
        }
        finally
        {
            fallbackCancellation.Cancel();
            try { await fallbackRun; } catch (OperationCanceledException) { } catch (InvalidOperationException) { }
            fallbackOverlay.Close();
        }
    }
    static async Task Until(Func<bool> ready, Task running)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!ready())
        {
            if (running.IsCompleted) { await running; throw new Exception("Session ended too early."); }
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Expected live-session state was not reached.");
            await Task.Delay(20);
        }
    }
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(nint context);
}

namespace Translumo.Local
{
    public sealed class LocalTranslator
    {
        public Task<IReadOnlyList<string>> TranslateAsync(string[] texts, string source, string target, CancellationToken token)
            => Task.FromResult<IReadOnlyList<string>>(texts.Select(text => text switch {
                "1" => "First translated caption", "2" => new string('W', 5000), "3" => "Recovered caption", "5" => "Colored caption", _ => ""
            }).ToArray());
    }
    public sealed class SpatialOcr : IDisposable
    {
        public Task<IReadOnlyList<TextRegion>> RecognizeAsync(Bitmap bitmap, string language, CancellationToken token)
            => Task.FromResult<IReadOnlyList<TextRegion>>(new[] {
                new TextRegion(bitmap.GetPixel(0, 0).R.ToString(), new Rectangle(200, 100, ScreenCapture.Marker == 5 ? 40 : 150, 60)) });
        public void Dispose() { }
    }
    public static class ScreenCapture
    {
        public static int Marker;
        public static Bitmap Capture(Rectangle bounds)
        {
            var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var drawing = Graphics.FromImage(bitmap))
            {
                drawing.Clear(Marker == 5 ? Color.FromArgb(176, 32, 81) : Color.White);
                if (Marker == 5)
                    for (int x = 0; x < bounds.Width; x += 12)
                        drawing.FillRectangle(Brushes.Black, x, 0, 6, bounds.Height);
            }
            bitmap.SetPixel(0, 0, Color.FromArgb(Marker, 0, 0));
            return bitmap;
        }
    }
}
