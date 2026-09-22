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
        Rectangle previousBounds = default;
        long changedAt = 0;
        int processedVersion = -1;
        Task<TranslatedFrame?>? pending = null;
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
                        _version++;
                        previousHash = null;
                        _overlay.Clear();
                    }
                    _status("Waiting for the selected window to be visible…");
                    if (pending is { IsCompleted: true }) { await pending; pending = null; }
                }
                else
                {
                    using var bitmap = ScreenCapture.Capture(bounds.Value);
                    var hash = Fingerprint(bitmap);
                    if (previousHash is null || !hash.AsSpan().SequenceEqual(previousHash) || bounds.Value != previousBounds)
                    {
                        bool needsCover = previousHash is null || bounds.Value != previousBounds;
                        _version++;
                        previousHash = hash;
                        previousBounds = bounds.Value;
                        changedAt = Environment.TickCount64;
                        if (hideOriginals) { if (needsCover) _overlay.Preparing(bounds.Value); }
                        else _overlay.Clear();
                    }
                    if (pending is { IsCompleted: true })
                    {
                        var result = await pending;
                        pending = null;
                        if (result is not null && result.Version == _version)
                        {
                            try
                            {
                                _overlay.Render(result.Bounds, result.Regions, result.Translations, style, padding, result.Frame);
                                var unreadable = result.UnreadableCount > 0 ? $" \u00b7 {result.UnreadableCount} unreadable blocks; try zooming in" : "";
                                _status($"{result.Regions.Count - result.UnreadableCount} translated blocks \u00b7 {result.ElapsedMs:N0} ms \u00b7 local model{unreadable}");
                            }
                            catch (SubtitleLayoutException error)
                            {
                                _status(error.Message + (hideOriginals ? " The previous view stays covered while watching for changes." : " Watching for changes."));
                            }
                        }
                    }
                    // ponytail: whole-frame stability suits static pages; use per-region tracking for animated pages.
                    if (pending is null && processedVersion != _version && Environment.TickCount64 - changedAt >= 160)
                    {
                        processedVersion = _version;
                        pending = TranslateFrameAsync((Bitmap)bitmap.Clone(), bounds.Value, _version,
                            source, target, style, padding, token, hideOriginals);
                    }
                }
                await Task.Delay(100, token);
            }
        }
        finally
        {
            _version++;
            _overlay.Clear();
            try
            {
                if (pending is not null)
                {
                    try { await pending; }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                }
            }
            finally
            {
                try { await warmup; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                finally { _ocr.Dispose(); }
            }
        }
    }

    private async Task<TranslatedFrame?> TranslateFrameAsync(Bitmap bitmap, Rectangle bounds, int version,
        string source, string target, SubtitleStyle style, int padding, CancellationToken token, bool hideOriginals = false)
    {
        using (bitmap)
        {
            var timer = Stopwatch.StartNew();
            _status("Recognizing text on this computer…");
            var regions = await _ocr.RecognizeAsync(bitmap, source, token);
            if (version != _version || token.IsCancellationRequested) return null;
            int unreadable = regions.Count(region => string.IsNullOrWhiteSpace(region.Text));
            if (regions.Count == 0 || (hideOriginals && unreadable == regions.Count))
            {
                if (hideOriginals)
                {
                    _status(regions.Count == 0
                        ? "No text detected on the new page. The previous view stays covered; try zooming in."
                        : "Detected text could not be read. The previous view stays covered; try zooming in.");
                    return null;
                }
                _status("Watching for text…");
                return new(version, bounds, regions, Array.Empty<string>(), timer.ElapsedMilliseconds, null, 0);
            }
            if (style == SubtitleStyle.Overwrite && !hideOriginals)
            {
                try { _overlay.Render(bounds, regions, regions.Select(_ => "\u2026").ToArray(), style, padding); }
                catch (SubtitleLayoutException) { /* Continue translating; the final caption may fit differently. */ }
            }
            _status($"Translating {regions.Count} text blocks on this computer…");
            var translations = new List<string>(regions.Count);
            foreach (var batch in regions.Chunk(64))
            {
                if (version != _version) return null;
                translations.AddRange(await _translator.TranslateAsync(batch.Select(r => r.Text).ToArray(), source, target, token));
            }
            for (int i = 0; i < regions.Count; i++)
                if (string.IsNullOrWhiteSpace(regions[i].Text)) translations[i] = "\u2026";
            return new(version, bounds, regions, translations, timer.ElapsedMilliseconds, hideOriginals ? Snapshot(bitmap) : null, unreadable);
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

    private sealed record TranslatedFrame(int Version, Rectangle Bounds, IReadOnlyList<TextRegion> Regions,
        IReadOnlyList<string> Translations, long ElapsedMs, BitmapSource? Frame, int UnreadableCount);
}
