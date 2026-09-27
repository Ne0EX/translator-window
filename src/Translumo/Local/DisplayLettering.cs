using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Rectangle = System.Drawing.Rectangle;

namespace Translumo.Local;

internal sealed record DisplayLettering(SourceCoverPlan Cover, Color Ink, Color Highlight, Color Outline, byte[] SourcePixels)
{
    private Brush? patch;

    internal Brush Patch(CancellationToken token)
    {
        if (patch is not null) return patch;
        var area = Cover.FootprintBounds;
        var bitmap = BitmapSource.Create(area.Width, area.Height, 96, 96, PixelFormats.Bgra32,
            null, Cover.CreatePatch(token), area.Width * 4);
        bitmap.Freeze();
        patch = new ImageBrush(bitmap) { Stretch = Stretch.Fill };
        patch.Freeze();
        return patch;
    }

    internal bool Matches(byte[] pixels, int width)
    {
        var area = Cover.PermittedArea;
        for (int y = 0; y < area.Height; y++)
            if (!pixels.AsSpan(((area.Y + y) * width + area.X) * 4, area.Width * 4)
                .SequenceEqual(SourcePixels.AsSpan(y * area.Width * 4, area.Width * 4))) return false;
        return true;
    }

    internal static DisplayLettering? TryCreate(byte[] pixels, int width, int height,
        Rectangle source, CancellationToken token)
    {
        // ponytail: yellow/white lettering on red bands only. Other textures need separately qualified masks and fill sampling.
        if (source.Width < 120 || source.Width < source.Height * 3 || source.Height < 16
            || (long)source.Width * source.Height > 250_000) return null;
        int count = source.Width * source.Height;
        var columns = new int[source.Width];
        var inkRows = new long[3, 2];
        var inkSamples = new int[2];
        int bandCount = 0, inkCount = 0;
        long rb = 0, gb = 0, bb = 0, ri = 0, gi = 0, bi = 0;
        for (int y = 0; y < source.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < source.Width; x++)
            {
                int offset = ((source.Y + y) * width + source.X + x) * 4;
                int b = pixels[offset], g = pixels[offset + 1], r = pixels[offset + 2];
                if (r > 180 && g < 130 && b < 180 && r > g + 90 && r > b + 55)
                {
                    columns[x]++; bandCount++;
                    rb += r; gb += g; bb += b;
                }
                if (r > 210 && g > 175 && b < 140)
                {
                    inkCount++; ri += r; gi += g; bi += b;
                    int half = y < source.Height / 2 ? 0 : 1;
                    inkSamples[half]++; inkRows[0, half] += r; inkRows[1, half] += g; inkRows[2, half] += b;
                }
            }
        }
        int coveredColumns = 0;
        foreach (int n in columns) if (n >= source.Height * 0.08) coveredColumns++;
        if (bandCount < count * 0.18 || coveredColumns < source.Width * 0.7 || inkCount < count * 0.025)
            return null;
        var outline = Color.FromRgb((byte)(rb / bandCount), (byte)(gb / bandCount), (byte)(bb / bandCount));
        var ink = Color.FromRgb((byte)(ri / inkCount), (byte)(gi / inkCount), (byte)(bi / inkCount));
        int padding = Math.Clamp(source.Height / 8, 2, 18);
        var area = Rectangle.Intersect(new Rectangle(0, 0, width, height),
            Rectangle.Inflate(source, padding, padding));
        int length = area.Width * area.Height;
        var core = new bool[length];
        var original = new byte[length * 4];
        for (int y = 0; y < area.Height; y++)
        for (int x = 0; x < area.Width; x++)
        {
            int i = y * area.Width + x, offset = ((area.Y + y) * width + area.X + x) * 4;
            pixels.AsSpan(offset, 4).CopyTo(original.AsSpan(i * 4, 4));
            int b = pixels[offset], g = pixels[offset + 1], r = pixels[offset + 2];
            core[i] = r > 215 && g > 200 && (b < 185 || Math.Min(r, Math.Min(g, b)) > 228);
        }
        var seen = new bool[length];
        var mask = new bool[length];
        var queue = new int[length];
        int expansion = Math.Clamp(source.Height / 24, 2, 5);
        for (int start = 0; start < length; start++)
        {
            if (!core[start] || seen[start]) continue;
            int head = 0, tail = 0, yellow = 0, adjacent = 0;
            bool edge = false;
            seen[start] = true; queue[tail++] = start;
            while (head < tail)
            {
                if ((head & 1023) == 0) token.ThrowIfCancellationRequested();
                int i = queue[head++], x = i % area.Width, y = i / area.Width;
                edge |= x == 0 || y == 0 || x == area.Width - 1 || y == area.Height - 1;
                if (original[i * 4] < 185) yellow++;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= area.Width || ny >= area.Height) continue;
                    int n = ny * area.Width + nx;
                    if (original[n * 4 + 2] > original[n * 4 + 1] + 40) adjacent++;
                    if (!core[n] || seen[n]) continue;
                    seen[n] = true; queue[tail++] = n;
                }
            }
            if (tail < 2 || (yellow == 0 && (edge || adjacent < tail / 8))
                || tail > source.Height * source.Height / 2) continue;
            for (int k = 0; k < tail; k++)
            {
                int x = queue[k] % area.Width, y = queue[k] / area.Width;
                for (int dy = -expansion; dy <= expansion; dy++)
                for (int dx = -expansion; dx <= expansion; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx >= 0 && ny >= 0 && nx < area.Width && ny < area.Height)
                        mask[ny * area.Width + nx] = true;
                }
            }
        }
        // Include the old colored stroke/halo, otherwise its Japanese letter shapes survive the fill cover.
        for (int i = 0; i < length; i++)
        {
            int x = i % area.Width + area.X, y = i / area.Width + area.Y;
            int b = original[i * 4], g = original[i * 4 + 1], r = original[i * 4 + 2];
            if (r > 180 && r > g + 45 && r > b + 30) mask[i] = true;
            if (source.Contains(x, y) && Math.Abs(y - (source.Top + source.Height * .55)) < source.Height * .3)
                mask[i] = true;
        }
        var rebuilt = (byte[])original.Clone();
        var distance = new int[length];
        Array.Fill(distance, -1);
        int front = 0, end = 0, covered = 0;
        for (int i = 0; i < length; i++)
        {
            if (mask[i]) { covered++; continue; }
            distance[i] = 0; queue[end++] = i;
        }
        if (covered < count * 0.04 || covered > length * 0.95) return null;
        while (front < end)
        {
            if ((front & 1023) == 0) token.ThrowIfCancellationRequested();
            int i = queue[front++], x = i % area.Width, y = i / area.Width;
            if (mask[i])
            {
                int n = 0, r = 0, g = 0, b = 0;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= area.Width || ny >= area.Height) continue;
                    int neighbor = ny * area.Width + nx;
                    if (distance[neighbor] < 0 || distance[neighbor] >= distance[i]) continue;
                    b += rebuilt[neighbor * 4]; g += rebuilt[neighbor * 4 + 1]; r += rebuilt[neighbor * 4 + 2]; n++;
                }
                if (n > 0) { rebuilt[i * 4] = (byte)(b / n); rebuilt[i * 4 + 1] = (byte)(g / n); rebuilt[i * 4 + 2] = (byte)(r / n); }
            }
            if (x > 0) Visit(i, i - 1);
            if (x + 1 < area.Width) Visit(i, i + 1);
            if (y > 0) Visit(i, i - area.Width);
            if (y + 1 < area.Height) Visit(i, i + area.Width);
        }
        // Rebuild a smooth band over the inferred background; never retain the old glyph-shaped glow.
        for (int i = 0; i < length; i++)
        {
            if (!mask[i]) continue;
            double x = i % area.Width + area.X, y = i / area.Width + area.Y;
            double vertical = Math.Clamp((.5 - Math.Abs((y - source.Top) / source.Height - .55)) / .2, 0, 1);
            double horizontal = Math.Clamp(Math.Min(x - area.Left, area.Right - 1 - x) / padding, 0, 1);
            double alpha = vertical * horizontal;
            rebuilt[i * 4] = (byte)(rebuilt[i * 4] * (1 - alpha) + outline.B * alpha);
            rebuilt[i * 4 + 1] = (byte)(rebuilt[i * 4 + 1] * (1 - alpha) + outline.G * alpha);
            rebuilt[i * 4 + 2] = (byte)(rebuilt[i * 4 + 2] * (1 - alpha) + outline.R * alpha);
        }
        var cover = new SourceCoverPlan(SourceCoverClass.Gradient, new Rectangle(0, 0, width, height),
            area, area, mask, rebuilt, covered);
        return new DisplayLettering(cover, SampleInk(0), SampleInk(1), outline, original);

        Color SampleInk(int half) => inkSamples[half] == 0 ? ink : Color.FromRgb(
            (byte)(inkRows[0, half] / inkSamples[half]), (byte)(inkRows[1, half] / inkSamples[half]),
            (byte)(inkRows[2, half] / inkSamples[half]));

        void Visit(int from, int to)
        {
            if (distance[to] >= 0) return;
            distance[to] = distance[from] + 1; queue[end++] = to;
        }
    }

    internal TextBlock CreateText(string value, CaptionStyleProfile style, double size)
    {
        var fill = new LinearGradientBrush(Ink, Highlight, 90);
        fill.Freeze();
        return new DisplayText {
            Tag = Cover,
            Text = value, FontFamily = new FontFamily(style.Typeface), FontWeight = style.Weight,
            FontSize = size, Foreground = style.Role == CaptionRole.Auto ? fill : new SolidColorBrush(style.Foreground),
            Stroke = new SolidColorBrush(Outline), StrokeWidth = style.Effect == CaptionEffect.Outline ? Math.Max(1.2, size * 0.07) : 0,
            Effect = style.Effect == CaptionEffect.None ? null : new DropShadowEffect { Color = Outline, BlurRadius = Math.Max(3, size * 0.18),
                ShadowDepth = style.Effect == CaptionEffect.Shadow ? 2 : 0, Opacity = 0.95 },
            TextWrapping = TextWrapping.NoWrap, TextAlignment = TextAlignment.Center,
            Language = System.Windows.Markup.XmlLanguage.GetLanguage("th-TH")
        };
    }

    private sealed class DisplayText : TextBlock
    {
        public Brush Stroke { get; init; } = Brushes.Black;
        public double StrokeWidth { get; init; }

        public DisplayText() => SizeChanged += (_, _) => DrawStroke();

        private void DrawStroke()
        {
            if (StrokeWidth <= 0) { Background = null; return; }
            var text = new FormattedText(Text, CultureInfo.GetCultureInfo("th-TH"), FlowDirection.LeftToRight,
                new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground,
                VisualTreeHelper.GetDpi(this).PixelsPerDip) {
                TextAlignment = TextAlignment.Center, MaxTextWidth = Math.Max(1, ActualWidth)
            };
            var geometry = text.BuildGeometry(new Point(0, 0));
            var drawing = new GeometryDrawing(null, new Pen(Stroke, StrokeWidth) { LineJoin = PenLineJoin.Round }, geometry);
            drawing.Freeze();
            var brush = new DrawingBrush(drawing) {
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight)),
                ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight)),
                Stretch = Stretch.None
            };
            brush.Freeze(); Background = brush;
        }
    }
}
