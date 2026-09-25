using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Translumo.Local;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

internal static class Program
{
    private static void CheckThaiWrapping()
    {
        var compactFonts = SubtitleOverlay.CaptionFontSizes(12, 1).ToArray();
        Require(compactFonts.Max() == 10 && compactFonts.Min() == 8,
            "A short textbox must search down to readable compact sizes instead of forcing a large generic caption.");
        Require(SubtitleOverlay.CaptionFontSizes(100, 1).Max() == 24,
            "A large textbox may use a larger font, within the caption size limit.");
        var text = new TextBlock { FontFamily = new FontFamily("Leelawadee UI"), FontSize = 20,
            Language = System.Windows.Markup.XmlLanguage.GetLanguage("th-TH"), TextWrapping = TextWrapping.NoWrap };
        var segmenters = new[] { (Requested: "th", Value: new Windows.Data.Text.WordsSegmenter("th")),
            (Requested: "th-TH", Value: new Windows.Data.Text.WordsSegmenter("th-TH")) };
        Console.WriteLine($"Thai layout environment: OS={Environment.OSVersion.VersionString}; "
            + $"Leelawadee UI installed={Fonts.SystemFontFamilies.Any(font => font.Source.Equals("Leelawadee UI", StringComparison.OrdinalIgnoreCase))}; "
            + string.Join("; ", segmenters.Select(segmenter =>
                $"{segmenter.Requested}->{segmenter.Value.ResolvedLanguage}=[{string.Join(" | ", segmenter.Value.GetTokens("มหาวิทยาลัยแพทย์ของอาวเวอร์ซู").Select(token =>
                    $"{token.SourceTextSegment.StartPosition}:{token.SourceTextSegment.Length}:{token.Text}"))}]")));
        const string caption = "ดังนั้นซาตโต";
        var words = SubtitleOverlay.ThaiWords(caption);
        Require(string.Concat(words) == caption, "Thai segmentation must preserve the original characters.");
        Require(SubtitleOverlay.WrapWords(text, words, 70), "A short Thai caption must fit on two lines.");
        Require(text.Text.Replace("\r", "") == "ดังนั้น\nซาตโต", "Balanced Thai wrapping must not strand half of a name.");
        text.FontSize = 14;
        const string university = "มหาวิทยาลัยแพทย์ของอาวเวอร์ซู";
        Require(SubtitleOverlay.WrapWords(text, SubtitleOverlay.ThaiWords(university), 70),
            "A long Thai caption must wrap without splitting dictionary words.");
        Console.WriteLine("Thai university wrap: " + string.Join(" | ", text.Text.Split('\n').Select(line =>
            $"{line.TrimEnd('\r')}={MeasureLineWidth(line.TrimEnd('\r')):0.###} DIP")));
        Require(text.Text.Contains("มหาวิทยาลัย"), "Thai university word must stay intact.");
        Require(text.Text.Replace("\r", "").Replace("\n", "") == university, "Wrapping must not lose text.");
        foreach (string line in text.Text.Split('\n'))
            Require(MeasureLineWidth(line.TrimEnd('\r')) <= 70, "Every Thai line must fit without clipping.");
        const string spacedCaption = "แพทย์ ของ";
        var spacedWords = SubtitleOverlay.ThaiWords(spacedCaption);
        double spacedWidth = spacedWords.Max(MeasureLineWidth);
        Require(spacedWidth < MeasureLineWidth(spacedCaption)
            && SubtitleOverlay.WrapWords(text, spacedWords, spacedWidth),
            "A spaced Thai caption must wrap at its existing space.");
        Require(text.Text.Replace("\r", "").Replace("\n", "") == spacedCaption,
            "Wrapping must preserve spaces as well as Thai graphemes.");
        Require(!SubtitleOverlay.WrapWords(text, new[] { "มหาวิทยาลัย" }, 10),
            "A word wider than the caption must request another width or font size, never split characters.");
        const string decomposed = "ทํางานอย่างจริงจัง";
        Require(string.Concat(SubtitleOverlay.ThaiWords(decomposed)) == decomposed,
            "Segmentation must preserve decomposed Thai marks instead of silently normalizing the caption.");
        var hyCaptions = new[] {
            "วันนี้ขอร้องให้คุณมาทำงานพาร์ทไทม์ในตอนค่ำที่โรงพยาบาลของเราด้วย.",
            "แล้วก็ ซาโต้คุง", "ไซโตะ เอ็นจิโร่ อายุ 25 ปี", "นั่นแหละคือ “เอลิเต้” คนนั้น...",
            "ยงไตจบการศึกษาเหรอ...", "นี่เป็นครั้งแรกที่ฉันมาประจำการในโรงพยาบาลนี้...",
            "จบการศึกษาจากคณะแพทยศาสตร์ มหาวิทยาลัยเอ็นโรกุ...", "โรงพยาบาลเชิงดุล"
        };
        foreach (string hyCaption in hyCaptions)
        {
            var thaiWords = SubtitleOverlay.ThaiWords(hyCaption);
            Require(string.Concat(thaiWords) == hyCaption, "HY caption segmentation must preserve every original character.");
            var elements = StringInfo.ParseCombiningCharacters(hyCaption).ToHashSet();
            int offset = 0;
            foreach (string word in thaiWords)
            {
                Require(elements.Contains(offset) && !IsMark(word), "Thai dictionary boundaries must not separate a tone mark from its base.");
                offset += word.Length;
            }
            foreach (double font in new[] { 12d, 14d, 18d, 20d })
                foreach (double width in new[] { 60d, 70d, 80d, 100d, 120d, 140d })
                {
                    text.FontSize = font;
                    if (!SubtitleOverlay.WrapWords(text, thaiWords, width)) continue;
                    Require(text.Text.Split('\n').All(line => !IsMark(line.TrimStart())),
                        "No narrow HY caption line may start with an unattached combining mark.");
                }
        }
        Console.WriteLine("All eight HY captions retain grapheme clusters at 12-20 DIP and six narrow caption widths.");

        static bool IsMark(string value) => value.Length > 0 && CharUnicodeInfo.GetUnicodeCategory(value, 0)
            is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
        double MeasureLineWidth(string value)
        {
            var measure = new TextBlock { Text = value, FontFamily = text.FontFamily,
                FontSize = text.FontSize, Language = text.Language, TextWrapping = TextWrapping.NoWrap };
            measure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return measure.DesiredSize.Width;
        }
        Console.WriteLine("Thai word boundaries, balanced lines, preserved text and no-clipping checks passed.");
    }

    [STAThread]
    private static void Main(string[] args)
    {
        SetProcessDpiAwarenessContext(new nint(-4));
        CheckLargeMaskGradient();
        CheckThaiWrapping();
        SubtitleLayout.SelfCheck();
        CheckCometMarginIfAvailable();
        Console.WriteLine("Subtitle geometry checks passed.");
        if (args.Contains("--vertical"))
        {
            var verticalApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try { CheckReadableVerticalCaption(); CheckVerticalIndex(); }
            finally { verticalApp.Shutdown(); }
            return;
        }
        if (!args.Contains("--visual")) return;
        if (args.Contains("--margin-preview"))
        {
            var previewApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try { CheckDenseMarginFallback(); }
            finally { previewApp.Shutdown(); }
            return;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        CheckColoredOverwrite();
        CheckTexturedOverwrite();
        CheckDenseMarginFallback();
        CheckReadableVerticalCaption();
        CheckVerticalIndex();
        var screen = Forms.Screen.PrimaryScreen!.Bounds;
        var capture = new Drawing.Rectangle(screen.Left + 80, screen.Top + 60, 720, Math.Min(800, screen.Height - 100));
        var regions = new[] {
            new TextRegion("ここはどこ？", new Drawing.Rectangle(80, 80, 180, 55)),
            new TextRegion("一緒に行こう！", new Drawing.Rectangle(360, 260, 220, 55)),
            new TextRegion("君を待っていた。", new Drawing.Rectangle(100, 480, 240, 60))
        };
        var translations = new[] { "Where am I?", "Let's go together!", "I have been waiting for you." };
        var sourceVisual = new DrawingVisual();
        using (var drawing = sourceVisual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.LightSteelBlue, null, new Rect(0, 0, capture.Width, capture.Height));
            foreach (var region in regions)
            {
                var bounds = region.Bounds;
                drawing.DrawRoundedRectangle(Brushes.White, new Pen(Brushes.Black, 2),
                    new Rect(bounds.X - 25, bounds.Y - 25, bounds.Width + 50, bounds.Height + 50), 35, 35);
                drawing.DrawText(new FormattedText(region.Text, CultureInfo.GetCultureInfo("ja-JP"),
                    FlowDirection.LeftToRight, new Typeface("Yu Gothic"), 24, Brushes.DarkGreen, 1),
                    new Point(bounds.X, bounds.Y + 10));
            }
        }
        var source = new RenderTargetBitmap(capture.Width, capture.Height, 96, 96, PixelFormats.Pbgra32);
        source.Render(sourceVisual);
        var page = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Topmost = true, Content = new Image { Source = source, Stretch = Stretch.Fill } };
        page.Show();
        SetWindowPos(new WindowInteropHelper(page).Handle, new nint(-1), capture.X, capture.Y,
            capture.Width, capture.Height, 0x0010);
        Pump(app);
        using var baseline = Screenshot(capture);
        var overlay = new SubtitleOverlay();
        try
        {
            foreach (var style in new[] { SubtitleStyle.Overwrite, SubtitleStyle.Overlay })
            {
                overlay.Render(capture, regions, translations, style);
                Pump(app);
                overlay.UpdateLayout();
                var hwnd = new WindowInteropHelper(overlay).Handle;
                Require(GetWindowDisplayAffinity(hwnd, out uint affinity) && affinity == 0x11,
                    "Subtitle window must be excluded from capture.");
                Require((GetWindowLong(hwnd, -20) & 0x080000A0) == 0x080000A0,
                    "Subtitle window must be click-through, no-activate, and absent from the taskbar.");
                var canvas = (Canvas)overlay.Content;
                foreach (var border in canvas.Children.OfType<Border>().Where(b => b.Child is TextBlock))
                {
                    var text = (TextBlock)border.Child;
                    text.Measure(new Size(text.ActualWidth, double.PositiveInfinity));
                    Require(text.DesiredSize.Height <= text.ActualHeight + 0.1,
                        "Translation text must fit its allocated caption height.");
                    Require(border.Background == Brushes.White, "Overwrite captions must remain opaque.");
                }
                using var captured = Screenshot(capture);
                Require(Identical(baseline, captured), "Screen capture must contain the original page without subtitle feedback.");
                Require(SetWindowDisplayAffinity(hwnd, 0), "Cannot enable the native visual check.");
                try
                {
                    DwmFlush();
                    using var actual = Screenshot(capture);
                    int visibleSourcePixels = CountGreen(actual);
                    Require(CountGreen(baseline) > 100, "The fixture must have visible original-language pixels.");
                    Require(style == SubtitleStyle.Overwrite ? visibleSourcePixels == 0 : visibleSourcePixels == CountGreen(baseline),
                        "Native subtitle pixels must hide all originals in overwrite and retain all originals beside.");
                }
                finally { Require(SetWindowDisplayAffinity(hwnd, 0x11), "Cannot restore capture exclusion."); }

                var desktop = Forms.SystemInformation.VirtualScreen;
                var device = PresentationSource.FromVisual(overlay)!.CompositionTarget!.TransformFromDevice;
                var overlayBitmap = new RenderTargetBitmap(desktop.Width, desktop.Height,
                    96 / device.M11, 96 / device.M22, PixelFormats.Pbgra32);
                overlayBitmap.Render(overlay);
                var combined = new DrawingVisual();
                using (var drawing = combined.RenderOpen())
                {
                    drawing.DrawImage(source, new Rect(0, 0, capture.Width, capture.Height));
                    drawing.DrawImage(overlayBitmap,
                        new Rect(desktop.X - capture.X, desktop.Y - capture.Y, desktop.Width, desktop.Height));
                }
                var result = new RenderTargetBitmap(capture.Width, capture.Height, 96, 96, PixelFormats.Pbgra32);
                result.Render(combined);
                Directory.CreateDirectory(".cache/verification");
                var output = Path.GetFullPath($".cache/verification/subtitle-{style.ToString().ToLowerInvariant()}.png");
                using var stream = File.Create(output);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(result));
                encoder.Save(stream);
                Console.WriteLine($"{style}: text fits; native behavior and actual capture exclusion passed. {output}");
                overlay.Clear();
                Pump(app);
            }
            overlay.Render(capture, regions, translations, SubtitleStyle.Overwrite, frame: source);
            Pump(app);
            page.Content = new Border { Background = Brushes.LightPink };
            Pump(app);
            using (var held = VisibleScreenshot(overlay, capture))
            {
                Require(held.GetPixel(10, 10).ToArgb() == baseline.GetPixel(10, 10).ToArgb(),
                    "Holding must preserve artwork while underlying content changes.");
                Require(CountGreen(held) == 0, "Held pages must retain source-text masks.");
            }
            page.Topmost = false;
            var controls = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, Background = Brushes.LimeGreen };
            try
            {
                controls.Show();
                var controlsHandle = new WindowInteropHelper(controls).Handle;
                overlay.ControlsHandle = controlsHandle;
                SetWindowPos(controlsHandle, nint.Zero, capture.X + 20, capture.Y + 20, 120, 90, 0x0010 | 0x0004);
                overlay.RefreshControlExclusion(); Pump(app);
                using (var hole = VisibleScreenshot(overlay, capture))
                    Require(hole.GetPixel(40, 40).ToArgb() == Drawing.Color.LimeGreen.ToArgb(),
                        "Live controls must remain visible through the held page.");
                SetWindowPos(controlsHandle, nint.Zero, capture.X + 150, capture.Y + 30, 120, 90, 0x0010 | 0x0004);
                overlay.RefreshControlExclusion(); Pump(app);
                using (var moved = VisibleScreenshot(overlay, capture))
                {
                    Require(moved.GetPixel(170, 50).ToArgb() == Drawing.Color.LimeGreen.ToArgb(), "Control cutout must follow movement.");
                    Require(moved.GetPixel(40, 40).ToArgb() == baseline.GetPixel(40, 40).ToArgb(), "Old control location must be covered again.");
                }
                controls.WindowState = WindowState.Minimized;
                overlay.RefreshControlExclusion(); Pump(app);
                using (var minimized = VisibleScreenshot(overlay, capture))
                    Require(minimized.GetPixel(170, 50).ToArgb() == baseline.GetPixel(170, 50).ToArgb(), "Minimized controls must leave no hole.");
            }
            finally { controls.Close(); }
            Console.WriteLine("Preparing, held artwork, hidden originals, and moving/minimized control cutouts passed.");
        }
        finally { overlay.Close(); page.Close(); app.Shutdown(); }
    }

    private static void CheckColoredOverwrite()
    {
        const int width = 600, height = 300;
        var pixels = new byte[width * height * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 81;
            pixels[offset + 1] = 32;
            pixels[offset + 2] = 176;
            pixels[offset + 3] = 255;
        }
        var frame = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        frame.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        frame.Freeze();
        var overlay = new SubtitleOverlay();
        try
        {
            overlay.Render(new Drawing.Rectangle(0, 0, width, height),
                new[] { new TextRegion("縦書き", new Drawing.Rectangle(280, 90, 40, 120)) },
                new[] { "นี่คือคำแปล" }, SubtitleStyle.Overwrite, 6, frame);
            overlay.UpdateLayout();
            Require(((Canvas)overlay.Content).Children.OfType<Border>().Any(b => b.Child is TextBlock),
                "Overwrite must render a caption over a colored manga page.");
            Require(((Canvas)overlay.Content).Children.OfType<Border>().Where(b => b.Child is TextBlock)
                .All(b => ((TextBlock)b.Child).Foreground == Brushes.White),
                "Dark sampled backgrounds must keep Thai captions readable.");
        }
        finally { overlay.Close(); }
        Console.WriteLine("Colored manga overwrite rendering passed.");
    }

    private static void CheckLargeMaskGradient()
    {
        const int width = 280, height = 360;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                byte shade = (byte)(80 + y / 2);
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = shade;
                pixels[offset + 3] = 255;
            }
        var mask = new Drawing.Rectangle(40, 60, 200, 200);
        var brush = SubtitleOverlay.MaskBrush(mask, new Drawing.Rectangle(0, 0, width, height), pixels);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(brush, null, new Rect(0, 0, mask.Width, mask.Height));
        var rendered = new RenderTargetBitmap(mask.Width, mask.Height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        var actual = new byte[mask.Width * mask.Height * 4];
        rendered.CopyPixels(actual, mask.Width * 4, 0);
        foreach (int y in new[] { 40, 100, 160 })
        {
            int shade = actual[(y * mask.Width + mask.Width / 2) * 4];
            int expected = 80 + (mask.Top + y) / 2;
            Require(Math.Abs(shade - expected) <= 12,
                $"A large source mask must follow the page gradient, not repeat an adjacent strip (y={y}: {shade} vs {expected}).");
        }
        Array.Fill(pixels, (byte)40);
        for (int offset = 3; offset < pixels.Length; offset += 4) pixels[offset] = 255;
        Require(SubtitleOverlay.ForegroundBrush(SubtitleOverlay.MaskBrush(mask,
            new Drawing.Rectangle(0, 0, width, height), pixels)) == Brushes.White,
            "Dark reconstructed page backgrounds need white translation text.");

        Array.Fill(pixels, (byte)255);
        for (int offset = 3; offset < pixels.Length; offset += 4) pixels[offset] = 255;
        for (int y = 96; y < 120; y++)
            for (int x = mask.Left - 14; x < mask.Left - 2; x++)
            {
                int offset = (y * width + x) * 4;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 0;
            }
        brush = SubtitleOverlay.MaskBrush(mask, new Drawing.Rectangle(0, 0, width, height), pixels);
        visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(brush, null, new Rect(0, 0, mask.Width, mask.Height));
        rendered = new RenderTargetBitmap(mask.Width, mask.Height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        rendered.CopyPixels(actual, mask.Width * 4, 0);
        Require(actual[((108 - mask.Top) * mask.Width + 1) * 4] >= 240,
            "A single dark edge outlier must not smear across an otherwise white speech bubble.");
        Console.WriteLine("Large mask background tracks the page gradient.");
    }

    private static void CheckTexturedOverwrite()
    {
        const int width = 600, height = 300;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                bool stripe = x % 12 < 6;
                pixels[offset] = stripe ? (byte)81 : (byte)0;
                pixels[offset + 1] = stripe ? (byte)32 : (byte)0;
                pixels[offset + 2] = stripe ? (byte)176 : (byte)0;
                pixels[offset + 3] = 255;
            }
        var frame = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        frame.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        frame.Freeze();
        var overlay = new SubtitleOverlay();
        try
        {
            overlay.Render(new Drawing.Rectangle(0, 0, width, height),
                new[] {
                    new TextRegion("縦書き", new Drawing.Rectangle(280, 90, 40, 120)),
                    new TextRegion("本文", new Drawing.Rectangle(340, 90, 40, 120))
                },
                new[] { "นี่คือคำแปล", "ข้อความ" }, SubtitleStyle.Overwrite, 6, frame);
            Require(((Canvas)overlay.Content).Children.OfType<Border>().Count(b => b.Child is TextBlock) == 2,
                "Overwrite must render over textured artwork instead of exposing the source text.");
        }
        finally { overlay.Close(); }
        Console.WriteLine("Textured manga overwrite rendering passed.");
    }

    private static void CheckDenseMarginFallback()
    {
        const int width = 1000, height = 700;
        var pixels = new byte[width * height * 4];
        var regions = new System.Collections.Generic.List<TextRegion>();
        var translations = new System.Collections.Generic.List<string>();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                byte value = x < 280 || x >= 720 ? (byte)112 : (byte)(175 + (x + y) % 55);
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        for (int row = 0; row < 8; row++)
            for (int column = 0; column < 3; column++)
            {
                int x = 310 + column * 130, y = 42 + row * 80;
                regions.Add(new TextRegion("original", new Drawing.Rectangle(x, y, 50, 50)));
                int index = row * 3 + column;
                int repeats = index switch { 5 => 24, 8 => 23, 12 => 22, 16 or 20 => 15, _ => 2 };
                translations.Add(index == 0
                    ? "\u65e5\u672c\u8a9e \u0e2a\u0e27\u0e31\u0e2a\u0e14\u0e35"
                    : string.Join(" ", Enumerable.Repeat("\u0e2a\u0e27\u0e31\u0e2a\u0e14\u0e35", repeats)));
                for (int py = y - 10; py < y + 60; py++)
                    for (int px = x - 20; px < x + 70; px++)
                    {
                        int offset = (py * width + px) * 4;
                        pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 255;
                    }
                for (int py = y + 17; py < y + 33; py++)
                    for (int px = x + 10; px < x + 40; px++)
                    {
                        int offset = (py * width + px) * 4;
                        pixels[offset] = 0; pixels[offset + 1] = 100; pixels[offset + 2] = 0;
                    }
            }
        var frame = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        frame.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        frame.Freeze();
        var screen = Forms.Screen.PrimaryScreen!.Bounds;
        var capture = new Drawing.Rectangle(screen.Left + 40, screen.Top + 40, width, height);
        var fixture = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Topmost = true, Content = new Image { Source = frame, Stretch = Stretch.Fill } };
        var overlay = new SubtitleOverlay();
        try
        {
            fixture.Show();
            var handle = new WindowInteropHelper(fixture).Handle;
            SetWindowPos(handle, nint.Zero, capture.X, capture.Y, width, height, 0x0010);
            Pump(Application.Current);
            overlay.Render(capture, regions, new string[regions.Count], SubtitleStyle.Overwrite, 6, frame,
                japaneseToThai: true, allowMissingTranslations: true);
            Require(((Canvas)overlay.Content).Children.Count == 0,
                "Untranslated manga text must remain visible over the live page.");
            overlay.Render(capture, regions, translations, SubtitleStyle.Overwrite, 6, frame, japaneseToThai: true);
            Pump(Application.Current);
            var marginsField = typeof(SubtitleOverlay).GetField("plainMargins",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var cachedMargins = (Drawing.Rectangle[]?)marginsField.GetValue(overlay);
            overlay.Render(capture, regions, translations, SubtitleStyle.Overwrite, 6, frame, japaneseToThai: true);
            Require(cachedMargins is not null && ReferenceEquals(cachedMargins, marginsField.GetValue(overlay)),
                "An unchanged frozen manga frame must reuse its plain-margin scan.");

            overlay.Render(capture, regions, translations, SubtitleStyle.Overwrite, 7, frame, japaneseToThai: true);
            var paddedMargins = (Drawing.Rectangle[]?)marginsField.GetValue(overlay);
            Require(paddedMargins is not null && !ReferenceEquals(cachedMargins, paddedMargins),
                "Changed padding must invalidate the plain-margin scan.");

            overlay.Render(capture, regions, translations, SubtitleStyle.Overwrite, 6, frame, japaneseToThai: true);
            var regionBaseline = (Drawing.Rectangle[]?)marginsField.GetValue(overlay);
            var originalRegion = regions[5];
            regions[5] = originalRegion with { Bounds = new Drawing.Rectangle(
                originalRegion.Bounds.X + 1, originalRegion.Bounds.Y, originalRegion.Bounds.Width, originalRegion.Bounds.Height) };
            overlay.Render(capture, regions, translations, SubtitleStyle.Overwrite, 6, frame, japaneseToThai: true);
            var movedMargins = (Drawing.Rectangle[]?)marginsField.GetValue(overlay);
            Require(regionBaseline is not null && movedMargins is not null
                && !ReferenceEquals(regionBaseline, movedMargins),
                "Changed region geometry must invalidate the plain-margin scan.");
            regions[5] = originalRegion;

            overlay.Render(capture, regions, translations, SubtitleStyle.Overwrite, 6, frame, japaneseToThai: true);
            var captureBaseline = (Drawing.Rectangle[]?)marginsField.GetValue(overlay);
            var shiftedCapture = new Drawing.Rectangle(capture.X + 1, capture.Y, capture.Width, capture.Height);
            overlay.Render(shiftedCapture, regions, translations, SubtitleStyle.Overwrite, 6, frame, japaneseToThai: true);
            var shiftedMargins = (Drawing.Rectangle[]?)marginsField.GetValue(overlay);
            Require(captureBaseline is not null && shiftedMargins is not null
                && !ReferenceEquals(captureBaseline, shiftedMargins),
                "Changed capture bounds must invalidate the plain-margin scan.");

            overlay.Render(capture, regions, translations, SubtitleStyle.Overwrite, 6, frame, japaneseToThai: true);
            var frameBaseline = (Drawing.Rectangle[]?)marginsField.GetValue(overlay);
            var otherFrame = (BitmapSource)frame.Clone();
            otherFrame.Freeze();
            overlay.Render(capture, regions, translations, SubtitleStyle.Overwrite, 6, otherFrame, japaneseToThai: true);
            var otherFrameMargins = (Drawing.Rectangle[]?)marginsField.GetValue(overlay);
            Require(frameBaseline is not null && otherFrameMargins is not null
                && !ReferenceEquals(frameBaseline, otherFrameMargins),
                "A changed frozen frame must invalidate the plain-margin scan.");

            overlay.Render(capture, regions, translations, SubtitleStyle.Overwrite, 6, frame, japaneseToThai: true);
            var mutableFrame = (BitmapSource)frame.Clone();
            overlay.Render(capture, regions, translations, SubtitleStyle.Overwrite, 6, mutableFrame, japaneseToThai: true);
            Require(marginsField.GetValue(overlay) is null,
                "A mutable frame must not retain a plain-margin scan.");
            overlay.Render(capture, regions, translations, SubtitleStyle.Overwrite, 6, frame, japaneseToThai: true);
            Pump(Application.Current);
            Console.WriteLine("Frozen Thai margin scans reuse only exact frame geometry.");
            var allBorders = ((Canvas)overlay.Content).Children.OfType<Border>().ToArray();
            var captions = allBorders.Where(border => border.Tag is int && border.Uid != "association-badge"
                && border.Child is TextBlock).ToArray();
            Require(captions.Length == translations.Count, "Dense fallback must preserve every Thai translation.");
            double deviceScale = PresentationSource.FromVisual(overlay)!.CompositionTarget!.TransformFromDevice.M11;
            var badges = allBorders.Where(border => border.Uid == "association-badge").ToArray();
            var overflowTags = badges.Select(badge => (int)badge.Tag).ToHashSet();
            var overflowCaptions = captions.Where(border => overflowTags.Contains((int)border.Tag)).ToArray();
            var marginXs = overflowCaptions.Select(border => Math.Round(Canvas.GetLeft(border), 1)).Distinct().ToArray();
            var localMarginXs = marginXs.Select(x => x / deviceScale + Forms.SystemInformation.VirtualScreen.Left - capture.Left).ToArray();
            var inPage = captions.Single(border => (int)border.Tag == 0);
            double inPageX = Canvas.GetLeft(inPage) / deviceScale + Forms.SystemInformation.VirtualScreen.Left - capture.Left;
            Require(inPageX >= 280 && inPageX < 720 && badges.Length > 0 && badges.Length < translations.Count
                && overflowCaptions.All(border => {
                    double x = Canvas.GetLeft(border) / deviceScale + Forms.SystemInformation.VirtualScreen.Left - capture.Left;
                    return x < 280 || x >= 720;
                }), "Short Thai translations must stay on the page while only overflow uses plain margins.");
            if (marginXs.Length == 2)
                Require(localMarginXs[0] >= 720 && localMarginXs[1] < 280,
                    "Reading order must continue from the right margin into the left margin.");
            Require(captions.All(border => ((TextBlock)border.Child).FontSize >= 12)
                && overflowCaptions[0].Background is SolidColorBrush marginColor && marginColor.Color.R == 112,
                "Every realistic Thai block must fit at 12 DIP or larger on the sampled gray margins.");
            Require(captions.Select(border => (TextBlock)border.Child).All(text => text.Text.All(character =>
                character is not (>= '\u3040' and <= '\u30ff') and not (>= '\u3400' and <= '\u9fff'))),
                "Mixed model output must not expose Japanese in the fallback margin.");
            Require(badges.Length == overflowCaptions.Length && badges.All(badge => overflowCaptions.Any(caption =>
                Equals(caption.Tag, badge.Tag) && ((TextBlock)caption.Child).Text.StartsWith(
                    ((TextBlock)badge.Child).Text + ". ", StringComparison.Ordinal))),
                "Each overflow source mask must have a number matching its margin caption.");
            var desktop = Forms.SystemInformation.VirtualScreen;
            var transform = PresentationSource.FromVisual(overlay)!.CompositionTarget!.TransformFromDevice;
            var overlayBitmap = new RenderTargetBitmap(desktop.Width, desktop.Height,
                96 / transform.M11, 96 / transform.M22, PixelFormats.Pbgra32);
            overlayBitmap.Render(overlay);
            var compositeVisual = new DrawingVisual();
            using (var drawing = compositeVisual.RenderOpen())
            {
                drawing.DrawImage(frame, new Rect(0, 0, width, height));
                drawing.DrawImage(overlayBitmap,
                    new Rect(desktop.X - capture.X, desktop.Y - capture.Y, desktop.Width, desktop.Height));
            }
            var preview = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            preview.Render(compositeVisual);
            var compositePixels = new byte[width * height * 4];
            preview.CopyPixels(compositePixels, width * 4, 0);
            Require(CountGreen(compositePixels) == 0, "Dense fallback must cover all original Japanese source pixels.");
            int artOffset = (380 * width + 400) * 4;
            Require(compositePixels[artOffset] == pixels[artOffset]
                && compositePixels[artOffset + 1] == pixels[artOffset + 1]
                && compositePixels[artOffset + 2] == pixels[artOffset + 2],
                "Dense fallback must leave manga artwork outside source masks unchanged.");
            Directory.CreateDirectory(".cache/diagnostics");
            using var stream = File.Create(".cache/diagnostics/dense-margin-fallback.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(preview));
            encoder.Save(stream);
            overlay.ShiftVertical(24, Enumerable.Range(1, regions.Count - 1).ToArray());
            var scrolledBorders = ((Canvas)overlay.Content).Children.OfType<Border>().ToArray();
            Require(scrolledBorders.Count(border => border.Uid == "association-badge") == badges.Length
                && scrolledBorders.Where(border => border.Uid == "association-badge").All(badge => scrolledBorders.Any(caption =>
                    Equals(caption.Tag, badge.Tag) && caption.Uid != "association-badge"
                    && caption.Child is TextBlock text && text.Text.StartsWith(
                        ((TextBlock)badge.Child).Text + ". ", StringComparison.Ordinal))),
                "Source numbers must remain paired with margin captions while the page scrolls.");
            var narrowRegions = regions.Select(region => region with {
                Bounds = new Drawing.Rectangle(region.Bounds.X, region.Bounds.Y, region.Bounds.Width, 10)
            }).ToArray();
            overlay.Render(capture, narrowRegions, translations, SubtitleStyle.Overwrite, 6, frame, japaneseToThai: true);
            var allMargin = ((Canvas)overlay.Content).Children.OfType<Border>().ToArray();
            var allMarginCaptions = allMargin.Where(border => border.Tag is int && border.Uid != "association-badge"
                && border.Child is TextBlock).ToArray();
            Require(allMarginCaptions.Length == translations.Count && allMarginCaptions.All(border => {
                    double x = Canvas.GetLeft(border) / deviceScale + Forms.SystemInformation.VirtualScreen.Left - capture.Left;
                    return x < 280 || x >= 720;
                }) && allMargin.Count(border => border.Uid == "association-badge") == translations.Count,
                "A dense page with no in-place fits must keep every translation in the margins.");
            Console.WriteLine("Dense Thai side-margin fallback passed: .cache/diagnostics/dense-margin-fallback.png");
        }
        finally { overlay.Close(); fixture.Close(); }
    }

    private static void CheckReadableVerticalCaption()
    {
        const int width = 1000, height = 700;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                byte shade = x < 280 || x >= 720 ? (byte)112
                    : (x is >= 464 and <= 467 or >= 530 and <= 533) && y is >= 200 and <= 390
                        || (y is >= 200 and <= 203 or >= 387 and <= 390) && x is >= 464 and <= 533
                        ? (byte)0 : (byte)238;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = shade;
                pixels[offset + 3] = 255;
            }
        var frame = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        frame.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        frame.Freeze();
        var overlay = new SubtitleOverlay();
        try
        {
            overlay.Render(new Drawing.Rectangle(0, 0, width, height),
                new[] { new TextRegion("vertical", new Drawing.Rectangle(480, 250, 38, 108)) },
                new[] { "\u0e17\u0e23\u0e07\u0e1c\u0e21\u0e22\u0e32\u0e27\u0e02\u0e2d\u0e07\u0e1e\u0e23\u0e30\u0e19\u0e32\u0e07\u0e1a\u0e34\u0e2a\u0e40\u0e1a\u0e30" },
                SubtitleStyle.Overwrite, 6, frame, japaneseToThai: true);
            var caption = ((Canvas)overlay.Content).Children.OfType<Border>()
                .Single(border => border.Child is TextBlock && border.Uid != "association-badge");
            Require(((TextBlock)caption.Child).FontSize >= 12,
                "A short Thai caption from a narrow vertical textbox must remain readable.");

            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    if (x < 280 || x >= 720)
                    {
                        int offset = (y * width + x) * 4;
                        pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 255;
                    }
            var noMarginFrame = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            noMarginFrame.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
            noMarginFrame.Freeze();
            overlay.Render(new Drawing.Rectangle(0, 0, width, height),
                new[] { new TextRegion("vertical", new Drawing.Rectangle(480, 250, 38, 108)) },
                new[] { "\u0e17\u0e23\u0e07\u0e1c\u0e21\u0e22\u0e32\u0e27\u0e02\u0e2d\u0e07\u0e1e\u0e23\u0e30\u0e19\u0e32\u0e07\u0e1a\u0e34\u0e2a\u0e40\u0e1a\u0e30" },
                SubtitleStyle.Overwrite, 6, noMarginFrame, japaneseToThai: true);
            Require(((Canvas)overlay.Content).Children.OfType<Border>()
                .Any(border => border.Child is TextBlock && border.Uid != "association-badge"),
                "A page without a usable margin must still show its translation.");

            var roomyRegions = new[] { new TextRegion("short", new Drawing.Rectangle(350, 450, 160, 50)) };
            var roomyTranslations = new[] { "\u0e2a\u0e27\u0e31\u0e2a\u0e14\u0e35" };
            overlay.Render(new Drawing.Rectangle(0, 0, width, height), roomyRegions,
                roomyTranslations, SubtitleStyle.Overwrite, 6, frame, japaneseToThai: true);
            var firstCaption = ((Canvas)overlay.Content).Children.OfType<Border>()
                .Single(border => border.Child is TextBlock);
            overlay.Render(new Drawing.Rectangle(0, 0, width, height), roomyRegions,
                roomyTranslations, SubtitleStyle.Overwrite, 6, frame, japaneseToThai: true);
            var repeatedCaption = ((Canvas)overlay.Content).Children.OfType<Border>()
                .Single(border => border.Child is TextBlock);
            Require(ReferenceEquals(firstCaption, repeatedCaption),
                "A partial update must reuse unchanged caption layout on the same page.");
            roomyTranslations[0] = "\u0e02\u0e2d\u0e1a\u0e04\u0e38\u0e13";
            overlay.Render(new Drawing.Rectangle(0, 0, width, height), roomyRegions,
                roomyTranslations, SubtitleStyle.Overwrite, 6, frame, japaneseToThai: true);
            var updatedCaption = ((Canvas)overlay.Content).Children.OfType<Border>()
                .Single(border => border.Child is TextBlock);
            Require(!ReferenceEquals(firstCaption, updatedCaption)
                && ((TextBlock)updatedCaption.Child).Text == roomyTranslations[0],
                "A changed translation must invalidate its cached caption.");

            overlay.Render(new Drawing.Rectangle(0, 0, width, height), roomyRegions,
                new[] { "..." }, SubtitleStyle.Overwrite, 6, frame, japaneseToThai: true);
            Require(((Canvas)overlay.Content).Children.OfType<Border>()
                .Any(border => border.Tag is 0 && border.Child is TextBlock text && text.Text == "..."),
                "A punctuation-only translation must remain intact inside its source bubble.");

            overlay.Render(new Drawing.Rectangle(0, 0, width, height),
                new[] { new TextRegion("...", new Drawing.Rectangle(480, 250, 3, 3)) },
                new[] { "..." }, SubtitleStyle.Overwrite, 6, frame, japaneseToThai: true);
            Require(((Canvas)overlay.Content).Children.OfType<Border>()
                    .Any(border => border.Tag is 0 && border.Uid != "association-badge"
                        && border.Child is TextBlock text && text.Text == "1. ...")
                && ((Canvas)overlay.Content).Children.OfType<Border>()
                    .Any(border => border.Tag is 0 && border.Uid == "association-badge"),
                "A punctuation-only translation must remain intact in the side-margin fallback.");
        }
        finally { overlay.Close(); }
    }

    private static void CheckVerticalIndex()
    {
        const int width = 960, height = 700;
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)255);
        var frame = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        frame.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        frame.Freeze();
        var regions = new[] { new TextRegion("目次", new Drawing.Rectangle(840, 80, 24, 80)) }
            .Concat(Enumerable.Range(0, 8).Select(i => new TextRegion("その肆三輪山の大物主神",
                new Drawing.Rectangle(800 - i * 28, 140, 24, 420))))
            .Concat(Enumerable.Range(0, 10).Select(i => new TextRegion("歴代天皇系図",
                new Drawing.Rectangle(420 - i * 28, 140, 24, 420)))).ToArray();
        var translations = regions.Select((_, i) => i == 0 ? "สารบัญ" : "เรื่องราวของภูเขาและทะเล").ToArray();
        translations[4] = "その添()";
        var overlay = new SubtitleOverlay();
        try
        {
            overlay.Render(new Drawing.Rectangle(0, 0, width, height), regions, translations,
                SubtitleStyle.Overwrite, 6, frame, japaneseToThai: true);
            var children = ((Canvas)overlay.Content).Children.OfType<Border>().ToArray();
            Require(children.Count(border => border.Uid == "index-panel") == 1
                && children.Count(border => border.Child is TextBlock) == regions.Length
                && children.Count(border => border.Child is null) == regions.Length
                && children.Where(border => border.Child is TextBlock)
                    .All(border => ((TextBlock)border.Child).FontSize >= 12)
                && children.Any(border => border.Tag is 4 && border.Child is TextBlock text
                    && text.Text.Contains("แปลไม่สำเร็จ")),
                "A dense vertical contents page must keep every heading covered and readable in two page columns.");
        }
        finally { overlay.Close(); }
        Console.WriteLine("Dense vertical index layout passed.");
    }

    private static void CheckCometMarginIfAvailable()
    {
        string path = Path.GetFullPath(".cache/diagnostics/comet-page-clean.jpg");
        if (!File.Exists(path)) return;
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(path); image.EndInit(); image.Freeze();
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
        var capture = new Drawing.Rectangle(0, 0, bgra.PixelWidth, bgra.PixelHeight);
        var margin = SubtitleLayout.FindPlainMargin(capture,
            new[] { new Drawing.Rectangle(20, 90, 100, 24) }, pixels);
        if (margin is null || margin.Value.Top < 114 || margin.Value.Height < 400)
            throw new InvalidOperationException("The real Comet page must use a clean side gutter below detected browser-toolbar text.");
        var safeMargin = margin.Value;
        Console.WriteLine($"Comet side margin: {safeMargin.Width}x{safeMargin.Height} at {safeMargin.Left},{safeMargin.Top}.");
    }

    private static void Pump(Application app)
    {
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        // Allow WPF's render thread to submit the new visual frame before native pixel assertions.
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static Drawing.Bitmap Screenshot(Drawing.Rectangle bounds)
    {
        var bitmap = new Drawing.Bitmap(bounds.Width, bounds.Height);
        using var graphics = Drawing.Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Location, Drawing.Point.Empty, bounds.Size);
        return bitmap;
    }
    private static bool Identical(Drawing.Bitmap left, Drawing.Bitmap right)
    {
        // Sample every caption and margin; this also detects capture APIs that return black exclusion holes.
        for (int y = 2; y < left.Height; y += 4)
            for (int x = 2; x < left.Width; x += 4)
                if (left.GetPixel(x, y) != right.GetPixel(x, y)) return false;
        return true;
    }
    private static Drawing.Bitmap VisibleScreenshot(SubtitleOverlay overlay, Drawing.Rectangle bounds)
    {
        var hwnd = new WindowInteropHelper(overlay).Handle;
        Require(SetWindowDisplayAffinity(hwnd, 0), "Cannot enable diagnostic capture.");
        try { DwmFlush(); return Screenshot(bounds); }
        finally { Require(SetWindowDisplayAffinity(hwnd, 0x11), "Cannot restore capture exclusion."); }
    }
    private static int CountGreen(Drawing.Bitmap image)
    {
        int count = 0;
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                var pixel = image.GetPixel(x, y);
                if (pixel.G > pixel.R + 15 && pixel.G > pixel.B + 15) count++;
            }
        return count;
    }

    private static int CountGreen(byte[] pixels)
    {
        int count = 0;
        for (int offset = 0; offset < pixels.Length; offset += 4)
            if (pixels[offset + 1] > 80 && pixels[offset + 1] > pixels[offset + 2] * 1.4
                && pixels[offset + 1] > pixels[offset] * 1.4) count++;
        return count;
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
}
