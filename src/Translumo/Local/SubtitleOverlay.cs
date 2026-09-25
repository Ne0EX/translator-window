using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
    private BitmapSource? layoutFrame;
    private byte[]? layoutPixels;
    private readonly Dictionary<(string Text, double Size, double Width), string?> wrappedCache = new();
    private BitmapSource? maskFrame;
    private Drawing.Rectangle maskCapture;
    private readonly Dictionary<Drawing.Rectangle, Brush> maskCache = new();
    private Drawing.Rectangle plainMarginCapture;
    private Drawing.Rectangle[]? plainMarginMasks;
    private Drawing.Rectangle[]? plainMargins;
    private BitmapSource? captionFrame;
    private IReadOnlyList<TextRegion>? captionRegions;
    private (Drawing.Rectangle Capture, SubtitleStyle Style, int Padding, bool JapaneseToThai,
        double ScaleX, double ScaleY) captionOptions;
    private readonly Dictionary<int, (string Translation, Drawing.Rectangle Source, Border? Caption,
        Drawing.Rectangle Bounds)> captionCache = new();
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

    internal Task PrepareThaiLayoutAsync()
    {
        Dispatcher.VerifyAccess();
        // Create the click-through HWND while it is still empty and hidden. Font and WinRT
        // initialization use disposable objects on their own STA; no UI object crosses threads.
        new WindowInteropHelper(this).EnsureHandle();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                const string sample = "\u0e20\u0e32\u0e29\u0e32\u0e44\u0e17\u0e22";
                _ = ThaiWords(sample);
                var text = new TextBlock {
                    Text = sample, FontFamily = new FontFamily("Leelawadee UI"), FontSize = 24,
                    Language = XmlLanguage.GetLanguage("th-TH")
                };
                text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                completion.SetResult();
            }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true, Name = "Thai layout warmup" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    public void Clear()
    {
        Dispatcher.VerifyAccess();
        rollbackChildren = null;
        canvas.Children.Clear();
        layoutFrame = maskFrame = null;
        layoutPixels = null;
        captionFrame = null;
        captionRegions = null;
        wrappedCache.Clear();
        maskCache.Clear();
        plainMarginMasks = plainMargins = null;
        captionCache.Clear();
        Hide();
    }

    internal void ConfirmRender() => rollbackChildren = null;

    internal void Suspend()
    {
        Dispatcher.VerifyAccess();
        Hide();
    }

    internal void ShiftVertical(int pixels, IReadOnlyList<int>? retainedIndices = null)
    {
        Dispatcher.VerifyAccess();
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        double scaleY = transform?.M22 ?? 1;
        Dictionary<int, int>? remap = retainedIndices?.Select((oldIndex, newIndex) => (oldIndex, newIndex))
            .ToDictionary(pair => pair.oldIndex, pair => pair.newIndex);
        for (int i = canvas.Children.Count - 1; i >= 0; i--)
        {
            var child = canvas.Children[i];
            if (remap is not null)
            {
                if (child is not Border border || border.Tag is not int oldIndex || !remap.TryGetValue(oldIndex, out int newIndex))
                {
                    canvas.Children.RemoveAt(i);
                    continue;
                }
                border.Tag = newIndex;
            }
            double top = Canvas.GetTop(child);
            Canvas.SetTop(child, (double.IsNaN(top) ? 0 : top) + pixels * scaleY);
        }
    }

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
        IReadOnlyList<string> translations, SubtitleStyle style, int padding = 6, BitmapSource? frame = null,
        bool japaneseToThai = false, bool allowMissingTranslations = false)
        => RenderCore(captureBounds, regions, translations, style, padding, frame,
            japaneseToThai, allowMissingTranslations, preferReadableThai: true);

    private void RenderCore(Drawing.Rectangle captureBounds, IReadOnlyList<TextRegion> regions,
        IReadOnlyList<string> translations, SubtitleStyle style, int padding, BitmapSource? frame,
        bool japaneseToThai, bool allowMissingTranslations, bool preferReadableThai)
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
            throw new ArgumentException("The captured page must match the captured pixel dimensions.", nameof(frame));
        if (regions.Count == 0) { Clear(); return; }
        if (frame is null || !ReferenceEquals(layoutFrame, frame))
        {
            wrappedCache.Clear();
            layoutFrame = frame;
            layoutPixels = null;
            plainMarginMasks = plainMargins = null;
        }

        var (desktop, scaleX, scaleY) = PrepareWindow();
        bool cacheCaptions = preferReadableThai && frame?.IsFrozen == true;
        var options = (captureBounds, style, padding, japaneseToThai, scaleX, scaleY);
        if (preferReadableThai && (!cacheCaptions || !ReferenceEquals(captionFrame, frame)
            || !ReferenceEquals(captionRegions, regions) || captionOptions != options))
        {
            captionCache.Clear();
            captionFrame = cacheCaptions ? frame : null;
            captionRegions = cacheCaptions ? regions : null;
            captionOptions = options;
        }

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
            if (frame.IsFrozen && layoutPixels is not null)
                backgroundPixels = layoutPixels;
            else
            {
                var background = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                backgroundPixels = new byte[checked(frame.PixelWidth * frame.PixelHeight * 4)];
                background.CopyPixels(backgroundPixels, frame.PixelWidth * 4, 0);
                if (frame.IsFrozen) layoutPixels = backgroundPixels;
            }
        }
        var placed = new List<Drawing.Rectangle>();
        var visuals = new List<(Border Border, Drawing.Rectangle Bounds)>();
        var marginIndices = new List<int>();

        if (style == SubtitleStyle.Overwrite)
            for (int i = 0; i < masks.Length; i++)
                if (masks[i].Width > 0 && masks[i].Height > 0
                    && !string.IsNullOrWhiteSpace(translations[i]))
                    visuals.Add((new Border { Background = MaskBrushForFrame(masks[i], captureBounds, backgroundPixels, frame), Tag = i }, masks[i]));

        if (!allowMissingTranslations && preferReadableThai && japaneseToThai && style == SubtitleStyle.Overwrite && frame is not null
            && translations.All(translation => !string.IsNullOrWhiteSpace(translation))
            && backgroundPixels is not null && TryRenderVerticalIndex(captureBounds, regions, translations,
                sources, backgroundPixels, desktop, scaleX, scaleY, visuals))
            return;

        for (int i = 0; i < regions.Count; i++)
        {
            if (sources[i].Width <= 0 || sources[i].Height <= 0) continue;
            if (string.IsNullOrWhiteSpace(translations[i]))
            {
                if (string.IsNullOrWhiteSpace(regions[i].Text)
                    || allowMissingTranslations && style == SubtitleStyle.Overwrite) continue;
                throw new InvalidOperationException("The local model returned an empty translation; subtitles were not shown.");
            }
            var source = masks[i];
            string translation = japaneseToThai && style == SubtitleStyle.Overwrite
                ? SafeThaiTranslation(translations[i]) : translations[i];
            if (cacheCaptions && captionCache.TryGetValue(i, out var previous)
                && previous.Translation == translation && previous.Source == source)
            {
                if (previous.Caption is null) marginIndices.Add(i);
                else { placed.Add(previous.Bounds); visuals.Add((previous.Caption, previous.Bounds)); }
                continue;
            }
            if (cacheCaptions)
                foreach (int stale in captionCache.Keys.Where(index => index >= i).ToArray())
                    captionCache.Remove(stale);
            var monitor = Forms.Screen.FromPoint(new Drawing.Point(source.Left + source.Width / 2,
                source.Top + source.Height / 2)).Bounds;
            var blockers = masks.Where((mask, index) => index != i && mask.Width > 0 && mask.Height > 0)
                .Concat(placed).ToArray();
            Border? caption = null;
            Drawing.Rectangle? position = null;
            bool backgroundRejected = false;
            bool thai = translation.Any(character => character is >= '\u0e00' and <= '\u0e7f');
            var words = thai ? ThaiWords(translation) : null;
            // Keep captions tied to the recognized textbox; grow only enough to fit its translation.
            foreach (double fontSize in CaptionFontSizes(source.Height, scaleY)
                .Where(size => !preferReadableThai || !japaneseToThai || style != SubtitleStyle.Overwrite || size >= 12))
            {
                var text = new TextBlock {
                    Text = translation, FontFamily = new FontFamily(thai ? "Leelawadee UI" : "Segoe UI"), FontSize = fontSize,
                    Language = XmlLanguage.GetLanguage(thai ? "th-TH" : "en-US"),
                    Foreground = Brushes.Black, TextWrapping = thai ? TextWrapping.NoWrap : TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.None
                };
                Brush? captionBackground = backgroundPixels is null ? Brushes.White : null;
                double maxWidthDip = Math.Min(monitor.Width * scaleX, Math.Min(320, Math.Max(80, source.Width * scaleX * 2.5)));
                var lineWidths = new Dictionary<string, double>();
                foreach (double widthDip in new[] { source.Width * scaleX, source.Width * scaleX * 1.5,
                    source.Width * scaleX * 2, maxWidthDip }.Select(width => Math.Min(width, maxWidthDip)).Distinct().Order())
                {
                    int width = Math.Min(monitor.Width, (int)Math.Ceiling(widthDip / scaleX));
                    double insetX = padding * scaleX, insetY = padding * scaleY;
                    double contentWidth = Math.Max(1, width * scaleX - insetX * 2);
                    if (words is not null)
                    {
                        var key = (translation, fontSize, contentWidth);
                        if (!wrappedCache.TryGetValue(key, out string? wrapped))
                        {
                            wrapped = WrapWords(text, words, contentWidth, lineWidths) ? text.Text : null;
                            wrappedCache[key] = wrapped;
                        }
                        if (wrapped is null) continue;
                        text.Text = wrapped;
                    }
                    text.Measure(new Size(contentWidth, double.PositiveInfinity));
                    int height = (int)Math.Ceiling((text.DesiredSize.Height + insetY * 2) / scaleY);
                    double maxHeight = source.Height * (style == SubtitleStyle.Overlay ? 2.4 : 1.8);
                    if (height > maxHeight || height > width * 1.6) continue;
                    if (backgroundPixels is not null)
                        captionBackground = null;
                    position = SubtitleLayout.Place(source, new Drawing.Size(width, height), monitor,
                        blockers, style == SubtitleStyle.Overlay, padding, frame is null ? null
                            : candidate => {
                                if (style == SubtitleStyle.Overwrite
                                    && Math.Abs(candidate.Left + candidate.Width / 2 - (source.Left + source.Width / 2)) > Math.Max(32, source.Width / 2))
                                    return false;
                                captionBackground = BackgroundBrush(candidate, source, captureBounds, backgroundPixels, frame, style);
                                return captionBackground is not null;
                            });
                    if (position is null && style == SubtitleStyle.Overwrite && backgroundPixels is not null)
                        backgroundRejected = true;
                    if (position is null) continue;

                    text.Foreground = ForegroundBrush(captionBackground!);
                    caption = new Border {
                        Tag = i,
                        Background = captionBackground ?? Brushes.White, Padding = new Thickness(insetX, insetY, insetX, insetY),
                        Child = text
                    };
                    break;
                }
                if (caption is not null) break;
            }
            if (caption is null || position is null)
            {
                if (japaneseToThai && style == SubtitleStyle.Overwrite && frame is not null)
                {
                    marginIndices.Add(i);
                    if (cacheCaptions) captionCache[i] = (translation, source, null, Drawing.Rectangle.Empty);
                    continue;
                }
                throw new SubtitleLayoutException(backgroundRejected);
            }
            placed.Add(position.Value);
            visuals.Add((caption, position.Value));
            if (cacheCaptions) captionCache[i] = (translation, source, caption, position.Value);
        }

        if (marginIndices.Count > 0)
        {
            if (!TryRenderThaiMargin(captureBounds, masks, sources, translations, marginIndices,
                    backgroundPixels!, frame, desktop, scaleX, scaleY, padding, visuals))
            {
                if (preferReadableThai && japaneseToThai && style == SubtitleStyle.Overwrite)
                {
                    RenderCore(captureBounds, regions, translations, style, padding, frame,
                        japaneseToThai, allowMissingTranslations, preferReadableThai: false);
                    return;
                }
                throw new SubtitleLayoutException();
            }
            return;
        }

        // Commit only after every caption fits.
        rollbackChildren ??= canvas.Children.Cast<UIElement>().ToArray();
        canvas.Children.Clear();
        foreach (var (border, bounds) in visuals)
        {
            border.Width = bounds.Width * scaleX;
            border.Height = bounds.Height * scaleY;
            Canvas.SetLeft(border, (bounds.X - desktop.X) * scaleX);
            Canvas.SetTop(border, (bounds.Y - desktop.Y) * scaleY);
            canvas.Children.Add(border);
        }
    }

    private bool TryRenderVerticalIndex(Drawing.Rectangle capture, IReadOnlyList<TextRegion> regions,
        IReadOnlyList<string> translations, Drawing.Rectangle[] sources, byte[] pixels, Drawing.Rectangle desktop,
        double scaleX, double scaleY, List<(Border Border, Drawing.Rectangle Bounds)> visuals)
    {
        if (regions.Count < 12 || regions.Count(region => region.Bounds.Width <= capture.Width / 35
            && region.Bounds.Height >= region.Bounds.Width * 4) < regions.Count * 3 / 4)
            return false;
        var panel = Drawing.Rectangle.FromLTRB(
            Math.Max(capture.Left, sources.Min(source => source.Left) - 80),
            Math.Max(capture.Top, sources.Min(source => source.Top) - 80),
            Math.Min(capture.Right, sources.Max(source => source.Right) + 80),
            Math.Min(capture.Bottom, sources.Max(source => source.Bottom) - 60));
        if (panel.Width < 700 || panel.Height < 500) return false;
        int samples = 0, white = 0;
        for (int y = panel.Top; y < panel.Bottom; y += 24)
            for (int x = panel.Left; x < panel.Right; x += 24)
            {
                int offset = ((y - capture.Top) * capture.Width + x - capture.Left) * 4;
                if (pixels[offset] >= 232 && pixels[offset + 1] >= 232 && pixels[offset + 2] >= 232) white++;
                samples++;
            }
        if (white < samples * 0.72) return false;

        int title = Enumerable.Range(0, regions.Count).FirstOrDefault(i => regions[i].Text.Contains("目次"), -1);
        var order = Enumerable.Range(0, regions.Count).Where(i => i != title)
            .OrderByDescending(i => sources[i].Left + sources[i].Width / 2).ToArray();
        int split = -1, largestGap = 0;
        for (int i = 5; i <= order.Length - 5; i++)
        {
            int gap = sources[order[i - 1]].Left + sources[order[i - 1]].Width / 2
                - sources[order[i]].Left - sources[order[i]].Width / 2;
            if (gap > largestGap) { largestGap = gap; split = i; }
        }
        if (split < 0 || largestGap < panel.Width / 15) return false;
        int columnWidth = panel.Width / 2 - 28;
        int bodyTop = panel.Top + (title >= 0 ? 80 : 20);
        int rowHeight = (panel.Bottom - bodyTop - 12) / Math.Max(split, order.Length - split);
        if (rowHeight < 38) return false;
        var captions = new List<(Border Border, Drawing.Rectangle Bounds)>();
        for (int position = -1; position < order.Length; position++)
        {
            int i = position < 0 ? title : order[position];
            if (i < 0 || string.IsNullOrWhiteSpace(translations[i])) continue;
            string value = title == i ? "สารบัญ" : SafeThaiTranslation(translations[i]);
            var box = position < 0
                ? new Drawing.Rectangle(panel.Left + 20, panel.Top + 10, panel.Width - 40, 58)
                : new Drawing.Rectangle(panel.Left + (position < split ? panel.Width / 2 + 8 : 20),
                    bodyTop + (position < split ? position : position - split) * rowHeight,
                    columnWidth, rowHeight);
            TextBlock? text = null;
            foreach (double fontSize in position < 0 ? new[] { 24d } : new[] { 18d, 17d, 16d, 15d, 14d, 13d, 12d })
            {
                var candidate = new TextBlock { FontFamily = new FontFamily("Leelawadee UI"), FontSize = fontSize,
                    Language = XmlLanguage.GetLanguage("th-TH"), Foreground = Brushes.Black,
                    TextAlignment = position < 0 ? TextAlignment.Center : TextAlignment.Left };
                double width = (box.Width - 16) * scaleX;
                if (!WrapWords(candidate, ThaiWords(value), width)) continue;
                candidate.Measure(new Size(width, double.PositiveInfinity));
                if (candidate.DesiredSize.Height <= (box.Height - 4) * scaleY) { text = candidate; break; }
            }
            if (text is null) return false;
            captions.Add((new Border { Tag = i, Background = Brushes.White,
                Padding = new Thickness(8 * scaleX, 2 * scaleY, 8 * scaleX, 2 * scaleY), Child = text }, box));
        }

        visuals.Add((new Border { Uid = "index-panel", Background = Brushes.White, Child = new Grid() }, panel));
        foreach (var source in sources.Where(source => source.Bottom > panel.Bottom))
        {
            var tail = Drawing.Rectangle.Intersect(capture, Drawing.Rectangle.FromLTRB(
                source.Left - 24, panel.Bottom, source.Right + 24, source.Bottom + 8));
            if (tail.Width > 0 && tail.Height > 0)
                visuals.Add((new Border { Uid = "index-tail", Background = Brushes.White, Child = new Grid() }, tail));
        }
        visuals.AddRange(captions);
        rollbackChildren ??= canvas.Children.Cast<UIElement>().ToArray();
        canvas.Children.Clear();
        foreach (var (border, box) in visuals)
        {
            border.Width = box.Width * scaleX;
            border.Height = box.Height * scaleY;
            Canvas.SetLeft(border, (box.X - desktop.X) * scaleX);
            Canvas.SetTop(border, (box.Y - desktop.Y) * scaleY);
            canvas.Children.Add(border);
        }
        return true;
    }

    private bool TryRenderThaiMargin(Drawing.Rectangle capture, Drawing.Rectangle[] masks,
        Drawing.Rectangle[] sources, IReadOnlyList<string> translations, IReadOnlyList<int> overflow, byte[] pixels,
        BitmapSource? frame, Drawing.Rectangle desktop, double scaleX, double scaleY, int padding,
        List<(Border Border, Drawing.Rectangle Bounds)> visuals)
    {
        var margins = PlainMarginsForFrame(capture, masks, pixels, frame);
        if (margins.Length == 0) return false;
        var marginBackgrounds = margins.Select(margin => BackgroundBrush(margin, Drawing.Rectangle.Empty,
            capture, pixels, null, SubtitleStyle.Overwrite) ?? Brushes.White).ToArray();
        var placed = new List<(Border Border, Drawing.Rectangle Bounds)>();
        var readingOrder = overflow
            .OrderByDescending(i => sources[i].Left + sources[i].Width / 2)
            .ThenBy(i => sources[i].Top).ToArray();
        var numbers = readingOrder.Select((sourceIndex, rank) => (sourceIndex, number: rank + 1))
            .ToDictionary(pair => pair.sourceIndex, pair => pair.number);
        foreach (double fontSize in new[] { 12d, 11d, 10d, 9d, 8d })
        foreach (int firstColumnCount in margins.Length == 1
                     ? new[] { readingOrder.Length }
                     : fontSize >= 12
                         ? new[] { readingOrder.Length, (readingOrder.Length + 1) / 2 }
                         : new[] { (readingOrder.Length + 1) / 2, readingOrder.Length })
        {
            placed.Clear();
            int[] tops = margins.Select(margin => margin.Top).ToArray();
            bool fits = true;
            for (int orderIndex = 0; orderIndex < readingOrder.Length; orderIndex++)
            {
                int i = readingOrder[orderIndex];
                int column = orderIndex < firstColumnCount ? 0 : 1;
                var margin = margins[column];
                string translation = $"{numbers[i]}. {SafeThaiTranslation(translations[i])}";
                var text = new TextBlock {
                    Text = translation, FontFamily = new FontFamily("Leelawadee UI"), FontSize = fontSize,
                    Language = XmlLanguage.GetLanguage("th-TH"), Foreground = Brushes.Black,
                    TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center
                };
                double insetX = padding * scaleX, insetY = scaleY;
                double contentWidth = margin.Width * scaleX - insetX * 2;
                if (!WrapWords(text, ThaiWords(translation), contentWidth)) { fits = false; break; }
                text.Measure(new Size(contentWidth, double.PositiveInfinity));
                int height = (int)Math.Ceiling((text.DesiredSize.Height + insetY * 2) / scaleY);
                if (tops[column] + height > margin.Bottom) { fits = false; break; }
                var bounds = new Drawing.Rectangle(margin.Left, tops[column], margin.Width, height);
                text.Foreground = ForegroundBrush(marginBackgrounds[column]);
                placed.Add((new Border { Tag = i, Background = marginBackgrounds[column],
                    Padding = new Thickness(insetX, insetY, insetX, insetY), Child = text }, bounds));
                tops[column] += height + 1;
            }
            if (!fits) continue;
            visuals.AddRange(placed);
            foreach (int i in readingOrder)
            {
                var sourceMask = masks[i];
                var badge = Badge(numbers[i], i);
                visuals.Add((badge, new Drawing.Rectangle(sourceMask.Left, sourceMask.Top,
                    Math.Min(sourceMask.Width, 24), Math.Min(sourceMask.Height, 24))));
            }
            rollbackChildren ??= canvas.Children.Cast<UIElement>().ToArray();
            canvas.Children.Clear();
            foreach (var (border, bounds) in visuals) canvas.Children.Add(Positioned(border, bounds));
            return true;
        }
        return false;

        Border Positioned(Border border, Drawing.Rectangle bounds)
        {
            border.Width = bounds.Width * scaleX;
            border.Height = bounds.Height * scaleY;
            Canvas.SetLeft(border, (bounds.X - desktop.X) * scaleX);
            Canvas.SetTop(border, (bounds.Y - desktop.Y) * scaleY);
            return border;
        }
    }

    private Drawing.Rectangle[] PlainMarginsForFrame(Drawing.Rectangle capture, Drawing.Rectangle[] masks,
        byte[] pixels, BitmapSource? frame)
    {
        if (frame?.IsFrozen == true && plainMargins is not null && plainMarginMasks is not null
            && plainMarginCapture == capture && plainMarginMasks.SequenceEqual(masks))
            return plainMargins;

        var margins = SubtitleLayout.FindPlainMargins(capture, masks, pixels);
        if (frame?.IsFrozen == true)
        {
            plainMarginCapture = capture;
            plainMarginMasks = (Drawing.Rectangle[])masks.Clone();
            plainMargins = margins;
        }
        else plainMarginMasks = plainMargins = null;
        return margins;
    }

    private static Border Badge(int number, int sourceIndex)
    {
        var text = new TextBlock { Text = number.ToString(CultureInfo.InvariantCulture), FontSize = 11,
            FontWeight = FontWeights.Bold, Foreground = Brushes.White, TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center };
        return new Border { Tag = sourceIndex, Uid = "association-badge",
            Background = new SolidColorBrush(Color.FromRgb(35, 75, 120)), CornerRadius = new CornerRadius(8),
            Child = text, IsHitTestVisible = false };
    }

    private static string SafeThaiTranslation(string value)
    {
        static bool IsJapanese(char character) => character is >= '\u3040' and <= '\u30ff'
            or >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff';
        bool removedJapanese = value.Any(IsJapanese);
        var text = new string(value.Where(character => !IsJapanese(character)).ToArray()).Trim();
        return text.Length > 0 && (text.Any(char.IsLetterOrDigit) || !removedJapanese)
            ? text : "\u0e41\u0e1b\u0e25\u0e44\u0e21\u0e48\u0e2a\u0e33\u0e40\u0e23\u0e47\u0e08";
    }

    internal static IEnumerable<double> CaptionFontSizes(int sourceHeight, double scaleY)
    {
        double largest = Math.Clamp(Math.Round(sourceHeight * scaleY * 0.65), 10, 24);
        for (double size = largest; size >= 8; size -= 1) yield return size;
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

    internal static bool WrapWords(TextBlock text, IReadOnlyList<string> words, double width,
        Dictionary<string, double>? lineWidths = null)
    {
        var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
        var culture = text.Language.GetEquivalentCulture();
        var flowDirection = text.FlowDirection;
        double fontSize = text.FontSize;
        var foreground = text.Foreground;
        var numberSubstitution = new NumberSubstitution(NumberSubstitution.GetCultureSource(text),
            NumberSubstitution.GetCultureOverride(text), NumberSubstitution.GetSubstitution(text));
        var formattingMode = TextOptions.GetTextFormattingMode(text);
        double pixelsPerDip = VisualTreeHelper.GetDpi(text).PixelsPerDip;
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
                string candidate = line.Trim();
                if (lineWidths is null || !lineWidths.TryGetValue(candidate, out double measured))
                {
                    measured = new FormattedText(candidate, culture, flowDirection, typeface, fontSize,
                        foreground, numberSubstitution, formattingMode, pixelsPerDip).WidthIncludingTrailingWhitespace;
                    if (lineWidths is not null) lineWidths[candidate] = measured;
                }
                double remaining = width - measured;
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
        if (mask.Contains(caption)) return MaskBrush(mask, capture, pixels);
        long sampleUpperBound = ((long)caption.Width + 1) / 2 * (((long)caption.Height + 1) / 2);
        int samples = 0, bright = 0, minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
        long totalR = 0, totalG = 0, totalB = 0;
        for (int y = caption.Top; y < caption.Bottom; y += 2)
            for (int x = caption.Left; x < caption.Right; x += 2)
            {
                if (mask.Contains(x, y)) continue;
                int offset = ((y - capture.Y) * capture.Width + x - capture.X) * 4;
                int b = pixels[offset], g = pixels[offset + 1], r = pixels[offset + 2];
                if (r >= 232 && g >= 232 && b >= 232) bright++;
                minR = Math.Min(minR, r); minG = Math.Min(minG, g); minB = Math.Min(minB, b);
                maxR = Math.Max(maxR, r); maxG = Math.Max(maxG, g); maxB = Math.Max(maxB, b);
                totalR += r; totalG += g; totalB += b; samples++;
                // Once over 5% of the full sample grid is nonbright, the 95%-white exception is impossible.
                if ((maxR - minR > 32 || maxG - minG > 32 || maxB - minB > 32)
                    && 20L * (samples - bright) > sampleUpperBound) return null;
            }
        if (samples == 0 || bright >= samples * 0.95) return Brushes.White;
        // ponytail: keep captions in a flat bubble; region-aware inpainting is needed for text on artwork.
        if (maxR - minR > 32 || maxG - minG > 32 || maxB - minB > 32) return null;
        var brush = new SolidColorBrush(Color.FromRgb((byte)(totalR / samples), (byte)(totalG / samples), (byte)(totalB / samples)));
        brush.Freeze();
        return brush;
    }

    private Brush MaskBrushForFrame(Drawing.Rectangle mask, Drawing.Rectangle capture,
        byte[]? pixels, BitmapSource? frame)
    {
        if (frame is null || !frame.IsFrozen) return MaskBrush(mask, capture, pixels);
        if (!ReferenceEquals(maskFrame, frame) || maskCapture != capture)
        {
            maskFrame = frame;
            maskCapture = capture;
            maskCache.Clear();
        }
        if (!maskCache.TryGetValue(mask, out var brush))
            maskCache[mask] = brush = MaskBrush(mask, capture, pixels);
        return brush;
    }

    internal static Brush MaskBrush(Drawing.Rectangle mask, Drawing.Rectangle capture, byte[]? pixels)
    {
        if (pixels is null) return Brushes.White;
        var fill = new byte[checked(mask.Width * mask.Height * 4)];
        var above = Sample(mask.Left + mask.Width / 2 - 6, mask.Left + mask.Width / 2 + 6, mask.Top - 8);
        var below = Sample(mask.Left + mask.Width / 2 - 6, mask.Left + mask.Width / 2 + 6, mask.Bottom + 8);
        for (int y = 0; y < mask.Height; y++)
        {
            int sourceY = Math.Clamp(mask.Top + y, capture.Top, capture.Bottom - 1);
            var left = Sample(mask.Left - 14, mask.Left - 2, sourceY);
            var right = Sample(mask.Right + 2, mask.Right + 14, sourceY);
            if (left is null && right is null)
            {
                left = above ?? below ?? Colors.White;
                right = below ?? above ?? Colors.White;
            }
            else { left ??= right; right ??= left; }
            if (above is Color top && below is Color bottom && Similar(top, bottom)
                && left is Color leftColor && right is Color rightColor)
            {
                bool leftMatches = Similar(leftColor, top) && Similar(leftColor, bottom);
                bool rightMatches = Similar(rightColor, top) && Similar(rightColor, bottom);
                if (leftMatches && !rightMatches) right = leftColor;
                else if (rightMatches && !leftMatches) left = rightColor;
            }
            for (int x = 0; x < mask.Width; x++)
            {
                double t = (x + 0.5) / mask.Width;
                int offset = (y * mask.Width + x) * 4;
                fill[offset] = (byte)Math.Round(left!.Value.B * (1 - t) + right!.Value.B * t);
                fill[offset + 1] = (byte)Math.Round(left.Value.G * (1 - t) + right.Value.G * t);
                fill[offset + 2] = (byte)Math.Round(left.Value.R * (1 - t) + right.Value.R * t);
                fill[offset + 3] = 255;
            }
        }
        var texture = BitmapSource.Create(mask.Width, mask.Height, 96, 96, PixelFormats.Bgra32, null, fill, mask.Width * 4);
        texture.Freeze();
        var brush = new ImageBrush(texture) { Stretch = Stretch.Fill };
        brush.Freeze();
        return brush;

        static bool Similar(Color first, Color second) => Math.Abs(first.R - second.R) <= 32
            && Math.Abs(first.G - second.G) <= 32 && Math.Abs(first.B - second.B) <= 32;

        Color? Sample(int startX, int endX, int centerY)
        {
            startX = Math.Max(startX, capture.Left);
            endX = Math.Min(endX, capture.Right);
            if (startX >= endX || centerY < capture.Top - 4 || centerY >= capture.Bottom + 4) return null;
            int top = Math.Max(capture.Top, centerY - 4), bottom = Math.Min(capture.Bottom, centerY + 5);
            int brightest = 0;
            for (int sy = top; sy < bottom; sy++)
                for (int sx = startX; sx < endX; sx++)
                {
                    int offset = ((sy - capture.Top) * capture.Width + sx - capture.Left) * 4;
                    brightest = Math.Max(brightest, (pixels[offset] + pixels[offset + 1] + pixels[offset + 2]) / 3);
                }
            long b = 0, g = 0, r = 0;
            int count = 0;
            for (int sy = top; sy < bottom; sy++)
                for (int sx = startX; sx < endX; sx++)
                {
                    int offset = ((sy - capture.Top) * capture.Width + sx - capture.Left) * 4;
                    if ((pixels[offset] + pixels[offset + 1] + pixels[offset + 2]) / 3 < brightest - 24) continue;
                    b += pixels[offset]; g += pixels[offset + 1]; r += pixels[offset + 2]; count++;
                }
            return count == 0 ? null : Color.FromRgb((byte)(r / count), (byte)(g / count), (byte)(b / count));
        }
    }

    internal static Brush ForegroundBrush(Brush background)
    {
        Color? color = background is SolidColorBrush solid ? solid.Color : null;
        if (background is ImageBrush { ImageSource: BitmapSource image })
        {
            var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var pixel = new byte[4];
            bgra.CopyPixels(new Int32Rect(image.PixelWidth / 2, image.PixelHeight / 2, 1, 1), pixel, 4, 0);
            color = Color.FromRgb(pixel[2], pixel[1], pixel[0]);
        }
        if (color is { } sampled)
        {
            double luminance = (0.299 * sampled.R) + (0.587 * sampled.G) + (0.114 * sampled.B);
            if (luminance < 145) return Brushes.White;
        }
        return Brushes.Black;
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
