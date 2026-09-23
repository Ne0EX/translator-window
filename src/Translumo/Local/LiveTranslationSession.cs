using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Translumo.Local;

/// <summary>One active frame and one inference. Obsolete results never reach the overlay.</summary>
public sealed class LiveTranslationSession
{
    private readonly SpatialOcr _ocr;
    private readonly LocalTranslator _translator;
    private readonly SubtitleOverlay _overlay;
    private readonly Action<string> _status;
    private int _version;

    public LiveTranslationSession(LocalTranslator translator, SubtitleOverlay overlay, Action<string> status, SpatialOcr? ocr = null)
        => (_translator, _overlay, _status, _ocr) = (translator, overlay, status, ocr ?? new SpatialOcr());

    public async Task RunAsync(Func<Rectangle?> getBounds, string source, string target,
        SubtitleStyle style, int padding, CancellationToken token, bool hideOriginals = false)
    {
        var warmup = _translator.TranslateAsync(Array.Empty<string>(), source, target, token);
        byte[]? previousHash = null;
        byte[]? lastScanHash = null;
        RegionFingerprint[]? stableRegions = null;
        TranslatedFrame? stableTranslation = null;
        TranslatedFrame? recognizedFrame = null;
        long lastScanStarted = 0;
        Rectangle previousBounds = default;
        long changedAt = 0;
        int processedVersion = -1;
        Task<TranslatedFrame?>? pending = null;
        CancellationTokenSource? pendingCancellation = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (warmup.IsFaulted) await warmup;
                var bounds = getBounds();
                if (bounds is null || bounds.Value.Width < 4 || bounds.Value.Height < 4)
                {
                    if (previousHash is not null)
                    {
                        pendingCancellation?.Cancel();
                        _version++;
                        previousHash = null;
                        stableRegions = null;
                        stableTranslation = null;
                        recognizedFrame = null;
                        lastScanHash = null;
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
                    var captureTimer = Stopwatch.StartNew();
                    using var bitmap = ScreenCapture.Capture(bounds.Value);
                    var hash = Fingerprint(bitmap);
                    captureTimer.Stop();
                    bool frameChanged = previousHash is null || !hash.AsSpan().SequenceEqual(previousHash);
                    bool boundsChanged = previousHash is not null && bounds.Value != previousBounds;
                    long now = Environment.TickCount64;
                    bool textChanged = frameChanged && (stableRegions is not null
                        ? stableRegions.Length == 0 || !RegionsMatch(bitmap, stableRegions)
                        : recognizedFrame is not null ? !RegionsMatch(bitmap, recognizedFrame.RegionFingerprints) : pending is null);
                    bool scanDue = pending is null && stableRegions is not null && lastScanHash is not null
                        && !hash.AsSpan().SequenceEqual(lastScanHash) && now - lastScanStarted >= 1000;
                    var reusable = stableTranslation ?? recognizedFrame;
                    if (textChanged && reusable?.Frame is not null)
                    {
                        using var sourceFrame = BitmapFromSnapshot(reusable.Frame);
                        if (ScrollAlignment.TryEstimateVerticalShift(sourceFrame, bitmap, out int shift)
                            && ScrollAlignment.TryMatchRegionsAfterShift(sourceFrame, bitmap, reusable.Regions, shift,
                                out var regions, out var retainedIndices))
                        {
                            var fingerprints = FingerprintRegions(bitmap, regions);
                            var translations = retainedIndices.Select(index => reusable.Translations[index]).ToArray();
                            var unreadable = regions.Count(region => string.IsNullOrWhiteSpace(region.Text));
                            var updated = reusable with { Regions = regions, Translations = translations, Frame = Snapshot(bitmap),
                                UnreadableCount = unreadable, RegionFingerprints = fingerprints };
                            _overlay.ShiftVertical(shift, retainedIndices);
                            if (stableTranslation is not null)
                            {
                                stableTranslation = updated;
                                stableRegions = fingerprints;
                            }
                            else recognizedFrame = updated;
                            textChanged = false;
                            _status("Captions follow the scroll; checking for new text…");
                        }
                    }
                    if (previousHash is null || boundsChanged || textChanged || scanDue)
                    {
                        // Let in-flight local workers finish; canceling kills their persistent model processes.
                        if (previousHash is null || processedVersion == _version) changedAt = now;
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
                            recognizedFrame = null;
                            lastScanHash = null;
                            _overlay.Clear();
                        }
                        else if (textChanged && (stableRegions is not null || recognizedFrame is not null))
                        {
                            stableRegions = null;
                            stableTranslation = null;
                            recognizedFrame = null;
                            _overlay.Clear();
                        }
                    }
                    if (frameChanged || boundsChanged) previousHash = hash;
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
                            using var sourceFrame = BitmapFromSnapshot(result.Frame);
                            if (ScrollAlignment.TryEstimateVerticalShift(sourceFrame, bitmap, out int pendingShift)
                                && ScrollAlignment.TryMatchRegionsAfterShift(sourceFrame, bitmap, result.Regions, pendingShift,
                                    out var regions, out var retainedIndices))
                            {
                                var translations = retainedIndices.Select(index => result.Translations[index]).ToArray();
                                result = result with { Regions = regions, Translations = translations, Frame = Snapshot(bitmap),
                                    UnreadableCount = regions.Count(region => string.IsNullOrWhiteSpace(region.Text)),
                                    RegionFingerprints = FingerprintRegions(bitmap, regions) };
                                resultMatches = true;
                            }
                        }
                        if (result is not null && result.Version == _version && resultMatches)
                        {
                            try
                            {
                                var layoutTimer = Stopwatch.StartNew();
                                _overlay.Render(result.Bounds, result.Regions, result.Translations, style, padding, result.Frame);
                                layoutTimer.Stop();
                                _overlay.ConfirmRender();
                                stableRegions = result.RegionFingerprints;
                                stableTranslation = result;
                                recognizedFrame = null;
                                lastScanHash = result.FrameHash;
                                lastScanStarted = result.ScanStarted;
                                var unreadable = result.UnreadableCount > 0 ? $" \u00b7 {result.UnreadableCount} unreadable blocks; try zooming in" : "";
                                _status($"{result.Regions.Count - result.UnreadableCount} translated blocks · local model · capture {result.CaptureMs:N0} ms · OCR {result.OcrMs:N0} ms · translation {result.TranslationMs:N0} ms · layout {layoutTimer.ElapsedMilliseconds:N0} ms{unreadable}");
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
                            _overlay.Clear();
                            recognizedFrame = null;
                        }
                    }
                    // Background-only animation keeps the current captions; scan changed pages once the worker is free.
                    if (pending is null && processedVersion != _version && Environment.TickCount64 - changedAt >= 160)
                    {
                        processedVersion = _version;
                        pendingCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                        lastScanStarted = Environment.TickCount64;
                        lastScanHash = hash;
                        pending = TranslateFrameAsync((Bitmap)bitmap.Clone(), bounds.Value, _version,
                            source, target, style, padding, pendingCancellation.Token, hideOriginals,
                            captureTimer.ElapsedMilliseconds, hash, lastScanStarted, frame => recognizedFrame = frame);
                    }
                }
                await Task.Delay(100, token);
            }
        }
        finally
        {
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
                finally { _ocr.Dispose(); }
            }
        }
    }

    private async Task<TranslatedFrame?> TranslateFrameAsync(Bitmap bitmap, Rectangle bounds, int version,
        string source, string target, SubtitleStyle style, int padding, CancellationToken token,
        bool hideOriginals, long captureMs, byte[] frameHash, long scanStarted, Action<TranslatedFrame> recognized)
    {
        using (bitmap)
        {
            _status("Recognizing text on this computer\u2026");
            var ocrTimer = Stopwatch.StartNew();
            var regions = await _ocr.RecognizeAsync(bitmap, source, token);
            ocrTimer.Stop();
            if (version != _version || token.IsCancellationRequested) return null;
            int unreadable = regions.Count(region => string.IsNullOrWhiteSpace(region.Text));
            if (regions.Count == 0 || (hideOriginals && unreadable == regions.Count))
            {
                if (hideOriginals)
                {
                    _overlay.Clear();
                    _status(regions.Count == 0
                        ? "No text detected on the changed view; watching for text."
                        : "Detected text could not be read on the changed view; try zooming in.");
                    return null;
                }
                _status("Watching for text\u2026");
                return new(version, bounds, regions, Array.Empty<string>(), captureMs, ocrTimer.ElapsedMilliseconds,
                    0, null, 0, FingerprintRegions(bitmap, regions), frameHash, scanStarted);
            }
            BitmapSource? frame = Snapshot(bitmap);
            if (style == SubtitleStyle.Overwrite || style == SubtitleStyle.Overlay)
            {
                try { _overlay.Render(bounds, regions, regions.Select(_ => "\u2026").ToArray(), style, padding, frame); }
                catch (SubtitleLayoutException) { /* Continue translating; the final caption may fit differently. */ }
            }
            recognized(new(version, bounds, regions, regions.Select(_ => "\u2026").ToArray(), captureMs,
                ocrTimer.ElapsedMilliseconds, 0, frame, unreadable, FingerprintRegions(bitmap, regions), frameHash, scanStarted));
            _status($"Translating {regions.Count} text blocks on this computer\u2026");
            var translationTimer = Stopwatch.StartNew();
            var translations = new List<string>(regions.Count);
            foreach (var batch in regions.Chunk(64))
            {
                if (version != _version) return null;
                translations.AddRange(await _translator.TranslateAsync(batch.Select(r => r.Text).ToArray(), source, target, token));
            }
            for (int i = 0; i < regions.Count; i++)
                if (string.IsNullOrWhiteSpace(regions[i].Text)) translations[i] = "\u2026";
            translationTimer.Stop();
            return new(version, bounds, regions, translations, captureMs, ocrTimer.ElapsedMilliseconds,
                translationTimer.ElapsedMilliseconds, frame, unreadable, FingerprintRegions(bitmap, regions), frameHash, scanStarted);
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
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[bitmap.Width * bitmap.Height * 4];
            frame.CopyPixels(bytes, bitmap.Width * 4, 0);
            for (int y = 0; y < bitmap.Height; y++)
                Marshal.Copy(bytes, y * bitmap.Width * 4, IntPtr.Add(data.Scan0, y * data.Stride), bitmap.Width * 4);
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }
    internal static byte[] Fingerprint(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int length = Math.Abs(data.Stride) * data.Height;
        var bytes = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            Marshal.Copy(data.Scan0, bytes, 0, length);
            return SHA256.HashData(bytes.AsSpan(0, length));
        }
        finally { bitmap.UnlockBits(data); ArrayPool<byte>.Shared.Return(bytes); }
    }

    private static RegionFingerprint[] FingerprintRegions(Bitmap bitmap, IReadOnlyList<TextRegion> regions)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var image = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            return regions.Select(region =>
            {
                var bounds = Rectangle.Intersect(image, region.Bounds);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                    return new RegionFingerprint(region.Bounds, SHA256.HashData(Array.Empty<byte>()));
                var pixels = new byte[bounds.Width * bounds.Height * 4];
                for (int y = 0; y < bounds.Height; y++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, (bounds.Y + y) * data.Stride + bounds.X * 4),
                        pixels, y * bounds.Width * 4, bounds.Width * 4);
                return new RegionFingerprint(region.Bounds, SHA256.HashData(pixels));
            }).ToArray();
        }
        finally { bitmap.UnlockBits(data); }
    }

    private static bool RegionsMatch(Bitmap bitmap, IReadOnlyList<RegionFingerprint> fingerprints)
    {
        var current = FingerprintRegions(bitmap, fingerprints.Select(item => new TextRegion("", item.Bounds)).ToArray());
        return current.Length == fingerprints.Count && current.Zip(fingerprints)
            .All(pair => pair.First.Bounds == pair.Second.Bounds && pair.First.Hash.AsSpan().SequenceEqual(pair.Second.Hash));
    }

    private sealed record RegionFingerprint(Rectangle Bounds, byte[] Hash);
    private sealed record TranslatedFrame(int Version, Rectangle Bounds, IReadOnlyList<TextRegion> Regions,
        IReadOnlyList<string> Translations, long CaptureMs, long OcrMs, long TranslationMs, BitmapSource? Frame,
        int UnreadableCount, RegionFingerprint[] RegionFingerprints, byte[] FrameHash, long ScanStarted);
}
