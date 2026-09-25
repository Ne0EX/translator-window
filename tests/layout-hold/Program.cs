using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
        using (var still = new Bitmap(100, 100))
        using (var jitter = (Bitmap)still.Clone())
        using (var changed = (Bitmap)still.Clone())
        {
            using var jitterInk = Graphics.FromImage(jitter);
            jitterInk.FillRectangle(Brushes.White, 0, 0, 10, 10);
            using var changedInk = Graphics.FromImage(changed);
            changedInk.FillRectangle(Brushes.White, 0, 0, 12, 12);
            byte[] stillHash = LiveTranslationSession.Fingerprint(still);
            byte[] changedHash = LiveTranslationSession.Fingerprint(changed);
            Require(!LiveTranslationSession.MeaningfullyDifferent(still, jitter)
                && LiveTranslationSession.MeaningfullyDifferent(still, changed)
                && stillHash.AsSpan().SequenceEqual(LegacyFingerprint(still))
                && changedHash.AsSpan().SequenceEqual(LegacyFingerprint(changed))
                && !stillHash.AsSpan().SequenceEqual(changedHash)
                && NegativeStrideFingerprintMatches(),
                "Row hashing must preserve positive-stride digests, changed pixels, and logical negative-stride rows.");
        }
        using (var regionBitmap = new Bitmap(1200, 800, PixelFormat.Format32bppArgb))
        {
            using (var drawing = Graphics.FromImage(regionBitmap))
            {
                drawing.Clear(Color.White);
                for (int y = 0; y < regionBitmap.Height; y += 17)
                    drawing.DrawLine(Pens.Black, 0, y, regionBitmap.Width, (y * 11) % regionBitmap.Height);
            }
            var regions = new[] {
                new TextRegion("", new Rectangle(20, 30, 1000, 600)),
                new TextRegion("", new Rectangle(-40, 650, 300, 200)),
                new TextRegion("", new Rectangle(1300, 900, 20, 20))
            };
            byte[][] expected = regions.Select(region => LegacyRegionFingerprint(regionBitmap, region.Bounds)).ToArray();
            _ = RegionFingerprints(regionBitmap, regions);
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            byte[][] actual = RegionFingerprints(regionBitmap, regions);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Require(actual.Length == expected.Length
                && expected.Zip(actual).All(pair => pair.First.AsSpan().SequenceEqual(pair.Second))
                && allocated < 100_000,
                $"Region hashing must preserve clipped digests without per-region pixel arrays; allocated {allocated:N0} bytes.");
            Console.WriteLine("PASS: region hashing preserves clipped digests without per-region pixel arrays.");
        }
        const string thaiSample = "ภาษาไทย";
        string[] thaiWords = SubtitleOverlay.ThaiWords(thaiSample);
        var warmupOverlay = new SubtitleOverlay { Opacity = 0, ShowActivated = false, Topmost = false };
        try
        {
            nint foreground = GetForegroundWindow();
            Task layoutWarmup = warmupOverlay.PrepareThaiLayoutAsync();
            Require(!warmupOverlay.IsVisible && !warmupOverlay.IsActive
                && ((Canvas)warmupOverlay.Content).Children.Count == 0,
                "Thai layout warmup must keep its empty overlay hidden and inactive.");
            await layoutWarmup;
            Require(!warmupOverlay.IsVisible && !warmupOverlay.IsActive
                && ((Canvas)warmupOverlay.Content).Children.Count == 0
                && GetForegroundWindow() == foreground
                && SubtitleOverlay.ThaiWords(thaiSample).SequenceEqual(thaiWords),
                "Thai layout warmup must preserve focus, visibility, content, and word segmentation.");
            warmupOverlay.Render(new Rectangle(80, 60, 600, 300),
                new[] { new TextRegion("日本語", new Rectangle(120, 100, 100, 48)) },
                new[] { thaiSample }, SubtitleStyle.Overwrite, japaneseToThai: true);
            Require(((Canvas)warmupOverlay.Content).Children.OfType<Border>()
                    .Any(border => border.Child is TextBlock text
                        && text.Text.Replace("\r", "", StringComparison.Ordinal)
                            .Replace("\n", "", StringComparison.Ordinal) == thaiSample),
                "Thai layout warmup must not change the rendered caption text.");
            Console.WriteLine("PASS: Thai layout warmup stays hidden, preserves focus, and leaves caption output unchanged.");
        }
        finally
        {
            warmupOverlay.Clear();
            warmupOverlay.Close();
        }
        var captureOverlay = new SubtitleOverlay();
        var captureStatuses = new List<string>();
        using var captureCancellation = new CancellationTokenSource();
        var captureGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ScreenCapture.CaptureGate = captureGate;
        ScreenCapture.CaptureStarted = captureStarted;
        ScreenCapture.CapturedOffDispatcher = false;
        int captureSessionsCreatedBefore = ScreenCapture.SessionCreated;
        int captureSessionsDisposedBefore = ScreenCapture.SessionDisposed;
        bool boundsOffDispatcher = false;
        ScreenCapture.Marker = 1;
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            captureGate.TrySetResult();
        });
        var captureRun = new LiveTranslationSession(new LocalTranslator(), captureOverlay, captureStatuses.Add)
            .RunAsync(() =>
            {
                boundsOffDispatcher |= !captureOverlay.Dispatcher.CheckAccess();
                return new Rectangle(80, 60, 600, 300);
            }, "ja", "th", SubtitleStyle.Overwrite, 6, captureCancellation.Token, hideOriginals: true);
        bool yieldedBeforeCaptureFinished = !captureGate.Task.IsCompleted;
        try
        {
            await captureStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            bool dispatcherResponded = false;
            await captureOverlay.Dispatcher.InvokeAsync(() => dispatcherResponded = true, DispatcherPriority.ApplicationIdle);
            captureCancellation.Cancel();
            captureGate.TrySetResult();
            try { await captureRun; } catch (OperationCanceledException) { }
            Require(yieldedBeforeCaptureFinished && dispatcherResponded && ScreenCapture.CapturedOffDispatcher
                && !boundsOffDispatcher && !captureOverlay.IsVisible
                && ScreenCapture.SessionCreated == captureSessionsCreatedBefore + 1
                && ScreenCapture.SessionDisposed == captureSessionsDisposedBefore + 1
                && !captureStatuses.Any(status => status.Contains("translated blocks", StringComparison.Ordinal)),
                "Capture must use one disposed session, yield the UI, and prevent late captions after cancellation.");
            Console.WriteLine("PASS: one capture session yields the UI, stops without late captions, and is disposed.");
        }
        finally
        {
            captureCancellation.Cancel();
            captureGate.TrySetResult();
            try { await captureRun; } catch (OperationCanceledException) { }
            captureOverlay.Close();
            ScreenCapture.CaptureGate = null;
            ScreenCapture.CaptureStarted = null;
        }
        var ownershipOverlay = new SubtitleOverlay();
        using var ownershipCancellation = new CancellationTokenSource();
        var firstOwnedCapture = new TaskCompletionSource<Bitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextOwnershipCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int ownershipCaptureCount = 0;
        ScreenCapture.Marker = 1;
        ScreenCapture.CaptureObserver = bitmap =>
        {
            int count = Interlocked.Increment(ref ownershipCaptureCount);
            if (count == 1) firstOwnedCapture.TrySetResult(bitmap);
            else if (count == 2) nextOwnershipCapture.TrySetResult();
        };
        var ownershipRun = new LiveTranslationSession(new LocalTranslator(), ownershipOverlay, _ => { })
            .RunAsync(() => new Rectangle(80, 60, 600, 300), "ja", "th",
                SubtitleStyle.Overwrite, 6, ownershipCancellation.Token, hideOriginals: true);
        Bitmap ownedCapture = await firstOwnedCapture.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            await nextOwnershipCapture.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await ownershipOverlay.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(BitmapReadable(ownedCapture),
                "The unchanged captured frame must remain owned as the next comparison frame.");
            ownershipCancellation.Cancel();
            try { await ownershipRun; } catch (OperationCanceledException) { }
            Require(!BitmapReadable(ownedCapture),
                "Stopping the session must dispose the captured comparison frame it owns.");
            Console.WriteLine("PASS: captured comparison-frame ownership transfers once and is disposed on Stop.");
        }
        finally
        {
            ScreenCapture.CaptureObserver = null;
            ownershipCancellation.Cancel();
            try { await ownershipRun; } catch (OperationCanceledException) { }
            ownershipOverlay.Close();
        }
        var motionOverlay = new SubtitleOverlay();
        using var motionCancellation = new CancellationTokenSource();
        int motionCapture = 0;
        bool moving = true;
        ScreenCapture.Marker = 3;
        ScreenCapture.MarkerProvider = () => Volatile.Read(ref moving)
            ? Interlocked.Increment(ref motionCapture) % 2 == 0 ? 1 : 3
            : 3;
        SpatialOcr.RecognizeCalls = 0;
        var motionRun = new LiveTranslationSession(new LocalTranslator(), motionOverlay, _ => { })
            .RunAsync(() => new Rectangle(80, 60, 600, 300), "ja", "th",
                SubtitleStyle.Overwrite, 6, motionCancellation.Token, hideOriginals: true);
        try
        {
            await Task.Delay(800);
            Require(SpatialOcr.RecognizeCalls == 0 && !motionOverlay.IsVisible,
                "Continuously changing text frames must not start OCR before the view becomes quiet.");
            Volatile.Write(ref moving, false);
            ScreenCapture.MarkerProvider = null;
            await Until(() => Volatile.Read(ref SpatialOcr.RecognizeCalls) > 0, motionRun);
            Console.WriteLine("PASS: continuous text motion defers OCR until the captured view becomes quiet.");
        }
        finally
        {
            Volatile.Write(ref moving, false);
            ScreenCapture.MarkerProvider = null;
            motionCancellation.Cancel();
            try { await motionRun; } catch (OperationCanceledException) { }
            motionOverlay.Close();
        }
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
            Require(!overlay.IsVisible, "The manga page must remain visible while its text is being recognized.");
            await Until(() => statuses.Any(s => s.Contains("translated blocks")), running);
            await overlay.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var originalVisuals = ((Canvas)overlay.Content).Children.Cast<object>().ToArray();
            Require(originalVisuals.Length == 2 && overlay.IsVisible
                && originalVisuals.Count(visual => visual is Border) == 2
                && originalVisuals.All(visual => visual is not System.Windows.Controls.Image),
                "Manga overwrite must draw captions over the live page, without a captured page image.");
            ScreenCapture.Marker = 6;
            await Until(() => statuses.Any(s => s.StartsWith("No text detected")), running);
            Require(!overlay.IsVisible,
                "A no-text page must remain visible without stale translated artwork.");
            ScreenCapture.Marker = 2;
            await Until(() => statuses.Any(s => s.Contains("not enough space")), running);
            await overlay.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(!running.IsCompleted, "A layout failure must leave the live session running.");
            var failedVisuals = ((Canvas)overlay.Content).Children.OfType<Border>().ToArray();
            Require(!overlay.IsVisible && failedVisuals.Length == 0,
                "A failed layout must leave the live manga visible without stale captions.");
            Require(statuses.Last().Contains("Watching for changes") || statuses.Last().Contains("Waiting for a readable frame"),
                "A layout failure must explain that the app is waiting for a readable frame.");
            int completed = statuses.Count(s => s.Contains("translated blocks"));
            ScreenCapture.Marker = 3;
            await Until(() => statuses.Count(s => s.Contains("translated blocks")) > completed, running);
            Require(((Canvas)overlay.Content).Children.OfType<Border>().Any(b => b.Child is TextBlock t && t.Text == "Recovered caption"),
                "The next changed readable frame must replace the held page.");
            ScreenCapture.Marker = 4;
            try { await running.WaitAsync(TimeSpan.FromSeconds(10)); throw new Exception("Invalid model output was swallowed."); }
            catch (InvalidOperationException error) when (error is not SubtitleLayoutException && error.Message.Contains("empty translation")) { }
            Require(!overlay.IsVisible, "Fatal model errors must still end and clean up the session.");
            Console.WriteLine("PASS: live manga stays visible through OCR and layout failure; the next frame recovers, invalid model reply remains fatal.");
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

        var backgroundOverlay = new SubtitleOverlay();
        var backgroundStatuses = new List<string>();
        using var backgroundCancellation = new CancellationTokenSource();
        ScreenCapture.Marker = 1;
        SpatialOcr.RecognizeCalls = 0;
        var backgroundRun = new LiveTranslationSession(new LocalTranslator(), backgroundOverlay, backgroundStatuses.Add)
            .RunAsync(() => bounds, "ja", "th", SubtitleStyle.Overwrite, 6,
                backgroundCancellation.Token, hideOriginals: true);
        try
        {
            await Until(() => backgroundStatuses.Any(status => status.Contains("translated blocks")), backgroundRun);
            bool hiddenDuringBackgroundAnimation = false;
            backgroundOverlay.IsVisibleChanged += (_, _) => hiddenDuringBackgroundAnimation |= !backgroundOverlay.IsVisible;
            int callsBeforeAnimation = Volatile.Read(ref SpatialOcr.RecognizeCalls);
            int backgroundFrame = 0;
            ScreenCapture.BackgroundProvider = () => Interlocked.Increment(ref backgroundFrame) % 2 == 0 ? 30 : 220;
            await Until(() => Volatile.Read(ref SpatialOcr.RecognizeCalls) > callsBeforeAnimation, backgroundRun);
            Require(backgroundOverlay.IsVisible && !hiddenDuringBackgroundAnimation,
                "Animation outside known text must keep captions visible while periodic rescans remain possible.");
            Console.WriteLine("PASS: background-only animation keeps captions visible and remains eligible for rescans.");
        }
        finally
        {
            ScreenCapture.BackgroundProvider = null;
            backgroundCancellation.Cancel();
            try { await backgroundRun; } catch (OperationCanceledException) { } catch (InvalidOperationException) { }
            backgroundOverlay.Close();
        }

        var bounceOverlay = new SubtitleOverlay();
        var bounceStatuses = new List<string>();
        using var bounceCancellation = new CancellationTokenSource();
        ScreenCapture.Marker = 8;
        ScreenCapture.ScrollOffset = 0;
        SpatialOcr.RecognizeCalls = 0;
        var bounceRun = new LiveTranslationSession(new LocalTranslator(), bounceOverlay, bounceStatuses.Add)
            .RunAsync(() => new Rectangle(80, 60, 640, 480), "ja", "th",
                SubtitleStyle.Overwrite, 6, bounceCancellation.Token, hideOriginals: true);
        try
        {
            await Until(() => bounceStatuses.Any(status => status.Contains("2 translated blocks")), bounceRun);
            int callsBeforeMotion = Volatile.Read(ref SpatialOcr.RecognizeCalls);
            int[] offsets = { 120, 60, 0, 0, 60, 120, 120, 60 };
            int offsetIndex = -1;
            int corruptionCapture = 0;
            bool shownDuringMotion = false;
            ScreenCapture.CaptureDelayMs = 80;
            ScreenCapture.ScrollOffsetProvider = () => offsets[Interlocked.Increment(ref offsetIndex) % offsets.Length];
            ScreenCapture.ScrollCorruptionProvider = () => Interlocked.Increment(ref corruptionCapture) == 1;
            bounceOverlay.IsVisibleChanged += (_, _) => shownDuringMotion |= bounceOverlay.IsVisible;
            await Until(() => bounceStatuses.Any(status => status.StartsWith("Scrolling detected", StringComparison.Ordinal)), bounceRun);
            shownDuringMotion = false;
            await Task.Delay(1100);
            Require(!bounceOverlay.IsVisible && !shownDuringMotion
                && Volatile.Read(ref SpatialOcr.RecognizeCalls) == callsBeforeMotion,
                "One repeated sampled position during continuous motion must not restore captions or start OCR.");
            ScreenCapture.ScrollOffsetProvider = null;
            ScreenCapture.ScrollCorruptionProvider = null;
            ScreenCapture.CaptureDelayMs = 0;
            ScreenCapture.ScrollOffset = 120;
            int statusBeforeSettle = bounceStatuses.Count;
            await Until(() => bounceOverlay.IsVisible
                && bounceStatuses.Any(status => status.StartsWith("Captions restored after scrolling", StringComparison.Ordinal)), bounceRun);
            int restored = bounceStatuses.FindIndex(statusBeforeSettle,
                status => status.StartsWith("Captions restored after scrolling", StringComparison.Ordinal));
            int rescan = bounceStatuses.FindIndex(statusBeforeSettle,
                status => status.StartsWith("Recognizing text", StringComparison.Ordinal));
            Require(restored >= 0 && (rescan < 0 || restored < rescan),
                "The original trusted frame must restore before a post-scroll rescan starts.");
            Console.WriteLine("PASS: a failed intermediate motion proof keeps one hidden trusted frame, then restores it only after two quiet observations.");
        }
        finally
        {
            ScreenCapture.ScrollOffsetProvider = null;
            ScreenCapture.ScrollCorruptionProvider = null;
            ScreenCapture.CaptureDelayMs = 0;
            ScreenCapture.ScrollOffset = 0;
            bounceCancellation.Cancel();
            try { await bounceRun; } catch (OperationCanceledException) { } catch (InvalidOperationException) { }
            bounceOverlay.Close();
        }

        var rejectedCandidateOverlay = new SubtitleOverlay();
        var rejectedCandidateStatuses = new List<string>();
        using var rejectedCandidateCancellation = new CancellationTokenSource();
        ScreenCapture.Marker = 8;
        ScreenCapture.ScrollOffset = 0;
        ScreenCapture.ScrollPaddingChanged = false;
        ScreenCapture.ScrollReplacementPage = false;
        SpatialOcr.RecognizeCalls = 0;
        var rejectedCandidateRun = new LiveTranslationSession(new LocalTranslator(), rejectedCandidateOverlay,
            rejectedCandidateStatuses.Add).RunAsync(() => new Rectangle(80, 60, 640, 480), "ja", "th",
                SubtitleStyle.Overwrite, 6, rejectedCandidateCancellation.Token, hideOriginals: true);
        try
        {
            await Until(() => rejectedCandidateStatuses.Any(status => status.Contains("2 translated blocks")),
                rejectedCandidateRun);
            int initialCalls = Volatile.Read(ref SpatialOcr.RecognizeCalls);
            int corruptionCapture = 0;
            ScreenCapture.ScrollOffset = 60;
            ScreenCapture.ScrollCorruptionProvider = () => Interlocked.Increment(ref corruptionCapture) == 1;
            await Until(() => rejectedCandidateStatuses.Any(status => status.StartsWith("Scrolling detected",
                StringComparison.Ordinal)), rejectedCandidateRun);
            ScreenCapture.ScrollCorruptionProvider = null;
            ScreenCapture.ScrollOffset = 120;
            ScreenCapture.ScrollPaddingChanged = true;
            int paddingPhase = rejectedCandidateStatuses.Count;
            await Until(() => Volatile.Read(ref SpatialOcr.RecognizeCalls) > initialCalls, rejectedCandidateRun);
            Require(!rejectedCandidateStatuses.Skip(paddingPhase).Any(status =>
                    status.StartsWith("Captions restored after scrolling", StringComparison.Ordinal)),
                "A changed pixel in the recognizer padding must reject the hidden caption candidate.");

            await Until(() => rejectedCandidateOverlay.IsVisible
                && rejectedCandidateStatuses.Skip(paddingPhase).Any(status => status.Contains("translated blocks")),
                rejectedCandidateRun);
            int callsBeforePage = Volatile.Read(ref SpatialOcr.RecognizeCalls);
            int pagePhase = rejectedCandidateStatuses.Count;
            ScreenCapture.ScrollReplacementPage = true;
            await Until(() => Volatile.Read(ref SpatialOcr.RecognizeCalls) > callsBeforePage, rejectedCandidateRun);
            Require(!rejectedCandidateStatuses.Skip(pagePhase).Any(status =>
                    status.StartsWith("Captions restored after scrolling", StringComparison.Ordinal)),
                "An unrelated page must reject the hidden caption candidate and use fresh OCR.");
            Console.WriteLine("PASS: changed OCR padding and an unrelated page reject the hidden motion candidate.");
        }
        finally
        {
            ScreenCapture.ScrollCorruptionProvider = null;
            ScreenCapture.ScrollPaddingChanged = false;
            ScreenCapture.ScrollReplacementPage = false;
            ScreenCapture.ScrollOffset = 0;
            rejectedCandidateCancellation.Cancel();
            try { await rejectedCandidateRun; } catch (OperationCanceledException) { } catch (InvalidOperationException) { }
            rejectedCandidateOverlay.Close();
        }

        var progressiveOverlay = new SubtitleOverlay();
        using var progressiveCancellation = new CancellationTokenSource();
        var progressiveTranslator = new LocalTranslator();
        var progressiveStatuses = new List<string>();
        LocalTranslator.WarmupGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LocalTranslator.ProgressiveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LocalTranslator.FirstProgressDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LocalTranslator.RequestBatches.Clear();
        bool progressiveStatusOffDispatcher = false;
        ScreenCapture.Marker = 7;
        var progressiveRun = new LiveTranslationSession(progressiveTranslator, progressiveOverlay, status =>
        {
            progressiveStatuses.Add(status);
            progressiveStatusOffDispatcher |= !progressiveOverlay.Dispatcher.CheckAccess();
        })
            .RunAsync(() => bounds, "ja", "th", SubtitleStyle.Overwrite, 6,
                progressiveCancellation.Token, hideOriginals: true);
        try
        {
            await Until(() => progressiveStatuses.Any(status => status.StartsWith("Translating 5 text blocks", StringComparison.Ordinal)),
                progressiveRun);
            Require(progressiveTranslator.Parallelism == 1 && LocalTranslator.RequestBatches.Count == 0,
                "Translation must wait for warmup capability before choosing its first batch size.");
            LocalTranslator.WarmupGate.SetResult();
            await Until(() => LocalTranslator.FirstProgressDelivered.Task.IsCompleted, progressiveRun);
            await progressiveOverlay.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var partial = ((Canvas)progressiveOverlay.Content).Children;
            Require(partial.OfType<System.Windows.Controls.Image>().Count() == 0
                && partial.OfType<Border>().Count(border => border.Child is null) == 1
                && partial.OfType<Border>().Count(border => border.Child is TextBlock) == 1
                && progressiveTranslator.Parallelism == 4
                && LocalTranslator.RequestBatches.FirstOrDefault()?.SequenceEqual(
                    new[] { "7-0", "7-1", "7-2", "7-3" }) == true
                && !progressiveStatusOffDispatcher,
                "Warmup capability must select a four-region first batch before its first caption appears.");
            LocalTranslator.ProgressiveGate.SetResult();
            await Until(() => ((Canvas)progressiveOverlay.Content).Children.OfType<Border>()
                .Count(border => border.Child is TextBlock) == 5, progressiveRun);
            Console.WriteLine("PASS: the first manga caption appears over the live page before the next region finishes.");
        }
        finally
        {
            LocalTranslator.WarmupGate?.TrySetResult();
            LocalTranslator.ProgressiveGate.TrySetResult();
            progressiveCancellation.Cancel();
            try { await progressiveRun; } catch (OperationCanceledException) { } catch (InvalidOperationException) { }
            progressiveOverlay.Close();
            LocalTranslator.WarmupGate = null;
            LocalTranslator.ProgressiveGate = null;
            LocalTranslator.FirstProgressDelivered = null;
            LocalTranslator.RequestBatches.Clear();
        }

        var navigationOverlay = new SubtitleOverlay();
        var navigationStatuses = new List<string>();
        using var navigationCancellation = new CancellationTokenSource();
        using var navigation = new NavigationInputObserver();
        var navigationProgressGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var navigationFirstProgress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool visibleWhileNavigating = false;
        Bitmap? discardedCapture = null;
        LocalTranslator.ProgressiveGate = navigationProgressGate;
        LocalTranslator.FirstProgressDelivered = navigationFirstProgress;
        ScreenCapture.Marker = 7;
        ScreenCapture.CaptureCalls = 0;
        var navigationRun = new LiveTranslationSession(new LocalTranslator(), navigationOverlay,
            navigationStatuses.Add, null, navigation).RunAsync(() => bounds, "ja", "th",
                SubtitleStyle.Overwrite, 6, navigationCancellation.Token, hideOriginals: true);
        navigationOverlay.IsVisibleChanged += (_, _) =>
        {
            if (navigation.IsActive && navigationOverlay.IsVisible) visibleWhileNavigating = true;
        };
        try
        {
            await navigationFirstProgress.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await navigationOverlay.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(navigationOverlay.IsVisible,
                "The navigation gate fixture must begin with a visible progressive caption.");

            ScreenCapture.CaptureStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ScreenCapture.CaptureGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ScreenCapture.CaptureObserver = bitmap => discardedCapture = bitmap;
            await ScreenCapture.CaptureStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            int callsAtNavigation = Volatile.Read(ref ScreenCapture.CaptureCalls);

            navigation.SignalForTest();
            Require(!navigationOverlay.IsVisible,
                "Navigation input must immediately suspend captions before the captured page is analyzed.");
            navigationProgressGate.SetResult();
            ScreenCapture.CaptureGate.SetResult();
            await Task.Delay(250);
            Require(!navigationOverlay.IsVisible && !visibleWhileNavigating
                && Volatile.Read(ref ScreenCapture.CaptureCalls) == callsAtNavigation
                && discardedCapture is not null && !BitmapReadable(discardedCapture),
                "Navigation must discard an in-flight capture and block new capture and stale progress publication.");

            int completedBeforeRelease = navigationStatuses.Count(status => status.Contains("5 translated blocks"));
            ScreenCapture.CaptureGate = null;
            ScreenCapture.CaptureObserver = null;
            navigation.ReleaseForTest();
            await Until(() => navigationStatuses.Count(status => status.Contains("5 translated blocks"))
                    > completedBeforeRelease && navigationOverlay.IsVisible,
                navigationRun);
            Require(!visibleWhileNavigating,
                "Only fresh post-navigation captures may publish captions.");
            Console.WriteLine("PASS: navigation suspends captions, discards the active capture, and blocks stale progress until fresh captures settle.");
        }
        finally
        {
            ScreenCapture.CaptureGate?.TrySetResult();
            ScreenCapture.CaptureGate = null;
            ScreenCapture.CaptureStarted = null;
            ScreenCapture.CaptureObserver = null;
            LocalTranslator.ProgressiveGate?.TrySetResult();
            LocalTranslator.ProgressiveGate = null;
            LocalTranslator.FirstProgressDelivered = null;
            navigationCancellation.Cancel();
            try { await navigationRun; } catch (OperationCanceledException) { } catch (InvalidOperationException) { }
            navigationOverlay.Close();
        }
        Require(!navigation.HasSubscribers,
            "Stopping a reading session must detach its navigation callback.");

        var staleOverlay = new SubtitleOverlay();
        using var staleCancellation = new CancellationTokenSource();
        bool cancellationQueued = false;
        bool visibleAfterCancellation = false;
        staleOverlay.IsVisibleChanged += (_, _) =>
        {
            if (staleOverlay.IsVisible && staleCancellation.IsCancellationRequested)
                visibleAfterCancellation = true;
        };
        ScreenCapture.Marker = 7;
        var staleRun = new LiveTranslationSession(new LocalTranslator(), staleOverlay, status =>
        {
            if (!cancellationQueued && status.StartsWith("Translated 1 of", StringComparison.Ordinal))
            {
                cancellationQueued = true;
                staleOverlay.Dispatcher.BeginInvoke(staleCancellation.Cancel, DispatcherPriority.Normal);
            }
        }).RunAsync(() => bounds, "ja", "th", SubtitleStyle.Overwrite, 6,
            staleCancellation.Token, hideOriginals: true);
        try
        {
            try { await staleRun.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
            await staleOverlay.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(cancellationQueued && !visibleAfterCancellation,
                "A queued partial render must not show a stale caption after cancellation.");
            Console.WriteLine("PASS: queued partial render observes cancellation before touching the overlay.");
        }
        finally
        {
            staleCancellation.Cancel();
            try { await staleRun; } catch (OperationCanceledException) { }
            staleOverlay.Close();
        }

        var recognitionScrollOverlay = new SubtitleOverlay();
        var recognitionScrollStatuses = new List<string>();
        var recognitionScrollBounds = new Rectangle(80, 60, 640, 480);
        using var recognitionScrollCancellation = new CancellationTokenSource();
        ScreenCapture.Marker = 9;
        ScreenCapture.ScrollOffset = 0;
        SpatialOcr.FirstProgressiveChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SpatialOcr.NextProgressiveChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SpatialOcr.SecondProgressiveChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SpatialOcr.ProgressiveFinal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recognitionScrollRun = new LiveTranslationSession(new LocalTranslator(), recognitionScrollOverlay,
            recognitionScrollStatuses.Add).RunAsync(() => recognitionScrollBounds, "ja-comic", "th",
                SubtitleStyle.Overwrite, 6, recognitionScrollCancellation.Token, hideOriginals: true);
        try
        {
            await Until(() => SpatialOcr.FirstProgressiveChunk.Task.IsCompleted, recognitionScrollRun);
            Require(((Canvas)recognitionScrollOverlay.Content).Children.OfType<Border>()
                    .Any(border => border.Child is TextBlock text && text.Text == "Progress 1"),
                "A recognized OCR chunk must render a caption before final OCR completes.");
            ScreenCapture.ScrollOffset = 120;
            await Until(() => recognitionScrollStatuses.Any(status => status.StartsWith("Scrolling detected", StringComparison.Ordinal)),
                recognitionScrollRun);
            Require(!recognitionScrollOverlay.IsVisible,
                "Recognized captions must be hidden while their source geometry is moving.");
            SpatialOcr.NextProgressiveChunk.SetResult();
            await Until(() => SpatialOcr.SecondProgressiveChunk.Task.IsCompleted, recognitionScrollRun);
            await recognitionScrollOverlay.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(!recognitionScrollOverlay.IsVisible
                && !((Canvas)recognitionScrollOverlay.Content).Children.OfType<Border>()
                    .Any(border => border.Child is TextBlock text && text.Text == "Progress 5"),
                "A later OCR chunk must not show or republish the original frame during motion.");
            SpatialOcr.ProgressiveFinal.SetResult();
            await Until(() => recognitionScrollStatuses.Any(status => status.Contains("5 translated blocks")), recognitionScrollRun);
            Require(((Canvas)recognitionScrollOverlay.Content).Children.OfType<Border>()
                    .Count(border => border.Child is TextBlock) == 5,
                "Final progressive OCR must align and publish all captions after the scroll.");
            Console.WriteLine("PASS: OCR progress renders before final OCR and stays aligned across a mid-recognition scroll.");
        }
        finally
        {
            SpatialOcr.NextProgressiveChunk?.TrySetResult();
            SpatialOcr.ProgressiveFinal?.TrySetResult();
            recognitionScrollCancellation.Cancel();
            try { await recognitionScrollRun; } catch (OperationCanceledException) { } catch (InvalidOperationException) { }
            recognitionScrollOverlay.Close();
            SpatialOcr.FirstProgressiveChunk = null;
            SpatialOcr.NextProgressiveChunk = null;
            SpatialOcr.SecondProgressiveChunk = null;
            SpatialOcr.ProgressiveFinal = null;
            ScreenCapture.ScrollOffset = 0;
        }

        var equivalentOverlay = new SubtitleOverlay();
        var equivalentStatuses = new List<string>();
        using var equivalentCancellation = new CancellationTokenSource();
        ScreenCapture.Marker = 8;
        ScreenCapture.ScrollOffset = 0;
        ScreenCapture.ScrollRegionPixelChanged = false;
        SpatialOcr.RecognizeCalls = 0;
        LocalTranslator.RescanGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LocalTranslator.RescanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var equivalentRun = new LiveTranslationSession(new LocalTranslator(), equivalentOverlay,
            equivalentStatuses.Add).RunAsync(() => new Rectangle(80, 60, 640, 480), "ja", "th",
                SubtitleStyle.Overwrite, 6, equivalentCancellation.Token, hideOriginals: true);
        try
        {
            await LocalTranslator.RescanStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            int initialCalls = Volatile.Read(ref SpatialOcr.RecognizeCalls);
            ScreenCapture.ScrollOffset = 120;
            await Until(() => equivalentStatuses.Any(status => status.StartsWith("Scrolling detected",
                StringComparison.Ordinal)), equivalentRun);

            var exactZeroCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var changedZeroCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ScreenCapture.ScrollCaptured = (offset, changed) =>
            {
                if (offset == 0 && changed) changedZeroCaptured.TrySetResult();
                else if (offset == 0) exactZeroCaptured.TrySetResult();
            };
            ScreenCapture.ScrollOffset = 0;
            await exactZeroCaptured.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ScreenCapture.ScrollRegionPixelChanged = true;
            await changedZeroCaptured.Task.WaitAsync(TimeSpan.FromSeconds(5));
            int changedPhase = equivalentStatuses.Count;
            LocalTranslator.RescanGate.SetResult();

            await Until(() => Volatile.Read(ref SpatialOcr.RecognizeCalls) > initialCalls
                && equivalentOverlay.IsVisible, equivalentRun);
            int freshRecognition = equivalentStatuses.FindIndex(changedPhase,
                status => status.StartsWith("Recognizing text", StringComparison.Ordinal));
            int restored = equivalentStatuses.FindIndex(changedPhase,
                status => status.StartsWith("Captions restored after scrolling", StringComparison.Ordinal));
            Require(freshRecognition >= 0 && (restored < 0 || freshRecognition < restored),
                "A completed result with an exact region mismatch must start fresh OCR before captions can return.");
            Console.WriteLine("PASS: a sub-threshold region change rejects a pending stale result and cannot strand the hidden overlay.");
        }
        finally
        {
            LocalTranslator.RescanGate?.TrySetResult();
            LocalTranslator.RescanGate = null;
            LocalTranslator.RescanStarted = null;
            ScreenCapture.ScrollCaptured = null;
            ScreenCapture.ScrollRegionPixelChanged = false;
            ScreenCapture.ScrollOffset = 0;
            equivalentCancellation.Cancel();
            try { await equivalentRun; } catch (OperationCanceledException) { } catch (InvalidOperationException) { }
            equivalentOverlay.Close();
        }

        var scrollOverlay = new SubtitleOverlay();
        var scrollStatuses = new List<string>();
        var scrollBounds = new Rectangle(80, 60, 640, 480);
        using var scrollCancellation = new CancellationTokenSource();
        ScreenCapture.Marker = 8;
        ScreenCapture.ScrollOffset = 0;
        SpatialOcr.KnownRequests.Clear();
        using (var beforeScroll = ScreenCapture.Capture(scrollBounds))
        {
            ScreenCapture.ScrollOffset = 120;
            using var afterScroll = ScreenCapture.Capture(scrollBounds);
            Require(ScrollAlignment.TryEstimateVerticalShift(beforeScroll, afterScroll, out int shift) && shift == 120,
                $"Synthetic scroll fixture did not align; detected {shift} pixels.");
            ScreenCapture.ScrollOffset = 0;
        }
        LocalTranslator.Requests.Clear();
        var scrollRun = new LiveTranslationSession(new LocalTranslator(), scrollOverlay, scrollStatuses.Add)
            .RunAsync(() => scrollBounds, "ja", "th", SubtitleStyle.Overwrite, 6,
                scrollCancellation.Token, hideOriginals: true);
        try
        {
            await Until(() => scrollStatuses.Any(status => status.Contains("2 translated blocks")), scrollRun);
            LocalTranslator.Requests.Clear();
            LocalTranslator.RescanGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            LocalTranslator.RescanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            LocalTranslator.RescanProgressDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            LocalTranslator.RescanFinalGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ScreenCapture.ScrollOffset = 120;
            await Until(() => LocalTranslator.RescanStarted.Task.IsCompleted, scrollRun);
            await Until(() => scrollStatuses.Any(status => status.StartsWith("Captions restored after scrolling", StringComparison.Ordinal)), scrollRun);
            await scrollOverlay.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var retained = ((Canvas)scrollOverlay.Content).Children.OfType<Border>().ToArray();
            var retainedTexts = retained.Select(border => border.Child).OfType<TextBlock>()
                .Select(text => text.Text).OrderBy(text => text).ToArray();
            Require(scrollOverlay.IsVisible
                && retained.Count(border => border.Child is null) == 2
                && retained.OfType<Border>().Count(border => border.Child is TextBlock) == 2
                && retainedTexts.SequenceEqual(new[] { "Known A", "Known B" })
                && SpatialOcr.KnownRequests.Any(request => request.SequenceEqual(new[] { "8-A", "8-B" }))
                && LocalTranslator.Requests.SequenceEqual(new[] { "8-C" }),
                $"A scroll rescan must reuse known OCR and captions, then translate only newly revealed text; saw captions [{string.Join(", ", retainedTexts)}], OCR reuse [{string.Join("; ", SpatialOcr.KnownRequests.Select(request => string.Join(",", request)))}], requests [{string.Join(", ", LocalTranslator.Requests)}], statuses [{string.Join(" | ", scrollStatuses)}].");
            int detectedScrolls = scrollStatuses.Count(status => status.StartsWith("Scrolling detected", StringComparison.Ordinal));
            int restoredScrolls = scrollStatuses.Count(status => status.StartsWith("Captions restored after scrolling", StringComparison.Ordinal));
            ScreenCapture.ScrollOffset = 140;
            await Until(() => scrollStatuses.Count(status => status.StartsWith("Scrolling detected", StringComparison.Ordinal)) > detectedScrolls, scrollRun);
            Require(!scrollOverlay.IsVisible, "Cached captions must stay hidden while a later scroll is moving them.");
            await Until(() => scrollStatuses.Count(status => status.StartsWith("Captions restored after scrolling", StringComparison.Ordinal)) > restoredScrolls, scrollRun);
            Require(scrollOverlay.IsVisible,
                "Cached captions must return after scrolling settles even while the older rescan is still pending.");
            detectedScrolls = scrollStatuses.Count(status => status.StartsWith("Scrolling detected", StringComparison.Ordinal));
            restoredScrolls = scrollStatuses.Count(status => status.StartsWith("Captions restored after scrolling", StringComparison.Ordinal));
            int staleHashCorruption = 0;
            ScreenCapture.ScrollCorruptionProvider = () => Interlocked.Increment(ref staleHashCorruption) == 1;
            ScreenCapture.ScrollOffset = 160;
            await Until(() => scrollStatuses.Count(status => status.StartsWith("Scrolling detected", StringComparison.Ordinal)) > detectedScrolls, scrollRun);
            ScreenCapture.ScrollCorruptionProvider = null;
            ScreenCapture.ScrollOffset = 140;
            await Until(() => scrollStatuses.Count(status => status.StartsWith("Captions restored after scrolling", StringComparison.Ordinal)) > restoredScrolls, scrollRun);
            Require(scrollOverlay.IsVisible,
                "A trusted shifted snapshot must restore at zero relative shift even when its retained scan hash is older.");
            LocalTranslator.RescanGate.SetResult();
            await Until(() => LocalTranslator.RescanProgressDelivered.Task.IsCompleted, scrollRun);
            await scrollOverlay.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(!((Canvas)scrollOverlay.Content).Children.OfType<Border>()
                    .Any(border => border.Child is TextBlock text && text.Text == "New C"),
                "Progress from the pre-scroll frame must not redraw captions at stale coordinates.");
            LocalTranslator.RescanFinalGate.SetResult();
            await Until(() => ((Canvas)scrollOverlay.Content).Children.OfType<Border>()
                .Any(border => border.Child is TextBlock text && text.Text == "New C"), scrollRun);
            Console.WriteLine("PASS: scroll rescans preserve known captions and translate only newly revealed text.");
        }
        finally
        {
            LocalTranslator.RescanGate?.TrySetResult();
            LocalTranslator.RescanFinalGate?.TrySetResult();
            scrollCancellation.Cancel();
            try { await scrollRun; } catch (OperationCanceledException) { } catch (InvalidOperationException) { }
            scrollOverlay.Close();
            LocalTranslator.RescanGate = null;
            LocalTranslator.RescanStarted = null;
            LocalTranslator.RescanProgressDelivered = null;
            LocalTranslator.RescanFinalGate = null;
            LocalTranslator.Requests.Clear();
            SpatialOcr.KnownRequests.Clear();
            ScreenCapture.ScrollCorruptionProvider = null;
            ScreenCapture.ScrollOffset = 0;
        }

        var stopOverlay = new SubtitleOverlay();
        var stopStatuses = new List<string>();
        using var stopCancellation = new CancellationTokenSource();
        var alignmentStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool visibleAfterStop = false;
        stopOverlay.IsVisibleChanged += (_, _) =>
        {
            if (stopOverlay.IsVisible && stopCancellation.IsCancellationRequested) visibleAfterStop = true;
        };
        ScreenCapture.Marker = 8;
        ScreenCapture.ScrollOffset = 0;
        var stopRun = new LiveTranslationSession(new LocalTranslator(), stopOverlay, status =>
        {
            stopStatuses.Add(status);
            if (status.StartsWith("Scrolling detected", StringComparison.Ordinal))
            {
                alignmentStarted.TrySetResult();
                _ = Task.Run(async () => { await Task.Delay(5); stopCancellation.Cancel(); });
            }
        }).RunAsync(() => new Rectangle(0, 0, 3840, 2088), "ja", "th",
            SubtitleStyle.Overwrite, 6, stopCancellation.Token, hideOriginals: true);
        try
        {
            await Until(() => stopStatuses.Any(status => status.Contains("2 translated blocks")), stopRun);
            ScreenCapture.ScrollOffset = 120;
            await alignmentStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            bool dispatcherResponded = false;
            await stopOverlay.Dispatcher.InvokeAsync(() => dispatcherResponded = true, DispatcherPriority.ApplicationIdle);
            try { await stopRun.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            await stopOverlay.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(dispatcherResponded && !stopOverlay.IsVisible && !visibleAfterStop,
                "Stopping during background scroll analysis must keep the UI responsive and prevent late captions.");
            Console.WriteLine("PASS: stop during background scroll analysis publishes no late overlay state.");
        }
        finally
        {
            stopCancellation.Cancel();
            try { await stopRun; } catch (OperationCanceledException) { } catch (InvalidOperationException) { }
            stopOverlay.Close();
            ScreenCapture.ScrollOffset = 0;
        }
    }

    private static byte[][] RegionFingerprints(Bitmap bitmap, IReadOnlyList<TextRegion> regions)
    {
        var method = typeof(LiveTranslationSession).GetMethod("FingerprintRegions",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var output = (Array)method.Invoke(null, new object[] { bitmap, regions })!;
        return output.Cast<object>().Select(item => (byte[])item.GetType().GetProperty("Hash")!.GetValue(item)!).ToArray();
    }

    private static byte[] LegacyRegionFingerprint(Bitmap bitmap, Rectangle region)
    {
        var bounds = Rectangle.Intersect(new Rectangle(0, 0, bitmap.Width, bitmap.Height), region);
        if (bounds.Width <= 0 || bounds.Height <= 0) return SHA256.HashData(Array.Empty<byte>());
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[bounds.Width * bounds.Height * 4];
            for (int y = 0; y < bounds.Height; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, (bounds.Y + y) * data.Stride + bounds.X * 4),
                    pixels, y * bounds.Width * 4, bounds.Width * 4);
            return SHA256.HashData(pixels);
        }
        finally { bitmap.UnlockBits(data); }
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
    static bool BitmapReadable(Bitmap bitmap)
    {
        try { _ = bitmap.GetPixel(0, 0); return true; }
        catch (ArgumentException) { return false; }
        catch (ExternalException) { return false; }
    }
    static byte[] LegacyFingerprint(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[Math.Abs(data.Stride) * data.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return SHA256.HashData(bytes);
        }
        finally { bitmap.UnlockBits(data); }
    }
    static bool NegativeStrideFingerprintMatches()
    {
        const int width = 7, height = 4, stride = width * 4;
        nint pixels = Marshal.AllocHGlobal(stride * height);
        try
        {
            using var positive = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            nint topRow = IntPtr.Add(pixels, stride * (height - 1));
            for (int y = 0; y < height; y++)
            {
                var row = new byte[stride];
                for (int x = 0; x < width; x++)
                {
                    Color color = Color.FromArgb(255, 20 + x * 9, 30 + y * 17, 40 + x + y);
                    positive.SetPixel(x, y, color);
                    int offset = x * 4;
                    row[offset] = color.B;
                    row[offset + 1] = color.G;
                    row[offset + 2] = color.R;
                    row[offset + 3] = color.A;
                }
                Marshal.Copy(row, 0, IntPtr.Add(topRow, -y * stride), stride);
            }
            using var negative = new Bitmap(width, height, -stride, PixelFormat.Format32bppArgb, topRow);
            var data = negative.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            bool signedStride;
            try { signedStride = data.Stride < 0; }
            finally { negative.UnlockBits(data); }
            return signedStride && LiveTranslationSession.Fingerprint(positive).AsSpan()
                .SequenceEqual(LiveTranslationSession.Fingerprint(negative));
        }
        finally { Marshal.FreeHGlobal(pixels); }
    }
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
}

namespace Translumo.Local
{
    internal sealed class NavigationInputObserver : IDisposable
    {
        private bool _active;
        private bool _disposed;
        private long _generation;

        internal event Action? Navigation;
        internal long Generation => Interlocked.Read(ref _generation);
        internal bool IsActive => _active;
        internal int RemainingQuietMilliseconds => _active ? 20 : 0;
        internal bool HasSubscribers => Navigation is not null;

        internal void SignalForTest()
        {
            if (_disposed) return;
            _active = true;
            Interlocked.Increment(ref _generation);
            Navigation?.Invoke();
        }

        internal void ReleaseForTest() => _active = false;

        public void Dispose()
        {
            _disposed = true;
            _active = false;
            Navigation = null;
        }
    }

    public sealed class LocalTranslator
    {
        public static TaskCompletionSource? WarmupGate;
        public static TaskCompletionSource? ProgressiveGate;
        public static TaskCompletionSource? FirstProgressDelivered;
        public static TaskCompletionSource? RescanGate;
        public static TaskCompletionSource? RescanStarted;
        public static TaskCompletionSource? RescanProgressDelivered;
        public static TaskCompletionSource? RescanFinalGate;
        public static List<string> Requests { get; } = new();
        public static List<string[]> RequestBatches { get; } = new();
        private int _parallelism = 1;
        internal int Parallelism => _parallelism;
        public async Task<string[]> TranslateAsync(string[] texts, string source, string target, CancellationToken token)
        {
            if (texts.Length == 0)
            {
                if (WarmupGate is { } warmupGate) await warmupGate.Task.WaitAsync(token);
                _parallelism = 4;
                return Array.Empty<string>();
            }
            Requests.AddRange(texts);
            RequestBatches.Add(texts.ToArray());
            if (texts.Any(text => text is "8-B" or "8-C") && RescanGate is { } rescanGate)
            {
                RescanStarted?.TrySetResult();
                await rescanGate.Task.WaitAsync(token);
            }
            return texts.Select(text => text.StartsWith("7-", StringComparison.Ordinal) ? $"Read {text[2..]}"
                : text switch {
                    "1" => "First translated caption", "2" => new string('W', 5000),
                    "3" => "Recovered caption", "5" => "Colored caption",
                    "8-A" => "Known A", "8-B" => "Known B", "8-C" => "New C",
                    "9-A" => "Progress 1", "9-B" => "Progress 2", "9-C" => "Progress 3",
                    "9-D" => "Progress 4", "9-E" => "Progress 5", _ => ""
                }).ToArray();
        }
        internal async Task<string[]> TranslateProgressiveAsync(IReadOnlyList<string> texts, string source, string target,
            Func<int, string, Task> progress, CancellationToken token)
        {
            Requests.AddRange(texts);
            RequestBatches.Add(texts.ToArray());
            var translations = texts.Select(text => text.StartsWith("7-", StringComparison.Ordinal) ? $"Read {text[2..]}"
                : text switch {
                    "1" => "First translated caption", "2" => new string('W', 5000),
                    "3" => "Recovered caption", "5" => "Colored caption",
                    "8-A" => "Known A", "8-B" => "Known B", "8-C" => "New C",
                    "9-A" => "Progress 1", "9-B" => "Progress 2", "9-C" => "Progress 3",
                    "9-D" => "Progress 4", "9-E" => "Progress 5", _ => ""
                }).ToArray();
            if (texts.Any(text => text is "8-B" or "8-C") && RescanGate is { } rescanGate)
            {
                RescanStarted?.TrySetResult();
                await rescanGate.Task;
            }
            for (int index = 0; index < translations.Length; index++)
            {
                await Task.Run(() => progress(index, translations[index]));
                if (texts[index] == "8-C")
                {
                    RescanProgressDelivered?.TrySetResult();
                    if (RescanFinalGate is { } finalGate) await finalGate.Task;
                }
                if (index == 0 && texts.Any(text => text.StartsWith("7-", StringComparison.Ordinal)))
                {
                    FirstProgressDelivered?.TrySetResult();
                    if (ProgressiveGate is { } gate) await gate.Task;
                }
            }
            return translations;
        }
    }
    public sealed class SpatialOcr : IDisposable
    {
        public static List<string[]> KnownRequests { get; } = new();
        public static int RecognizeCalls;
        public static TaskCompletionSource? FirstProgressiveChunk;
        public static TaskCompletionSource? NextProgressiveChunk;
        public static TaskCompletionSource? SecondProgressiveChunk;
        public static TaskCompletionSource? ProgressiveFinal;
        public Task<IReadOnlyList<TextRegion>> RecognizeAsync(Bitmap bitmap, string language, CancellationToken token)
            => Task.FromResult(Regions(bitmap));
        internal Task<(IReadOnlyList<TextRegion> Regions, bool HasOcrInputProof)> RecognizeFrameAsync(Bitmap bitmap,
            string language, CancellationToken token, IReadOnlyList<TextRegion> knownRegions)
        {
            Interlocked.Increment(ref RecognizeCalls);
            KnownRequests.Add(knownRegions.Select(region => region.Text).ToArray());
            return Task.FromResult((Regions(bitmap), true));
        }
        internal async Task<(IReadOnlyList<TextRegion> Regions, bool HasOcrInputProof)> RecognizeFrameProgressiveAsync(
            Bitmap bitmap, string language, CancellationToken token, IReadOnlyList<TextRegion> knownRegions,
            Func<IReadOnlyList<TextRegion>, Task> progress)
        {
            if (ScreenCapture.Marker != 9)
                return await RecognizeFrameAsync(bitmap, language, token, knownRegions);
            var regions = Enumerable.Range(0, 5)
                .Select(index => new TextRegion("", new Rectangle(30 + index * 115, 100, 85, 45))).ToArray();
            await progress(regions);
            for (int index = 0; index < 4; index++) regions[index] = regions[index] with { Text = $"9-{(char)('A' + index)}" };
            await progress(regions);
            FirstProgressiveChunk?.TrySetResult();
            if (NextProgressiveChunk is { } next) await next.Task.WaitAsync(token);
            regions[4] = regions[4] with { Text = "9-E" };
            await progress(regions);
            SecondProgressiveChunk?.TrySetResult();
            if (ProgressiveFinal is { } final) await final.Task.WaitAsync(token);
            return (regions, true);
        }
        private static IReadOnlyList<TextRegion> Regions(Bitmap bitmap)
            => ScreenCapture.Marker == 6 ? Array.Empty<TextRegion>()
                : ScreenCapture.Marker == 7
                    ? Enumerable.Range(0, 5).Select(i => new TextRegion($"7-{i}", new Rectangle(25 + i * 110, 100, 80, 40))).ToArray()
                    : ScreenCapture.Marker == 8
                        ? new[] {
                            new TextRegion("8-A", new Rectangle(50, 50 + ScreenCapture.ScrollOffset, 80, 40)),
                            new TextRegion("8-B", new Rectangle(250, 120 + ScreenCapture.ScrollOffset, 80, 40))
                        }.Concat(ScreenCapture.ScrollOffset == 0 ? Array.Empty<TextRegion>()
                            : new[] { new TextRegion("8-C", new Rectangle(420, 250, 80, 40)) }).ToArray()
                    : new[] { new TextRegion(bitmap.GetPixel(0, 0).R.ToString(),
                        new Rectangle(200, 100, ScreenCapture.Marker == 5 ? 40 : 150, 60)) };
        public void Dispose() { }
    }
    public static class ScreenCapture
    {
        internal sealed class Session : IDisposable
        {
            private bool _disposed;

            public Session() => Interlocked.Increment(ref SessionCreated);

            public Bitmap Capture(Rectangle bounds, CancellationToken token)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                token.ThrowIfCancellationRequested();
                Interlocked.Increment(ref CaptureCalls);
                Bitmap bitmap = ScreenCapture.Capture(bounds);
                try
                {
                    token.ThrowIfCancellationRequested();
                    CaptureObserver?.Invoke(bitmap);
                    return bitmap;
                }
                catch
                {
                    bitmap.Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                Interlocked.Increment(ref SessionDisposed);
            }
        }

        public static int SessionCreated;
        public static int SessionDisposed;
        public static int CaptureCalls;
        public static int Marker;
        public static int ScrollOffset;
        public static Func<int>? ScrollOffsetProvider;
        public static Func<bool>? ScrollCorruptionProvider;
        public static bool ScrollPaddingChanged;
        public static bool ScrollReplacementPage;
        public static bool ScrollRegionPixelChanged;
        public static Action<int, bool>? ScrollCaptured;
        public static int CaptureDelayMs;
        public static Func<int>? MarkerProvider;
        public static Func<int>? BackgroundProvider;
        public static TaskCompletionSource? CaptureGate;
        public static TaskCompletionSource? CaptureStarted;
        public static Action<Bitmap>? CaptureObserver;
        public static bool CapturedOffDispatcher;
        public static Bitmap Capture(Rectangle bounds)
        {
            int markerValue = MarkerProvider?.Invoke() ?? Marker;
            int scrollOffset = ScrollOffsetProvider?.Invoke() ?? ScrollOffset;
            CapturedOffDispatcher |= Application.Current is { } app && !app.Dispatcher.CheckAccess();
            CaptureStarted?.TrySetResult();
            CaptureGate?.Task.GetAwaiter().GetResult();
            if (CaptureDelayMs > 0) Thread.Sleep(CaptureDelayMs);
            var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            if (markerValue is 8 or 9)
            {
                var pixels = new byte[bitmap.Width * bitmap.Height * 4];
                var random = new Random(ScrollReplacementPage ? 43 : 17);
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    byte shade = random.Next(2) == 0 ? (byte)20 : (byte)235;
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = shade;
                    pixels[i + 3] = 255;
                }
                var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); }
                finally { bitmap.UnlockBits(data); }
                if (scrollOffset == 0)
                {
                    if (ScrollRegionPixelChanged) bitmap.SetPixel(60, 55, Color.Red);
                    ScrollCaptured?.Invoke(scrollOffset, ScrollRegionPixelChanged);
                    return bitmap;
                }
                var scrolled = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
                using (var drawing = Graphics.FromImage(scrolled))
                {
                    drawing.Clear(Color.White);
                    drawing.DrawImageUnscaled(bitmap, 0, scrollOffset);
                }
                if (ScrollCorruptionProvider?.Invoke() == true)
                    scrolled.SetPixel(60, 55 + scrollOffset, Color.Red);
                if (ScrollPaddingChanged)
                    scrolled.SetPixel(134, 55 + scrollOffset, Color.Blue);
                if (ScrollRegionPixelChanged)
                    scrolled.SetPixel(60, 55 + scrollOffset, Color.Red);
                ScrollCaptured?.Invoke(scrollOffset, ScrollRegionPixelChanged);
                bitmap.Dispose();
                return scrolled;
            }
            using (var drawing = Graphics.FromImage(bitmap))
            {
                drawing.Clear(markerValue == 5 ? Color.FromArgb(176, 32, 81) : Color.White);
                if (markerValue == 5)
                    for (int x = 0; x < bounds.Width; x += 12)
                        drawing.FillRectangle(Brushes.Black, x, 0, 6, bounds.Height);
                if (BackgroundProvider is { } background)
                {
                    using var animation = new SolidBrush(Color.FromArgb(background(), 0, 0));
                    drawing.FillRectangle(animation, 10, 10, 12, 12);
                }
                using var marker = new SolidBrush(Color.FromArgb(markerValue * 30, 0, 0));
                drawing.FillRectangle(marker, 210, 110, 12, 12);
            }
            bitmap.SetPixel(0, 0, Color.FromArgb(markerValue, 0, 0));
            bitmap.SetPixel(210, 110, Color.FromArgb(markerValue, 0, 0));
            return bitmap;
        }
    }
}
