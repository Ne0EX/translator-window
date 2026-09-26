using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Translumo.Local;
using DrawingColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;

internal static class Program
{
    private const int Width = 80;
    private const int Height = 48;
    private const int Stride = Width * 4;
    private static readonly Rectangle Permitted = new(8, 7, 64, 34);
    private static readonly Rectangle TextRegion = new(23, 16, 34, 16);

    [STAThread]
    private static void Main(string[] args)
    {
        CheckPlainCover();
        CheckGradientCover();
        CheckStripedArtworkFallback();
        CheckInteriorArtworkFallback();
        CheckCompactArtworkFallback();
        CheckThinConnectorFallback();
        CheckArtworkFallback();
        CheckBoundedCover();
        CheckCancellation();
        CheckCaptionStyles();
        Console.WriteLine("PASS: qualified source covers and accessible caption styles.");
        if (args.Length == 2 && args[0] == "--src01") CheckSrc01(args[1]);
    }

    private static void CheckPlainCover()
    {
        byte[] clean = Frame((_, _) => DrawingColor.FromArgb(255, 239, 205, 91));
        byte[] source = (byte[])clean.Clone();
        DrawLettering(source, DrawingColor.FromArgb(255, 38, 31, 24));

        var plan = SourceCover.TryCreate(source, Width, Height, Stride, TextRegion, Permitted);
        Require(plan is { Classification: SourceCoverClass.Plain }, "A plain color balloon must qualify.");
        Require(Permitted.Contains(plan!.FootprintBounds) && TextRegion.Contains(plan.FootprintBounds),
            "The automatic footprint must stay inside the permitted lettering area.");
        byte[] result = plan!.Apply(source, Width, Height, Stride);
        Require(UnchangedOutside(result, source, plan.FootprintBounds),
            "A source cover must preserve every pixel outside its source-text footprint.");
        Require(MeanError(result, clean, plan.FootprintBounds) <= 2,
            "A qualified plain cover must restore the known plain background.");
        byte[] patch = plan.CreatePatch();
        Require(patch.Length == plan.FootprintBounds.Width * plan.FootprintBounds.Height * 4
            && patch.Where((_, index) => index % 4 == 3).Any(alpha => alpha == 0)
            && patch.Where((_, index) => index % 4 == 3).Any(alpha => alpha == 255),
            "The live overlay patch must paint only the inferred lettering footprint.");
    }

    private static void CheckGradientCover()
    {
        byte[] clean = Frame((x, y) => DrawingColor.FromArgb(255,
            188 + y * 42 / (Height - 1), 54 + y * 72 / (Height - 1), 92 + y * 30 / (Height - 1)));
        byte[] source = (byte[])clean.Clone();
        DrawLettering(source, DrawingColor.FromArgb(255, 250, 242, 214));

        var plan = SourceCover.TryCreate(source, Width, Height, Stride, TextRegion, Permitted);
        Require(plan is { Classification: SourceCoverClass.Gradient }, "A smooth color banner gradient must qualify.");
        byte[] result = plan!.Apply(source, Width, Height, Stride);
        Require(UnchangedOutside(result, source, plan.FootprintBounds),
            "Gradient reconstruction must not alter protected pixels.");
        Require(MeanError(result, clean, plan.FootprintBounds) <= 12,
            "A qualified gradient cover must closely reconstruct the known background.");
    }

    private static void CheckArtworkFallback()
    {
        byte[] source = Frame((x, y) => (x / 3 + y / 3) % 2 == 0
            ? DrawingColor.FromArgb(255, 25, 42, 100) : DrawingColor.FromArgb(255, 238, 171, 44));
        DrawLettering(source, DrawingColor.White);
        Require(SourceCover.TryCreate(source, Width, Height, Stride, TextRegion, Permitted) is null,
            "Detailed artwork must keep its source visible and use the readable caption fallback.");
    }

    private static void CheckStripedArtworkFallback()
    {
        byte[] source = Frame((_, y) => y / 2 % 2 == 0
            ? DrawingColor.FromArgb(255, 188, 54, 92) : DrawingColor.FromArgb(255, 88, 118, 188));
        DrawLettering(source, DrawingColor.White);
        Require(SourceCover.TryCreate(source, Width, Height, Stride, TextRegion, Permitted) is null,
            "Striped artwork must not qualify as a smooth linear gradient.");
    }

    private static void CheckInteriorArtworkFallback()
    {
        byte[] source = Frame((_, _) => DrawingColor.FromArgb(255, 239, 205, 91));
        for (int y = 19; y < 28; y++)
            for (int x = 32; x < 46; x++) Set(source, x, y, DrawingColor.FromArgb(255, 38, 31, 24));
        Require(SourceCover.TryCreate(source, Width, Height, Stride, TextRegion, Permitted) is null,
            "A large interior logo must not be mistaken for source lettering.");
    }

    private static void CheckCompactArtworkFallback()
    {
        byte[] source = Frame((_, _) => DrawingColor.FromArgb(255, 239, 205, 91));
        for (int y = 18; y < 24; y++)
            for (int x = 27; x < 33; x++) Set(source, x, y, DrawingColor.FromArgb(255, 38, 31, 24));
        for (int y = 24; y < 30; y++)
            for (int x = 45; x < 51; x++) Set(source, x, y, DrawingColor.FromArgb(255, 38, 31, 24));
        Require(SourceCover.TryCreate(source, Width, Height, Stride, TextRegion, Permitted) is null,
            "Small disconnected artwork must not be mistaken for source lettering.");
    }

    private static void CheckThinConnectorFallback()
    {
        byte[] source = Frame((_, _) => DrawingColor.FromArgb(255, 239, 205, 91));
        DrawLettering(source, DrawingColor.FromArgb(255, 38, 31, 24));
        for (int x = TextRegion.Left + 1; x < TextRegion.Right - 1; x++)
            Set(source, x, TextRegion.Top, DrawingColor.FromArgb(255, 38, 31, 24));
        Require(SourceCover.TryCreate(source, Width, Height, Stride, TextRegion, Permitted) is null,
            "A chart connector spanning a text region must remain visible.");
    }

    private static void CheckBoundedCover()
    {
        const int width = 1002, height = 1000, stride = width * 4;
        var source = new byte[stride * height];
        Require(SourceCover.TryCreate(source, width, height, stride,
                new Rectangle(2, 2, width - 4, height - 4), new Rectangle(0, 0, width, height)) is null,
            "Oversized reconstruction must use the source-visible fallback instead of blocking the UI thread.");
    }

    private static void CheckCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        bool observed = false;
        try
        {
            SourceCover.TryCreate(Frame((_, _) => DrawingColor.White), Width, Height, Stride,
                TextRegion, Permitted, cancelled.Token);
        }
        catch (OperationCanceledException) { observed = true; }
        Require(observed, "Source cover qualification must observe cancellation.");

        byte[] source = Frame((_, _) => DrawingColor.FromArgb(255, 239, 205, 91));
        DrawLettering(source, DrawingColor.FromArgb(255, 38, 31, 24));
        var plan = SourceCover.TryCreate(source, Width, Height, Stride, TextRegion, Permitted)!;
        observed = false;
        try { plan.CreatePatch(cancelled.Token); }
        catch (OperationCanceledException) { observed = true; }
        Require(observed, "Source cover patch creation must observe cancellation.");
    }

    private static void CheckCaptionStyles()
    {
        var dialogue = CaptionStyles.Resolve(new CaptionStyleOptions(), new[] { "Leelawadee UI", "Segoe UI" });
        var narration = CaptionStyles.Resolve(new CaptionStyleOptions(CaptionRole.Narration), new[] { "Leelawadee UI" });
        var emphasis = CaptionStyles.Resolve(new CaptionStyleOptions(CaptionRole.Emphasis), new[] { "Leelawadee UI" });
        Require(dialogue.Role == CaptionRole.Dialogue && dialogue.Weight == FontWeights.Normal,
            "Existing settings must default to the readable dialogue preset.");
        Require(narration.Weight == FontWeights.SemiBold && emphasis.Weight == FontWeights.Bold,
            "Narration and emphasis presets must preserve body/headline hierarchy.");
        Require(emphasis.Effect == CaptionEffect.Shadow && emphasis.Foreground == Colors.White,
            "The emphasis preset must remain legible on color backgrounds.");

        var automatic = CaptionStyles.Resolve(new CaptionStyleOptions(CaptionRole.Auto), new[] { "Leelawadee UI" });
        var headline = CaptionStyles.ForRegion(automatic,
            new TextRegion("wide heading", new Rectangle(10, 10, 360, 48)));
        var body = CaptionStyles.ForRegion(automatic,
            new TextRegion("dialogue", new Rectangle(10, 80, 160, 54)));
        Require(headline.Role == CaptionRole.Emphasis && headline.Weight == FontWeights.Bold
            && body.Role == CaptionRole.Dialogue && body.Weight == FontWeights.Normal,
            "Automatic styling must keep body text readable and give only an obvious wide heading emphasis.");
        Require(CaptionStyles.ForRegion(dialogue,
                new TextRegion("explicit override", new Rectangle(10, 10, 360, 48))).Role == CaptionRole.Dialogue,
            "An explicit caption role must override automatic source cues.");

        var custom = CaptionStyles.Resolve(new CaptionStyleOptions(CaptionRole.Dialogue, "Missing Thai Font",
            CaptionWeight.Bold, "#FF0066CC", CaptionEffect.Shadow), new[] { "Leelawadee UI" });
        Require(custom.Typeface == "Leelawadee UI" && custom.Weight == FontWeights.Bold
            && custom.Foreground == MediaColor.FromRgb(0, 102, 204),
            "Overrides must retain weight/color while a missing typeface falls back to a Thai-capable font.");

        string oldSettings = "{\"Source\":\"ja\",\"Target\":\"th\",\"Overwrite\":true}";
        var compatible = JsonSerializer.Deserialize<LocalWindowSettings>(oldSettings)!;
        Require(compatible.CaptionRole == "Dialogue" && compatible.CaptionTypeface == "Leelawadee UI"
            && compatible.CaptionWeight == "Normal" && compatible.CaptionForeground == "#FF000000"
            && compatible.CaptionEffect == "Outline" && compatible.Overwrite,
            "A missing caption-style setting must retain the dialogue default for existing installations.");
    }

    private static void CheckSrc01(string path)
    {
        using var bitmap = new Bitmap(Path.GetFullPath(path));
        int stride = bitmap.Width * 4;
        var pixels = new byte[stride * bitmap.Height];
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < bitmap.Height; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * stride, stride);
        }
        finally { bitmap.UnlockBits(data); }
        var watch = Stopwatch.StartNew();
        var plain = SourceCover.TryCreate(pixels, bitmap.Width, bitmap.Height, stride,
            new Rectangle(427, 181, 124, 175), new Rectangle(399, 153, 180, 226));
        long plainMs = watch.ElapsedMilliseconds;
        watch.Restart();
        var gradient = SourceCover.TryCreate(pixels, bitmap.Width, bitmap.Height, stride,
            new Rectangle(414, 1247, 148, 67), new Rectangle(383, 1228, 207, 101));
        long gradientMs = watch.ElapsedMilliseconds;
        watch.Restart();
        var artwork = SourceCover.TryCreate(pixels, bitmap.Width, bitmap.Height, stride,
            new Rectangle(326, 449, 601, 88), new Rectangle(308, 431, 637, 124));
        long artworkMs = watch.ElapsedMilliseconds;
        Console.WriteLine($"SRC01 probe: plain={plain?.Classification.ToString() ?? "fallback"}; "
            + $"gradient={gradient?.Classification.ToString() ?? "fallback"}; artwork={artwork?.Classification.ToString() ?? "fallback"}.");
        Require(plain is { Classification: SourceCoverClass.Plain }, "SRC01's upper dialogue balloon must qualify as plain.");
        Require(gradient is null, "SRC01's decorated lower button must remain source-visible.");
        Require(artwork is null, "SRC01's large lettering over faces and hair must remain source-visible.");
        Console.WriteLine($"SRC01 support: plain={plainMs} ms/{plain!.CoveredPixelCount} px; "
            + $"decorated gradient=fallback ({gradientMs} ms); detailed artwork=fallback ({artworkMs} ms); CPU only.");
    }

    private static byte[] Frame(Func<int, int, DrawingColor> colorAt)
    {
        var pixels = new byte[Stride * Height];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++) Set(pixels, x, y, colorAt(x, y));
        return pixels;
    }

    private static void DrawLettering(byte[] pixels, DrawingColor color)
    {
        foreach (int left in new[] { 26, 35, 44, 51 })
        {
            for (int y = 18; y < 30; y++)
                for (int x = left; x < left + 3; x++) Set(pixels, x, y, color);
            for (int y = 23; y < 26; y++)
                for (int x = left; x < left + 7 && x < TextRegion.Right; x++) Set(pixels, x, y, color);
        }
    }

    private static bool UnchangedOutside(byte[] actual, byte[] expected, Rectangle changed)
    {
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                if (!changed.Contains(x, y) && !Pixel(actual, x, y).SequenceEqual(Pixel(expected, x, y))) return false;
        return true;
    }

    private static double MeanError(byte[] actual, byte[] expected, Rectangle area)
    {
        long error = 0;
        int samples = 0;
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
            {
                var a = Pixel(actual, x, y); var e = Pixel(expected, x, y);
                error += Math.Abs(a[0] - e[0]) + Math.Abs(a[1] - e[1]) + Math.Abs(a[2] - e[2]);
                samples += 3;
            }
        return samples == 0 ? double.MaxValue : error / (double)samples;
    }

    private static Span<byte> Pixel(byte[] pixels, int x, int y) => pixels.AsSpan(y * Stride + x * 4, 4);
    private static void Set(byte[] pixels, int x, int y, DrawingColor color)
    {
        var pixel = Pixel(pixels, x, y);
        pixel[0] = color.B; pixel[1] = color.G; pixel[2] = color.R; pixel[3] = color.A;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
