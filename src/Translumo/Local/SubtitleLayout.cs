using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace Translumo.Local;

internal static class SubtitleLayout
{
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

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}

