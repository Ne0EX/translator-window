using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;

namespace Translumo.Local;

public enum SourceCoverClass { Plain, Gradient }

public sealed class SourceCoverPlan
{
    private readonly Rectangle image;
    private readonly bool[] mask;
    private readonly byte[] reconstruction;

    internal SourceCoverPlan(SourceCoverClass classification, Rectangle image, Rectangle permittedArea,
        Rectangle footprintBounds, bool[] mask, byte[] reconstruction, int coveredPixelCount)
    {
        Classification = classification;
        this.image = image;
        PermittedArea = permittedArea;
        FootprintBounds = footprintBounds;
        this.mask = mask;
        this.reconstruction = reconstruction;
        CoveredPixelCount = coveredPixelCount;
    }

    public SourceCoverClass Classification { get; }
    public Rectangle PermittedArea { get; }
    public Rectangle FootprintBounds { get; }
    public int CoveredPixelCount { get; }

    internal bool Covers(int x, int y) => PermittedArea.Contains(x, y)
        && mask[(y - PermittedArea.Top) * PermittedArea.Width + x - PermittedArea.Left];

    internal SourceCoverPlan IncludeFootprint(SourceCoverPlan footprint, Rectangle source,
        Action<int> sampleRow, CancellationToken cancellationToken)
    {
        var overlap = Rectangle.Intersect(source, Rectangle.Intersect(FootprintBounds, footprint.FootprintBounds));
        bool[]? combined = null;
        int covered = CoveredPixelCount;
        for (int y = overlap.Top; y < overlap.Bottom; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sampleRow(overlap.Width);
            for (int x = overlap.Left; x < overlap.Right; x++)
            {
                if (Covers(x, y) || !footprint.Covers(x, y)) continue;
                combined ??= (bool[])mask.Clone();
                combined[(y - PermittedArea.Top) * PermittedArea.Width + x - PermittedArea.Left] = true;
                covered++;
            }
        }
        if (combined is null) return this;

        // Recovered lettering can enclose pale gaps that were connected to the exterior before the union.
        // Fill only those holes inside the original text region; preserve every gap still reaching its edges.
        var interior = Rectangle.Intersect(source, FootprintBounds);
        int count = checked(interior.Width * interior.Height);
        var exterior = new bool[count];
        var pending = new int[count];
        int head = 0, tail = 0;
        for (int y = 0; y < interior.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sampleRow(interior.Width);
            Visit(y * interior.Width);
            Visit((y + 1) * interior.Width - 1);
        }
        for (int x = 0; x < interior.Width; x++)
        {
            Visit(x);
            Visit((interior.Height - 1) * interior.Width + x);
        }
        while (head < tail)
        {
            if ((head & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            int index = pending[head++], x = index % interior.Width, y = index / interior.Width;
            if (x > 0) Visit(index - 1);
            if (x + 1 < interior.Width) Visit(index + 1);
            if (y > 0) Visit(index - interior.Width);
            if (y + 1 < interior.Height) Visit(index + interior.Width);
        }
        for (int y = 0; y < interior.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sampleRow(interior.Width);
            for (int x = 0; x < interior.Width; x++)
            {
                int index = y * interior.Width + x;
                int offset = (y + interior.Top - PermittedArea.Top) * PermittedArea.Width
                    + x + interior.Left - PermittedArea.Left;
                if (exterior[index] || combined[offset]) continue;
                combined[offset] = true;
                covered++;
            }
        }
        return new SourceCoverPlan(Classification, image, PermittedArea,
            FootprintBounds, combined, reconstruction, covered);

        void Visit(int index)
        {
            if (exterior[index]) return;
            int offset = (index / interior.Width + interior.Top - PermittedArea.Top) * PermittedArea.Width
                + index % interior.Width + interior.Left - PermittedArea.Left;
            if (combined[offset]) return;
            exterior[index] = true;
            pending[tail++] = index;
        }
    }

    internal SourceCoverPlan? Excluding(IReadOnlyList<Rectangle> areas, Action<int> sampleRow,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clipped = (bool[])mask.Clone();
        int covered = CoveredPixelCount;
        foreach (var area in areas)
        {
            var overlap = Rectangle.Intersect(area, PermittedArea);
            for (int y = overlap.Top; y < overlap.Bottom; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sampleRow(overlap.Width);
                for (int x = overlap.Left; x < overlap.Right; x++)
                {
                    int offset = (y - PermittedArea.Top) * PermittedArea.Width + x - PermittedArea.Left;
                    if (!clipped[offset]) continue;
                    clipped[offset] = false;
                    covered--;
                }
            }
        }
        return covered == 0 ? null : new SourceCoverPlan(Classification, image, PermittedArea,
            FootprintBounds, clipped, reconstruction, covered);
    }

    public byte[] CreatePatch(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var patch = new byte[checked(FootprintBounds.Width * FootprintBounds.Height * 4)];
        for (int y = FootprintBounds.Top; y < FootprintBounds.Bottom; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = FootprintBounds.Left; x < FootprintBounds.Right; x++)
            {
                int maskOffset = (y - PermittedArea.Top) * PermittedArea.Width + x - PermittedArea.Left;
                if (!mask[maskOffset]) continue;
                int source = maskOffset * 4;
                int target = ((y - FootprintBounds.Top) * FootprintBounds.Width + x - FootprintBounds.Left) * 4;
                reconstruction.AsSpan(source, 4).CopyTo(patch.AsSpan(target, 4));
            }
        }
        return patch;
    }

    public byte[] Apply(byte[] sourceBgra32, int width, int height, int stride)
    {
        SourceCover.Validate(sourceBgra32, width, height, stride);
        if (width != image.Width || height != image.Height)
            throw new ArgumentException("The source dimensions differ from the qualified source cover.", nameof(sourceBgra32));
        var result = (byte[])sourceBgra32.Clone();
        int reconstructionOffset = 0;
        for (int y = PermittedArea.Top; y < PermittedArea.Bottom; y++)
            for (int x = PermittedArea.Left; x < PermittedArea.Right; x++, reconstructionOffset += 4)
            {
                int maskOffset = (y - PermittedArea.Top) * PermittedArea.Width + x - PermittedArea.Left;
                if (!mask[maskOffset]) continue;
                reconstruction.AsSpan(reconstructionOffset, 4).CopyTo(result.AsSpan(y * stride + x * 4, 4));
            }
        return result;
    }
}

public static class SourceCover
{
    private const int MaxPermittedPixels = 1_000_000;

    internal static bool IsWithinBudget(Rectangle area)
        => area.Width > 0 && area.Height > 0
            && (long)area.Width * area.Height <= MaxPermittedPixels;

    internal static SourceCoverPlan? TryCreateBubble(byte[] pixels, int width, int height,
        Rectangle text, Rectangle search, bool allowExpansion, Func<int, bool> reserveFlood,
        CancellationToken cancellationToken)
    {
        if (!IsWithinBudget(search) || !search.Contains(text)) return null;
        // ponytail: bounded closed bubbles with nearly uniform fill; open borders and stronger textures use the footprint fallback.
        int stride = width * 4;
        // Sample around the text: one fixed seed can land on a glyph or furigana.
        var samples = new (int X, int Y, Pixel Color)[12];
        for (int i = 0; i < samples.Length; i++)
        {
            int fraction = i % 3 + 1;
            int x = (i / 3) switch { 0 => text.Left - 3, 1 => text.Right + 2,
                _ => text.Left + text.Width * fraction / 4 };
            int y = (i / 3) switch { 2 => text.Top - 3, 3 => text.Bottom + 2,
                _ => text.Top + text.Height * fraction / 4 };
            x = Math.Clamp(x, search.Left, search.Right - 1);
            y = Math.Clamp(y, search.Top, search.Bottom - 1);
            int offset = y * stride + x * 4;
            samples[i] = (x, y, new Pixel(pixels[offset], pixels[offset + 1], pixels[offset + 2]));
        }
        int selected = 0, support = 0;
        long nearest = long.MaxValue;
        for (int i = 0; i < samples.Length; i++)
        {
            int matches = 0;
            foreach (var sample in samples)
                if (Math.Abs(sample.Color.B - samples[i].Color.B) <= 16
                    && Math.Abs(sample.Color.G - samples[i].Color.G) <= 16
                    && Math.Abs(sample.Color.R - samples[i].Color.R) <= 16) matches++;
            int dx = samples[i].X * 2 - text.Left - text.Right;
            int dy = samples[i].Y * 2 - text.Top - text.Bottom;
            long distance = (long)dx * dx + (long)dy * dy;
            if (matches > support || matches == support && distance < nearest)
            { support = matches; nearest = distance; selected = i; }
        }
        var (seedX, seedY, fill) = samples[selected];
        int brighterTolerance = 0.299 * fill.R + 0.587 * fill.G + 0.114 * fill.B >= 145 ? 16 : 12;
        int count = search.Width * search.Height;
        if (!reserveFlood(count)) return null;
        var inside = new bool[count];
        var pending = new int[count];
        int head = 0, tail = 0;
        int minX = seedX, maxX = seedX, minY = seedY, maxY = seedY;
        bool expandedSearch = false;
        // ponytail: one alternate seed and one axis expansion; wider/open containers retain the footprint fallback.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt != 0)
            {
                if (!reserveFlood(count)) return null;
                if (inside.Length != count)
                {
                    inside = new bool[count];
                    pending = new int[count];
                }
                else Array.Clear(inside);
            }
            head = tail = 0;
            minX = maxX = seedX; minY = maxY = seedY;
            int seed = (seedY - search.Top) * search.Width + seedX - search.Left;
            inside[seed] = true;
            pending[tail++] = seed;
            Point? escaped = null;
            while (head < tail)
            {
                if ((head & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                int index = pending[head++];
                int x = index % search.Width + search.Left, y = index / search.Width + search.Top;
                if (x == search.Left || x == search.Right - 1 || y == search.Top || y == search.Bottom - 1)
                { escaped = new Point(x, y); break; }
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                Visit(index - 1, x - 1, y); Visit(index + 1, x + 1, y);
                Visit(index - search.Width, x, y - 1); Visit(index + search.Width, x, y + 1);
            }
            if (escaped is null) break;
            if (expandedSearch) return null;

            // Matching exterior paper can win the seed tie; retry once outside that escaped component.
            selected = -1;
            nearest = long.MaxValue;
            for (int i = 0; attempt == 0 && i < samples.Length; i++)
            {
                var sample = samples[i];
                int index = (sample.Y - search.Top) * search.Width + sample.X - search.Left;
                if (inside[index] || !MatchesFill(sample.Color.B, sample.Color.G, sample.Color.R)) continue;
                int dx = sample.X * 2 - text.Left - text.Right;
                int dy = sample.Y * 2 - text.Top - text.Bottom;
                long distance = (long)dx * dx + (long)dy * dy;
                if (distance < nearest) { nearest = distance; selected = i; }
            }
            if (selected >= 0)
            {
                (seedX, seedY, _) = samples[selected];
                continue;
            }

            // A larger closed component may enclose artwork; require independently qualified lettering.
            if (!allowExpansion) return null;
            var edge = escaped.Value;
            // Reaching the captured image edge cannot be repaired by enlarging the search.
            if (edge.X == 0 || edge.X == width - 1 || edge.Y == 0 || edge.Y == height - 1) return null;
            int extraX = edge.X == search.Left || edge.X == search.Right - 1
                ? Math.Max(text.Left - search.Left, search.Right - text.Right) : 0;
            int extraY = edge.Y == search.Top || edge.Y == search.Bottom - 1
                ? Math.Max(text.Top - search.Top, search.Bottom - text.Bottom) : 0;
            var enlarged = search;
            enlarged.Inflate(extraX, extraY);
            enlarged.Intersect(new Rectangle(0, 0, width, height));
            if (enlarged == search || !IsWithinBudget(enlarged)) return null;
            search = enlarged;
            count = search.Width * search.Height;
            expandedSearch = true;
        }
        var bounds = Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
        // Narrow lettering can occupy little of a bubble; also allow a square twice the utterance's length.
        long letteringLength = Math.Max(text.Width, text.Height);
        long maximumArea = Math.Max(96 * 96,
            Math.Max((long)text.Width * text.Height * 8, letteringLength * letteringLength * 4));
        if (!bounds.Contains(text) || tail < text.Width * text.Height / 2
            || (long)bounds.Width * bounds.Height > maximumArea) return null;

        // Flood the complement from outside: disconnected holes are lettering, the connected outline is preserved.
        var exterior = new bool[count];
        head = tail = 0;
        exterior[0] = true;
        pending[tail++] = 0;
        while (head < tail)
        {
            if ((head & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            int index = pending[head++], x = index % search.Width, y = index / search.Width;
            if (x > 0) Outside(index - 1);
            if (x + 1 < search.Width) Outside(index + 1);
            if (y > 0) Outside(index - search.Width);
            if (y + 1 < search.Height) Outside(index + search.Width);
        }
        int covered = 0, ink = 0;
        var holes = new bool[count];
        for (int index = 0; index < count; index++)
        {
            int offset = ((index / search.Width + search.Top) * width + index % search.Width + search.Left) * 4;
            // Enclosed white counters belong to the bubble fill, not the glyph's ink density.
            holes[index] = !exterior[index] && !inside[index]
                && (Math.Abs(pixels[offset] - fill.B) > 16 || Math.Abs(pixels[offset + 1] - fill.G) > 16
                    || Math.Abs(pixels[offset + 2] - fill.R) > 16);
            if (holes[index]) ink++;
            inside[index] = !exterior[index];
            if (inside[index]) covered++;
        }
        if (ink < 4 || ink > covered * 0.35
            || HasLargeNonTextComponent(holes, search, bounds, ink, cancellationToken, text)) return null;
        // OCR corners can cross the exterior of offset joined bubbles; only the qualified contour is painted.
        // Neutral near-white paper uses white reconstruction; retain the sampled color for qualification and tinted fills.
        bool whitePaper = fill.B >= 232 && fill.G >= 232 && fill.R >= 232
            && Math.Max(fill.B, Math.Max(fill.G, fill.R)) - Math.Min(fill.B, Math.Min(fill.G, fill.R)) <= 8;
        var reconstructionFill = whitePaper ? new Pixel(255, 255, 255) : fill;
        var reconstruction = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            reconstruction[i * 4] = reconstructionFill.B; reconstruction[i * 4 + 1] = reconstructionFill.G;
            reconstruction[i * 4 + 2] = reconstructionFill.R; reconstruction[i * 4 + 3] = 255;
        }
        return new SourceCoverPlan(SourceCoverClass.Plain, new Rectangle(0, 0, width, height),
            search, bounds, inside, reconstruction, covered);

        void Visit(int index, int x, int y)
        {
            if (inside[index]) return;
            int offset = y * stride + x * 4;
            if (!MatchesFill(pixels[offset], pixels[offset + 1], pixels[offset + 2])) return;
            inside[index] = true;
            pending[tail++] = index;
        }
        // Keep faint darker outlines closed while allowing brighter highlights inside a pale bubble.
        bool MatchesFill(byte b, byte g, byte r) => b >= fill.B - 12 && b <= fill.B + brighterTolerance
            && g >= fill.G - 12 && g <= fill.G + brighterTolerance
            && r >= fill.R - 12 && r <= fill.R + brighterTolerance;
        void Outside(int index)
        {
            if (inside[index] || exterior[index]) return;
            exterior[index] = true;
            pending[tail++] = index;
        }
    }

    public static SourceCoverPlan? TryCreate(byte[] sourceBgra32, int width, int height, int stride,
        Rectangle textRegion, Rectangle permittedArea, CancellationToken cancellationToken = default)
    {
        Validate(sourceBgra32, width, height, stride);
        cancellationToken.ThrowIfCancellationRequested();
        var image = new Rectangle(0, 0, width, height);
        if (textRegion.Width < 3 || textRegion.Height < 3 || !image.Contains(permittedArea)
            || !permittedArea.Contains(textRegion)) return null;
        // ponytail: one million pixels bounds UI-thread reconstruction; tile only if oversized text regions become supported.
        if (!IsWithinBudget(permittedArea)) return null;
        int sampleWidth = Math.Min(6, Math.Min(textRegion.Left - permittedArea.Left,
            permittedArea.Right - textRegion.Right));
        if (sampleWidth < 2) return null;

        var left = new Pixel[textRegion.Height];
        var right = new Pixel[textRegion.Height];
        long sampleError = 0;
        int sampleCount = 0;
        for (int row = 0; row < textRegion.Height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int y = textRegion.Top + row;
            left[row] = Median(textRegion.Left - sampleWidth, textRegion.Left, y);
            right[row] = Median(textRegion.Right, textRegion.Right + sampleWidth, y);
            AccumulateError(textRegion.Left - sampleWidth, textRegion.Left, y, left[row]);
            AccumulateError(textRegion.Right, textRegion.Right + sampleWidth, y, right[row]);
        }
        if (sampleCount == 0 || sampleError / (double)(sampleCount * 3) > 20) return null;
        if (!IsSmoothLinear(left) || !IsSmoothLinear(right)) return null;

        long horizontalChange = 0;
        for (int row = 0; row < textRegion.Height; row++)
            horizontalChange += Difference(left[row], right[row]);
        int verticalChange = Difference(left[0], left[^1]) + Difference(right[0], right[^1]);
        var classification = horizontalChange / (double)(textRegion.Height * 3) <= 18
            && verticalChange / 6.0 <= 8
            ? SourceCoverClass.Plain : SourceCoverClass.Gradient;

        var mask = new bool[permittedArea.Width * permittedArea.Height];
        var reconstruction = new byte[checked(permittedArea.Width * permittedArea.Height * 4)];
        int candidates = 0;
        for (int y = permittedArea.Top; y < permittedArea.Bottom; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int sampleRow = Math.Clamp(y - textRegion.Top, 0, textRegion.Height - 1);
            for (int x = permittedArea.Left; x < permittedArea.Right; x++)
            {
                double t = (x - (textRegion.Left - sampleWidth / 2.0))
                    / (textRegion.Width + sampleWidth);
                var expected = Pixel.Lerp(left[sampleRow], right[sampleRow], Math.Clamp(t, 0, 1));
                int local = (y - permittedArea.Top) * permittedArea.Width + x - permittedArea.Left;
                int rebuilt = local * 4;
                reconstruction[rebuilt] = expected.B;
                reconstruction[rebuilt + 1] = expected.G;
                reconstruction[rebuilt + 2] = expected.R;
                reconstruction[rebuilt + 3] = 255;
                if (!textRegion.Contains(x, y)) continue;
                int source = y * stride + x * 4;
                if (Math.Max(Math.Abs(sourceBgra32[source] - expected.B), Math.Max(
                    Math.Abs(sourceBgra32[source + 1] - expected.G),
                    Math.Abs(sourceBgra32[source + 2] - expected.R))) < 40) continue;
                mask[local] = true;
                candidates++;
            }
        }
        if (candidates < 4 || candidates > textRegion.Width * textRegion.Height * 0.55) return null;
        if (HasLargeNonTextComponent(mask, permittedArea, textRegion, candidates, cancellationToken)) return null;

        var expanded = (bool[])mask.Clone();
        for (int y = textRegion.Top; y < textRegion.Bottom; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = textRegion.Left; x < textRegion.Right; x++)
            {
                int local = (y - permittedArea.Top) * permittedArea.Width + x - permittedArea.Left;
                if (!mask[local]) continue;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int px = x + dx, py = y + dy;
                        if (textRegion.Contains(px, py))
                            expanded[(py - permittedArea.Top) * permittedArea.Width + px - permittedArea.Left] = true;
                    }
            }
        }
        mask = expanded;
        int covered = 0, minX = textRegion.Right, minY = textRegion.Bottom, maxX = textRegion.Left, maxY = textRegion.Top;
        for (int y = textRegion.Top; y < textRegion.Bottom; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = textRegion.Left; x < textRegion.Right; x++)
                if (mask[(y - permittedArea.Top) * permittedArea.Width + x - permittedArea.Left])
                {
                    covered++; minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                }
        }
        return new SourceCoverPlan(classification, image, permittedArea,
            Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1), mask, reconstruction, covered);

        Pixel Median(int startX, int endX, int y)
        {
            int count = endX - startX;
            Span<byte> b = stackalloc byte[count], g = stackalloc byte[count], r = stackalloc byte[count];
            for (int x = startX; x < endX; x++)
            {
                int offset = y * stride + x * 4;
                b[x - startX] = sourceBgra32[offset];
                g[x - startX] = sourceBgra32[offset + 1];
                r[x - startX] = sourceBgra32[offset + 2];
            }
            b.Sort(); g.Sort(); r.Sort();
            int lower = (count - 1) / 2, upper = count / 2;
            return new Pixel((byte)((b[lower] + b[upper]) / 2),
                (byte)((g[lower] + g[upper]) / 2), (byte)((r[lower] + r[upper]) / 2));
        }
        void AccumulateError(int startX, int endX, int y, Pixel average)
        {
            for (int x = startX; x < endX; x++)
            {
                int offset = y * stride + x * 4;
                sampleError += Math.Abs(sourceBgra32[offset] - average.B)
                    + Math.Abs(sourceBgra32[offset + 1] - average.G)
                    + Math.Abs(sourceBgra32[offset + 2] - average.R);
                sampleCount++;
            }
        }
    }

    internal static void Validate(byte[] pixels, int width, int height, int stride)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0 || stride < checked(width * 4)
            || pixels.Length < checked(stride * height))
            throw new ArgumentException("Expected a complete BGRA32 image.", nameof(pixels));
    }

    private static int Difference(Pixel first, Pixel second) => Math.Abs(first.B - second.B)
        + Math.Abs(first.G - second.G) + Math.Abs(first.R - second.R);

    private static bool IsSmoothLinear(Pixel[] samples)
    {
        for (int row = 1; row < samples.Length - 1; row++)
            if (Difference(samples[row], Pixel.Lerp(samples[0], samples[^1], row / (double)(samples.Length - 1))) > 60)
                return false;
        return true;
    }

    private static bool HasLargeNonTextComponent(bool[] mask, Rectangle permittedArea,
        Rectangle textRegion, int candidateCount, CancellationToken cancellationToken, Rectangle? letteringArea = null)
    {
        var seen = new bool[mask.Length];
        var pending = new int[candidateCount];
        int minimumPixels = Math.Max(8, textRegion.Width * textRegion.Height / 5);
        int minimumWidth = Math.Max(4, (textRegion.Width + 2) / 3);
        int minimumHeight = Math.Max(4, (textRegion.Height + 2) / 3);
        for (int y = textRegion.Top; y < textRegion.Bottom; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = textRegion.Left; x < textRegion.Right; x++)
            {
                int start = (y - permittedArea.Top) * permittedArea.Width + x - permittedArea.Left;
                if (!mask[start] || seen[start]) continue;
                int top = 0, count = 0, minX = x, maxX = x, minY = y, maxY = y;
                seen[start] = true;
                pending[top++] = start;
                while (top > 0)
                {
                    if ((count & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                    int current = pending[--top];
                    int currentY = current / permittedArea.Width + permittedArea.Top;
                    int currentX = current % permittedArea.Width + permittedArea.Left;
                    count++;
                    minX = Math.Min(minX, currentX); maxX = Math.Max(maxX, currentX);
                    minY = Math.Min(minY, currentY); maxY = Math.Max(maxY, currentY);
                    // ponytail: coarse component gate; use glyph segmentation if connected display lettering is needed.
                    if (count >= minimumPixels && maxX - minX + 1 >= minimumWidth
                        && maxY - minY + 1 >= minimumHeight) return true;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nextX = currentX + dx, nextY = currentY + dy;
                            if ((dx == 0 && dy == 0) || !textRegion.Contains(nextX, nextY)) continue;
                            int next = (nextY - permittedArea.Top) * permittedArea.Width + nextX - permittedArea.Left;
                            if (!mask[next] || seen[next]) continue;
                            seen[next] = true;
                            pending[top++] = next;
                        }
                }
                int componentWidth = maxX - minX + 1, componentHeight = maxY - minY + 1;
                // ponytail: large disconnected strokes outside the source can be artwork; large neighboring lettering needs segmentation.
                if (letteringArea is { } lettering && componentWidth >= lettering.Width / 2d
                    && componentHeight > lettering.Height / 2d
                    && (!lettering.IntersectsWith(new Rectangle(minX, minY, componentWidth, componentHeight))
                        || count >= candidateCount / 5 && componentHeight > lettering.Width)) return true;
                // ponytail: near-span strokes are treated as chart connectors; use glyph segmentation to recover display text.
                if ((componentWidth >= Math.Max(8, textRegion.Width * 3 / 4) && componentHeight <= 2)
                    || (componentHeight >= Math.Max(8, textRegion.Height * 3 / 4) && componentWidth <= 2)) return true;
                // ponytail: near-solid compact components are treated as artwork; glyph segmentation is needed to distinguish them fully.
                // Keep tiny punctuation inside detected lettering; this size heuristic does not classify general artwork.
                bool smallPunctuation = letteringArea is { } source
                    && source.Contains(new Rectangle(minX, minY, componentWidth, componentHeight))
                    && Math.Max(componentWidth, componentHeight) * 6 <= Math.Min(source.Width, source.Height);
                if (count >= 12 && componentWidth >= 4 && componentHeight >= 4
                    && componentWidth >= componentHeight * 0.65 && componentHeight >= componentWidth * 0.65
                    && count >= componentWidth * componentHeight * 0.95 && !smallPunctuation) return true;
            }
        }
        return false;
    }

    private readonly record struct Pixel(byte B, byte G, byte R)
    {
        public static Pixel Lerp(Pixel first, Pixel second, double amount) => new(
            (byte)Math.Round(first.B * (1 - amount) + second.B * amount),
            (byte)Math.Round(first.G * (1 - amount) + second.G * amount),
            (byte)Math.Round(first.R * (1 - amount) + second.R * amount));
    }
}
