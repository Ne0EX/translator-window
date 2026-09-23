using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace Translumo.Local;

internal static class SubtitleLayout
{
    internal static Rectangle? FindPlainMargin(Rectangle capture, IReadOnlyList<Rectangle> masks, byte[] pixels)
    {
        if (pixels.Length != (long)capture.Width * capture.Height * 4) return null;
        int widest = Math.Min(360, capture.Width / 3);
        for (int width = widest / 20 * 20; width >= 120; width -= 20)
        {
            Rectangle? best = null;
            foreach (int left in new[] { capture.Right - width, capture.Left })
            {
                int runTop = -1;
                for (int y = capture.Top; y <= capture.Bottom; y += 4)
                {
                    var row = new Rectangle(left, y, width, Math.Min(4, capture.Bottom - y));
                    bool plain = row.Height > 0 && !masks.Any(mask => mask.IntersectsWith(row));
                    int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
                    if (plain)
                    {
                        int sampleY = y + row.Height / 2;
                        for (int x = left; x < left + width; x += Math.Max(1, width / 16))
                        {
                            int offset = ((sampleY - capture.Top) * capture.Width + x - capture.Left) * 4;
                            int b = pixels[offset], g = pixels[offset + 1], r = pixels[offset + 2];
                            minR = Math.Min(minR, r); minG = Math.Min(minG, g); minB = Math.Min(minB, b);
                            maxR = Math.Max(maxR, r); maxG = Math.Max(maxG, g); maxB = Math.Max(maxB, b);
                        }
                        plain = maxR - minR <= 20 && maxG - minG <= 20 && maxB - minB <= 20
                            && minR >= 35 && minG >= 35 && minB >= 35 && maxR <= 245 && maxG <= 245 && maxB <= 245;
                    }
                    if (plain) runTop = runTop < 0 ? y : runTop;
                    else if (runTop >= 0)
                    {
                        if (y - runTop >= 400 && (best is null || y - runTop > best.Value.Height))
                            best = new Rectangle(left, runTop, width, y - runTop);
                        runTop = -1;
                    }
                }
            }
            if (best.HasValue) return best;
        }
        return null;
    }

    internal static Rectangle[] FindPlainMargins(Rectangle capture, IReadOnlyList<Rectangle> masks, byte[] pixels)
    {
        var first = FindPlainMargin(capture, masks, pixels);
        if (first is null) return Array.Empty<Rectangle>();
        var blockers = masks.Append(first.Value).ToArray();
        var second = FindPlainMargin(capture, blockers, pixels);
        return second is null ? new[] { first.Value } : new[] { first.Value, second.Value };
    }

    internal static Rectangle? Place(Rectangle source, Size size, Rectangle screen,
        IReadOnlyList<Rectangle> blockers, bool beside, int gap, Func<Rectangle, bool>? accepts = null)
    {
        if (size.Width <= 0 || size.Height <= 0 || size.Width > screen.Width || size.Height > screen.Height)
            return null;

        int centerX = source.Left + (source.Width - size.Width) / 2;
        int centerY = source.Top + (source.Height - size.Height) / 2;
        var candidates = new List<Point>();
        if (!beside)
            foreach (int x in new[] { 0, -1, 1, -2, 2 })
                foreach (int y in new[] { 0, -1, 1, -2, 2 })
                    candidates.Add(new Point(centerX + x * Math.Max(4, gap), centerY + y * Math.Max(4, gap)));
        AddEdges(source);
        foreach (var blocker in blockers) AddEdges(blocker);
        candidates.AddRange(new[] {
            new Point(screen.Left, centerY), new Point(screen.Right - size.Width, centerY),
            new Point(centerX, screen.Top), new Point(centerX, screen.Bottom - size.Height)
        });

        // ponytail: greedy placement suits sparse bubbles; use region-aware typesetting for dense pages.
        foreach (var point in candidates.Select(p => new Point(
                     Math.Clamp(p.X, screen.Left, screen.Right - size.Width),
                     Math.Clamp(p.Y, screen.Top, screen.Bottom - size.Height)))
                 .Distinct().OrderBy(p => Math.Pow(p.X - centerX, 2) + Math.Pow(p.Y - centerY, 2)))
        {
            var candidate = new Rectangle(point, size);
            if (beside ? candidate.IntersectsWith(source) : !candidate.IntersectsWith(source)) continue;
            if (blockers.All(b => !candidate.IntersectsWith(b)) && (accepts?.Invoke(candidate) ?? true)) return candidate;
        }
        return null;

        void AddEdges(Rectangle box)
        {
            candidates.Add(new Point(box.Right + gap, centerY));
            candidates.Add(new Point(box.Left - size.Width - gap, centerY));
            candidates.Add(new Point(centerX, box.Bottom + gap));
            candidates.Add(new Point(centerX, box.Top - size.Height - gap));
        }
    }

    internal static void SelfCheck()
    {
        var monitor = new Rectangle(-1920, -200, 1920, 1080);
        var source = new Rectangle(-1500, 100, 120, 80);
        var beside = Place(source, new Size(180, 80), monitor, Array.Empty<Rectangle>(), true, 6);
        Require(beside.HasValue && monitor.Contains(beside.Value) && !beside.Value.IntersectsWith(source),
            "Beside captions must fit a negative-coordinate monitor without hiding the source.");
        var overwrite = Place(source, new Size(240, 160), monitor, Array.Empty<Rectangle>(), false, 6);
        Require(overwrite.HasValue && monitor.Contains(overwrite.Value) && overwrite.Value.Contains(source),
            "An expanded overwrite caption must cover its source.");
        var inset = new Rectangle(source.X - 30 + 6, source.Y - 10, 180, 100);
        var adjusted = Place(source, inset.Size, monitor, Array.Empty<Rectangle>(), false, 6,
            candidate => inset.Contains(candidate));
        Require(adjusted == inset, "Bubble boundaries must shift captions into nearby free space.");
        Require(Place(source, new Size(180, 80), monitor, Array.Empty<Rectangle>(), false, 6, _ => false) is null,
            "A caption must not paint over artwork when no white bubble placement fits.");
        var blocked = Place(source, new Size(180, 80), monitor, new[] { monitor }, true, 6);
        Require(blocked is null, "Crowded layouts must report no space rather than hide another caption.");
        Require(Place(source, new Size(1921, 80), monitor, Array.Empty<Rectangle>(), false, 6) is null,
            "Oversized captions must not be clipped.");
        var edge = new Rectangle(-80, -195, 75, 60);
        var edgeCaption = Place(edge, new Size(200, 80), monitor, Array.Empty<Rectangle>(), true, 6);
        Require(edgeCaption.HasValue && monitor.Contains(edgeCaption.Value) && !edgeCaption.Value.IntersectsWith(edge),
            "Captions at monitor edges must use available space on the other side.");

        var page = new Rectangle(0, 0, 600, 400);
        var pixels = new byte[600 * 400 * 4];
        for (int y = 0; y < 400; y++)
            for (int x = 0; x < 600; x++)
            {
                int offset = (y * 600 + x) * 4;
                byte gray = (byte)(x < 130 || x >= 470 ? 120 : 220);
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = gray;
                pixels[offset + 3] = 255;
            }
        var plainMargin = FindPlainMargin(page, new[] { new Rectangle(150, 40, 200, 100) }, pixels);
        Require(plainMargin.HasValue && (plainMargin.Value.Right <= 130 || plainMargin.Value.Left >= 470),
            "Dense pages may fall back only into a clear, uniform side margin.");
        var margins = FindPlainMargins(page, new[] { new Rectangle(150, 40, 200, 100) }, pixels);
        Require(margins.Length == 2 && margins.Any(margin => margin.Right <= 130)
            && margins.Any(margin => margin.Left >= 470),
            "When one gutter is not enough, the fallback may use both clear page margins.");
        Require(FindPlainMargin(page, new[] { new Rectangle(0, 0, 600, 400) }, pixels) is null,
            "A covered margin must not trigger the side-margin fallback.");

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}

