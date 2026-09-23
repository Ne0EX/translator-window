using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Translumo.Local;

public sealed record TextRegion(string Text, Rectangle Bounds);

public enum CaptureMode { SelectedArea, Window, Screen }

public sealed record WindowTarget(nint Handle, string Title)
{
    public override string ToString() => Title;
}

public sealed record OcrLanguage(string LanguageTag, string DisplayName)
{
    public override string ToString() => DisplayName;
}

internal static class ScrollAlignment
{
    internal static bool TryEstimateVerticalShift(Bitmap previous, Bitmap current, out int shift)
    {
        shift = 0;
        if (previous.Size != current.Size) return false;
        var oldData = previous.LockBits(new Rectangle(0, 0, previous.Width, previous.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var newData = current.LockBits(new Rectangle(0, 0, current.Width, current.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = previous.Width * 4;
            var oldPixels = CopyPixels(oldData, previous.Height, rowBytes);
            var newPixels = CopyPixels(newData, current.Height, rowBytes);
            var points = new List<(int Offset, int Y, byte Luma)>();
            int sampleStep = Math.Max(4, (int)Math.Ceiling(Math.Max(previous.Width, previous.Height) / 320.0));
            for (int y = 2; y < previous.Height - 2; y += sampleStep)
                for (int x = 2; x < previous.Width - 2; x += sampleStep)
                {
                    int offset = y * rowBytes + x * 4;
                    byte center = Luma(oldPixels, offset);
                    int contrast = Math.Abs(center - Luma(oldPixels, offset - 4))
                        + Math.Abs(center - Luma(oldPixels, offset + 4))
                        + Math.Abs(center - Luma(oldPixels, offset - rowBytes))
                        + Math.Abs(center - Luma(oldPixels, offset + rowBytes));
                    if (contrast >= 70) points.Add((offset, y, center));
                }
            if (points.Count > 1024)
            {
                int stride = (int)Math.Ceiling(points.Count / 1024.0);
                var sampled = new List<(int Offset, int Y, byte Luma)>(1024);
                for (int i = 0; i < points.Count; i += stride) sampled.Add(points[i]);
                points = sampled;
            }
            if (points.Count < 24) return false;

            int best = 0;
            double bestScore = 0, runnerUpScore = 0;
            void Consider(int dy)
            {
                int matches = 0, compared = 0;
                foreach (var point in points)
                {
                    int y = point.Y + dy;
                    if ((uint)y >= (uint)current.Height) continue;
                    compared++;
                    if (Math.Abs(point.Luma - Luma(newPixels, y * rowBytes + point.Offset % rowBytes)) <= 20)
                        matches++;
                }
                if (compared < Math.Max(24, points.Count * 0.45)) return;
                double score = matches / (double)compared;
                if (score > bestScore)
                {
                    runnerUpScore = bestScore;
                    bestScore = score;
                    best = dy;
                }
                else if (score > runnerUpScore) runnerUpScore = score;
            }
            // ponytail: a half-screen jump uses normal OCR; this covers one high-DPI wheel step.
            int maxShift = Math.Min(360, previous.Height / 2);
            for (int dy = -maxShift; dy <= maxShift; dy += 4)
                if (Math.Abs(dy) >= 4) Consider(dy);
            int coarseBest = best;
            for (int dy = Math.Max(-maxShift, coarseBest - 3); dy <= Math.Min(maxShift, coarseBest + 3); dy++)
                if (Math.Abs(dy) >= 4 && dy % 4 != 0) Consider(dy);
            if (bestScore < 0.9 || bestScore - runnerUpScore < 0.08)
                return false;
            shift = best;
            return true;
        }
        finally
        {
            previous.UnlockBits(oldData);
            current.UnlockBits(newData);
        }
    }

    internal static bool RegionsMatchAfterShift(Bitmap previous, Bitmap current,
        IReadOnlyList<TextRegion> regions, int shift)
        => TryMatchRegionsAfterShift(previous, current, regions, shift, out _, out _);

    internal static bool TryMatchRegionsAfterShift(Bitmap previous, Bitmap current,
        IReadOnlyList<TextRegion> regions, int shift, out TextRegion[] retained, out int[] indices)
    {
        retained = Array.Empty<TextRegion>();
        indices = Array.Empty<int>();
        if (previous.Size != current.Size || regions.Count == 0) return false;
        var image = new Rectangle(0, 0, current.Width, current.Height);
        var keptRegions = new List<TextRegion>();
        var keptIndices = new List<int>();
        for (int i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            var oldBounds = region.Bounds;
            var newBounds = oldBounds;
            newBounds.Offset(0, shift);
            if (oldBounds.Width <= 0 || oldBounds.Height <= 0 || !image.Contains(oldBounds)) return false;
            if (!image.Contains(newBounds)) continue;
            if (!RegionHash(previous, oldBounds).AsSpan().SequenceEqual(RegionHash(current, newBounds)))
                return false;
            keptRegions.Add(region with { Bounds = newBounds });
            keptIndices.Add(i);
        }
        if (keptRegions.Count == 0) return false;
        retained = keptRegions.ToArray();
        indices = keptIndices.ToArray();
        return true;
    }

    private static byte[] CopyPixels(BitmapData data, int height, int rowBytes)
    {
        var pixels = new byte[rowBytes * height];
        for (int y = 0; y < height; y++)
            Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * rowBytes, rowBytes);
        return pixels;
    }

    private static byte Luma(byte[] pixels, int offset)
    {
        int blue = pixels[offset], green = pixels[offset + 1], red = pixels[offset + 2];
        return (byte)((red * 77 + green * 150 + blue * 29) >> 8);
    }

    private static byte[] RegionHash(Bitmap bitmap, Rectangle bounds)
    {
        var data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[bounds.Width * bounds.Height * 4];
            for (int y = 0; y < bounds.Height; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), bytes, y * bounds.Width * 4, bounds.Width * 4);
            return SHA256.HashData(bytes);
        }
        finally { bitmap.UnlockBits(data); }
    }
}
