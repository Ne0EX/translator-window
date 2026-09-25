using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Translumo.Local;

internal static class Program
{
    private const uint WdaExcludeFromCapture = 0x11;

    [STAThread]
    private static int Main()
    {
        SetProcessDpiAwarenessContext(new nint(-4));
        var screen = Screen.PrimaryScreen ?? throw new InvalidOperationException("No primary screen is active.");
        Require(screen.Bounds.Width >= 1024 && screen.Bounds.Height >= 720,
            "The production capture check needs a 1024x720 output.");

        var sourceBounds = new Rectangle(screen.Bounds.Left + 24, screen.Bounds.Top + 24, 960, 640);
        var initial = Rectangle.Inflate(sourceBounds, -32, -32);
        var resized = new Rectangle(sourceBounds.Left + 80, sourceBounds.Top + 70, 640, 360);
        var moved = new Rectangle(sourceBounds.Left + 180, sourceBounds.Top + 160, 640, 360);
        var overlayBounds = new Rectangle(sourceBounds.Left + 300, sourceBounds.Top + 220, 220, 120);
        var outputEdge = new Rectangle(screen.Bounds.Right - 1, screen.Bounds.Top + 20, 2, 32);

        using var source = new PatternWindow(sourceBounds);
        using var excluded = new Form
        {
            BackColor = Color.Magenta,
            Bounds = overlayBounds,
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            TopMost = true,
        };
        source.Show();
        excluded.Show();
        Require(SetWindowDisplayAffinity(excluded.Handle, WdaExcludeFromCapture),
            $"SetWindowDisplayAffinity failed: {Marshal.GetLastWin32Error()}.");
        excluded.BringToFront();
        Present(source, excluded);

        var result = new CheckResult { Bounds = initial, OutputBounds = screen.Bounds };
        try
        {
            Throws<ArgumentOutOfRangeException>(() => new DesktopDuplicationCapture(
                new Rectangle(initial.X, initial.Y, 0, initial.Height)));
            Throws<NotSupportedException>(() => new DesktopDuplicationCapture(outputEdge));

            var direct = new DesktopDuplicationCapture(initial);
            try
            {
                Require(direct.Bounds == initial && direct.Contains(initial), "Native initial geometry is wrong.");
                Require(!direct.Contains(outputEdge), "Native Contains accepted a crop crossing its output.");

                Present(source, excluded);
                using var expected0 = ScreenCapture.Capture(initial);
                using var actual0 = direct.Capture(CancellationToken.None);
                RequirePixels(expected0, actual0, "native initial");
                result.InitialHash = PixelHash(actual0);

                using var cached = direct.Capture(CancellationToken.None);
                RequirePixels(expected0, cached, "native static cached");

                source.Phase = 1;
                Present(source, excluded);
                using var expected1 = ScreenCapture.Capture(initial);
                using var actual1 = direct.Capture(CancellationToken.None);
                RequirePixels(expected1, actual1, "native changed frame");
                result.ChangedHash = PixelHash(actual1);
                Require(result.InitialHash != result.ChangedHash, "A changed source returned the static frame.");

                direct.ResizeCrop(resized);
                using var expectedResized = ScreenCapture.Capture(resized);
                using var actualResized = direct.Capture(CancellationToken.None);
                Require(actualResized.Size == resized.Size, "ResizeCrop returned the wrong dimensions.");
                RequirePixels(expectedResized, actualResized, "native resized crop");

                direct.ResizeCrop(moved);
                using var expectedMoved = ScreenCapture.Capture(moved);
                using var actualMoved = direct.Capture(CancellationToken.None);
                Require(actualMoved.Size == moved.Size, "Moved crop returned the wrong dimensions.");
                RequirePixels(expectedMoved, actualMoved, "native moved crop");
                result.MovedHash = PixelHash(actualMoved);

                Throws<NotSupportedException>(() => direct.ResizeCrop(outputEdge));
                Require(direct.Bounds == moved, "Rejected ResizeCrop mutated native bounds.");
                using var afterRejectedResize = direct.Capture(CancellationToken.None);
                RequirePixels(expectedMoved, afterRejectedResize, "native after rejected resize");

                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                Throws<OperationCanceledException>(() => direct.Capture(canceled.Token));
            }
            finally
            {
                direct.Dispose();
                direct.Dispose();
            }
            Throws<ObjectDisposedException>(() => direct.Capture(CancellationToken.None));
            Throws<ObjectDisposedException>(() => direct.ResizeCrop(initial));

            using var session = new ScreenCapture.Session();
            source.Phase = 0;
            using (var sessionNative = CaptureWhilePresenting(session, initial, source, excluded))
            using (var expected = ScreenCapture.Capture(initial))
                RequirePixels(expected, sessionNative, "session native");
            Require(GetNative(session) is not null,
                "Supported production Session silently used GDI instead of owning the native backend.");
            result.SessionNative = true;

            var cancelTimer = Stopwatch.StartNew();
            using (var stop = new CancellationTokenSource())
            {
                Task pending = Task.Run(() =>
                {
                    while (true)
                        using (session.Capture(screen.Bounds, stop.Token)) { }
                });
                stop.CancelAfter(1);
                try
                {
                    Require(SpinWait.SpinUntil(() => pending.IsCompleted, TimeSpan.FromSeconds(2)),
                        "Canceled capture loop did not complete within two seconds.");
                    pending.GetAwaiter().GetResult();
                    throw new InvalidOperationException("A canceled full-output capture loop ended without cancellation.");
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
            result.CancellationMs = Math.Round(cancelTimer.Elapsed.TotalMilliseconds, 2);
            Require(cancelTimer.Elapsed < TimeSpan.FromSeconds(2), "Capture cancellation was not bounded.");

            using (var afterCancel = CaptureWhilePresenting(session, initial, source, excluded))
            using (var expected = ScreenCapture.Capture(initial))
                RequirePixels(expected, afterCancel, "session after cancellation");

            ForceGdiFallback(session, initial);
            using (var fallback = session.Capture(initial, CancellationToken.None))
            using (var expected = ScreenCapture.Capture(initial))
                RequirePixels(expected, fallback, "session forced GDI fallback");
            Require(GetNative(session) is null, "Forced fallback unexpectedly created a native backend.");
            result.Fallback = true;

            ExpireRetry(session);
            source.Phase = 1;
            using (var recovered = CaptureWhilePresenting(session, initial, source, excluded))
            using (var expected = ScreenCapture.Capture(initial))
                RequirePixels(expected, recovered, "session native retry");
            Require(GetNative(session) is not null, "Session did not return to native capture after failure cleared.");
            result.NativeRetry = true;

            session.Dispose();
            session.Dispose();
            Throws<ObjectDisposedException>(() => session.Capture(initial, CancellationToken.None));

            // A failed constructor must not poison the next real native session.
            using var finalNative = new DesktopDuplicationCapture(initial);
            Present(source, excluded);
            using var finalFrame = finalNative.Capture(CancellationToken.None);
            using var finalExpected = ScreenCapture.Capture(initial);
            RequirePixels(finalExpected, finalFrame, "native after unsupported constructor");

            result.Passed = true;
            string artifact = Path.GetFullPath(".cache/verification/production-capture-check.json");
            Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
            File.WriteAllText(artifact, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(result));
            Console.WriteLine($"PASS: production capture lifecycle; {artifact}");
            return 0;
        }
        finally
        {
            excluded.Hide();
            source.Hide();
        }
    }

    private static Bitmap CaptureWhilePresenting(ScreenCapture.Session session, Rectangle bounds,
        PatternWindow source, Form overlay)
    {
        Task<Bitmap> pending = Task.Run(() => session.Capture(bounds, CancellationToken.None));
        var timeout = Stopwatch.StartNew();
        while (!pending.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(2))
        {
            Present(source, overlay);
            Thread.Sleep(10);
        }
        Require(pending.IsCompleted, "Session capture did not complete within two seconds.");
        return pending.GetAwaiter().GetResult();
    }

    private static void ForceGdiFallback(ScreenCapture.Session session, Rectangle bounds)
    {
        var type = typeof(ScreenCapture.Session);
        if (GetNative(session) is IDisposable native) native.Dispose();
        Field(type, "_native").SetValue(session, null);
        Method(type, "RecordFailure").Invoke(session, new object[] { bounds });
    }

    private static void ExpireRetry(ScreenCapture.Session session)
        => Field(typeof(ScreenCapture.Session), "_retryAt").SetValue(session,
            unchecked(Environment.TickCount64 - 1));

    private static object? GetNative(ScreenCapture.Session session)
        => Field(typeof(ScreenCapture.Session), "_native").GetValue(session);

    private static FieldInfo Field(Type type, string name)
        => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
           ?? throw new MissingFieldException(type.FullName, name);

    private static MethodInfo Method(Type type, string name)
        => type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
           ?? throw new MissingMethodException(type.FullName, name);

    private static void Present(PatternWindow source, Form overlay)
    {
        source.Invalidate();
        source.Update();
        overlay.BringToFront();
        Application.DoEvents();
        DwmFlush();
        Thread.Sleep(20);
        Application.DoEvents();
    }

    private static string PixelHash(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] row = new byte[checked(bitmap.Width * 4)];
            for (int y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                hash.AppendData(row);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { bitmap.UnlockBits(data); }
    }

    private static void RequirePixels(Bitmap expected, Bitmap actual, string name)
    {
        Require(expected.Size == actual.Size, $"{name}: size mismatch {expected.Size} != {actual.Size}.");
        string expectedHash = PixelHash(expected);
        string actualHash = PixelHash(actual);
        Require(expectedHash == actualHash, $"{name}: pixel mismatch {expectedHash} != {actualHash}.");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class PatternWindow : Form
    {
        public int Phase { get; set; }

        public PatternWindow(Rectangle bounds)
        {
            Bounds = bounds;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.CompositingMode = CompositingMode.SourceCopy;
            Color[] colors = Phase == 0
                ? [Color.FromArgb(255, 37, 181, 92), Color.FromArgb(255, 212, 69, 47),
                   Color.FromArgb(255, 44, 88, 211), Color.FromArgb(255, 239, 196, 41)]
                : [Color.FromArgb(255, 89, 42, 207), Color.FromArgb(255, 23, 173, 219),
                   Color.FromArgb(255, 226, 77, 153), Color.FromArgb(255, 61, 201, 74)];
            int halfWidth = ClientSize.Width / 2;
            int halfHeight = ClientSize.Height / 2;
            using var a = new SolidBrush(colors[0]);
            using var b = new SolidBrush(colors[1]);
            using var c = new SolidBrush(colors[2]);
            using var d = new SolidBrush(colors[3]);
            e.Graphics.FillRectangle(a, 0, 0, halfWidth, halfHeight);
            e.Graphics.FillRectangle(b, halfWidth, 0, ClientSize.Width - halfWidth, halfHeight);
            e.Graphics.FillRectangle(c, 0, halfHeight, halfWidth, ClientSize.Height - halfHeight);
            e.Graphics.FillRectangle(d, halfWidth, halfHeight,
                ClientSize.Width - halfWidth, ClientSize.Height - halfHeight);
        }
    }

    private sealed class CheckResult
    {
        public bool Passed { get; set; }
        public Rectangle Bounds { get; set; }
        public Rectangle OutputBounds { get; set; }
        public string InitialHash { get; set; } = "";
        public string ChangedHash { get; set; } = "";
        public string MovedHash { get; set; } = "";
        public bool SessionNative { get; set; }
        public double CancellationMs { get; set; }
        public bool Fallback { get; set; }
        public bool NativeRetry { get; set; }
        public string FallbackGate => "Existing Session failure state forced by reflection; hardware failure was not induced.";
        public string AccessLost => "Not induced: requires a real display reset; production allows one full native recovery per Capture.";
        public bool PixelsPersisted => false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(nint handle, uint affinity);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(nint value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
