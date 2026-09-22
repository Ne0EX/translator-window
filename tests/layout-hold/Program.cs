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
        var session = new LiveTranslationSession(new LocalTranslator(), overlay, statuses.Add);
        ScreenCapture.Marker = 1;
        var running = session.RunAsync(() => bounds, "ja", "th", SubtitleStyle.Overwrite, 6,
            cancellation.Token, hideOriginals: true);
        try
        {
            await Until(() => statuses.Any(s => s.Contains("translated blocks")), running);
            await overlay.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var originalVisuals = ((Canvas)overlay.Content).Children.Cast<object>().ToArray();
            Require(originalVisuals.Length == 3 && overlay.IsVisible, "The first translated page must be visible.");
            ScreenCapture.Marker = 2;
            await Until(() => statuses.Any(s => s.Contains("not enough space")), running);
            await overlay.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(!running.IsCompleted, "A layout failure must leave the live session running.");
            Require(overlay.IsVisible && ((Canvas)overlay.Content).Children.Cast<object>().SequenceEqual(originalVisuals),
                "A failed layout must retain exactly the previous artwork, mask and caption.");
            Require(statuses.Last().Contains("previous view stays covered"), "The fit problem and held view must be explained.");
            int completed = statuses.Count(s => s.Contains("translated blocks"));
            ScreenCapture.Marker = 3;
            await Until(() => statuses.Count(s => s.Contains("translated blocks")) > completed, running);
            Require(((Canvas)overlay.Content).Children.OfType<Border>().Any(b => b.Child is TextBlock t && t.Text == "Recovered caption"),
                "The next changed readable frame must replace the held page.");
            ScreenCapture.Marker = 4;
            try { await running.WaitAsync(TimeSpan.FromSeconds(10)); throw new Exception("Invalid model output was swallowed."); }
            catch (InvalidOperationException error) when (error is not SubtitleLayoutException && error.Message.Contains("empty translation")) { }
            Require(!overlay.IsVisible, "Fatal model errors must still end and clean up the session.");
            Console.WriteLine("PASS: layout failure retains all previous visuals, session stays alive, next frame recovers, invalid model reply remains fatal.");
        }
        finally
        {
            cancellation.Cancel();
            try { await running; } catch (OperationCanceledException) { } catch (InvalidOperationException) { }
            overlay.Close();
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
                "1" => "First translated caption", "2" => new string('W', 5000), "3" => "Recovered caption", _ => ""
            }).ToArray());
    }
    public sealed class SpatialOcr : IDisposable
    {
        public Task<IReadOnlyList<TextRegion>> RecognizeAsync(Bitmap bitmap, string language, CancellationToken token)
            => Task.FromResult<IReadOnlyList<TextRegion>>(new[] {
                new TextRegion(bitmap.GetPixel(0, 0).R.ToString(), new Rectangle(200, 100, 150, 60)) });
        public void Dispose() { }
    }
    public static class ScreenCapture
    {
        public static int Marker;
        public static Bitmap Capture(Rectangle bounds)
        {
            var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var drawing = Graphics.FromImage(bitmap)) drawing.Clear(Color.White);
            bitmap.SetPixel(0, 0, Color.FromArgb(Marker, 0, 0));
            return bitmap;
        }
    }
}