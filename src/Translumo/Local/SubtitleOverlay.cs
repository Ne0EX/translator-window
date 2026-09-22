using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Translumo.Local;

public enum SubtitleStyle { Overlay, Overwrite }

public sealed class SubtitleLayoutException : InvalidOperationException
{
    public bool BackgroundRejected { get; }
    public SubtitleLayoutException(bool backgroundRejected = false)
        : base("There is not enough space for readable subtitles. Select a smaller set of text or zoom the page out.")
        => BackgroundRejected = backgroundRejected;
}

public sealed class SubtitleOverlay : Window
{
    private readonly Canvas canvas = new() { ClipToBounds = true, IsHitTestVisible = false };
    private UIElement[]? rollbackChildren;
    private nint handle;
    public nint ControlsHandle { get; set; }

    public SubtitleOverlay()
    {
        Title = "Local translator subtitles";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        Topmost = true;
        Width = Height = 1;
        Content = canvas;
        SourceInitialized += (_, _) => ConfigureNativeWindow();
    }

    public void Clear()
    {
        Dispatcher.VerifyAccess();
        rollbackChildren = null;
        canvas.Children.Clear();
        Hide();
    }

    internal void ConfirmRender() => rollbackChildren = null;

    internal void RestorePrevious()
    {
        Dispatcher.VerifyAccess();
        if (rollbackChildren is null) return;
        canvas.Children.Clear();
        foreach (var child in rollbackChildren) canvas.Children.Add(child);
        rollbackChildren = null;
        if (canvas.Children.Count == 0) Hide(); else Show();
    }

    public void Render(Drawing.Rectangle captureBounds, IReadOnlyList<TextRegion> regions,
        IReadOnlyList<string> translations, SubtitleStyle style, int padding = 6, BitmapSource? frame = null)
    {
        Dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(translations);
        if (regions.Count != translations.Count)
            throw new ArgumentException("Each recognized text region needs one translation.");
        if (captureBounds.Width <= 0 || captureBounds.Height <= 0 || padding < 0 || padding > 100)
            throw new ArgumentOutOfRangeException(nameof(captureBounds));
        if (style is not SubtitleStyle.Overlay and not SubtitleStyle.Overwrite)
            throw new ArgumentOutOfRangeException(nameof(style));
        if (frame is not null && (frame.PixelWidth != captureBounds.Width || frame.PixelHeight != captureBounds.Height))
            throw new ArgumentException("The held page must match the captured pixel dimensions.", nameof(frame));
        if (regions.Count == 0 && frame is null) { Clear(); return; }

        var (desktop, scaleX, scaleY) = PrepareWindow();

        var sources = regions.Select(region => {
            var clipped = Drawing.Rectangle.Intersect(region.Bounds,
                new Drawing.Rectangle(0, 0, captureBounds.Width, captureBounds.Height));
            clipped.Offset(captureBounds.Location);
            return clipped;
        }).ToArray();
        var masks = sources.Select(source => {
            if (source.Width <= 0 || source.Height <= 0) return Drawing.Rectangle.Empty;
            source.Inflate(padding, padding);
            return Drawing.Rectangle.Intersect(source, desktop);
        }).ToArray();
        byte[]? backgroundPixels = null;
        if (frame is not null && style == SubtitleStyle.Overwrite)
        {
            var background = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            backgroundPixels = new byte[checked(frame.PixelWidth * frame.PixelHeight * 4)];
            background.CopyPixels(backgroundPixels, frame.PixelWidth * 4, 0);
        }
        var placed = new List<Drawing.Rectangle>();
        var visuals = new List<(Border Border, Drawing.Rectangle Bounds)>();

        if (style == SubtitleStyle.Overwrite)
            foreach (var mask in masks.Where(mask => mask.Width > 0 && mask.Height > 0))
                visuals.Add((new Border { Background = Brushes.White }, mask));

        for (int i = 0; i < regions.Count; i++)
        {
            if (sources[i].Width <= 0 || sources[i].Height <= 0) continue;
            if (string.IsNullOrWhiteSpace(translations[i]))
                throw new InvalidOperationException("The local model returned an empty translation; subtitles were not shown.");
            var source = masks[i];
            var monitor = Forms.Screen.FromPoint(new Drawing.Point(source.Left + source.Width / 2,
                source.Top + source.Height / 2)).Bounds;
            var blockers = masks.Where((mask, index) => index != i && mask.Width > 0 && mask.Height > 0)
                .Concat(placed).ToArray();
            Border? caption = null;
            Drawing.Rectangle? position = null;
            bool backgroundRejected = false;
            bool thai = translations[i].Any(character => character is >= '\u0e00' and <= '\u0e7f');
            var words = thai ? ThaiWords(translations[i]) : null;
            // Prefer natural horizontal lines; narrow vertical OCR boxes are not subtitle columns.
            foreach (double fontSize in CaptionFontSizes(source.Height, scaleY))
            {
                var text = new TextBlock {
                    Text = translations[i], FontFamily = new FontFamily(thai ? "Leelawadee UI" : "Segoe UI"), FontSize = fontSize,
                    Language = XmlLanguage.GetLanguage(thai ? "th-TH" : "en-US"),
                    Foreground = Brushes.Black, TextWrapping = thai ? TextWrapping.NoWrap : TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.None
                };
                Brush? captionBackground = backgroundPixels is null ? Brushes.White : null;
                foreach (double widthDip in new[] { Math.Max(source.Width * scaleX, 140), 120d, 100d, 180d, 220d, 260d, 360d, 480d, Math.Max(source.Width * scaleX, 80) }.Distinct())
                {
                    int width = Math.Min(monitor.Width, (int)Math.Ceiling(widthDip / scaleX));
                    double insetX = padding * scaleX, insetY = padding * scaleY;
                    double contentWidth = Math.Max(1, width * scaleX - insetX * 2);
                    if (words is not null && !WrapWords(text, words, contentWidth)) continue;
                    text.Measure(new Size(contentWidth, double.PositiveInfinity));
                    int height = (int)Math.Ceiling((text.DesiredSize.Height + insetY * 2) / scaleY);
                    double maxHeight = Math.Max(style == SubtitleStyle.Overlay ? source.Height * 2.4 : source.Height * 1.8,
                        72 / scaleY);
                    if (height > maxHeight || height > width * 1.6) continue;
                    if (backgroundPixels is not null && SubtitleLayout.Place(source, new Drawing.Size(width, height), monitor,
                        blockers, false, padding) is not null)
                        backgroundRejected = true;
                    if (backgroundPixels is not null)
                        captionBackground = null;
                    position = SubtitleLayout.Place(source, new Drawing.Size(width, height), monitor,
                        blockers, style == SubtitleStyle.Overlay, padding, frame is null ? null
                            : candidate => {
                                 captionBackground = BackgroundBrush(candidate, source, captureBounds, backgroundPixels, frame, style);
                                return captionBackground is not null;
                            });
                    if (position is null && style == SubtitleStyle.Overwrite)
                    {
                        // Overwrite masks already cover the source; let a crowded page reuse nearby space.
                        position = SubtitleLayout.Place(source, new Drawing.Size(width, height), monitor,
                            Array.Empty<Drawing.Rectangle>(), false, padding, frame is null ? null
                                : candidate => {
                                     captionBackground = BackgroundBrush(candidate, source, captureBounds, backgroundPixels, frame, style);
                                    return captionBackground is not null;
                                });
                    }
                    if (position is null) continue;

                    text.Foreground = ForegroundBrush(captionBackground!);
                    caption = new Border {
                        Background = captionBackground ?? Brushes.White, Padding = new Thickness(insetX, insetY, insetX, insetY),
                        Child = text
                    };
                    break;
                }
                if (caption is not null) break;
            }
            if (caption is null || position is null)
                throw new SubtitleLayoutException(backgroundRejected);
            placed.Add(position.Value);
            visuals.Add((caption, position.Value));
        }

        // Commit only after every caption fits; failed layout keeps the last translated page visible.
        rollbackChildren ??= canvas.Children.Cast<UIElement>().ToArray();
        canvas.Children.Clear();
        if (frame is not null && style == SubtitleStyle.Overwrite)
        {
            var image = new Image { Source = frame, Stretch = Stretch.Fill,
                Width = captureBounds.Width * scaleX, Height = captureBounds.Height * scaleY };
            Canvas.SetLeft(image, (captureBounds.X - desktop.X) * scaleX);
            Canvas.SetTop(image, (captureBounds.Y - desktop.Y) * scaleY);
            canvas.Children.Add(image);
        }
        foreach (var (border, bounds) in visuals)
        {
            border.Width = bounds.Width * scaleX;
            border.Height = bounds.Height * scaleY;
            Canvas.SetLeft(border, (bounds.X - desktop.X) * scaleX);
            Canvas.SetTop(border, (bounds.Y - desktop.Y) * scaleY);
            canvas.Children.Add(border);
        }
    }

    internal static IEnumerable<double> CaptionFontSizes(int sourceHeight, double scaleY)
    {
        double largest = Math.Clamp(Math.Round(sourceHeight * scaleY * 0.8), 20, 26);
        for (double size = largest; size >= 8; size -= 2) yield return size;
    }

    internal static string[] ThaiWords(string value)
    {
        // Windows supplies dictionary boundaries for Thai, whose words are not separated by spaces.
        var tokens = new Windows.Data.Text.WordsSegmenter("th").GetTokens(value);
        var textElements = StringInfo.ParseCombiningCharacters(value).ToHashSet();
        var words = new List<string>();
        int start = 0;
        for (int i = 1; i < tokens.Count; i++)
        {
            int end = (int)tokens[i].SourceTextSegment.StartPosition;
            // The dictionary may split before a Thai tone mark; keep the complete grapheme together.
            if (!textElements.Contains(end)) continue;
            words.Add(value[start..end]);
            start = end;
        }
        words.Add(value[start..]);
        return words.ToArray();
    }

    internal static bool WrapWords(TextBlock text, IReadOnlyList<string> words, double width)
    {
        // Minimize line count first, then balance the lines so a short final word is not stranded.
        var counts = Enumerable.Repeat(int.MaxValue, words.Count + 1).ToArray();
        var costs = new double[words.Count + 1];
        var next = new int[words.Count];
        counts[words.Count] = 0;
        for (int start = words.Count - 1; start >= 0; start--)
        {
            string line = "";
            for (int end = start; end < words.Count; end++)
            {
                line += words[end];
                text.Text = line.Trim();
                text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double remaining = width - text.DesiredSize.Width;
                if (remaining < 0) break;
                if (counts[end + 1] == int.MaxValue) continue;
                int count = counts[end + 1] + 1;
                double cost = remaining * remaining + costs[end + 1];
                if (count < counts[start] || count == counts[start] && cost < costs[start])
                {
                    counts[start] = count;
                    costs[start] = cost;
                    next[start] = end + 1;
                }
            }
        }
        if (counts[0] == int.MaxValue) return false;
        var lines = new StringBuilder();
        for (int start = 0; start < words.Count; start = next[start])
        {
            if (lines.Length > 0) lines.AppendLine();
            lines.Append(string.Concat(words.Skip(start).Take(next[start] - start)).Trim());
        }
        text.Text = lines.ToString();
        return true;
    }

    private static Brush? BackgroundBrush(Drawing.Rectangle caption, Drawing.Rectangle mask,
        Drawing.Rectangle capture, byte[]? pixels, BitmapSource? frame, SubtitleStyle style)
    {
        if (!capture.Contains(caption)) return style == SubtitleStyle.Overlay ? Brushes.White : null;
        if (style == SubtitleStyle.Overlay && frame is not null)
        {
            var imageBrush = new ImageBrush(frame) {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(caption.X - capture.X, caption.Y - capture.Y, caption.Width, caption.Height),
                ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                Viewport = new Rect(0, 0, 1, 1)
            };
            imageBrush.Freeze();
            return imageBrush;
        }
        if (pixels is null) return Brushes.White;
        int samples = 0, minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
        long totalR = 0, totalG = 0, totalB = 0;
        for (int y = caption.Top; y < caption.Bottom; y += 2)
            for (int x = caption.Left; x < caption.Right; x += 2)
            {
                if (mask.Contains(x, y)) continue;
                int offset = ((y - capture.Y) * capture.Width + x - capture.X) * 4;
                int b = pixels[offset], g = pixels[offset + 1], r = pixels[offset + 2];
                minR = Math.Min(minR, r); minG = Math.Min(minG, g); minB = Math.Min(minB, b);
                maxR = Math.Max(maxR, r); maxG = Math.Max(maxG, g); maxB = Math.Max(maxB, b);
                totalR += r; totalG += g; totalB += b; samples++;
            }
        if (samples == 0) return Brushes.White;
        if (minR >= 240 && minG >= 240 && minB >= 240) return Brushes.White;
        // ponytail: overwrite must hide the source even over textured artwork; white is the safe fallback.
        if (maxR - minR > 32 || maxG - minG > 32 || maxB - minB > 32) return Brushes.White;
        var brush = new SolidColorBrush(Color.FromRgb((byte)(totalR / samples), (byte)(totalG / samples), (byte)(totalB / samples)));
        brush.Freeze();
        return brush;
    }

    private static Brush ForegroundBrush(Brush background)
    {
        if (background is SolidColorBrush solid)
        {
            var color = solid.Color;
            double luminance = (0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B);
            if (luminance < 145) return Brushes.White;
        }
        return Brushes.Black;
    }
    public void Preparing(Drawing.Rectangle captureBounds)
    {
        Dispatcher.VerifyAccess();
        if (captureBounds.Width <= 0 || captureBounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(captureBounds));
        var (desktop, scaleX, scaleY) = PrepareWindow();
        rollbackChildren = null;
        canvas.Children.Clear();
        var cover = new Border {
            Background = Brushes.White, Width = captureBounds.Width * scaleX, Height = captureBounds.Height * scaleY,
            Padding = new Thickness(20), Child = new TextBlock {
                Text = "Preparing translated page\u2026", FontSize = 18, Foreground = Brushes.Black,
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        Canvas.SetLeft(cover, (captureBounds.X - desktop.X) * scaleX);
        Canvas.SetTop(cover, (captureBounds.Y - desktop.Y) * scaleY);
        canvas.Children.Add(cover);
    }

    public void RefreshControlExclusion()
    {
        Dispatcher.VerifyAccess();
        if (handle == 0) return;
        var desktop = Forms.SystemInformation.VirtualScreen;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        if (transform is null) return;
        var clip = new RectangleGeometry(new Rect(0, 0, desktop.Width * transform.Value.M11, desktop.Height * transform.Value.M22));
        if (ControlsHandle != 0 && IsWindowVisible(ControlsHandle) && !IsIconic(ControlsHandle)
            && GetWindowRect(ControlsHandle, out var controls))
        {
            var hole = new RectangleGeometry(new Rect((controls.Left - desktop.X) * transform.Value.M11,
                (controls.Top - desktop.Y) * transform.Value.M22,
                (controls.Right - controls.Left) * transform.Value.M11, (controls.Bottom - controls.Top) * transform.Value.M22));
            canvas.Clip = new CombinedGeometry(GeometryCombineMode.Exclude, clip, hole);
        }
        else canvas.Clip = clip;
    }
    private (Drawing.Rectangle Desktop, double ScaleX, double ScaleY) PrepareWindow()
    {
        new WindowInteropHelper(this).EnsureHandle();
        // A tight area selection may have no room beside its text. Let captions use its monitor.
        var desktop = Forms.SystemInformation.VirtualScreen;
        if (!IsVisible) Show();
        if (!SetWindowPos(handle, new nint(-1), desktop.X, desktop.Y, desktop.Width, desktop.Height, 0x0010 | 0x0200))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot position the subtitle overlay.");

        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
            ?? throw new InvalidOperationException("Subtitle overlay has no display transform.");
        double scaleX = transform.M11, scaleY = transform.M22;
        canvas.Width = desktop.Width * scaleX;
        canvas.Height = desktop.Height * scaleY;

        RefreshControlExclusion();
        return (desktop, scaleX, scaleY);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rectangle);
    private void ConfigureNativeWindow()
    {
        handle = new WindowInteropHelper(this).Handle;
        // Layered + transparent makes clicks pass through to other applications, including scrolling.
        const int index = -20;
        int style = GetWindowLong(handle, index) | 0x00000020 | 0x00000080 | 0x08000000;
        Marshal.SetLastPInvokeError(0);
        if (SetWindowLong(handle, index, style) == 0 && Marshal.GetLastPInvokeError() != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot make subtitles click-through.");
        HwndSource.FromHwnd(handle)?.AddHook((nint _, int message, nint wParam, nint lParam, ref bool handled) => {
            if (message == 0x0084) { handled = true; return new nint(-1); } // HTTRANSPARENT
            if (message == 0x0021) { handled = true; return new nint(3); } // MA_NOACTIVATE
            return nint.Zero;
        });
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
            || !SetWindowDisplayAffinity(handle, 0x00000011)
            || !GetWindowDisplayAffinity(handle, out uint affinity) || affinity != 0x00000011)
            throw new InvalidOperationException("Windows cannot exclude the subtitle overlay from capture. Windows 10 version 2004 or newer and desktop composition are required; translation was stopped to prevent OCR feedback.");
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(nint hwnd, int index, int value);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
}
