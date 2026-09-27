using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Media = System.Windows.Media;

namespace Translumo.Local;

/// <summary>One active frame and one inference. Obsolete results never reach the overlay.</summary>
public sealed class LiveTranslationSession
{
    private readonly SpatialOcr _ocr;
    private readonly LocalTranslator _translator;
    private readonly SubtitleOverlay _overlay;
    private readonly Action<string> _status;
    private readonly NavigationInputObserver? _navigation;
    private readonly string? _diagnosticsDirectory;
    private int _version;

    public LiveTranslationSession(LocalTranslator translator, SubtitleOverlay overlay, Action<string> status, SpatialOcr? ocr = null)
        : this(translator, overlay, status, ocr, null) { }

    internal LiveTranslationSession(LocalTranslator translator, SubtitleOverlay overlay, Action<string> status,
        SpatialOcr? ocr, NavigationInputObserver? navigation, string? diagnosticsDirectory = null)
        => (_translator, _overlay, _status, _ocr, _navigation, _diagnosticsDirectory)
            = (translator, overlay, status, ocr ?? new SpatialOcr(), navigation, diagnosticsDirectory);

    public async Task RunAsync(Func<Rectangle?> getBounds, string source, string target,
        SubtitleStyle style, int padding, CancellationToken token, bool hideOriginals = false,
        CaptionStyleOptions? captionStyle = null)
    {
        using var captureSession = new ScreenCapture.Session();
        var captionProfile = CaptionStyles.ResolveInstalled(captionStyle ?? new CaptionStyleOptions());
        bool progressiveManga = hideOriginals && style == SubtitleStyle.Overwrite;
        var warmup = _translator.TranslateAsync(Array.Empty<string>(), source, target, token);
        Task layoutWarmup = Task.CompletedTask;
        byte[]? previousHash = null;
        byte[]? lastScanHash = null;
        Bitmap? previousFrame = null;
        Bitmap? lastScanFrame = null;
        RegionFingerprint[]? stableRegions = null;
        TranslatedFrame? stableTranslation = null;
        TranslatedFrame? motionTranslation = null;
        TranslatedFrame? recognizedFrame = null;
        long lastScanStarted = 0;
        Rectangle previousBounds = default;
        long changedAt = 0;
        bool captionsSuppressedForMotion = false;
        bool navigationEpisodeActive = false;
        int quietCapturedFrames = 0;
        int processedVersion = -1;
        Task<TranslatedFrame?>? pending = null;
        CancellationTokenSource? pendingCancellation = null;
        void NavigationStarted()
        {
            if (navigationEpisodeActive) return;
            navigationEpisodeActive = true;
            _version++;
            changedAt = Environment.TickCount64;
            quietCapturedFrames = 0;
            if (stableTranslation?.Frame is not null) motionTranslation ??= stableTranslation;
            captionsSuppressedForMotion = true;
            _overlay.Suspend();
        }
        if (_navigation is not null) _navigation.Navigation += NavigationStarted;
        try
        {
            layoutWarmup = target.Split('-', 2)[0] == "th"
                ? _overlay.PrepareThaiLayoutAsync() : Task.CompletedTask;
            while (!token.IsCancellationRequested)
            {
                if (warmup.IsFaulted) await warmup;
                if (layoutWarmup.IsFaulted) await layoutWarmup;
                if (_navigation is { IsActive: true } activeNavigation)
                {
                    await Task.Delay(Math.Max(1, Math.Min(100, activeNavigation.RemainingQuietMilliseconds)), token);
                    continue;
                }
                navigationEpisodeActive = false;
                var bounds = getBounds();
                if (bounds is null || bounds.Value.Width < 4 || bounds.Value.Height < 4)
                {
                    if (previousHash is not null)
                    {
                        pendingCancellation?.Cancel();
                        _version++;
                        previousHash = null;
                        previousFrame?.Dispose();
                        previousFrame = null;
                        stableRegions = null;
                        stableTranslation = null;
                        motionTranslation = null;
                        recognizedFrame = null;
                        lastScanHash = null;
                        lastScanFrame?.Dispose();
                        lastScanFrame = null;
                        captionsSuppressedForMotion = false;
                        quietCapturedFrames = 0;
                        _overlay.Clear();
                    }
                    _status("Waiting for the selected window to be visible\u2026");
                    if (pending is { IsCompleted: true })
                    {
                        try { await pending; }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                        finally
                        {
                            pending = null;
                            pendingCancellation?.Dispose();
                            pendingCancellation = null;
                        }
                    }
                }
                else
                {
                    long navigationGeneration = _navigation?.Generation ?? 0;
                    var capture = await CaptureFrameAsync(captureSession, bounds.Value, token);
                    var bitmap = capture.Bitmap;
                    bool bitmapTransferred = false;
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        if (_navigation is { } navigationAfterCapture
                            && (navigationAfterCapture.Generation != navigationGeneration || navigationAfterCapture.IsActive)) continue;
                        var hash = capture.Hash;
                        bool boundsChanged = previousHash is not null && bounds.Value != previousBounds;
                        bool frameChanged = previousHash is null || boundsChanged
                            || !hash.AsSpan().SequenceEqual(previousHash)
                                && (previousFrame is null || MeaningfullyDifferent(previousFrame, bitmap));
                        long now = Environment.TickCount64;
                        bool textChanged = frameChanged && (stableRegions is not null
                            ? stableRegions.Length == 0 || !RegionsMatch(bitmap, stableRegions)
                            : recognizedFrame is not null ? !RegionsMatch(bitmap, recognizedFrame.RegionFingerprints)
                            : pending is null || lastScanHash is not null && !hash.AsSpan().SequenceEqual(lastScanHash));
                        bool preAlignmentMotion = previousHash is null || boundsChanged || textChanged;
                        if (preAlignmentMotion)
                        {
                            changedAt = now;
                            quietCapturedFrames = 0;
                        }
                        else quietCapturedFrames = Math.Min(2, quietCapturedFrames + 1);
                        var reusable = stableTranslation ?? recognizedFrame;
                        if (textChanged && reusable?.Frame is not null)
                        {
                            if (!captionsSuppressedForMotion)
                            {
                                motionTranslation = stableTranslation?.Frame is not null ? stableTranslation : null;
                                captionsSuppressedForMotion = true;
                                _overlay.Suspend();
                                _status("Scrolling detected; captions will return when the page stops.");
                            }
                            int expectedVersion = _version;
                            var expectedFrame = reusable.Frame;
                            var analysis = await AnalyzeScrollAsync(expectedFrame, bitmap, reusable.Regions,
                                reusable.OcrProofs, checkEquivalent: false, token);
                            token.ThrowIfCancellationRequested();
                            if (_navigation is { } navigationAfterMotionAnalysis
                                && (navigationAfterMotionAnalysis.Generation != navigationGeneration
                                    || navigationAfterMotionAnalysis.IsActive)) continue;
                            var currentReusable = stableTranslation ?? recognizedFrame;
                            if (analysis is not null && expectedVersion == _version && currentReusable?.Frame is not null
                                && ReferenceEquals(currentReusable.Frame, expectedFrame)
                                && TryApplyScroll(currentReusable, analysis, out var updated))
                            {
                                if (stableTranslation is not null)
                                {
                                    stableTranslation = updated;
                                    stableRegions = updated.RegionFingerprints;
                                    recognizedFrame = null;
                                }
                                else recognizedFrame = updated;
                                textChanged = false;
                            }
                        }
                        var heldForMotion = stableTranslation ?? recognizedFrame;
                        bool heldMatchesCurrent = captionsSuppressedForMotion && heldForMotion is not null
                            && (stableTranslation is not null || heldForMotion.Version == _version)
                            && RegionsMatch(bitmap, heldForMotion.RegionFingerprints);
                        bool motionQuiet = quietCapturedFrames >= 2 && now - changedAt >= 160;
                        bool scanDue = pending is null && stableRegions is not null && lastScanHash is not null
                            && !hash.AsSpan().SequenceEqual(lastScanHash) && now - lastScanStarted >= 1000
                            && motionQuiet && (lastScanFrame is null || MeaningfullyDifferent(lastScanFrame, bitmap));
                        if (captionsSuppressedForMotion && motionQuiet && !textChanged)
                        {
                            var held = heldForMotion;
                            if (motionTranslation?.Frame is { } motionFrame)
                            {
                                var candidate = motionTranslation;
                                int expectedVersion = _version;
                                var analysis = await AnalyzeScrollAsync(motionFrame, bitmap, candidate.Regions,
                                    candidate.OcrProofs, checkEquivalent: false, token, tryExactShift: true);
                                token.ThrowIfCancellationRequested();
                                if (_navigation is { } navigationAfterRestoreAnalysis
                                    && (navigationAfterRestoreAnalysis.Generation != navigationGeneration
                                        || navigationAfterRestoreAnalysis.IsActive)) continue;
                                if (expectedVersion == _version && ReferenceEquals(motionTranslation, candidate)
                                    && candidate.Bounds == bounds.Value && analysis is not null
                                    && TryApplyScroll(candidate, analysis, out var restored)
                                    && HasCompleteOcrProofs(bitmap, restored))
                                {
                                    held = restored;
                                    heldMatchesCurrent = true;
                                    stableTranslation = restored;
                                    stableRegions = restored.RegionFingerprints;
                                    recognizedFrame = null;
                                }
                                else if (expectedVersion == _version && ReferenceEquals(motionTranslation, candidate))
                                {
                                    motionTranslation = null;
                                    stableRegions = null;
                                    stableTranslation = null;
                                    recognizedFrame = null;
                                    captionsSuppressedForMotion = false;
                                    _version++;
                                    _overlay.Clear();
                                    held = null;
                                    heldMatchesCurrent = false;
                                }
                            }
                            if (captionsSuppressedForMotion && held is not null && heldMatchesCurrent)
                            {
                                captionsSuppressedForMotion = false;
                                motionTranslation = null;
                                try
                                {
                                    _overlay.Render(held.Bounds, held.Regions, held.Translations, style, padding,
                                        held.Frame, source.Split('-', 2)[0] == "ja" && target == "th",
                                        allowMissingTranslations: stableTranslation is null, captionStyle: captionProfile,
                                        cancellationToken: token);
                                    _overlay.ConfirmRender();
                                    TryWriteLiveEvidence(held, _version, "restored", token);
                                    int translated = held.Translations.Count(value => !string.IsNullOrWhiteSpace(value));
                                    _status($"Captions restored after scrolling · {translated} translated blocks.");
                                }
                                catch (SubtitleLayoutException)
                                {
                                    _overlay.Clear();
                                    _status("There is not enough space for readable subtitles. Watching for changes.");
                                }
                            }
                        }
                        if (previousHash is null || boundsChanged || textChanged || scanDue)
                        {
                            // Let in-flight local inference finish; _version discards its stale frame and the next request reuses the worker.
                            _version++;
                            if (scanDue)
                            {
                                lastScanStarted = now;
                                _status("Checking for new or moved text\u2026");
                            }
                            if (boundsChanged)
                            {
                                stableRegions = null;
                                stableTranslation = null;
                                motionTranslation = null;
                                recognizedFrame = null;
                                lastScanHash = null;
                                lastScanFrame?.Dispose();
                                lastScanFrame = null;
                                captionsSuppressedForMotion = false;
                                quietCapturedFrames = 0;
                                _overlay.Clear();
                            }
                            else if (textChanged && (stableRegions is not null || recognizedFrame is not null))
                            {
                                stableRegions = null;
                                stableTranslation = null;
                                recognizedFrame = null;
                                if (motionTranslation is null)
                                {
                                    captionsSuppressedForMotion = false;
                                    _overlay.Clear();
                                }
                                else
                                {
                                    captionsSuppressedForMotion = true;
                                    _overlay.Suspend();
                                }
                            }
                        }
                        previousBounds = bounds.Value;
                        if (pending is { IsCompleted: true })
                        {
                            TranslatedFrame? result = null;
                            try { result = await pending; }
                            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                            finally
                            {
                                pending = null;
                                pendingCancellation?.Dispose();
                                pendingCancellation = null;
                            }
                            bool resultMatches = result is not null && result.Version == _version
                                && RegionsMatch(bitmap, result.RegionFingerprints);
                            if (result is not null && result.Version == _version && !resultMatches && result.Frame is not null)
                            {
                                int expectedVersion = _version;
                                var analysis = await AnalyzeScrollAsync(result.Frame, bitmap, result.Regions,
                                    result.OcrProofs, checkEquivalent: true, token);
                                token.ThrowIfCancellationRequested();
                                if (_navigation is { } navigationAfterResultAnalysis
                                    && (navigationAfterResultAnalysis.Generation != navigationGeneration
                                        || navigationAfterResultAnalysis.IsActive)) continue;
                                if (expectedVersion == _version && analysis is not null
                                    && TryApplyScroll(result, analysis, out var aligned))
                                {
                                    result = aligned;
                                    resultMatches = true;
                                }
                            }
                            if (result is not null && result.Version == _version && resultMatches)
                            {
                                bool captionsUnchanged = stableTranslation is not null
                                    && stableTranslation.Bounds == result.Bounds
                                    && stableTranslation.Regions.SequenceEqual(result.Regions)
                                    && stableTranslation.Translations.SequenceEqual(result.Translations, StringComparer.Ordinal);
                                if (captionsUnchanged)
                                    captionsUnchanged = await _overlay.UnderlyingVisualPixelsMatchAsync(result.Bounds,
                                        stableTranslation!.Frame, result.Frame, token);
                                if (token.IsCancellationRequested || result.Version != _version
                                    || _navigation is { } navigationAfterPixelCheck
                                    && (navigationAfterPixelCheck.Generation != navigationGeneration
                                        || navigationAfterPixelCheck.IsActive))
                                    continue;
                                if (captionsSuppressedForMotion)
                                {
                                    motionTranslation = null;
                                    stableRegions = result.RegionFingerprints;
                                    stableTranslation = result;
                                    recognizedFrame = null;
                                    lastScanHash = result.FrameHash;
                                    lastScanStarted = result.ScanStarted;
                                }
                                else try
                                {
                                    motionTranslation = null;
                                    var layoutTimer = Stopwatch.StartNew();
                                    if (!captionsUnchanged)
                                        _overlay.Render(result.Bounds, result.Regions, result.Translations, style, padding,
                                            result.Frame, source.Split('-', 2)[0] == "ja" && target == "th",
                                            captionStyle: captionProfile, cancellationToken: token);
                                    layoutTimer.Stop();
                                    _overlay.ConfirmRender();
                                    TryWriteLiveEvidence(result, _version, "complete", token);
                                    stableRegions = result.RegionFingerprints;
                                    stableTranslation = result;
                                    recognizedFrame = null;
                                    lastScanHash = result.FrameHash;
                                    lastScanStarted = result.ScanStarted;
                                    var unreadable = result.UnreadableCount > 0 ? $" \u00b7 {result.UnreadableCount} unreadable blocks; try zooming in" : "";
                                    string processing = progressiveManga
                                        ? $"processing {result.OcrMs + result.TranslationMs:N0} ms"
                                        : $"OCR {result.OcrMs:N0} ms · translation {result.TranslationMs:N0} ms";
                                    var headingStatus = captionProfile.Role == CaptionRole.Auto ? $", {_overlay.StyledHeadingCount} styled headings" : "";
                                    _status($"{result.Regions.Count - result.UnreadableCount} translated blocks{headingStatus} · local model · capture {result.CaptureMs:N0} ms · {processing} · layout {layoutTimer.ElapsedMilliseconds:N0} ms{unreadable}");
                                }
                                catch (SubtitleLayoutException error) when (error.BackgroundRejected
                                    && style == SubtitleStyle.Overwrite && hideOriginals)
                                {
                                    _overlay.Clear();
                                    _status(error.Message + " Waiting for a readable frame.");
                                }
                                catch (SubtitleLayoutException error)
                                {
                                    _overlay.Clear();
                                    _status(error.Message + " Watching for changes.");
                                }
                            }
                            else if (result is not null && result.Version == _version)
                            {
                                _version++;
                                changedAt = now;
                                quietCapturedFrames = 0;
                                _overlay.Clear();
                                if (!captionsSuppressedForMotion) motionTranslation = null;
                                recognizedFrame = null;
                            }
                        }
                        // Background-only animation keeps the current captions; scan changed pages once the worker is free.
                        if (pending is null && processedVersion != _version && quietCapturedFrames >= 2
                            && Environment.TickCount64 - changedAt >= 160)
                        {
                            processedVersion = _version;
                            pendingCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                            lastScanStarted = Environment.TickCount64;
                            lastScanHash = hash;
                            lastScanFrame?.Dispose();
                            lastScanFrame = (Bitmap)bitmap.Clone();
                            pending = TranslateFrameAsync((Bitmap)bitmap.Clone(), bounds.Value, _version,
                                source, target, style, padding, pendingCancellation.Token, hideOriginals,
                                stableTranslation, progressiveManga || stableTranslation is null,
                                capture.ElapsedMs, hash, lastScanStarted, frame => recognizedFrame = frame,
                                frame => recognizedFrame is { } current && current.Version == _version
                                    && ReferenceEquals(current.Frame, frame) && !captionsSuppressedForMotion,
                                warmup, captionProfile);
                        }
                        if (frameChanged)
                        {
                            var oldPreviousFrame = previousFrame;
                            previousFrame = bitmap;
                            previousHash = hash;
                            bitmapTransferred = true;
                            oldPreviousFrame?.Dispose();
                        }
                    }
                    finally
                    {
                        if (!bitmapTransferred) bitmap.Dispose();
                    }
                }
                await Task.Delay(100, token);
            }
        }
        finally
        {
            if (_navigation is not null) _navigation.Navigation -= NavigationStarted;
            previousFrame?.Dispose();
            lastScanFrame?.Dispose();
            _version++;
            _overlay.Clear();
            pendingCancellation?.Cancel();
            try
            {
                if (pending is not null)
                {
                    try { await pending; }
                    catch (OperationCanceledException) when (token.IsCancellationRequested || pendingCancellation?.IsCancellationRequested == true) { }
                }
            }
            finally
            {
                pendingCancellation?.Dispose();
                try { await warmup; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                finally
                {
                    try { await layoutWarmup; }
                    finally { _ocr.Dispose(); }
                }
            }
        }
    }

    private void TryWriteLiveEvidence(TranslatedFrame accepted, int generation, string phase, CancellationToken token)
    {
        if (_diagnosticsDirectory is null) return;
        try
        {
            string request = Path.Combine(_diagnosticsDirectory, "request");
            if (!File.Exists(request) || token.IsCancellationRequested || generation != _version
                || _navigation?.IsActive == true || !_overlay.IsVisible || accepted.Frame is not { } frame) return;
            _overlay.Dispatcher.VerifyAccess();
            var canvas = (Canvas)_overlay.Content;
            var desktop = System.Windows.Forms.SystemInformation.VirtualScreen;
            var capture = accepted.Bounds;
            // ponytail: one requested snapshot of the accepted live visuals; never instrument every layout.
            const long maximumPixels = 16_000_000;
            if (canvas.Children.Count == 0 || (long)desktop.Width * desktop.Height > maximumPixels
                || (long)capture.Width * capture.Height > maximumPixels
                || frame.PixelWidth != capture.Width || frame.PixelHeight != capture.Height) return;
            _overlay.UpdateLayout();
            if (token.IsCancellationRequested || generation != _version || _navigation?.IsActive == true
                || !_overlay.IsVisible) return;
            var transform = System.Windows.PresentationSource.FromVisual(_overlay)?.CompositionTarget?.TransformFromDevice
                ?? throw new InvalidOperationException("The live caption display transform is unavailable.");
            double scaleX = transform.M11, scaleY = transform.M22;
            if (!double.IsFinite(scaleX) || !double.IsFinite(scaleY) || scaleX <= 0 || scaleY <= 0) return;
            var layer = new RenderTargetBitmap(desktop.Width, desktop.Height, 96 / scaleX, 96 / scaleY,
                Media.PixelFormats.Pbgra32);
            layer.Render(canvas); // Includes the actual parent clip, including the app-controls exclusion.
            layer.Freeze();
            var compositeDrawing = new Media.DrawingVisual();
            using (var drawing = compositeDrawing.RenderOpen())
            {
                drawing.DrawImage(frame, new System.Windows.Rect(0, 0, capture.Width, capture.Height));
                drawing.DrawImage(layer, new System.Windows.Rect(desktop.X - capture.X, desktop.Y - capture.Y,
                    desktop.Width, desktop.Height));
            }
            var composite = new RenderTargetBitmap(capture.Width, capture.Height, 96, 96, Media.PixelFormats.Pbgra32);
            composite.Render(compositeDrawing);
            composite.Freeze();
            if (token.IsCancellationRequested || generation != _version || _navigation?.IsActive == true) return;
            string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", System.Globalization.CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N")[..8];
            string prefix = Path.Combine(_diagnosticsDirectory, id);
            Save(frame, prefix + ".source.png");
            Save(layer, prefix + ".layer.png");
            Save(composite, prefix + ".rendered.png");
            using var sourceFile = File.OpenRead(prefix + ".source.png");
            var assembly = typeof(LiveTranslationSession).Assembly;
            var borders = canvas.Children.OfType<Border>().ToArray();
            var report = new {
                schemaVersion = 1, id, capturedUtc = DateTime.UtcNow, phase, acceptedGeneration = generation,
                sourceGeneration = accepted.Version,
                assemblyInformationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                assemblyModuleVersionId = assembly.ManifestModule.ModuleVersionId,
                source = id + ".source.png", layer = id + ".layer.png", rendered = id + ".rendered.png",
                sourceSha256 = Convert.ToHexString(SHA256.HashData(sourceFile)),
                captureBounds = new[] { capture.X, capture.Y, capture.Width, capture.Height },
                virtualScreen = new[] { desktop.X, desktop.Y, desktop.Width, desktop.Height },
                dpi = new[] { 96 / scaleX, 96 / scaleY },
                transformFromDevice = new[] { transform.M11, transform.M12, transform.M21, transform.M22, transform.OffsetX, transform.OffsetY },
                overlayVisible = _overlay.IsVisible, overlayOpacity = _overlay.Opacity,
                canvasClip = canvas.Clip?.GetFlattenedPathGeometry().ToString(System.Globalization.CultureInfo.InvariantCulture),
                regions = accepted.Regions.Select((region, index) => new {
                    id = index.ToString(), bounds = new[] { region.Bounds.X, region.Bounds.Y, region.Bounds.Width, region.Bounds.Height },
                    recognizedText = region.Text, translation = accepted.Translations[index]
                }).ToArray(),
                captions = borders.Select(border => {
                    var text = border.Child as TextBlock;
                    int index = border.Tag is int value ? value : -1;
                    return new {
                        kind = border.Uid == "association-badge" ? "association-badge" : text is null ? "cover" : "caption",
                        regionId = index >= 0 && index < accepted.Regions.Count ? index.ToString() : null, uid = border.Uid,
                        bounds = new[] { (int)Math.Round(Canvas.GetLeft(border) / scaleX) + desktop.X - capture.X,
                            (int)Math.Round(Canvas.GetTop(border) / scaleY) + desktop.Y - capture.Y,
                            (int)Math.Round(border.Width / scaleX), (int)Math.Round(border.Height / scaleY) },
                        text = text?.Text, typeface = text?.FontFamily.Source, fontSize = text?.FontSize,
                        fontWeight = text?.FontWeight.ToString(),
                        fallbackReason = text is not null && border.Uid != "association-badge"
                            && borders.Any(other => other.Uid == "association-badge" && Equals(other.Tag, border.Tag))
                            ? "readable-margin" : null
                    };
                }).ToArray()
            };
            File.WriteAllText(prefix + ".json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            File.Delete(request);
        }
        catch (Exception error)
        {
            // Optional diagnostics must never end translation or consume a failed request.
            Debug.WriteLine("Live caption evidence was not saved: " + error.Message);
        }

        static void Save(BitmapSource image, string path)
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(file);
        }
    }

    private static Task<(Bitmap Bitmap, byte[] Hash, long ElapsedMs)> CaptureFrameAsync(
        ScreenCapture.Session session, Rectangle bounds, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        Bitmap? bitmap = null;
        try
        {
            bitmap = session.Capture(bounds, token);
            token.ThrowIfCancellationRequested();
            var hash = Fingerprint(bitmap);
            token.ThrowIfCancellationRequested();
            timer.Stop();
            return (bitmap, hash, timer.ElapsedMilliseconds);
        }
        catch
        {
            bitmap?.Dispose();
            throw;
        }
    }, token);

    private static Task<ScrollAnalysis?> AnalyzeScrollAsync(BitmapSource previousFrame, Bitmap currentFrame,
        IReadOnlyList<TextRegion> regions, IReadOnlyList<OcrRegionProof> proofs,
        bool checkEquivalent, CancellationToken token, bool tryExactShift = false)
    {
        var sourceRegions = regions.ToArray();
        var sourceProofs = proofs.ToArray();
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using var previous = BitmapFromSnapshot(previousFrame);
            if (checkEquivalent && !MeaningfullyDifferent(previous, currentFrame))
                return null;
            int shift = 0;
            TextRegion[] shifted;
            int[] retainedIndices;
            if (!tryExactShift || !ScrollAlignment.TryMatchRegionsAfterShift(previous, currentFrame,
                    sourceRegions, 0, out shifted, out retainedIndices))
            {
                if (!ScrollAlignment.TryEstimateVerticalShift(previous, currentFrame, out shift)
                    || !ScrollAlignment.TryMatchRegionsAfterShift(previous, currentFrame, sourceRegions, shift,
                        out shifted, out retainedIndices)) return null;
            }
            token.ThrowIfCancellationRequested();
            var fingerprints = FingerprintRegions(currentFrame, shifted);
            var shiftedProofs = ScrollAlignment.ShiftOcrProofs(previous, currentFrame, sourceRegions,
                sourceProofs, retainedIndices, shifted);
            var frame = Snapshot(currentFrame);
            token.ThrowIfCancellationRequested();
            return new ScrollAnalysis(false, shift, retainedIndices.Select(index => sourceRegions[index].Bounds).ToArray(),
                shifted.Select(region => region.Bounds).ToArray(), retainedIndices, frame, fingerprints, shiftedProofs);
        }, token);
    }

    private static bool HasCompleteOcrProofs(Bitmap bitmap, TranslatedFrame frame)
    {
        int expected = frame.Regions.Count(region => !string.IsNullOrWhiteSpace(region.Text));
        return expected > 0
            && ScrollAlignment.ValidateOcrProofs(bitmap, frame.Regions, frame.OcrProofs).Length == expected;
    }

    private static bool TryApplyScroll(TranslatedFrame source, ScrollAnalysis analysis,
        out TranslatedFrame updated)
    {
        updated = source;
        if (analysis.Equivalent || analysis.Frame is null
            || analysis.Indices.Length != analysis.SourceBounds.Length
            || analysis.Indices.Length != analysis.ShiftedBounds.Length
            || analysis.Indices.Length != analysis.Fingerprints.Length
            || analysis.Indices.Length != analysis.OcrProofs.Length) return false;
        var regions = new TextRegion[analysis.Indices.Length];
        var translations = new string[analysis.Indices.Length];
        for (int i = 0; i < analysis.Indices.Length; i++)
        {
            int index = analysis.Indices[i];
            if ((uint)index >= (uint)source.Regions.Count || (uint)index >= (uint)source.Translations.Count
                || source.Regions[index].Bounds != analysis.SourceBounds[i]) return false;
            regions[i] = source.Regions[index] with { Bounds = analysis.ShiftedBounds[i] };
            translations[i] = source.Translations[index];
        }
        updated = source with { Regions = regions, Translations = translations, Frame = analysis.Frame,
            UnreadableCount = regions.Count(region => string.IsNullOrWhiteSpace(region.Text)),
            RegionFingerprints = analysis.Fingerprints, OcrProofs = analysis.OcrProofs };
        return true;
    }

    private async Task<TranslatedFrame?> TranslateFrameAsync(Bitmap bitmap, Rectangle bounds, int version,
        string source, string target, SubtitleStyle style, int padding, CancellationToken token,
        bool hideOriginals, TranslatedFrame? reusableTranslation, bool renderProvisionalMasks,
        long captureMs, byte[] frameHash, long scanStarted,
        Action<TranslatedFrame> recognized, Func<BitmapSource?, bool> provisionalCurrent, Task translatorReady,
        CaptionStyleProfile captionProfile)
    {
        using (bitmap)
        {
            _status("Recognizing text on this computer\u2026");
            var ocrTimer = Stopwatch.StartNew();
            bool progressiveManga = hideOriginals && style == SubtitleStyle.Overwrite;
            var knownRegions = progressiveManga && reusableTranslation is not null
                ? ScrollAlignment.ValidateOcrProofs(bitmap, reusableTranslation.Regions, reusableTranslation.OcrProofs)
                : Array.Empty<TextRegion>();
            bool progressiveOcr = progressiveManga && renderProvisionalMasks && reusableTranslation is null
                && source.Equals("ja-comic", StringComparison.OrdinalIgnoreCase);
            BitmapSource? frame = null;
            TextRegion[]? progressiveRegions = null;
            string[]? progressiveTranslations = null;
            bool progressFramePublished = false;
            bool translatorReadyObserved = false;
            long lastProvisionalRender = 0;
            int lastRenderedCount = 0;

            void RenderProgress(IReadOnlyList<TextRegion> currentRegions, string[] currentTranslations, bool force)
            {
                if (!renderProvisionalMasks || frame is null || version != _version || token.IsCancellationRequested
                    || !provisionalCurrent(frame)) return;
                int ready = currentTranslations.Count(translation => !string.IsNullOrWhiteSpace(translation));
                if (ready == lastRenderedCount || !force && lastProvisionalRender != 0
                    && Stopwatch.GetElapsedTime(lastProvisionalRender) < TimeSpan.FromMilliseconds(100)) return;
                try
                {
                    _overlay.Render(bounds, currentRegions, currentTranslations, style, padding, frame,
                        source.Split('-', 2)[0] == "ja" && target == "th", allowMissingTranslations: true,
                        captionStyle: captionProfile, cancellationToken: token);
                }
                catch (SubtitleLayoutException)
                {
                    if (version == _version && !token.IsCancellationRequested && provisionalCurrent(frame))
                        _overlay.RestorePrevious();
                }
                lastProvisionalRender = Stopwatch.GetTimestamp();
                lastRenderedCount = ready;
            }

            async Task TranslateAvailableAsync(IReadOnlyList<TextRegion> currentRegions,
                string[] currentTranslations, bool finalPass)
            {
                var pending = currentRegions.Select((region, index) => (Region: region, Index: index))
                    .Where(item => !string.IsNullOrWhiteSpace(item.Region.Text)
                        && string.IsNullOrWhiteSpace(currentTranslations[item.Index])).ToArray();
                int offset = 0;
                while (offset < pending.Length)
                {
                    if (version != _version || token.IsCancellationRequested) return;
                    if (!translatorReadyObserved)
                    {
                        await translatorReady.WaitAsync(token);
                        if (version != _version || token.IsCancellationRequested) return;
                        translatorReadyObserved = true;
                    }
                    int count = Math.Min(Math.Clamp(_translator.Parallelism, 1, 4), pending.Length - offset);
                    var batch = pending.Skip(offset).Take(count).ToArray();
                    bool finalBatch = finalPass && offset + batch.Length == pending.Length;
                    var values = await _translator.TranslateProgressiveAsync(
                        batch.Select(item => item.Region.Text).ToArray(), source, target, async (responseIndex, translation) =>
                        {
                            await _overlay.Dispatcher.InvokeAsync(() =>
                            {
                                if (version != _version || token.IsCancellationRequested) return;
                                currentTranslations[batch[responseIndex].Index] = translation;
                                int completed = currentTranslations.Count(value => !string.IsNullOrWhiteSpace(value));
                                _status($"Translated {completed} of {currentRegions.Count} text blocks on this computer…");
                                RenderProgress(currentRegions, currentTranslations, false);
                            });
                        }, token);
                    if (version != _version || token.IsCancellationRequested) return;
                    await _overlay.Dispatcher.InvokeAsync(() =>
                    {
                        if (version != _version || token.IsCancellationRequested) return;
                        for (int index = 0; index < values.Length; index++)
                            currentTranslations[batch[index].Index] = values[index];
                        RenderProgress(currentRegions, currentTranslations, finalBatch);
                    });
                    offset += batch.Length;
                }
            }

            async Task RecognizedProgressAsync(IReadOnlyList<TextRegion> currentRegions)
            {
                if (version != _version || token.IsCancellationRequested) return;
                string[]? translations = null;
                TextRegion[]? stableRegions = null;
                await _overlay.Dispatcher.InvokeAsync(() =>
                {
                    if (version != _version || token.IsCancellationRequested) return;
                    if (progressiveRegions is null)
                    {
                        progressiveRegions = currentRegions.ToArray();
                        progressiveTranslations = new string[currentRegions.Count];
                    }
                    else
                    {
                        if (progressiveRegions.Length != currentRegions.Count)
                            throw new InvalidOperationException("Progressive OCR changed the detected region count.");
                        for (int index = 0; index < currentRegions.Count; index++)
                        {
                            if (progressiveRegions[index].Bounds != currentRegions[index].Bounds)
                                throw new InvalidOperationException("Progressive OCR changed detected region geometry.");
                            progressiveRegions[index] = currentRegions[index];
                        }
                    }
                    stableRegions = progressiveRegions;
                    translations = progressiveTranslations;
                    if (!progressFramePublished)
                    {
                        frame ??= Snapshot(bitmap);
                        var fingerprints = FingerprintRegions(bitmap, stableRegions);
                        var proofs = ScrollAlignment.FingerprintOcrInputs(bitmap, stableRegions);
                        progressFramePublished = true;
                        recognized(new(version, bounds, stableRegions, translations!, captureMs,
                            ocrTimer.ElapsedMilliseconds, 0, frame,
                            stableRegions.Count(region => string.IsNullOrWhiteSpace(region.Text)),
                            fingerprints, proofs, frameHash, scanStarted));
                    }
                    int pending = stableRegions.Count(region => !string.IsNullOrWhiteSpace(region.Text))
                        - translations!.Count(value => !string.IsNullOrWhiteSpace(value));
                    _status($"Translating {Math.Max(0, pending)} recognized text blocks while reading the page…");
                });
                if (stableRegions is not null && translations is not null)
                    await TranslateAvailableAsync(stableRegions, translations, false);
            }

            var ocrResult = progressiveOcr
                ? await _ocr.RecognizeFrameProgressiveAsync(bitmap, source, token, knownRegions, RecognizedProgressAsync)
                : await _ocr.RecognizeFrameAsync(bitmap, source, token, knownRegions);
            IReadOnlyList<TextRegion> regions = progressiveRegions ?? ocrResult.Regions;
            if (progressiveRegions is not null)
                for (int index = 0; index < progressiveRegions.Length; index++)
                    progressiveRegions[index] = ocrResult.Regions[index];
            var ocrProofs = ScrollAlignment.FingerprintOcrInputs(bitmap, regions);
            if (!ocrResult.HasOcrInputProof)
                ocrProofs = ocrProofs.Select(proof => proof with { Hash = Array.Empty<byte>() }).ToArray();
            ocrTimer.Stop();
            if (version != _version || token.IsCancellationRequested) return null;
            int unreadable = regions.Count(region => string.IsNullOrWhiteSpace(region.Text));
            if (regions.Count == 0 || (hideOriginals && unreadable == regions.Count))
            {
                if (hideOriginals)
                {
                    await _overlay.Dispatcher.InvokeAsync(_overlay.Clear);
                    _status(regions.Count == 0
                        ? "No text detected on the changed view; watching for text."
                        : "Detected text could not be read on the changed view; try zooming in.");
                    return null;
                }
                _status("Watching for text\u2026");
                return new(version, bounds, regions, Array.Empty<string>(), captureMs, ocrTimer.ElapsedMilliseconds,
                    0, null, 0, FingerprintRegions(bitmap, regions), ocrProofs, frameHash, scanStarted);
            }
            frame ??= Snapshot(bitmap);
            if (!progressiveManga && renderProvisionalMasks)
            {
                await _overlay.Dispatcher.InvokeAsync(() =>
                {
                    if (version != _version || token.IsCancellationRequested || _navigation?.IsActive == true) return;
                    try
                    {
                        _overlay.Render(bounds, regions, regions.Select(_ => "\u2026").ToArray(),
                            style, padding, frame, captionStyle: captionProfile, cancellationToken: token);
                    }
                    catch (SubtitleLayoutException) { /* Continue translating; the final caption may fit differently. */ }
                });
            }
            if (!progressFramePublished)
            {
                await _overlay.Dispatcher.InvokeAsync(() =>
                {
                    if (version == _version && !token.IsCancellationRequested && !progressFramePublished)
                    {
                        progressFramePublished = true;
                        recognized(new(version, bounds, regions, regions.Select(_ => "\u2026").ToArray(), captureMs,
                            ocrTimer.ElapsedMilliseconds, 0, frame, unreadable, FingerprintRegions(bitmap, regions),
                            ocrProofs, frameHash, scanStarted));
                    }
                });
            }
            var translationTimer = Stopwatch.StartNew();
            var translations = progressiveTranslations ?? new string[regions.Count];
            if (progressiveManga && reusableTranslation is not null)
            {
                var known = reusableTranslation.Regions.Select((region, index) =>
                        (Text: region.Text, Translation: reusableTranslation.Translations[index]))
                    .Where(item => !string.IsNullOrWhiteSpace(item.Text) && !string.IsNullOrWhiteSpace(item.Translation))
                    .GroupBy(item => item.Text, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First().Translation, StringComparer.Ordinal);
                for (int i = 0; i < regions.Count; i++)
                    if (known.TryGetValue(regions[i].Text, out var translation)) translations[i] = translation;
                bool hasPendingRegions = regions.Select((region, index) => (Region: region, Index: index))
                    .Where(item => !string.IsNullOrWhiteSpace(item.Region.Text)
                        && string.IsNullOrWhiteSpace(translations[item.Index])).Any();
                if (renderProvisionalMasks && version == _version && !token.IsCancellationRequested
                    && hasPendingRegions
                    && translations.Any(translation => !string.IsNullOrWhiteSpace(translation)))
                {
                    try
                    {
                        await _overlay.Dispatcher.InvokeAsync(() =>
                        {
                            if (version != _version || token.IsCancellationRequested || !provisionalCurrent(frame)) return;
                            _overlay.Render(bounds, regions, translations, style, padding, frame,
                                source.Split('-', 2)[0] == "ja" && target == "th", allowMissingTranslations: true,
                                captionStyle: captionProfile, cancellationToken: token);
                        });
                    }
                    catch (SubtitleLayoutException)
                    {
                        await _overlay.Dispatcher.InvokeAsync(() =>
                        {
                            if (version == _version && !token.IsCancellationRequested && provisionalCurrent(frame))
                                _overlay.RestorePrevious();
                        });
                    }
                    lastProvisionalRender = Stopwatch.GetTimestamp();
                    lastRenderedCount = translations.Count(translation => !string.IsNullOrWhiteSpace(translation));
                }
            }
            var pendingRegions = regions.Select((region, index) => (Region: region, Index: index))
                .Where(item => !string.IsNullOrWhiteSpace(item.Region.Text)
                    && string.IsNullOrWhiteSpace(translations[item.Index])).ToArray();
            _status($"Translating {pendingRegions.Length} text blocks on this computer\u2026");

            if (progressiveManga)
            {
                await TranslateAvailableAsync(regions, translations, true);
                if (version != _version || token.IsCancellationRequested) return null;
            }
            else
            {
                foreach (var batch in pendingRegions.Chunk(64))
                {
                    if (version != _version || token.IsCancellationRequested) return null;
                    var batchTranslations = await _translator.TranslateAsync(
                        batch.Select(item => item.Region.Text).ToArray(), source, target, token);
                    if (version != _version || token.IsCancellationRequested) return null;
                    for (int index = 0; index < batchTranslations.Length; index++)
                        translations[batch[index].Index] = batchTranslations[index];
                }
            }
            translationTimer.Stop();
            return new(version, bounds, regions, translations, captureMs, ocrTimer.ElapsedMilliseconds,
                translationTimer.ElapsedMilliseconds,
                frame, unreadable, FingerprintRegions(bitmap, regions), ocrProofs,
                frameHash, scanStarted);
        }
    }

    private static BitmapSource Snapshot(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var image = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, data.Scan0, Math.Abs(data.Stride) * data.Height, data.Stride);
            image.Freeze();
            return image;
        }
        finally { bitmap.UnlockBits(data); }
    }

    private static Bitmap BitmapFromSnapshot(BitmapSource frame)
    {
        var bitmap = new Bitmap(frame.PixelWidth, frame.PixelHeight, PixelFormat.Format32bppArgb);
        try
        {
            BitmapData? data = null;
            try
            {
                data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppArgb);
                if (data.Stride <= 0) throw new InvalidOperationException("The bitmap has an unsupported scanline order.");
                frame.CopyPixels(new System.Windows.Int32Rect(0, 0, bitmap.Width, bitmap.Height), data.Scan0,
                    checked(data.Stride * bitmap.Height), data.Stride);
            }
            finally
            {
                if (data is not null) bitmap.UnlockBits(data);
            }
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }
    internal static byte[] Fingerprint(Bitmap bitmap)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = Math.Abs(data.Stride);
        var bytes = ArrayPool<byte>.Shared.Rent(stride);
        try
        {
            for (int row = 0; row < bitmap.Height; row++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, row * data.Stride), bytes, 0, stride);
                hash.AppendData(bytes.AsSpan(0, stride));
            }
            return hash.GetHashAndReset();
        }
        finally { bitmap.UnlockBits(data); ArrayPool<byte>.Shared.Return(bytes); }
    }

    // ponytail: Ignore fewer than 128 visibly changed pixels; lower this if tiny readable glyph edits are missed.
    internal static bool MeaningfullyDifferent(Bitmap first, Bitmap second)
    {
        if (first.Size != second.Size) return true;
        int width = first.Width;
        int height = first.Height;
        int rowLength = width * 4;
        var bounds = new Rectangle(0, 0, width, height);
        var a = first.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData? b = null;
        var left = ArrayPool<byte>.Shared.Rent(rowLength);
        var right = ArrayPool<byte>.Shared.Rent(rowLength);
        try
        {
            b = second.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int changed = 0;
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(IntPtr.Add(a.Scan0, y * a.Stride), left, 0, rowLength);
                Marshal.Copy(IntPtr.Add(b.Scan0, y * b.Stride), right, 0, rowLength);
                for (int x = 0; x < rowLength; x += 4)
                    if (Math.Abs(left[x] - right[x]) >= 16 || Math.Abs(left[x + 1] - right[x + 1]) >= 16
                        || Math.Abs(left[x + 2] - right[x + 2]) >= 16)
                        if (++changed >= 128) return true;
            }
            return false;
        }
        finally
        {
            first.UnlockBits(a);
            if (b is not null) second.UnlockBits(b);
            ArrayPool<byte>.Shared.Return(left);
            ArrayPool<byte>.Shared.Return(right);
        }
    }

    private static RegionFingerprint[] FingerprintRegions(Bitmap bitmap, IReadOnlyList<TextRegion> regions)
    {
        var image = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        int maxRowLength = 1;
        foreach (var region in regions)
            maxRowLength = Math.Max(maxRowLength, Rectangle.Intersect(image, region.Bounds).Width * 4);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var pixels = ArrayPool<byte>.Shared.Rent(maxRowLength);
        BitmapData? data = null;
        try
        {
            data = bitmap.LockBits(image, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var fingerprints = new RegionFingerprint[regions.Count];
            for (int index = 0; index < regions.Count; index++)
            {
                var region = regions[index];
                var bounds = Rectangle.Intersect(image, region.Bounds);
                int rowLength = bounds.Width * 4;
                for (int y = 0; y < bounds.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, (bounds.Y + y) * data.Stride + bounds.X * 4),
                        pixels, 0, rowLength);
                    hash.AppendData(pixels.AsSpan(0, rowLength));
                }
                fingerprints[index] = new RegionFingerprint(region.Bounds, hash.GetHashAndReset());
            }
            return fingerprints;
        }
        finally
        {
            if (data is not null) bitmap.UnlockBits(data);
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    private static bool RegionsMatch(Bitmap bitmap, IReadOnlyList<RegionFingerprint> fingerprints)
    {
        var current = FingerprintRegions(bitmap, fingerprints.Select(item => new TextRegion("", item.Bounds)).ToArray());
        return current.Length == fingerprints.Count && current.Zip(fingerprints)
            .All(pair => pair.First.Bounds == pair.Second.Bounds && pair.First.Hash.AsSpan().SequenceEqual(pair.Second.Hash));
    }

    private sealed record ScrollAnalysis(bool Equivalent, int Shift, Rectangle[] SourceBounds,
        Rectangle[] ShiftedBounds, int[] Indices, BitmapSource? Frame,
        RegionFingerprint[] Fingerprints, OcrRegionProof[] OcrProofs);
    private sealed record RegionFingerprint(Rectangle Bounds, byte[] Hash);
    private sealed record TranslatedFrame(int Version, Rectangle Bounds, IReadOnlyList<TextRegion> Regions,
        IReadOnlyList<string> Translations, long CaptureMs, long OcrMs, long TranslationMs, BitmapSource? Frame,
        int UnreadableCount, RegionFingerprint[] RegionFingerprints, OcrRegionProof[] OcrProofs,
        byte[] FrameHash, long ScanStarted);
}
