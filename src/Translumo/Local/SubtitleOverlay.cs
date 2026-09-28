using System;
using System.Buffers;
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
using System.Windows.Media.Effects;
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
    private const int MaxCaptionRegions = 256;
    private const int MaxCaptionLength = 1_024;
    private const long MaxTextLayoutWork = 1_000_000;
    private const long MaxCoverPixels = 4_000_000;
    private const int MaxPlacementChecks = 16_384;
    private const long MaxBackgroundPixelSamples = 32_000_000;
    private sealed class RenderWorkBudget
    {
        private int placementChecks;
        private long backgroundPixelSamples;
        private long bubblePixels;
        private readonly long maxBubblePixels;

        public RenderWorkBudget(long capturePixels)
        {
            // A large captured view needs room to qualify all its bubbles, including later results.
            // ponytail: one view's pixels with a fixed ceiling; denser views retain footprint fallback.
            maxBubblePixels = Math.Clamp(capturePixels, MaxCoverPixels, MaxBackgroundPixelSamples);
        }

        public bool TryBubble(int pixels) => (bubblePixels += pixels) <= maxBubblePixels;

        public void CheckPlacement()
        {
            if (++placementChecks > MaxPlacementChecks) throw new SubtitleLayoutException();
        }

        public void SampleBackgroundRow(int width)
        {
            backgroundPixelSamples += width;
            if (backgroundPixelSamples > MaxBackgroundPixelSamples) throw new SubtitleLayoutException();
        }
    }
    private readonly Canvas canvas = new() { ClipToBounds = true, IsHitTestVisible = false };
    private UIElement[]? rollbackChildren;
    private BitmapSource? layoutFrame;
    private byte[]? layoutPixels;
    private readonly Dictionary<(string Text, double Size, double Width, string Typeface, FontWeight Weight), string?> wrappedCache = new();
    private BitmapSource? maskFrame;
    private Drawing.Rectangle maskCapture;
    private readonly Dictionary<Drawing.Rectangle, Brush> maskCache = new();
    private readonly Dictionary<(Drawing.Rectangle Source, Drawing.Rectangle Mask),
        (Brush Patch, Drawing.Rectangle Bounds, Brush Caption, Color Background, SourceCoverPlan? Plan)?> sourceCoverCache = new();
    private readonly Dictionary<(Drawing.Rectangle Source, Drawing.Rectangle Mask), SourceCoverPlan?> sourcePlanCache = new();
    private readonly Dictionary<Drawing.Rectangle, DisplayLettering?> displayCache = new();
    private Drawing.Rectangle plainMarginCapture;
    private Drawing.Rectangle[]? plainMarginMasks;
    private Drawing.Rectangle[]? plainMargins;
    private BitmapSource? captionFrame;
    private Drawing.Rectangle[]? captionSources;
    private DisplayLettering?[]? captionDisplays;
    private int captionPadding;
    private (Drawing.Rectangle Capture, SubtitleStyle Style, bool JapaneseToThai,
        double ScaleX, double ScaleY, CaptionStyleProfile CaptionStyle) captionOptions;
    private sealed record CachedCaption(string Translation, string SourceText, Drawing.Rectangle Source,
        Border? Caption, Drawing.Rectangle Bounds, Border? Badge, Drawing.Rectangle BadgeBounds,
        double FontSize, string DisplayText, SourceCoverPlan[]? NeighborCovers = null);
    private readonly Dictionary<int, CachedCaption> captionCache = new();
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
        displayCache.Clear();
        layoutPixels = null;
        captionFrame = null;
        captionSources = null;
        captionDisplays = null;
        wrappedCache.Clear();
        maskCache.Clear();
        sourceCoverCache.Clear();
        sourcePlanCache.Clear();
        plainMarginMasks = plainMargins = null;
        captionCache.Clear();
        Hide();
    }

    internal int StyledHeadingCount => canvas.Children.OfType<Border>()
        .Count(border => border.Child is TextBlock { Tag: SourceCoverPlan });

    internal void ConfirmRender() => rollbackChildren = null;

    internal Task<bool> UnderlyingVisualPixelsMatchAsync(Drawing.Rectangle capture, BitmapSource? previous,
        BitmapSource? current, CancellationToken token)
    {
        if (!Dispatcher.CheckAccess() || !IsVisible || canvas.Children.Count == 0
            || previous is null || current is null || !previous.IsFrozen || !current.IsFrozen
            || previous.Format != PixelFormats.Bgra32
            || current.Format != PixelFormats.Bgra32 || previous.PixelWidth != capture.Width
            || previous.PixelHeight != capture.Height || current.PixelWidth != capture.Width
            || current.PixelHeight != capture.Height)
            return Task.FromResult(false);

        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        if (transform is null || transform.Value.M11 <= 0 || transform.Value.M22 <= 0
            || transform.Value.M11 != captionOptions.ScaleX || transform.Value.M22 != captionOptions.ScaleY)
            return Task.FromResult(false);
        var desktop = Forms.SystemInformation.VirtualScreen;
        var rectangles = new List<Int32Rect>(canvas.Children.Count);
        foreach (var child in canvas.Children)
        {
            if (child is not Border border || border.Visibility != Visibility.Visible) return Task.FromResult(false);
            double left = Canvas.GetLeft(border), top = Canvas.GetTop(border);
            if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(border.Width)
                || !double.IsFinite(border.Height) || border.Width <= 0 || border.Height <= 0)
                return Task.FromResult(false);
            var pixels = new Drawing.Rectangle(
                (int)Math.Round(left / transform.Value.M11) + desktop.Left,
                (int)Math.Round(top / transform.Value.M22) + desktop.Top,
                (int)Math.Round(border.Width / transform.Value.M11),
                (int)Math.Round(border.Height / transform.Value.M22));
            if (pixels.Width <= 0 || pixels.Height <= 0 || !capture.Contains(pixels))
                return Task.FromResult(false);
            if (border.Child is null && border.Tag is int) pixels.Inflate(14, 12);
            pixels.Intersect(capture);
            pixels.Offset(-capture.X, -capture.Y);
            rectangles.Add(new Int32Rect(pixels.X, pixels.Y, pixels.Width, pixels.Height));
        }
        return Task.Run(() => PixelsMatch(previous, current, rectangles, token), token);

        static bool PixelsMatch(BitmapSource first, BitmapSource second, IReadOnlyList<Int32Rect> regions,
            CancellationToken cancellationToken)
        {
            int capacity = regions.Max(region => checked(region.Width * region.Height * 4));
            var firstPixels = ArrayPool<byte>.Shared.Rent(capacity);
            var secondPixels = ArrayPool<byte>.Shared.Rent(capacity);
            try
            {
                foreach (var region in regions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int stride = checked(region.Width * 4);
                    int length = checked(stride * region.Height);
                    first.CopyPixels(region, firstPixels, stride, 0);
                    second.CopyPixels(region, secondPixels, stride, 0);
                    if (!firstPixels.AsSpan(0, length).SequenceEqual(secondPixels.AsSpan(0, length))) return false;
                }
                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(firstPixels);
                ArrayPool<byte>.Shared.Return(secondPixels);
            }
        }
    }

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
        captionDisplays = null;
        captionCache.Clear();
        sourceCoverCache.Clear();
        if (rollbackChildren is null) return;
        canvas.Children.Clear();
        foreach (var child in rollbackChildren) canvas.Children.Add(child);
        rollbackChildren = null;
        if (canvas.Children.Count == 0) Hide(); else Show();
    }

    public void Render(Drawing.Rectangle captureBounds, IReadOnlyList<TextRegion> regions,
        IReadOnlyList<string> translations, SubtitleStyle style, int padding = 6, BitmapSource? frame = null,
        bool japaneseToThai = false, bool allowMissingTranslations = false,
        CaptionStyleProfile? captionStyle = null, CancellationToken cancellationToken = default)
        => RenderCore(captureBounds, regions, translations, style, padding, frame,
            japaneseToThai, allowMissingTranslations, captionStyle ?? CaptionStyles.ResolveInstalled(new CaptionStyleOptions()),
            cancellationToken);

    private void RenderCore(Drawing.Rectangle captureBounds, IReadOnlyList<TextRegion> regions,
        IReadOnlyList<string> translations, SubtitleStyle style, int padding, BitmapSource? frame,
        bool japaneseToThai, bool allowMissingTranslations, CaptionStyleProfile captionStyle,
        CancellationToken cancellationToken)
    {
        Dispatcher.VerifyAccess();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(translations);
        if (regions.Count != translations.Count)
            throw new ArgumentException("Each recognized text region needs one translation.");
        long textLayoutWork = 0;
        foreach (string? translation in translations)
        {
            int length = translation?.Length ?? 0;
            if (length > MaxCaptionLength || (textLayoutWork += (long)length * length) > MaxTextLayoutWork)
                throw new SubtitleLayoutException();
        }
        if (regions.Count > MaxCaptionRegions) throw new SubtitleLayoutException();
        if (captureBounds.Width <= 0 || captureBounds.Height <= 0 || padding < 0 || padding > 100)
            throw new ArgumentOutOfRangeException(nameof(captureBounds));
        if (style is not SubtitleStyle.Overlay and not SubtitleStyle.Overwrite)
            throw new ArgumentOutOfRangeException(nameof(style));
        if (frame is not null && (frame.PixelWidth != captureBounds.Width || frame.PixelHeight != captureBounds.Height))
            throw new ArgumentException("The captured page must match the captured pixel dimensions.", nameof(frame));
        if (regions.Count == 0) { Clear(); return; }
        if (frame?.IsFrozen == true && layoutFrame?.IsFrozen == true && layoutPixels is not null
            && !ReferenceEquals(layoutFrame, frame) && frame.PixelWidth == layoutFrame.PixelWidth
            && frame.PixelHeight == layoutFrame.PixelHeight)
        {
            var comparison = ArrayPool<byte>.Shared.Rent(layoutPixels.Length);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                converted.CopyPixels(comparison, frame.PixelWidth * 4, 0);
                if (layoutPixels.AsSpan().SequenceEqual(comparison.AsSpan(0, layoutPixels.Length)))
                {
                    if (ReferenceEquals(maskFrame, layoutFrame)) maskFrame = frame;
                    if (ReferenceEquals(captionFrame, layoutFrame)) captionFrame = frame;
                    layoutFrame = frame;
                }
            }
            finally { ArrayPool<byte>.Shared.Return(comparison); }
        }
        if (frame is null || !ReferenceEquals(layoutFrame, frame))
        {
            layoutFrame = frame;
            layoutPixels = null;
            plainMarginMasks = plainMargins = null;
        }

        var (desktop, scaleX, scaleY) = PrepareWindow();
        bool cacheCaptions = frame?.IsFrozen == true;
        var options = (captureBounds, style, japaneseToThai, scaleX, scaleY, captionStyle);
        var sources = regions.Select(region => {
            var clipped = Drawing.Rectangle.Intersect(region.Bounds,
                new Drawing.Rectangle(0, 0, captureBounds.Width, captureBounds.Height));
            clipped.Offset(captureBounds.Location);
            return clipped;
        }).ToArray();
        bool sameCaptionInputs = cacheCaptions && captionSources is not null
            && captionSources.SequenceEqual(sources) && captionOptions == options;
        bool sameCaptionFrame = sameCaptionInputs && captionPadding == padding && ReferenceEquals(captionFrame, frame);
        if (!sameCaptionInputs)
        {
            captionCache.Clear();
            captionDisplays = null;
            wrappedCache.Clear();
            sourceCoverCache.Clear();
            sourcePlanCache.Clear();
            displayCache.Clear();
        }
        captionFrame = cacheCaptions ? frame : null;
        captionSources = cacheCaptions ? (Drawing.Rectangle[])sources.Clone() : null;
        captionPadding = padding;
        captionOptions = options;

        var masks = sources.Select(source => {
            if (source.Width <= 0 || source.Height <= 0) return Drawing.Rectangle.Empty;
            source.Inflate(padding, padding);
            return Drawing.Rectangle.Intersect(source, captureBounds);
        }).ToArray();
        if (masks.Sum(mask => (long)mask.Width * mask.Height) > MaxCoverPixels)
            throw new SubtitleLayoutException();
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
        var sourceCovers = new (Brush Patch, Drawing.Rectangle Bounds, Brush Caption, Color Background, SourceCoverPlan? Plan)?[masks.Length];
        var workBudget = new RenderWorkBudget((long)captureBounds.Width * captureBounds.Height);
        var displayStyles = new DisplayLettering?[sources.Length];

        if (style == SubtitleStyle.Overwrite && backgroundPixels is not null
            && captionStyle.Role is CaptionRole.Auto or CaptionRole.Emphasis)
        {
            if (frame is not null) EnsureMaskCache(frame, captureBounds);
            for (int i = 0; i < sources.Length; i++)
            {
                if (sources[i].Width <= 0 || sources[i].Height <= 0 || string.IsNullOrWhiteSpace(translations[i])) continue;
                DisplayLettering? lettering = null;
                if (frame?.IsFrozen != true || !displayCache.TryGetValue(sources[i], out lettering)
                    || lettering is null && !sameCaptionFrame
                    || lettering is not null && !lettering.Matches(backgroundPixels, captureBounds.Width))
                {
                    var local = sources[i]; local.Offset(-captureBounds.X, -captureBounds.Y);
                    lettering = DisplayLettering.TryCreate(backgroundPixels, captureBounds.Width,
                        captureBounds.Height, local, cancellationToken);
                    if (frame?.IsFrozen == true) displayCache[sources[i]] = lettering;
                }
                displayStyles[i] = lettering;
            }
        }

        bool displayProtectionChanged = sameCaptionFrame && (captionDisplays is null || !captionDisplays.SequenceEqual(displayStyles));
        HashSet<int>? protectionInvalidated = null;
        if (displayProtectionChanged)
        {
            // Pending passages protect their rectangles; ready headings protect their qualified lettering masks.
            var changedAreas = new List<Drawing.Rectangle>();
            for (int i = 0; i < sources.Length; i++)
            {
                if (captionDisplays is not null && ReferenceEquals(captionDisplays[i]?.Cover, displayStyles[i]?.Cover)) continue;
                var changed = sources[i];
                changed.Offset(-captureBounds.X, -captureBounds.Y);
                if (captionDisplays?[i]?.Cover is { } previous) changed = Drawing.Rectangle.Union(changed, previous.FootprintBounds);
                if (displayStyles[i]?.Cover is { } current) changed = Drawing.Rectangle.Union(changed, current.FootprintBounds);
                changedAreas.Add(changed);
            }
            for (int i = 0; i < sources.Length; i++)
            {
                sourcePlanCache.TryGetValue((sources[i], masks[i]), out var plan);
                var local = masks[i]; local.Offset(-captureBounds.X, -captureBounds.Y);
                var area = plan?.FootprintBounds ?? displayStyles[i]?.Cover.FootprintBounds ?? local;
                if (!changedAreas.Any(area.IntersectsWith)) continue;
                if (captionCache.TryGetValue(i, out var previous)
                    && previous.SourceText == regions[i].Text
                    && previous.Translation == (japaneseToThai && style == SubtitleStyle.Overwrite
                        ? SafeThaiTranslation(translations[i]) : translations[i]))
                    (protectionInvalidated ??= new()).Add(i);
                captionCache.Remove(i);
            }
            sourceCoverCache.Clear();
        }
        captionDisplays = cacheCaptions ? displayStyles : null;

        if (style == SubtitleStyle.Overwrite)
            for (int i = 0; i < masks.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (masks[i].Width > 0 && masks[i].Height > 0
                    && !string.IsNullOrWhiteSpace(translations[i]))
                {
                    sourceCovers[i] = SourceCoverForFrame(sources[i], masks[i], captureBounds, backgroundPixels, frame,
                        sources, displayStyles, workBudget, cancellationToken, displayStyles[i]);
                    if (sourceCovers[i] is { } sourceCover)
                        visuals.Add((new Border { Background = sourceCover.Patch, Tag = i }, sourceCover.Bounds));
                }
            }

        if (!allowMissingTranslations && japaneseToThai && style == SubtitleStyle.Overwrite && frame is not null
            && translations.All(translation => !string.IsNullOrWhiteSpace(translation))
            && backgroundPixels is not null && TryRenderVerticalIndex(captureBounds, regions, translations,
                sources, backgroundPixels, desktop, scaleX, scaleY, visuals, captionStyle, cancellationToken))
            return;

        for (int i = 0; i < regions.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sources[i].Width <= 0 || sources[i].Height <= 0) continue;
            if (string.IsNullOrWhiteSpace(translations[i]))
            {
                if (string.IsNullOrWhiteSpace(regions[i].Text)
                    || allowMissingTranslations && style == SubtitleStyle.Overwrite) continue;
                throw new InvalidOperationException("The local model returned an empty translation; subtitles were not shown.");
            }
            var source = sources[i];
            var cover = masks[i];
            var permittedLocalArea = PermittedCaptionArea(source, captureBounds);
            var placementArea = sourceCovers[i] is { Plan: { Classification: SourceCoverClass.Plain } } interior
                && interior.Bounds.Contains(source) ? interior.Bounds : source;
            bool hasContainer = placementArea != source;
            if (hasContainer)
            {
                // A connected balloon can hold separate passages; keep each caption on its source's side.
                foreach (var other in sources)
                {
                    if (other == source || other.Width <= 0 || other.Height <= 0
                        || !placementArea.IntersectsWith(other)) continue;
                    int left = placementArea.Left, top = placementArea.Top;
                    int right = placementArea.Right, bottom = placementArea.Bottom;
                    if (other.Left < source.Right && other.Right > source.Left)
                    {
                        if (other.Bottom <= source.Top) top = Math.Max(top, other.Bottom);
                        else if (other.Top >= source.Bottom) bottom = Math.Min(bottom, other.Top);
                    }
                    if (other.Top < source.Bottom && other.Bottom > source.Top)
                    {
                        if (other.Right <= source.Left) left = Math.Max(left, other.Right);
                        else if (other.Left >= source.Right) right = Math.Min(right, other.Left);
                    }
                    placementArea = Drawing.Rectangle.FromLTRB(left, top, right, bottom);
                }
                permittedLocalArea = placementArea;
            }
            // Borrow only pixels already painted as the same solid white paper. Bounds alone are not coverage.
            var neighborCovers = hasContainer && sourceCovers[i] is
                { Background: var ownBackground, Plan.Classification: SourceCoverClass.Plain }
                && ownBackground == Colors.White
                ? sourceCovers.Where((candidate, index) => index != i && candidate is
                    { Background: var background, Plan.Classification: SourceCoverClass.Plain } other
                    && background == Colors.White && other.Bounds.IntersectsWith(placementArea))
                    .Select(candidate => candidate!.Value.Plan!).ToArray()
                : Array.Empty<SourceCoverPlan>();
            var appearance = CaptionAppearance(CaptionStyles.ForRegion(captionStyle, regions[i]),
                sourceCovers[i]?.Background,
                captionStyle.Role == CaptionRole.Auto && captionStyle.Foreground == Colors.Black
                    && backgroundPixels is not null && sourceCovers[i] is { } qualified
                    ? SourceInk(source, captureBounds, backgroundPixels, qualified.Background) : null);
            string translation = japaneseToThai && style == SubtitleStyle.Overwrite
                ? SafeThaiTranslation(translations[i]) : translations[i];
            if (displayStyles[i] is { } display && ReferenceEquals(sourceCovers[i]?.Plan, display.Cover))
            {
                if (sameCaptionInputs && captionCache.TryGetValue(i, out var headingCache)
                    && headingCache.Translation == translation && headingCache.SourceText == regions[i].Text
                    && headingCache.Source == source && headingCache.Caption?.Child is TextBlock cachedHeading
                    && ReferenceEquals(cachedHeading.Tag, display.Cover)
                    && !placed.Any(other => other.IntersectsWith(headingCache.Bounds)))
                {
                    placed.Add(headingCache.Bounds); visuals.Add((headingCache.Caption, headingCache.Bounds));
                    continue;
                }
                var headingStyle = captionStyle with {
                    Weight = captionStyle.Weight == FontWeights.Normal ? FontWeights.SemiBold : captionStyle.Weight,
                    Role = captionStyle.Foreground == Colors.Black ? CaptionRole.Auto : CaptionRole.Emphasis
                };
                for (double size = Math.Min(72, Math.Floor(source.Height * scaleY * 0.72)); size >= 12; size--)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var text = display.CreateText(translation, headingStyle, size);
                    double width = Math.Max(1, source.Width * scaleX - 4);
                    text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    if (text.DesiredSize.Width > width || text.DesiredSize.Height > source.Height * scaleY - 4) continue;
                    int height = (int)Math.Ceiling((text.DesiredSize.Height + 4) / scaleY);
                    var bounds = new Drawing.Rectangle(source.Left, source.Top + (source.Height - height) / 2,
                        source.Width, height);
                    if (sources.Where((_, index) => index != i).Concat(placed).Any(other => other.IntersectsWith(bounds))) break;
                    var heading = new Border { Tag = i, Background = Brushes.Transparent,
                        Padding = new Thickness(2), Child = text };
                    placed.Add(bounds); visuals.Add((heading, bounds));
                    if (cacheCaptions) captionCache[i] = new CachedCaption(translation, regions[i].Text, source,
                        heading, bounds, null, Drawing.Rectangle.Empty, size, translation);
                    break;
                }
                if (visuals.Any(visual => Equals(visual.Border.Tag, i) && visual.Border.Child is TextBlock)) continue;
            }
            if (cacheCaptions && captionCache.TryGetValue(i, out var previous)
                && previous.Translation == translation && previous.SourceText == regions[i].Text
                && previous.Source == source
                && (previous.Caption is null || !placed.Any(other => other.IntersectsWith(previous.Bounds)))
                && neighborCovers.SequenceEqual(previous.NeighborCovers ?? Array.Empty<SourceCoverPlan>()))
            {
                if (sameCaptionFrame && (previous.Caption is null || displayProtectionChanged && previous.Badge is not null))
                {
                    marginIndices.Add(i);
                    continue;
                }
                else if (sameCaptionFrame)
                {
                    placed.Add(previous.Bounds);
                    visuals.Add((previous.Caption!, previous.Bounds));
                    if (previous.Badge is not null) visuals.Add((previous.Badge, previous.BadgeBounds));
                    continue;
                }
                else if (previous.Caption is not null && previous.Badge is null
                    && (!hasContainer || placementArea.Contains(previous.Bounds)))
                {
                    var cachedBackground = BackgroundBrush(previous.Bounds, cover, captureBounds, backgroundPixels,
                        frame, style, sourceCovers[i]?.Caption, sourceCovers[i]?.Bounds,
                        sourceCovers[i]?.Background, workBudget, cancellationToken, sourceCovers[i]?.Plan);
                    if (cachedBackground is not null)
                    {
                        var cachedText = new TextBlock {
                            Text = previous.DisplayText, FontFamily = appearance.Font,
                            FontWeight = appearance.Weight, FontSize = previous.FontSize,
                            Language = XmlLanguage.GetLanguage(translation.Any(character => character is >= '\u0e00' and <= '\u0e7f')
                                ? "th-TH" : "en-US"),
                            Foreground = appearance.Foreground, Effect = appearance.Effect,
                            TextWrapping = translation.Any(character => character is >= '\u0e00' and <= '\u0e7f')
                                ? TextWrapping.NoWrap : TextWrapping.Wrap,
                            TextAlignment = TextAlignment.Center,
                            TextEffects = ((TextBlock)previous.Caption!.Child).TextEffects,
                            LineHeight = ((TextBlock)previous.Caption!.Child).LineHeight,
                            LineStackingStrategy = ((TextBlock)previous.Caption.Child).LineStackingStrategy,
                            TextTrimming = TextTrimming.None
                        };
                        var cachedCaption = new Border { Tag = i, Background = cachedBackground,
                            Padding = previous.Caption!.Padding,
                            Child = cachedText };
                        placed.Add(previous.Bounds);
                        visuals.Add((cachedCaption, previous.Bounds));
                        captionCache[i] = previous with { Caption = cachedCaption, Badge = null,
                            BadgeBounds = Drawing.Rectangle.Empty };
                        continue;
                    }
                }
            }
            if (cacheCaptions)
            {
                if (protectionInvalidated?.Contains(i) == true) captionCache.Remove(i);
                else foreach (int stale in captionCache.Keys.Where(index => index >= i).ToArray())
                    captionCache.Remove(stale);
            }
            var blockers = sources.Where((candidate, index) => index != i
                    && candidate.Width > 0 && candidate.Height > 0)
                .Concat(placed).ToArray();
            Border? caption = null;
            Drawing.Rectangle? position = null;
            double selectedFontSize = 0;
            string displayText = translation;
            bool backgroundRejected = false;
            bool thai = translation.Any(character => character is >= '\u0e00' and <= '\u0e7f');
            var words = thai ? ThaiWords(translation) : null;
            int largestFont = hasContainer
                ? Math.Max(24, (int)Math.Min(72, placementArea.Height * scaleY / 2)) : 24;
            // Keep caption layout independent from source-cover padding.
            foreach (double fontSize in Enumerable.Range(0, largestFont - 7).Select(step => (double)(largestFont - step))
                         .Where(size => !japaneseToThai && !thai || size >= 12))
            {
                cancellationToken.ThrowIfCancellationRequested();
                double bestScore = double.PositiveInfinity;
                double maxWidthDip = Math.Min(captureBounds.Width * scaleX,
                    hasContainer ? placementArea.Width * scaleX
                        : Math.Min(320, Math.Max(80, source.Width * scaleX * 2.5)));
                var lineWidths = new Dictionary<string, double>();
                if (hasContainer && words is not null && sourceCovers[i]?.Plan is { } bubblePlan)
                {
                    var bubbleText = new TextBlock {
                        Text = translation, FontFamily = appearance.Font, FontWeight = appearance.Weight,
                        FontSize = fontSize, Language = XmlLanguage.GetLanguage("th-TH"),
                        Foreground = appearance.Foreground, Effect = appearance.Effect,
                        TextWrapping = TextWrapping.NoWrap, TextAlignment = TextAlignment.Center
                    };
                    if (FitBubbleLines(bubbleText, words, placementArea, captureBounds, bubblePlan, neighborCovers, blockers,
                            scaleX, scaleY, lineWidths, workBudget, cancellationToken, out var bubbleBounds))
                    {
                        caption = new Border { Tag = i, Background = Brushes.Transparent, Child = bubbleText };
                        position = bubbleBounds;
                        selectedFontSize = fontSize;
                        displayText = bubbleText.Text;
                    }
                    // Keep the contour fit unless a centered paragraph fits at the same readable size.
                    if (fontSize > 24 && caption is null) continue;
                }
                foreach (double widthDip in new[] { source.Width * scaleX, (source.Width + 12) * scaleX,
                    source.Width * scaleX * 1.5, source.Width * scaleX * 2,
                    permittedLocalArea.Width * scaleX, maxWidthDip,
                    maxWidthDip * 0.65,
                    maxWidthDip * 0.8 }
                    .Select(width => Math.Min(width, maxWidthDip)).Distinct().Order())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var text = new TextBlock {
                        Text = translation, FontFamily = appearance.Font, FontWeight = appearance.Weight, FontSize = fontSize,
                        Language = XmlLanguage.GetLanguage(thai ? "th-TH" : "en-US"),
                        Foreground = appearance.Foreground, Effect = appearance.Effect,
                        TextWrapping = thai ? TextWrapping.NoWrap : TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.None
                    };
                    int width = Math.Min(captureBounds.Width, (int)Math.Ceiling(widthDip / scaleX));
                    double insetX = (hasContainer ? 2 : 6) * scaleX, insetY = (hasContainer ? 2 : 6) * scaleY;
                    double contentWidth = Math.Max(1, width * scaleX - insetX * 2);
                    if (words is not null)
                    {
                        var key = (translation, fontSize, contentWidth, appearance.Font.Source, appearance.Weight);
                        if (!wrappedCache.TryGetValue(key, out string? wrapped))
                        {
                            wrapped = WrapWords(text, words, contentWidth, lineWidths, cancellationToken) ? text.Text : null;
                            wrappedCache[key] = wrapped;
                        }
                        if (wrapped is null) continue;
                        text.Text = wrapped;
                    }
                    else
                    {
                        var measured = new FormattedText(translation, text.Language.GetEquivalentCulture(),
                            text.FlowDirection, new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
                            fontSize, text.Foreground, VisualTreeHelper.GetDpi(text).PixelsPerDip) {
                            MaxTextWidth = contentWidth, Trimming = TextTrimming.None
                        };
                        // TextBlock clamps DesiredSize to its constraint even when a single glyph overflows.
                        if (measured.WidthIncludingTrailingWhitespace > contentWidth) continue;
                    }
                    text.Measure(new Size(contentWidth, double.PositiveInfinity));
                    int height = (int)Math.Ceiling((text.DesiredSize.Height + insetY * 2) / scaleY);
                    double maxHeight = Math.Min(captureBounds.Height,
                        hasContainer ? placementArea.Height
                            : Math.Max(72, source.Height * (style == SubtitleStyle.Overlay ? 2.4 : 1.8)));
                    if (height > maxHeight || !hasContainer && height > width * 1.6) continue;
                    Brush? candidateBackground = backgroundPixels is null ? Brushes.White : null;
                    var candidatePosition = SubtitleLayout.Place(placementArea, new Drawing.Size(width, height), captureBounds,
                        blockers, style == SubtitleStyle.Overlay, 6, frame is null ? null
                            : candidate => {
                                workBudget.CheckPlacement();
                                if (style == SubtitleStyle.Overwrite && !permittedLocalArea.Contains(candidate))
                                    return false;
                                if (style == SubtitleStyle.Overwrite
                                    && !hasContainer
                                    && Math.Abs(candidate.Left + candidate.Width / 2 - (source.Left + source.Width / 2)) > Math.Max(32, source.Width / 2))
                                    return false;
                                candidateBackground = BackgroundBrush(candidate, cover, captureBounds, backgroundPixels,
                                    frame, style, sourceCovers[i]?.Caption, sourceCovers[i]?.Bounds,
                                    sourceCovers[i]?.Background, workBudget, cancellationToken, sourceCovers[i]?.Plan);
                                if (candidateBackground is null && thai
                                    && sourceCovers[i]?.Plan is { Classification: SourceCoverClass.Plain } containerPlan
                                    && LinesFitCover(text, candidate, captureBounds, containerPlan, neighborCovers,
                                        scaleX, scaleY, insetY, workBudget, cancellationToken,
                                        hasContainer ? null : line => BackgroundBrush(line, cover, captureBounds,
                                            backgroundPixels, frame, style, sourceCovers[i]?.Caption,
                                            sourceCovers[i]?.Bounds, sourceCovers[i]?.Background, workBudget,
                                            cancellationToken, containerPlan) is not null))
                                    candidateBackground = Brushes.Transparent;
                                return candidateBackground is not null;
                            });
                    if (candidatePosition is null && style == SubtitleStyle.Overwrite && backgroundPixels is not null)
                        backgroundRejected = true;
                    if (candidatePosition is null) continue;

                    double sourceRatio = (double)source.Width / Math.Max(1, source.Height);
                    double candidateRatio = (double)width / Math.Max(1, height);
                    double centerX = candidatePosition.Value.Left + candidatePosition.Value.Width / 2d
                        - (placementArea.Left + placementArea.Width / 2d);
                    double centerY = candidatePosition.Value.Top + candidatePosition.Value.Height / 2d
                        - (placementArea.Top + placementArea.Height / 2d);
                    double score = centerX * centerX + centerY * centerY
                        + Math.Abs(candidateRatio - sourceRatio) * 100;
                    if (score >= bestScore) continue;
                    bestScore = score;
                    caption = new Border {
                        Tag = i,
                        Background = candidateBackground ?? Brushes.White,
                        Padding = new Thickness(insetX, insetY, insetX, insetY),
                        Child = text
                    };
                    position = candidatePosition;
                    selectedFontSize = fontSize;
                    displayText = text.Text;
                }
                if (caption is not null) break;
            }
            if (caption is null || position is null)
            {
                if ((thai || japaneseToThai) && style == SubtitleStyle.Overwrite && frame is not null)
                {
                    marginIndices.Add(i);
                    if (cacheCaptions) captionCache[i] = new CachedCaption(translation, regions[i].Text, source,
                        null, Drawing.Rectangle.Empty, null, Drawing.Rectangle.Empty, 0, string.Empty, neighborCovers);
                    continue;
                }
                throw new SubtitleLayoutException(backgroundRejected);
            }
            placed.Add(position.Value);
            visuals.Add((caption, position.Value));
            if (cacheCaptions) captionCache[i] = new CachedCaption(translation, regions[i].Text, source,
                caption, position.Value, null, Drawing.Rectangle.Empty, selectedFontSize, displayText, neighborCovers);
        }

        if (marginIndices.Count > 0)
        {
            if (!TryRenderThaiMargin(captureBounds, masks, sources, translations, marginIndices,
                    backgroundPixels!, frame, desktop, scaleX, scaleY, visuals, captionStyle, regions,
                    workBudget, cancellationToken))
            {
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
        double scaleX, double scaleY, List<(Border Border, Drawing.Rectangle Bounds)> visuals,
        CaptionStyleProfile captionStyle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = panel.Left; x < panel.Right; x += 24)
            {
                int offset = ((y - capture.Top) * capture.Width + x - capture.Left) * 4;
                if (pixels[offset] >= 232 && pixels[offset + 1] >= 232 && pixels[offset + 2] >= 232) white++;
                samples++;
            }
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
            cancellationToken.ThrowIfCancellationRequested();
            int i = position < 0 ? title : order[position];
            if (i < 0 || string.IsNullOrWhiteSpace(translations[i])) continue;
            string value = title == i ? "สารบัญ" : SafeThaiTranslation(translations[i]);
            var appearance = CaptionAppearance(title == i && captionStyle.Role == CaptionRole.Auto
                ? CaptionStyles.Resolve(new CaptionStyleOptions(CaptionRole.Emphasis, captionStyle.Typeface),
                    new[] { captionStyle.Typeface })
                : CaptionStyles.ForRegion(captionStyle, regions[i]));
            var box = position < 0
                ? new Drawing.Rectangle(panel.Left + 20, panel.Top + 10, panel.Width - 40, 58)
                : new Drawing.Rectangle(panel.Left + (position < split ? panel.Width / 2 + 8 : 20),
                    bodyTop + (position < split ? position : position - split) * rowHeight,
                    columnWidth, rowHeight);
            TextBlock? text = null;
            foreach (double fontSize in position < 0 ? new[] { 24d } : new[] { 18d, 17d, 16d, 15d, 14d, 13d, 12d })
            {
                var candidate = new TextBlock { FontFamily = appearance.Font, FontWeight = appearance.Weight,
                    FontSize = fontSize, Language = XmlLanguage.GetLanguage("th-TH"),
                    Foreground = appearance.Foreground, Effect = appearance.Effect,
                    TextAlignment = position < 0 ? TextAlignment.Center : TextAlignment.Left };
                double width = (box.Width - 16) * scaleX;
                if (!WrapWords(candidate, ThaiWords(value), width,
                        cancellationToken: cancellationToken)) continue;
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
        BitmapSource? frame, Drawing.Rectangle desktop, double scaleX, double scaleY,
        List<(Border Border, Drawing.Rectangle Bounds)> visuals,
        CaptionStyleProfile captionStyle, IReadOnlyList<TextRegion> regions,
        RenderWorkBudget workBudget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var margins = PlainMarginsForFrame(capture, masks, pixels, frame, workBudget, cancellationToken);
        if (margins.Length == 0) return false;
        var safeMargins = margins.Select(margin => (Margin: margin,
                Background: BackgroundBrush(margin, Drawing.Rectangle.Empty, capture, pixels, null,
                    SubtitleStyle.Overwrite, null, null, null, workBudget, cancellationToken)))
            .Where(candidate => candidate.Background is not null).ToArray();
        if (safeMargins.Length == 0) return false;
        margins = safeMargins.Select(candidate => candidate.Margin).ToArray();
        var placed = new List<(Border Border, Drawing.Rectangle Bounds)>();
        var readingOrder = overflow
            .OrderByDescending(i => sources[i].Left + sources[i].Width / 2)
            .ThenBy(i => sources[i].Top).ToArray();
        var numbers = Enumerable.Range(0, sources.Length)
            .OrderByDescending(i => sources[i].Left + sources[i].Width / 2)
            .ThenBy(i => sources[i].Top)
            .Select((sourceIndex, rank) => (sourceIndex, number: rank + 1))
            .ToDictionary(pair => pair.sourceIndex, pair => pair.number);
        foreach (double fontSize in new[] { 18d, 16d, 14d, 12d })
        // ponytail: fill columns by measured height; arbitrary obstacle packing needs a richer layout.
        foreach (int firstColumn in Enumerable.Range(0, margins.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            placed.Clear();
            int[] tops = margins.Select(margin => visuals
                .Where(visual => visual.Border.Child is TextBlock && margin.IntersectsWith(visual.Bounds))
                .Select(visual => visual.Bounds.Bottom + 1).DefaultIfEmpty(margin.Top).Max()).ToArray();
            bool fits = true;
            int column = firstColumn;
            for (int orderIndex = 0; orderIndex < readingOrder.Length; orderIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int i = readingOrder[orderIndex];
                var appearance = CaptionAppearance(CaptionStyles.ForRegion(captionStyle, regions[i]));
                var margin = margins[column];
                string translation = $"{numbers[i]}. {SafeThaiTranslation(translations[i])}";
                var text = new TextBlock {
                    Text = translation, FontFamily = appearance.Font, FontWeight = appearance.Weight,
                    FontSize = fontSize, Language = XmlLanguage.GetLanguage("th-TH"),
                    Foreground = Brushes.Black,
                    TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Left
                };
                double insetX = 6 * scaleX, insetY = scaleY;
                double contentWidth = margin.Width * scaleX - insetX * 2;
                var key = (translation, fontSize, contentWidth, appearance.Font.Source, appearance.Weight);
                if (!wrappedCache.TryGetValue(key, out string? wrapped))
                {
                    wrapped = WrapWords(text, ThaiWords(translation), contentWidth,
                        cancellationToken: cancellationToken) ? text.Text : null;
                    wrappedCache[key] = wrapped;
                }
                if (wrapped is null) { fits = false; break; }
                text.Text = wrapped;
                text.Measure(new Size(contentWidth, double.PositiveInfinity));
                int height = (int)Math.Ceiling((text.DesiredSize.Height + insetY * 2) / scaleY);
                if (tops[column] + height > margin.Bottom)
                {
                    if (++column < margins.Length) { orderIndex--; continue; }
                    fits = false;
                    break;
                }
                var bounds = new Drawing.Rectangle(margin.Left, tops[column], margin.Width, height);
                if (visuals.Any(visual => visual.Border.Child is TextBlock
                    && visual.Bounds.IntersectsWith(bounds))) { fits = false; break; }
                placed.Add((new Border { Tag = i, Background = Brushes.White,
                    Padding = new Thickness(insetX, insetY, insetX, insetY), Child = text }, bounds));
                tops[column] += height + 1;
            }
            if (!fits) continue;
            visuals.AddRange(placed);
            var badges = new List<(Border Border, Drawing.Rectangle Bounds)>();
            foreach (int i in readingOrder)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = sources[i];
                var badge = Badge(numbers[i], i);
                var badgeBounds = new Drawing.Rectangle(source.Left, source.Top,
                    Math.Min(source.Width, 24), Math.Min(source.Height, 24));
                badges.Add((badge, badgeBounds));
                if (captionCache.TryGetValue(i, out var cached))
                {
                    var marginCaption = placed.Single(item => Equals(item.Border.Tag, i));
                    captionCache[i] = cached with { Caption = marginCaption.Border, Bounds = marginCaption.Bounds,
                        Badge = badge, BadgeBounds = badgeBounds, FontSize = fontSize,
                        DisplayText = ((TextBlock)marginCaption.Border.Child).Text };
                }
            }
            visuals.AddRange(badges);
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
        byte[] pixels, BitmapSource? frame, RenderWorkBudget workBudget, CancellationToken cancellationToken)
    {
        if (frame?.IsFrozen == true && plainMargins is not null && plainMarginMasks is not null
            && plainMarginCapture == capture && plainMarginMasks.SequenceEqual(masks))
            return plainMargins;

        var margins = SubtitleLayout.FindPlainMargins(capture, masks, pixels, cancellationToken,
            candidate => BackgroundBrush(candidate, Drawing.Rectangle.Empty, capture, pixels, null,
                SubtitleStyle.Overwrite, null, null, null, workBudget, cancellationToken) is not null);
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

    private static (FontFamily Font, FontWeight Weight, Brush Foreground, Effect? Effect)
        CaptionAppearance(CaptionStyleProfile style, Color? background = null, Color? sourceInk = null)
    {
        if (sourceInk is { } ink)
            style = style with { Foreground = ink,
                EffectColor = ((SolidColorBrush)ForegroundBrush(new SolidColorBrush(ink))).Color };
        if (style.Foreground == Colors.Black && background is { A: 255 } fill
            && ReferenceEquals(ForegroundBrush(new SolidColorBrush(fill)), Brushes.White))
            style = style with { Foreground = Colors.White, EffectColor = Colors.Black };
        var foreground = new SolidColorBrush(style.Foreground);
        foreground.Freeze();
        Effect? effect = style.Effect switch
        {
            CaptionEffect.Outline => new DropShadowEffect {
                Color = style.EffectColor, BlurRadius = 2, ShadowDepth = 0, Opacity = 1
            },
            CaptionEffect.Shadow => new DropShadowEffect {
                Color = style.EffectColor, BlurRadius = 3, ShadowDepth = 1, Opacity = 1
            },
            _ => null
        };
        effect?.Freeze();
        return (new FontFamily(style.Typeface), style.Weight, foreground, effect);
    }

    private static Color? SourceInk(Drawing.Rectangle source, Drawing.Rectangle capture, byte[] pixels, Color background)
    {
        if (background.A != 255) return null;
        // ponytail: one dominant ink on a qualified fill; multicolor lettering and exact font shapes need richer style detection.
        Span<int> counts = stackalloc int[512];
        Span<int> red = stackalloc int[512], green = stackalloc int[512], blue = stackalloc int[512];
        counts.Clear(); red.Clear(); green.Clear(); blue.Clear();
        int best = 0, samples = 0;
        int step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)source.Width * source.Height / 4096)));
        for (int y = source.Top; y < source.Bottom; y += step)
        for (int x = source.Left; x < source.Right; x += step)
        {
            int offset = ((y - capture.Y) * capture.Width + x - capture.X) * 4;
            int b = pixels[offset], g = pixels[offset + 1], r = pixels[offset + 2];
            if (Math.Max(Math.Abs(r - background.R), Math.Max(Math.Abs(g - background.G),
                Math.Abs(b - background.B))) < 64) continue;
            int bin = (r >> 5) * 64 + (g >> 5) * 8 + (b >> 5);
            counts[bin]++; red[bin] += r; green[bin] += g; blue[bin] += b; samples++;
            if (counts[bin] > counts[best]) best = bin;
        }
        return counts[best] >= 4 && counts[best] >= samples * 0.55
            ? Color.FromRgb((byte)(red[best] / counts[best]), (byte)(green[best] / counts[best]),
                (byte)(blue[best] / counts[best])) : null;
    }

    private static string SafeThaiTranslation(string value)
    {
        static bool IsJapanese(char character) => (character is >= '\u3040' and <= '\u30ff'
            or >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff') && !char.IsPunctuation(character);
        bool removedJapanese = value.Any(IsJapanese);
        var text = new string(value.Where(character => !IsJapanese(character)).ToArray()).Trim();
        return text.Length > 0 && (text.Any(char.IsLetterOrDigit) || !removedJapanese)
            ? text : "\u0e41\u0e1b\u0e25\u0e44\u0e21\u0e48\u0e2a\u0e33\u0e40\u0e23\u0e47\u0e08";
    }

    internal static IEnumerable<double> CaptionFontSizes()
    {
        for (double size = 24; size >= 8; size -= 1) yield return size;
    }

    internal static string[] ThaiWords(string value)
    {
        const int IcuWordBreakIterator = 1;
        const int IcuBreakDone = -1;
        const int IcuWordStatusMinimum = 100;
        if (value.Length == 0) return [value];
        GCHandle pinned = default;
        nint iterator = 0;
        try
        {
            pinned = GCHandle.Alloc(value, GCHandleType.Pinned);
            int status = 0;
            // Supported Windows versions ship ICU; negative status codes are success warnings.
            iterator = IcuBreakOpen(IcuWordBreakIterator, "th", pinned.AddrOfPinnedObject(), value.Length, ref status);
            if (iterator == 0 || status > 0) return [value];

            var breakPositions = StringInfo.ParseCombiningCharacters(value).ToHashSet();
            // ponytail: protect this common particle from contextual ICU splits; other dictionary gaps remain.
            const string particle = "เหรอ";
            for (int at = value.IndexOf(particle, StringComparison.Ordinal); at >= 0;
                 at = value.IndexOf(particle, at + particle.Length, StringComparison.Ordinal))
            {
                int end = at + particle.Length;
                if (!breakPositions.Contains(at) || end < value.Length && !breakPositions.Contains(end)) continue;
                for (int inside = at + 1; inside < end; inside++) breakPositions.Remove(inside);
            }
            var starts = new List<int> { 0 };
            int previous = IcuBreakFirst(iterator);
            bool sawWord = false;
            for (int end; (end = IcuBreakNext(iterator)) != IcuBreakDone; previous = end)
            {
                if (previous < 0 || end <= previous || end > value.Length) return [value];
                if (IcuBreakRuleStatus(iterator) < IcuWordStatusMinimum) continue;
                if (sawWord && previous > 0 && breakPositions.Contains(previous)) starts.Add(previous);
                sawWord = true;
            }
            return starts.Select((start, index) => value[start..(index + 1 < starts.Count
                ? starts[index + 1] : value.Length)]).ToArray();
        }
        catch (DllNotFoundException) { return [value]; }
        catch (EntryPointNotFoundException) { return [value]; }
        finally
        {
            if (iterator != 0) IcuBreakClose(iterator);
            if (pinned.IsAllocated) pinned.Free();
        }
    }

    internal static bool WrapWords(TextBlock text, IReadOnlyList<string> words, double width,
        Dictionary<string, double>? lineWidths = null, CancellationToken cancellationToken = default)
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
            cancellationToken.ThrowIfCancellationRequested();
            string line = "";
            for (int end = start; end < words.Count; end++)
            {
                line += words[end];
                string candidate = line;
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
            cancellationToken.ThrowIfCancellationRequested();
            if (lines.Length > 0) lines.AppendLine();
            lines.Append(string.Concat(words.Skip(start).Take(next[start] - start)));
        }
        text.Text = lines.ToString();
        return true;
    }

    private static Brush? BackgroundBrush(Drawing.Rectangle caption, Drawing.Rectangle mask,
        Drawing.Rectangle capture, byte[]? pixels, BitmapSource? frame, SubtitleStyle style,
        Brush? qualifiedSourceCover, Drawing.Rectangle? qualifiedFootprint, Color? qualifiedBackground,
        RenderWorkBudget workBudget, CancellationToken cancellationToken = default, SourceCoverPlan? coveredPlan = null)
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
        bool transparentQualifiedCover = ReferenceEquals(qualifiedSourceCover, Brushes.Transparent);
        if (mask.Contains(caption) && !transparentQualifiedCover) return qualifiedSourceCover;
        var protectedArea = transparentQualifiedCover && qualifiedFootprint.HasValue
            ? qualifiedFootprint.Value : qualifiedSourceCover is not null ? mask : Drawing.Rectangle.Empty;
        int samples = 0, minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
        long totalR = 0, totalG = 0, totalB = 0;
        for (int y = caption.Top; y < caption.Bottom; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            workBudget.SampleBackgroundRow(caption.Width);
            for (int x = caption.Left; x < caption.Right; x++)
            {
                if (coveredPlan is not null ? coveredPlan.Covers(x - capture.X, y - capture.Y)
                    : protectedArea.Contains(x, y)) continue;
                int offset = ((y - capture.Y) * capture.Width + x - capture.X) * 4;
                int b = pixels[offset], g = pixels[offset + 1], r = pixels[offset + 2];
                minR = Math.Min(minR, r); minG = Math.Min(minG, g); minB = Math.Min(minB, b);
                maxR = Math.Max(maxR, r); maxG = Math.Max(maxG, g); maxB = Math.Max(maxB, b);
                totalR += r; totalG += g; totalB += b; samples++;
                if (maxR - minR > 32 || maxG - minG > 32 || maxB - minB > 32) return null;
            }
        }
        if (samples == 0) return transparentQualifiedCover ? Brushes.Transparent : Brushes.White;
        int averageR = (int)(totalR / samples), averageG = (int)(totalG / samples), averageB = (int)(totalB / samples);
        if (transparentQualifiedCover)
        {
            if (qualifiedBackground is not Color reference
                || Math.Abs(averageR - reference.R) > 48
                || Math.Abs(averageG - reference.G) > 48
                || Math.Abs(averageB - reference.B) > 48) return null;
            // ponytail: reconstructed-color compatibility handles qualified fills/gradients; artwork needs segmentation.
            return Brushes.Transparent;
        }
        // ponytail: only neutral flat space is a safe automatic fallback; semantic artwork needs a real segmenter.
        if (Math.Max(averageR, Math.Max(averageG, averageB)) - Math.Min(averageR, Math.Min(averageG, averageB)) > 24)
            return null;
        if (averageR >= 232 && averageG >= 232 && averageB >= 232) return Brushes.White;
        var brush = new SolidColorBrush(Color.FromRgb((byte)averageR, (byte)averageG, (byte)averageB));
        brush.Freeze();
        return brush;
    }

    private Brush MaskBrushForFrame(Drawing.Rectangle mask, Drawing.Rectangle capture,
        byte[]? pixels, BitmapSource? frame, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (frame is null || !frame.IsFrozen) return MaskBrush(mask, capture, pixels, cancellationToken);
        EnsureMaskCache(frame, capture);
        if (!maskCache.TryGetValue(mask, out var brush))
            maskCache[mask] = brush = MaskBrush(mask, capture, pixels, cancellationToken);
        return brush;
    }

    private (Brush Patch, Drawing.Rectangle Bounds, Brush Caption, Color Background, SourceCoverPlan? Plan)? SourceCoverForFrame(
        Drawing.Rectangle source, Drawing.Rectangle mask, Drawing.Rectangle capture,
        byte[]? pixels, BitmapSource? frame, Drawing.Rectangle[] sources,
        DisplayLettering?[] displayStyles, RenderWorkBudget workBudget, CancellationToken cancellationToken,
        DisplayLettering? display = null)
    {
        var key = (source, mask);
        if (frame?.IsFrozen == true)
        {
            EnsureMaskCache(frame, capture);
            if (sourceCoverCache.TryGetValue(key, out var cached)) return cached;
        }

        var result = Create();
        if (frame?.IsFrozen == true) sourceCoverCache[key] = result;
        return result;

        (Brush Patch, Drawing.Rectangle Bounds, Brush Caption, Color Background, SourceCoverPlan? Plan)? Create()
        {
            if (pixels is null)
            {
                var plain = MaskBrushForFrame(mask, capture, null, frame, cancellationToken);
                return (plain, mask, plain, Colors.Transparent, null);
            }

            var localSource = source;
            var localMask = mask;
            localSource.Offset(-capture.X, -capture.Y);
            localMask.Offset(-capture.X, -capture.Y);
            var plan = display?.Cover;
            if (display is null && !(frame?.IsFrozen == true && sourcePlanCache.TryGetValue(key, out plan)))
            {
                int searchX = Math.Max(96, Math.Max(localSource.Width, localSource.Height));
                int searchY = Math.Max(96, localSource.Height);
                Drawing.Rectangle search;
                do
                {
                    search = localSource;
                    search.Inflate(searchX, searchY);
                    search.Intersect(new Drawing.Rectangle(0, 0, capture.Width, capture.Height));
                    if (SourceCover.IsWithinBudget(search) || searchX == 0 && searchY == 0) break;
                    // Keep the detected text intact while reducing oversized search padding, not the flood budget.
                    searchX /= 2;
                    searchY /= 2;
                } while (true);
                workBudget.SampleBackgroundRow(checked(localMask.Width * localMask.Height));
                var footprint = SourceCover.TryCreate(pixels, capture.Width, capture.Height, capture.Width * 4,
                    localSource, localMask, cancellationToken);
                plan = SourceCover.TryCreateBubble(pixels, capture.Width, capture.Height,
                    localSource, search, footprint, Array.ConvertAll(sources, area => {
                        area.Offset(-capture.X, -capture.Y); return area;
                    }), workBudget.TryBubble, cancellationToken);
                if (plan is not null && footprint is not null)
                    plan = plan.IncludeFootprint(footprint, localSource, workBudget.SampleBackgroundRow, cancellationToken);
                plan ??= footprint;
                // Neighbor readiness changes clipping, not the source pixels or their qualified bubble contour.
                if (frame?.IsFrozen == true) sourcePlanCache[key] = plan;
            }
            if (plan is not null)
            {
                var bubble = plan.FootprintBounds;
                List<(Drawing.Rectangle Area, SourceCoverPlan? Cover)>? exclusions = null;
                for (int i = 0; i < sources.Length; i++)
                {
                    if (sources[i] == source) continue;
                    var otherCover = displayStyles[i]?.Cover;
                    var other = sources[i];
                    other.Offset(-capture.X, -capture.Y);
                    if (otherCover is not null) other = otherCover.FootprintBounds;
                    if (!Conflicts(other, otherCover)) continue;
                    if (display is not null) return null;
                    if (otherCover is null && other.IntersectsWith(localSource)) return null;
                    // Keep the closed bubble, but leave neighboring passages untouched until their own cover is ready.
                    (exclusions ??= new()).Add((other, otherCover));
                }
                if (exclusions is not null)
                {
                    plan = plan.Excluding(exclusions, workBudget.SampleBackgroundRow, cancellationToken);
                    if (plan is null) return null;
                }

                bool Conflicts(Drawing.Rectangle other, SourceCoverPlan? otherCover)
                {
                    var overlap = Drawing.Rectangle.Intersect(bubble, other);
                    if (overlap.IsEmpty) return false;
                    // A cover's transparent bounding-box fringe can overlap the next OCR region.
                    for (int y = overlap.Top; y < overlap.Bottom; y++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        workBudget.SampleBackgroundRow(overlap.Width);
                        for (int x = overlap.Left; x < overlap.Right; x++)
                            if (plan.Covers(x, y) && (otherCover is null || otherCover.Covers(x, y))) return true;
                    }
                    return false;
                }
            }
            if (plan is null)
            {
                if (!CanUseLegacyMonochromeCover(source, mask, capture, pixels, cancellationToken)) return null;
                var plain = MaskBrushForFrame(mask, capture, pixels, frame, cancellationToken);
                return (plain, mask, plain, Colors.Transparent, null);
            }

            if (display is not null && ReferenceEquals(plan, display.Cover))
            {
                var headingBounds = plan.FootprintBounds;
                headingBounds.Offset(capture.Location);
                return (display.Patch(cancellationToken), headingBounds, Brushes.Transparent, display.Outline, plan);
            }
            byte[] patch = plan.CreatePatch(cancellationToken);
            long backgroundB = 0, backgroundG = 0, backgroundR = 0;
            int backgroundSamples = 0;
            for (int offset = 0; offset < patch.Length; offset += 4)
            {
                if ((offset & 65535) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (patch[offset + 3] == 0) continue;
                backgroundB += patch[offset]; backgroundG += patch[offset + 1]; backgroundR += patch[offset + 2];
                backgroundSamples++;
            }
            if (backgroundSamples == 0) return null;
            var background = Color.FromRgb((byte)(backgroundR / backgroundSamples),
                (byte)(backgroundG / backgroundSamples), (byte)(backgroundB / backgroundSamples));
            var texture = BitmapSource.Create(plan.FootprintBounds.Width, plan.FootprintBounds.Height,
                96, 96, PixelFormats.Bgra32, null, patch, plan.FootprintBounds.Width * 4);
            texture.Freeze();
            var patchBrush = new ImageBrush(texture) { Stretch = Stretch.Fill };
            patchBrush.Freeze();
            var bounds = plan.FootprintBounds;
            bounds.Offset(capture.Location);
            return (patchBrush, bounds, Brushes.Transparent, background, plan);
        }
    }

    private static bool FitBubbleLines(TextBlock text, string[] words, Drawing.Rectangle bubble,
        Drawing.Rectangle capture, SourceCoverPlan plan, SourceCoverPlan[] neighbors,
        Drawing.Rectangle[] blockers, double scaleX, double scaleY,
        Dictionary<string, double> widths, RenderWorkBudget budget, CancellationToken cancellationToken,
        out Drawing.Rectangle bounds)
    {
        bounds = Drawing.Rectangle.Empty;
        // ponytail: bound contour search for dialogue; long passages retain the existing paragraph fitter.
        if (words.Length > 64 || text.Text.Contains('\n')) return false;
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        int lineHeight = Math.Max(1, (int)Math.Ceiling(text.DesiredSize.Height / scaleY));
        string originalText = text.Text;
        if (!WrapWords(text, words, Math.Max(0, bubble.Width - 4) * scaleX, widths, cancellationToken)) return false;
        int minimumLines = text.Text.Count(character => character == '\n') + 1;
        text.Text = originalText;
        var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
        var strips = new Dictionary<int, (double Width, double Center)>();
        // Each line uses a contiguous strip contained by the contour throughout its full height.
        for (int count = minimumLines; count <= Math.Min(16, (bubble.Height - 4) / lineHeight); count++)
        {
            (string Text, TextEffectCollection Effects, Drawing.Rectangle Bounds, double Spread)? unaligned = null;
            // Only a valid but staggered joined-bubble fit needs the two alternate vertical anchors.
            for (int anchor = 0; anchor < (unaligned is null ? 1 : 3); anchor++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                budget.CheckPlacement();
                int top = anchor == 0 ? bubble.Top + (bubble.Height - count * lineHeight) / 2
                    : anchor == 1 ? bubble.Bottom - count * lineHeight - 2 : bubble.Top + 2;
                var candidateBounds = new Drawing.Rectangle(bubble.Left, top, bubble.Width, count * lineHeight);
                if (!bubble.Contains(candidateBounds)) continue;
                if (blockers.Any(other => other.IntersectsWith(candidateBounds))) continue;
                var available = new double[count];
                var centers = new double[count];
                for (int line = 0; line < count; line++)
                {
                    int rowTop = top + line * lineHeight;
                    // Row-count candidates revisit the same strips; line height and the cover are fixed for this call.
                    if (strips.TryGetValue(rowTop, out var strip))
                    {
                        available[line] = strip.Width;
                        centers[line] = strip.Center;
                        continue;
                    }
                    var common = new bool[bubble.Width];
                    Array.Fill(common, true);
                    for (int y = rowTop; y < rowTop + lineHeight; y++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        budget.SampleBackgroundRow(bubble.Width);
                        for (int x = 0; x < common.Length; x++)
                            common[x] &= CoveredForCaption(bubble.Left + x - capture.X, y - capture.Y,
                                plan, neighbors, budget);
                    }
                    int bestLeft = 0, bestWidth = 0;
                    for (int x = 0, start = 0; x <= common.Length; x++)
                    {
                        if (x < common.Length && common[x]) continue;
                        if (x - start > bestWidth) { bestLeft = start; bestWidth = x - start; }
                        start = x + 1;
                    }
                    available[line] = Math.Max(0, bestWidth - 4) * scaleX;
                    centers[line] = bestLeft + bestWidth / 2d;
                    strips[rowTop] = (available[line], centers[line]);
                }
                var costs = new double[count + 1, words.Length + 1];
                var breaks = new int[count, words.Length];
                for (int line = 0; line <= count; line++)
                for (int start = 0; start <= words.Length; start++) costs[line, start] = double.PositiveInfinity;
                costs[count, words.Length] = 0;
                for (int line = count - 1; line >= 0; line--)
                for (int start = words.Length - 1; start >= 0; start--)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // A narrow neck can remain empty while complete words use the adjoining lobes.
                    costs[line, start] = available[line] * available[line] + costs[line + 1, start];
                    breaks[line, start] = start;
                    string value = "";
                    for (int end = start; end < words.Length; end++)
                    {
                        value += words[end];
                        if (!widths.TryGetValue(value, out double width))
                            widths[value] = width = new FormattedText(value, text.Language.GetEquivalentCulture(),
                                text.FlowDirection, typeface, text.FontSize, text.Foreground,
                                VisualTreeHelper.GetDpi(text).PixelsPerDip).WidthIncludingTrailingWhitespace;
                        if (width > available[line]) break;
                        double remaining = available[line] - width;
                        double cost = remaining * remaining + costs[line + 1, end + 1];
                        if (cost >= costs[line, start]) continue;
                        costs[line, start] = cost;
                        breaks[line, start] = end + 1;
                    }
                }
                if (double.IsPositiveInfinity(costs[0, 0])) continue;
                var lines = new string[count];
                for (int line = 0, start = 0; line < count; line++)
                {
                    int end = breaks[line, start];
                    lines[line] = string.Concat(words.Skip(start).Take(end - start));
                    start = end;
                }
                text.Text = string.Join("\n", lines);
                text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                text.LineHeight = lineHeight * scaleY;
                double sharedLeft = double.NegativeInfinity, sharedRight = double.PositiveInfinity;
                for (int line = 0; line < lines.Length; line++)
                {
                    if (lines[line].Length == 0) continue;
                    double slack = Math.Max(0, available[line] - widths[lines[line]]) / (2 * scaleX);
                    sharedLeft = Math.Max(sharedLeft, centers[line] - slack);
                    sharedRight = Math.Min(sharedRight, centers[line] + slack);
                }
                // Align the paragraph when every occupied strip admits the same center at this font.
                if (sharedLeft <= sharedRight)
                    Array.Fill(centers, Math.Clamp(bubble.Width / 2d, sharedLeft, sharedRight));
                else
                    for (int line = 0; line < lines.Length; line++)
                    {
                        if (lines[line].Length == 0) continue;
                        double slack = Math.Max(0, available[line] - widths[lines[line]]) / (2 * scaleX);
                        // Use remaining space to minimize stagger without leaving any row's safe strip.
                        centers[line] = Math.Clamp((sharedLeft + sharedRight) / 2,
                            centers[line] - slack, centers[line] + slack);
                    }
                var effects = new TextEffectCollection();
                for (int line = 0, start = 0; line < lines.Length; start += lines[line++].Length + 1)
                    if (lines[line].Length > 0 && centers[line] != bubble.Width / 2d)
                        effects.Add(new TextEffect { PositionStart = start, PositionCount = lines[line].Length,
                            Transform = new TranslateTransform((centers[line] - bubble.Width / 2d) * scaleX, 0) });
                effects.Freeze();
                text.TextEffects = effects;
                bounds = candidateBounds;
                if (neighbors.Length > 0 && sharedLeft > sharedRight)
                {
                    double spread = sharedLeft - sharedRight;
                    if (unaligned is null || spread < unaligned.Value.Spread)
                        unaligned = (text.Text, effects, candidateBounds, spread);
                    continue;
                }
                return true;
            }
            if (unaligned is { } fallback)
            {
                text.Text = fallback.Text;
                text.TextEffects = fallback.Effects;
                bounds = fallback.Bounds;
                return true;
            }
        }
        return false;
    }

    private static bool LinesFitCover(TextBlock text, Drawing.Rectangle bounds, Drawing.Rectangle capture,
        SourceCoverPlan plan, SourceCoverPlan[] neighbors, double scaleX, double scaleY, double insetY,
        RenderWorkBudget workBudget, CancellationToken cancellationToken,
        Func<Drawing.Rectangle, bool>? plainLine = null)
    {
        // A bubble's rounded corners need not contain the empty corners of a centered text block.
        var lines = text.Text.Replace("\r", "").Split('\n');
        double lineHeight = text.DesiredSize.Height / lines.Length;
        var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
        for (int line = 0; line < lines.Length; line++)
        {
            var measured = new FormattedText(lines[line], text.Language.GetEquivalentCulture(),
                text.FlowDirection, typeface, text.FontSize, text.Foreground,
                VisualTreeHelper.GetDpi(text).PixelsPerDip);
            int width = (int)Math.Ceiling(measured.WidthIncludingTrailingWhitespace / scaleX) + 2;
            int left = bounds.Left + (bounds.Width - width) / 2;
            int top = bounds.Top + (int)Math.Floor((insetY + line * lineHeight) / scaleY);
            int bottom = bounds.Top + (int)Math.Ceiling((insetY + (line + 1) * lineHeight) / scaleY);
            if (plainLine is not null)
            {
                if (!plainLine(new Drawing.Rectangle(left, top, width, bottom - top))) return false;
                continue;
            }
            for (int y = top; y < bottom; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                workBudget.SampleBackgroundRow(width);
                for (int x = left; x < left + width; x++)
                    if (!CoveredForCaption(x - capture.X, y - capture.Y, plan, neighbors, workBudget)) return false;
            }
        }
        return true;
    }

    private static bool CoveredForCaption(int x, int y, SourceCoverPlan plan,
        SourceCoverPlan[] neighbors, RenderWorkBudget budget)
    {
        if (plan.Covers(x, y)) return true;
        foreach (var neighbor in neighbors)
        {
            budget.SampleBackgroundRow(1);
            if (neighbor.Covers(x, y)) return true;
        }
        return false;
    }

    internal static Drawing.Rectangle PermittedCaptionArea(Drawing.Rectangle source, Drawing.Rectangle capture)
    {
        if (source.Width <= 0 || source.Height <= 0) return Drawing.Rectangle.Empty;
        var permitted = source;
        permitted.Inflate(Math.Max(48, (int)Math.Ceiling(source.Width * 0.75)),
            Math.Max(36, (int)Math.Ceiling(source.Height * 0.4)));
        return Drawing.Rectangle.Intersect(permitted, capture);
    }

    private void EnsureMaskCache(BitmapSource frame, Drawing.Rectangle capture)
    {
        if (ReferenceEquals(maskFrame, frame) && maskCapture == capture) return;
        maskFrame = frame;
        maskCapture = capture;
        maskCache.Clear();
        sourceCoverCache.Clear();
        sourcePlanCache.Clear();
    }

    private static bool CanUseLegacyMonochromeCover(Drawing.Rectangle source, Drawing.Rectangle mask,
        Drawing.Rectangle capture, byte[] pixels, CancellationToken cancellationToken)
    {
        if (!SourceCover.IsWithinBudget(mask)) return false;
        int samples = 0, min = 255, max = 0;
        for (int y = mask.Top; y < mask.Bottom; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = mask.Left; x < mask.Right; x++)
            {
                if (source.Contains(x, y)) continue;
                int offset = ((y - capture.Top) * capture.Width + x - capture.Left) * 4;
                int b = pixels[offset], g = pixels[offset + 1], r = pixels[offset + 2];
                if (Math.Max(Math.Abs(r - g), Math.Max(Math.Abs(r - b), Math.Abs(g - b))) > 12)
                    return false;
                int shade = (r + g + b) / 3;
                min = Math.Min(min, shade);
                max = Math.Max(max, shade);
                samples++;
            }
        }
        return samples >= 4 && max - min <= 32;
    }

    internal static Brush MaskBrush(Drawing.Rectangle mask, Drawing.Rectangle capture, byte[]? pixels,
        CancellationToken cancellationToken = default)
    {
        if (pixels is null) return Brushes.White;
        cancellationToken.ThrowIfCancellationRequested();
        if (!SourceCover.IsWithinBudget(mask)) throw new SubtitleLayoutException();
        var fill = new byte[checked(mask.Width * mask.Height * 4)];
        var above = Sample(mask.Left + mask.Width / 2 - 6, mask.Left + mask.Width / 2 + 6, mask.Top - 8);
        var below = Sample(mask.Left + mask.Width / 2 - 6, mask.Left + mask.Width / 2 + 6, mask.Bottom + 8);
        for (int y = 0; y < mask.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int sx = startX; sx < endX; sx++)
                {
                    int offset = ((sy - capture.Top) * capture.Width + sx - capture.Left) * 4;
                    brightest = Math.Max(brightest, (pixels[offset] + pixels[offset + 1] + pixels[offset + 2]) / 3);
                }
            }
            long b = 0, g = 0, r = 0;
            int count = 0;
            for (int sy = top; sy < bottom; sy++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int sx = startX; sx < endX; sx++)
                {
                    int offset = ((sy - capture.Top) * capture.Width + sx - capture.Left) * 4;
                    if ((pixels[offset] + pixels[offset + 1] + pixels[offset + 2]) / 3 < brightest - 24) continue;
                    b += pixels[offset]; g += pixels[offset + 1]; r += pixels[offset + 2]; count++;
                }
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
    [DllImport("icu.dll", EntryPoint = "ubrk_open", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint IcuBreakOpen(int type, [MarshalAs(UnmanagedType.LPStr)] string locale,
        nint text, int length, ref int status);
    [DllImport("icu.dll", EntryPoint = "ubrk_first", CallingConvention = CallingConvention.Cdecl)]
    private static extern int IcuBreakFirst(nint iterator);
    [DllImport("icu.dll", EntryPoint = "ubrk_next", CallingConvention = CallingConvention.Cdecl)]
    private static extern int IcuBreakNext(nint iterator);
    [DllImport("icu.dll", EntryPoint = "ubrk_getRuleStatus", CallingConvention = CallingConvention.Cdecl)]
    private static extern int IcuBreakRuleStatus(nint iterator);
    [DllImport("icu.dll", EntryPoint = "ubrk_close", CallingConvention = CallingConvention.Cdecl)]
    private static extern void IcuBreakClose(nint iterator);
}
