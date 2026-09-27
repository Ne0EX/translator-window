using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Translumo.Local;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

internal static class Program
{
    private static readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        options.Converters.Add(new BoxConverter());
        return options;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (Value(args, "--processing") is { } manifest)
                RunProcessing(manifest, Value(args, "--root") ?? Directory.GetCurrentDirectory(),
                    Value(args, "--diagnostics"));
            else
                RunFixed(Value(args, "--fixtures") ?? Path.Combine(AppContext.BaseDirectory, "fixtures", "cases.json"),
                    Value(args, "--diagnostics"), args.Contains("--performance"));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void RunFixed(string fixturePath, string? diagnostics, bool performance)
    {
        var suite = Read<ReplaySuite>(fixturePath);
        Require(suite.SchemaVersion == 1 && suite.Cases.Length > 0, "Replay fixture schema is unsupported or empty.");
        foreach (var fixture in suite.Cases)
        {
            BitmapSource frame;
            if (fixture.CapturedView is { } captured)
            {
                using var bitmap = new Drawing.Bitmap(Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(fixturePath))!, captured)));
                frame = ToBitmapSource(bitmap);
                Require(frame.PixelWidth == fixture.Width && frame.PixelHeight == fixture.Height,
                    $"{fixture.Id}: captured image dimensions must match the fixture.");
            }
            else frame = BuildFrame(fixture);
            var first = Replay(fixture, frame, performance);
            var second = Replay(fixture, frame);
            Require(JsonSerializer.Serialize(first.Items.OrderBy(item => item.Kind).ThenBy(item => item.RegionId))
                == JsonSerializer.Serialize(second.Items.OrderBy(item => item.Kind).ThenBy(item => item.RegionId)),
                $"{fixture.Id}: repeated geometry, lines, font or color differed.");
            Require(first.RenderHash == second.RenderHash,
                $"{fixture.Id}: repeated renderer pixels differed.");
            if (diagnostics is not null) WriteDiagnostics(diagnostics, fixture, frame, first, "fixed-text");
            Verify(fixture, first);
            if (fixture.Background == "display-band")
            {
                int halo = (196 * fixture.Width + 333) * 4;
                Require(Pixels(first.Rendered)[halo + 1] > 220,
                    "Heading cover must remove the old red glyph halo beyond the detection rectangle.");
                var guarded = fixture with { LocalHeadings = null, SourceInkPoints = null,
                    Regions = fixture.Regions.Concat(new[] {
                        new RegionFixture("pending", new Box(224, 205, 3, 12), "pending", "") }).ToArray() };
                var pending = Replay(guarded, frame, allowMissing: true);
                int offset = (205 * fixture.Width + 225) * 4;
                Require(Pixels(frame).AsSpan(offset, 4).SequenceEqual(Pixels(pending.Rendered).AsSpan(offset, 4)),
                    "Heading cover must preserve ink belonging to a pending overlapping region.");
            }
            if (fixture.Background == "bubble")
            {
                var narrowDetection = fixture with { Regions = new[] {
                    fixture.Regions[0] with { Bounds = new Box(282, 143, 32, 120),
                        Translation = "ฉันจะใช้ความสามารถทั้งหมดที่มี เพื่อทำให้ความปรารถนาของฉันเป็นจริง" } } };
                var narrowResult = Replay(narrowDetection, BuildFrame(narrowDetection));
                Verify(narrowDetection, narrowResult);
                var inkAtSeed = Pixels(frame);
                Paint(inkAtSeed, fixture.Width, fixture.Height, new Drawing.Rectangle(255, 197, 4, 12),
                    (_, _) => ((byte)18, (byte)18, (byte)18));
                var inkFrame = BitmapSource.Create(fixture.Width, fixture.Height, fixture.Dpi, fixture.Dpi,
                    PixelFormats.Bgra32, null, inkAtSeed, fixture.Width * 4);
                inkFrame.Freeze();
                Verify(fixture, Replay(fixture, inkFrame));
                var offCenter = fixture with { Regions = new[] {
                    fixture.Regions[0] with { Bounds = new Box(300, 155, 40, 92) } } };
                var centered = Replay(offCenter, frame);
                var dialogue = centered.Items.Single(item => item.Kind == "caption");
                Require(Math.Abs(dialogue.Bounds.X + dialogue.Bounds.Width / 2d - 297.5) <= 4
                    && Math.Abs(dialogue.Bounds.Y + dialogue.Bounds.Height / 2d - 202.5) <= 4,
                    "Bubble captions must center in the bubble interior even when detection is off-center.");
                var darkPixels = Pixels(inkFrame);
                for (int pixelOffset = 0; pixelOffset < darkPixels.Length; pixelOffset += 4)
                {
                    if (darkPixels[pixelOffset] != darkPixels[pixelOffset + 1]
                        || darkPixels[pixelOffset] != darkPixels[pixelOffset + 2]) continue;
                    byte shade = darkPixels[pixelOffset] == 248 ? (byte)24
                        : darkPixels[pixelOffset] == 18 ? (byte)235 : darkPixels[pixelOffset];
                    darkPixels[pixelOffset] = darkPixels[pixelOffset + 1] = darkPixels[pixelOffset + 2] = shade;
                }
                var darkFrame = BitmapSource.Create(fixture.Width, fixture.Height, fixture.Dpi, fixture.Dpi,
                    PixelFormats.Bgra32, null, darkPixels, fixture.Width * 4);
                darkFrame.Freeze();
                var darkBubble = Replay(fixture with { SourceInkPoints = null }, darkFrame);
                Require(Pixels(darkBubble.Rendered)[(128 * fixture.Width + 293) * 4] == 24
                    && darkBubble.Items.Single(item => item.Kind == "caption").Foreground == "#FFFFFFFF",
                    "Bubble sampling must retain dark paper and contrasting light captions when a seed hits ink.");
                var progressive = fixture with { Regions = fixture.Regions.Concat(new[] {
                    new RegionFixture("pending", new Box(291, 123, 4, 12), "pending", "") }).ToArray() };
                var pending = Replay(progressive, frame, allowMissing: true);
                int offset = (128 * fixture.Width + 293) * 4;
                Require(Pixels(pending.Rendered)[offset] == 18,
                    "Progressive bubble fill must not conceal a different passage still awaiting translation.");
            }
            Console.WriteLine($"PASS {fixture.Id}: {first.Items.Count(item => item.Kind == "caption")} captions; render {first.RenderHash[..12]}.");
        }
        Console.WriteLine($"PASS fixed-text replay: {suite.Cases.Length} captured views repeated exactly.");
    }

    private static void RunProcessing(string manifestPath, string root, string? diagnostics)
    {
        var input = Read<ProcessingInput>(manifestPath);
        string viewPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, input.CapturedView));
        using var bitmap = new Drawing.Bitmap(viewPath);
        var frame = ToBitmapSource(bitmap);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var ocr = new SpatialOcr(Path.Combine(root, "models", "tessdata"),
            Path.Combine(root, ".venv", "Scripts", "python.exe"),
            Path.Combine(root, "local-ocr", "worker.py"),
            Path.Combine(root, "models", "comic-text-detector", "comictextdetector.onnx"));
        Box[]? detected = null;
        IReadOnlyList<TextRegion> regions;
        try
        {
            regions = ocr.RecognizeProgressiveAsync(bitmap, input.SourceLanguage, cancellation.Token, progress => {
                detected ??= progress.Select(region => Box.From(region.Bounds)).ToArray();
                return System.Threading.Tasks.Task.CompletedTask;
            }).GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            WriteFailureDiagnostics(diagnostics, input, frame, detected, null, null, "text-recognition", error);
            throw;
        }
        using var translator = new LocalTranslator(Path.Combine(root, ".venv", "Scripts", "python.exe"),
            Path.Combine(root, "local-model", "worker.py"), Path.Combine(root, "models", "hy-mt2"));
        string[] translations;
        try
        {
            translations = translator.TranslateAsync(regions.Select(region => region.Text).ToArray(),
                input.SourceLanguage, input.TargetLanguage, cancellation.Token).GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            WriteFailureDiagnostics(diagnostics, input, frame, detected, regions, null, "translation", error);
            throw;
        }
        var processingRegions = regions.Select((region, index) => new RegionFixture(index.ToString(), Box.From(region.Bounds),
            region.Text, translations[index])).ToArray();
        var fixture = new ReplayCase(input.Id, "complete-processing", bitmap.Width, bitmap.Height, input.Dpi,
            input.Zoom, input.SourceLanguage, input.TargetLanguage, "local-capture",
            processingRegions, Array.Empty<ContainerFixture>(), input.ProtectedArtwork,
            input.IntendedPassages.Select(passage => passage.RegionId is not null ? passage : passage with {
                RegionId = processingRegions.FirstOrDefault(region => region.RecognizedText == passage.SourceText)?.Id
            }).ToArray(), false, false, detected ?? Array.Empty<Box>(), AutoStyle: input.AutoStyle);
        ReplayResult result;
        try { result = Replay(fixture, frame); }
        catch (SubtitleLayoutException error)
        {
            WriteFailureDiagnostics(diagnostics, input, frame, detected, regions, translations,
                "caption-placement", error);
            throw;
        }
        if (diagnostics is not null) WriteDiagnostics(diagnostics, fixture, frame, result, "complete-processing");
        Verify(fixture, result);
        Console.WriteLine($"PASS complete processing: {regions.Count} recognized regions reached {result.Items.Count(item => item.Kind == "caption")} captions.");
    }

    private static void WriteFailureDiagnostics(string? directory, ProcessingInput input, BitmapSource frame,
        Box[]? detected, IReadOnlyList<TextRegion>? regions, string[]? translations, string stage, Exception error)
    {
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        SavePng(frame, Path.Combine(directory, input.Id + ".captured-view.png"));
        File.WriteAllText(Path.Combine(directory, input.Id + ".json"), JsonSerializer.Serialize(new {
            schemaVersion = 1,
            input.Id,
            mode = "complete-processing",
            commit = Commit(),
            captureTargetKind = "unknown",
            capturedViewEvidence = input.Id + ".captured-view.png",
            captureBounds = new[] { 0, 0, frame.PixelWidth, frame.PixelHeight },
            input.Dpi,
            input.Zoom,
            input.SourceLanguage,
            input.TargetLanguage,
            detectedRegions = detected,
            regions,
            translations,
            intendedPassages = input.IntendedPassages,
            stageOutcomes = input.IntendedPassages.Select(passage => new {
                passage.Id,
                observedStage = FailureStage(passage, detected, regions, stage)
            }),
            protectedArtwork = input.ProtectedArtwork,
            failureStage = stage,
            fallbackReason = error.Message
        }, _jsonOptions));
    }

    private static ReplayResult Replay(ReplayCase fixture, BitmapSource frame, bool performance = false, bool allowMissing = false)
    {
        var screen = Forms.Screen.PrimaryScreen?.Bounds ?? throw new InvalidOperationException("No primary display is available.");
        Require(fixture.Width <= screen.Width && fixture.Height <= screen.Height,
            $"{fixture.Id}: the synthetic captured view must fit the primary display.");
        var capture = new Drawing.Rectangle(screen.X, screen.Y, fixture.Width, fixture.Height);
        var regions = fixture.Regions.Select(region => new TextRegion(region.RecognizedText, region.Bounds.Drawing)).ToArray();
        var overlay = new SubtitleOverlay();
        try
        {
            bool japaneseToThai = fixture.SourceLanguage.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
                && fixture.TargetLanguage.StartsWith("th", StringComparison.OrdinalIgnoreCase);
            var captionStyle = fixture.AutoStyle || fixture.Background == "colored-ink"
                ? CaptionStyles.ResolveInstalled(new CaptionStyleOptions(CaptionRole.Auto)) : null;
            var firstRender = Stopwatch.StartNew();
            overlay.Render(capture, regions, fixture.Regions.Select(region => region.Translation).ToArray(),
                SubtitleStyle.Overwrite, 6, frame, japaneseToThai, allowMissingTranslations: allowMissing,
                captionStyle: captionStyle);
            overlay.UpdateLayout();
            firstRender.Stop();
            if (performance)
            {
                Console.WriteLine($"TIMING {fixture.Id}: first layout={firstRender.Elapsed.TotalMilliseconds:F2} ms.");
                var translations = fixture.Regions.Select(region => region.Translation).ToArray();
                var elapsed = new double[9];
                for (int iteration = 0; iteration < elapsed.Length; iteration++)
                {
                    var nextFrame = frame.Clone();
                    nextFrame.Freeze();
                    var watch = Stopwatch.StartNew();
                    overlay.Render(capture, regions, translations, SubtitleStyle.Overwrite, 6, nextFrame,
                        japaneseToThai, captionStyle: captionStyle);
                    overlay.UpdateLayout();
                    elapsed[iteration] = watch.Elapsed.TotalMilliseconds;
                    frame = nextFrame;
                }
                Array.Sort(elapsed);
                Console.WriteLine($"TIMING {fixture.Id}: repeated captured view median={elapsed[4]:F2} ms; worst={elapsed[^1]:F2} ms.");
                Require(elapsed[4] <= 1000d / 60,
                    $"{fixture.Id}: median repeated-view layout must fit one 60 Hz frame (16.67 ms), observed {elapsed[4]:F2} ms.");
            }
            var canvas = (Canvas)overlay.Content;
            var desktop = Forms.SystemInformation.VirtualScreen;
            var transform = PresentationSource.FromVisual(overlay)?.CompositionTarget?.TransformFromDevice
                ?? throw new InvalidOperationException("Subtitle overlay has no display transform.");
            var visuals = canvas.Children.OfType<Border>().ToArray();
            var items = visuals.Select(border => SceneItem.From(border, fixture, capture, desktop, transform)).ToArray();
            if (fixture.LocalHeadings is { Length: > 0 } headings)
            {
                foreach (string id in headings)
                {
                    int index = Array.FindIndex(fixture.Regions, region => region.Id == id);
                    Require(items.Any(item => item.RegionId == id && item.Kind == "caption"
                        && item.FallbackReason != "readable-margin"),
                        $"{fixture.Id}/{id}: the live heading still fell back to a margin caption.");
                    var heading = visuals.Single(border => Equals(border.Tag, index) && border.Child is TextBlock);
                    var text = (TextBlock)heading.Child;
                    Require(text.Foreground is LinearGradientBrush && text.Background is DrawingBrush
                        && text.Effect is System.Windows.Media.Effects.DropShadowEffect,
                        $"{fixture.Id}/{id}: heading needs a sampled fill, solid glyph stroke and glow.");
                }
                overlay.Render(capture, regions, fixture.Regions.Select(region => region.Translation).ToArray(),
                    SubtitleStyle.Overwrite, 6, frame, japaneseToThai, captionStyle: captionStyle);
                overlay.UpdateLayout();
                Require(visuals.Where(border => border.Child is TextBlock).All(border => canvas.Children.Contains(border)),
                    $"{fixture.Id}: unchanged headings must reuse their visuals.");
            }
            if (fixture.SourceInkPoints is { } points)
            {
                var coverIndices = Enumerable.Range(0, items.Length).Where(i => items[i].Kind == "cover").ToArray();
                var covered = Pixels(RenderCapturedView(frame, coverIndices.Select(i => visuals[i]).ToArray(),
                    coverIndices.Select(i => items[i]).ToArray(), fixture.Width, fixture.Height));
                foreach (var point in points)
                    Require(!(covered[(point.Y * fixture.Width + point.X) * 4 + 1] > 175
                        && covered[(point.Y * fixture.Width + point.X) * 4 + 1]
                            > covered[(point.Y * fixture.Width + point.X) * 4] + 60),
                        $"{fixture.Id}: original heading ink remains at {point.X},{point.Y} before drawing Thai: "
                        + string.Join(",", covered.Skip((point.Y * fixture.Width + point.X) * 4).Take(4)));
            }
            var rendered = RenderCapturedView(frame, visuals, items, fixture.Width, fixture.Height);
            if (fixture.LocalHeadings is { Length: > 0 })
            {
                var custom = CaptionStyles.ResolveInstalled(new CaptionStyleOptions(CaptionRole.Auto,
                    Foreground: "#0066FF", Effect: CaptionEffect.None));
                overlay.Render(capture, regions, fixture.Regions.Select(region => region.Translation).ToArray(),
                    SubtitleStyle.Overwrite, 6, frame, japaneseToThai, captionStyle: custom);
                overlay.UpdateLayout();
                foreach (var text in canvas.Children.OfType<Border>()
                    .Where(border => border.Tag is int index && fixture.LocalHeadings.Contains(fixture.Regions[index].Id))
                    .Select(border => border.Child).OfType<TextBlock>())
                    Require(text.Foreground is SolidColorBrush color && color.Color == Color.FromRgb(0, 102, 255)
                        && text.Effect is null && text.Background is null,
                        $"{fixture.Id}: explicit color and no-effect settings must replace the automatic treatment.");
                if (fixture.Background == "display-band")
                {
                    overlay.Render(capture, regions, fixture.Regions.Select(region => region.Translation).ToArray(),
                        SubtitleStyle.Overwrite, 6, frame, japaneseToThai, captionStyle: captionStyle);
                    var changedPixels = Pixels(frame);
                    foreach (var region in fixture.Regions)
                        Paint(changedPixels, fixture.Width, fixture.Height, region.Bounds.Drawing,
                            (_, _) => ((byte)248, (byte)248, (byte)248));
                    var changed = BitmapSource.Create(fixture.Width, fixture.Height, fixture.Dpi, fixture.Dpi,
                        PixelFormats.Bgra32, null, changedPixels, fixture.Width * 4);
                    changed.Freeze();
                    overlay.Render(capture, regions, fixture.Regions.Select(region => region.Translation).ToArray(),
                        SubtitleStyle.Overwrite, 6, changed, japaneseToThai, captionStyle: captionStyle);
                    overlay.UpdateLayout();
                    Require(canvas.Children.OfType<Border>().Select(border => border.Child).OfType<TextBlock>()
                        .All(text => text.Foreground is not LinearGradientBrush),
                        $"{fixture.Id}: changing source pixels must invalidate the old display treatment.");
                }
                overlay.Clear();
                Require(canvas.Children.Count == 0 && !overlay.IsVisible,
                    $"{fixture.Id}: Stop must clear the styled caption layer.");
            }
            string hash = Convert.ToHexString(SHA256.HashData(Pixels(rendered)));
            return new ReplayResult(items, hash, rendered);
        }
        finally { overlay.Close(); }
    }

    private static void Verify(ReplayCase fixture, ReplayResult result)
    {
        if (fixture.Background == "bubble")
        {
            var pixels = Pixels(result.Rendered);
            foreach (var point in new[] { new Drawing.Point(293, 128), new Drawing.Point(293, 277) })
            {
                int offset = (point.Y * fixture.Width + point.X) * 4;
                Require(pixels[offset] == 248 && pixels[offset + 1] == 248 && pixels[offset + 2] == 248,
                    "Enclosed bubble: source lettering outside the detection must be covered with its sampled fill.");
            }
            var source = Pixels(BuildFrame(fixture));
            for (int y = 0; y < fixture.Height; y++)
            for (int x = 0; x < fixture.Width; x++)
                if (Math.Pow((x - 297.5) / 77.5, 2) + Math.Pow((y - 202.5) / 97.5, 2) >= 0.94)
                {
                    int offset = (y * fixture.Width + x) * 4;
                    Require(source.AsSpan(offset, 4).SequenceEqual(pixels.AsSpan(offset, 4)),
                        "Enclosed bubble: outline and exterior artwork must remain unchanged.");
                }
        }
        var image = new Drawing.Rectangle(0, 0, fixture.Width, fixture.Height);
        Require(result.Items.All(item => image.Contains(item.Bounds.Drawing)),
            $"{fixture.Id}: caption layer escaped the captured view.");
        foreach (var region in fixture.Regions)
        {
            var caption = result.Items.SingleOrDefault(item => item.Kind == "caption" && item.RegionId == region.Id);
            Require(caption is not null, $"{fixture.Id}/{region.Id}: translation did not reach a caption.");
            Require(CaptionText(caption!.Text) == region.Translation,
                $"{fixture.Id}/{region.Id}: rendered text was shortened or changed.");
            Require(caption.FontSize >= 12, $"{fixture.Id}/{region.Id}: normal Thai caption fell below 12 DIP.");
            if (fixture.LocalHeadings?.Contains(region.Id) == true)
                Require(caption.FallbackReason is null && caption.Bounds.Drawing.IntersectsWith(region.Bounds.Drawing),
                    $"{fixture.Id}/{region.Id}: reference heading must be typeset at its source, not in a margin.");
            if (fixture.Background == "dark")
                Require(caption.Foreground == "#FFFFFFFF", "Dark source fill needs a contrasting light caption.");
            if (fixture.Background == "colored-ink")
                Require(caption.Foreground == "#FFC82040", "Automatic caption style must retain the dominant red source ink.");
            var container = fixture.TextContainers.SingleOrDefault(value => value.RegionId == region.Id);
            if (container?.RequireCaptionInside == true)
            {
                Require(container.Bounds.Drawing.Contains(caption.Bounds.Drawing),
                    $"{fixture.Id}/{region.Id}: caption escaped its annotated text container.");
                Require(result.Items.Where(item => item.Kind == "cover" && item.RegionId == region.Id)
                    .All(item => container.Bounds.Drawing.Contains(item.Bounds.Drawing)),
                    $"{fixture.Id}/{region.Id}: source cover escaped its annotated text container.");
            }
        }
        var captions = result.Items.Where(item => item.Kind == "caption").ToArray();
        Require(!captions.Where((caption, index) => captions.Skip(index + 1)
                .Any(other => caption.Bounds.Drawing.IntersectsWith(other.Bounds.Drawing))).Any(),
            $"{fixture.Id}: captions collided.");
        foreach (var protectedArea in fixture.ProtectedArtwork)
            Require(result.Items.All(item => !item.Bounds.Drawing.IntersectsWith(protectedArea.Bounds.Drawing)),
                $"{fixture.Id}: caption layer intersected protected artwork {protectedArea.Id}.");
        if (fixture.ExpectMarginCaption)
        {
            Require(captions.Where(item => item.FallbackReason == "readable-margin")
                    .All(item => item.FontSize >= 16 && item.Foreground == "#FF000000"
                        && item.Background == "#FFFFFFFF"),
                $"{fixture.Id}: side captions need at least 16 DIP and opaque black-on-white reading cards.");
            Require(result.Items.Any(item => item.Kind == "caption" && item.FallbackReason == "readable-margin"),
                $"{fixture.Id}: dense content did not use its readable margin.");
            Require(fixture.Regions.All(region => result.Items.Count(item => item.Kind == "association-badge"
                    && item.RegionId == region.Id) == 1),
                $"{fixture.Id}: every margin caption needs exactly one associated badge.");
        }
        if (fixture.ExpectColoredCaption)
            Require(result.Items.Where(item => item.Kind == "caption").All(item => item.Background != "#FFFFFFFF"),
                $"{fixture.Id}: colored balloon was replaced with a broad white caption.");
        foreach (var passage in fixture.IntendedPassages)
        {
            string stage = Stage(fixture, result, passage);
            Require(stage == passage.ExpectedStage,
                $"{fixture.Id}/{passage.Id}: expected {passage.ExpectedStage}, observed {stage}.");
        }
    }

    private static void WriteDiagnostics(string directory, ReplayCase fixture, BitmapSource frame,
        ReplayResult result, string mode)
    {
        Directory.CreateDirectory(directory);
        SavePng(frame, Path.Combine(directory, fixture.Id + ".captured-view.png"));
        SavePng(result.Rendered, Path.Combine(directory, fixture.Id + ".rendered.png"));
        var report = new {
            fixture.SchemaVersion,
            fixture.Id,
            mode,
            commit = Commit(),
            captureTargetKind = mode == "fixed-text" ? "selected-area" : "unknown",
            capturedViewEvidence = fixture.Id + ".captured-view.png",
            captureBounds = new[] { 0, 0, fixture.Width, fixture.Height },
            fixture.Dpi,
            fixture.Zoom,
            fixture.SourceLanguage,
            fixture.TargetLanguage,
            regions = fixture.Regions,
            detectedRegions = fixture.DetectedRegions,
            intendedPassages = fixture.IntendedPassages,
            stageOutcomes = fixture.IntendedPassages.Select(passage => new {
                passage.Id,
                observedStage = Stage(fixture, result, passage)
            }),
            textContainers = fixture.TextContainers,
            protectedArtwork = fixture.ProtectedArtwork,
            captions = result.Items,
            result.RenderHash
        };
        File.WriteAllText(Path.Combine(directory, fixture.Id + ".json"), JsonSerializer.Serialize(report, _jsonOptions));
    }

    private static BitmapSource BuildFrame(ReplayCase fixture)
    {
        var pixels = new byte[checked(fixture.Width * fixture.Height * 4)];
        for (int y = 0; y < fixture.Height; y++)
        for (int x = 0; x < fixture.Width; x++)
        {
            (byte r, byte g, byte b) = fixture.Background switch {
                "dark" => ((byte)24, (byte)24, (byte)24),
                "colored" => ((byte)176, (byte)32, (byte)81),
                "gradient" => ((byte)(68 + 18 * x / fixture.Width), (byte)(98 + 18 * x / fixture.Width), (byte)(170 + 18 * x / fixture.Width)),
                "navigation-gutters" when x < 300 || x >= fixture.Width - 300 => ((byte)120, (byte)120, (byte)120),
                "navigation-gutters" => (x / 12 + y / 12) % 2 == 0
                    ? ((byte)30, (byte)30, (byte)30) : ((byte)225, (byte)225, (byte)225),
                "dense-margins-window-border" when x < 8 || x >= fixture.Width - 16 => ((byte)24, (byte)24, (byte)24),
                "dense-margins" or "dense-margins-window-border" when x < 170 || x >= fixture.Width - 170 => ((byte)120, (byte)120, (byte)120),
                "dense-margins" or "dense-margins-window-border" when (x / 12 + y / 12) % 2 == 0 => ((byte)30, (byte)30, (byte)30),
                "dense-margins" or "dense-margins-window-border" => ((byte)225, (byte)225, (byte)225),
                _ => ((byte)248, (byte)248, (byte)248)
            };
            Set(pixels, fixture.Width, x, y, r, g, b);
        }
        foreach (var area in fixture.ProtectedArtwork)
            Paint(pixels, fixture.Width, fixture.Height, area.Bounds.Drawing, (x, y) =>
                (x / 5 + y / 5) % 2 == 0 ? ((byte)25, (byte)65, (byte)105) : ((byte)210, (byte)160, (byte)40));
        if (fixture.Background is "bubble" or "small-bubble")
        {
            var bubble = fixture.TextContainers[0].Bounds.Drawing;
            Paint(pixels, fixture.Width, fixture.Height, bubble, (x, y) => {
                double radius = Math.Pow((x - bubble.Left - bubble.Width / 2d) / (bubble.Width / 2d), 2)
                    + Math.Pow((y - bubble.Top - bubble.Height / 2d) / (bubble.Height / 2d), 2);
                return radius >= 0.94 && radius <= 1 ? ((byte)18, (byte)18, (byte)18)
                    : ((byte)248, (byte)248, (byte)248);
            });
            if (fixture.Background == "bubble")
            {
                Paint(pixels, fixture.Width, fixture.Height, new Drawing.Rectangle(291, 123, 4, 12),
                    (_, _) => ((byte)18, (byte)18, (byte)18));
                Paint(pixels, fixture.Width, fixture.Height, new Drawing.Rectangle(291, 272, 4, 12),
                    (_, _) => ((byte)223, (byte)223, (byte)223));
            }
        }
        foreach (var region in fixture.Regions)
        {
            var box = region.Bounds.Drawing;
            if (fixture.Background == "display-band" && region.Id == "headline")
                Paint(pixels, fixture.Width, fixture.Height, box,
                    (_, _) => ((byte)250, (byte)40, (byte)85));
            for (int x = box.Left + 4; x < box.Right - 3; x += 8)
                Paint(pixels, fixture.Width, fixture.Height,
                    new Drawing.Rectangle(x, box.Top + 5, 3, Math.Max(1, box.Height - 10)), (_, _) =>
                        fixture.Background == "display-band" && region.Id == "headline" ? ((byte)255, (byte)235, (byte)50)
                        : fixture.Background == "dark" ? ((byte)248, (byte)248, (byte)248)
                        : fixture.Background == "colored-ink" ? ((byte)200, (byte)32, (byte)64)
                        : ((byte)18, (byte)18, (byte)18));
        }
        if (fixture.Background == "display-band")
            Paint(pixels, fixture.Width, fixture.Height, new Drawing.Rectangle(330, 194, 7, 12),
                (_, _) => ((byte)250, (byte)40, (byte)85));
        var frame = BitmapSource.Create(fixture.Width, fixture.Height, fixture.Dpi, fixture.Dpi,
            PixelFormats.Bgra32, null, pixels, fixture.Width * 4);
        frame.Freeze();
        return frame;
    }

    private static RenderTargetBitmap RenderCapturedView(BitmapSource frame, IReadOnlyList<Border> visuals,
        IReadOnlyList<SceneItem> items, int width, int height)
    {
        var drawingVisual = new DrawingVisual();
        using (var drawing = drawingVisual.RenderOpen())
        {
            drawing.DrawImage(frame, new Rect(0, 0, width, height));
            for (int i = 0; i < visuals.Count; i++)
                drawing.DrawRectangle(new VisualBrush(visuals[i]) { Stretch = Stretch.Fill }, null,
                    new Rect(items[i].Bounds.X, items[i].Bounds.Y, items[i].Bounds.Width, items[i].Bounds.Height));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawingVisual);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[checked(converted.PixelWidth * converted.PixelHeight * 4)];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return pixels;
    }

    private static BitmapSource ToBitmapSource(Drawing.Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        stream.Position = 0;
        var source = new BitmapImage();
        source.BeginInit(); source.CacheOption = BitmapCacheOption.OnLoad; source.StreamSource = stream; source.EndInit();
        source.Freeze();
        return source;
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }

    private static void Paint(byte[] pixels, int width, int height, Drawing.Rectangle rectangle,
        Func<int, int, (byte R, byte G, byte B)> color)
    {
        var clipped = Drawing.Rectangle.Intersect(new Drawing.Rectangle(0, 0, width, height), rectangle);
        for (int y = clipped.Top; y < clipped.Bottom; y++)
        for (int x = clipped.Left; x < clipped.Right; x++)
        {
            var (r, g, b) = color(x, y);
            Set(pixels, width, x, y, r, g, b);
        }
    }

    private static void Set(byte[] pixels, int width, int x, int y, byte r, byte g, byte b)
    {
        int offset = (y * width + x) * 4;
        pixels[offset] = b; pixels[offset + 1] = g; pixels[offset + 2] = r; pixels[offset + 3] = 255;
    }

    private static string CaptionText(string? text)
    {
        string value = (text ?? "").Replace("\r", "").Replace("\n", "");
        int separator = value.IndexOf(". ", StringComparison.Ordinal);
        return separator > 0 && value[..separator].All(char.IsDigit) ? value[(separator + 2)..] : value;
    }

    private static string Stage(ReplayCase fixture, ReplayResult result, PassageFixture passage)
    {
        if (passage.RegionId is not null)
            return result.Items.Any(item => item.Kind == "caption" && item.RegionId == passage.RegionId)
                ? "caption-complete" : "caption-placement-failure";
        if (passage.Bounds is not { } intended || fixture.DetectedRegions is null)
            return "unresolved-before-recognized-text";
        var detected = fixture.DetectedRegions.Where(bounds => bounds.Drawing.IntersectsWith(intended.Drawing)).ToArray();
        if (detected.Length == 0) return "detection-miss";
        bool grouped = detected.Any(region => fixture.IntendedPassages.Count(other => other.Bounds is { } bounds
            && region.Drawing.IntersectsWith(bounds.Drawing)) > 1);
        if (grouped) return "region-grouping-error";
        return fixture.Regions.Any(region => region.Bounds.Drawing.IntersectsWith(intended.Drawing))
            ? "recognition-error" : "unreadable-region";
    }

    private static string FailureStage(PassageFixture passage, Box[]? detected,
        IReadOnlyList<TextRegion>? regions, string failedStage)
    {
        if (regions?.Any(region => region.Text == passage.SourceText) == true)
            return failedStage == "translation" ? "translation-failure" : "caption-placement-failure";
        if (passage.Bounds is not { } intended || detected is null)
            return "unresolved-before-recognized-text";
        var detectedMatches = detected.Where(bounds => bounds.Drawing.IntersectsWith(intended.Drawing)).ToArray();
        if (detectedMatches.Length == 0) return "detection-miss";
        return regions?.Any(region => region.Bounds.IntersectsWith(intended.Drawing)) == true
            ? "recognition-error" : "unreadable-region";
    }

    private static string Commit()
    {
        try
        {
            var start = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            start.ArgumentList.Add("rev-parse"); start.ArgumentList.Add("HEAD");
            using var process = Process.Start(start);
            string value = process?.StandardOutput.ReadToEnd().Trim() ?? "unknown";
            process?.WaitForExit();
            return process?.ExitCode == 0 ? value : "unknown";
        }
        catch { return "unknown"; }
    }

    private static T Read<T>(string path) where T : class
        => JsonSerializer.Deserialize<T>(File.ReadAllText(path), _jsonOptions)
            ?? throw new InvalidDataException($"Could not read {path}.");

    private static string? Value(string[] args, string option)
    {
        int index = Array.IndexOf(args, option);
        if (index < 0) return null;
        if (index + 1 >= args.Length) throw new ArgumentException($"{option} needs a value.");
        return args[index + 1];
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record ReplaySuite(int SchemaVersion, ReplayCase[] Cases);
    private sealed record ReplayCase(string Id, string DefectClass, int Width, int Height, double Dpi, double Zoom,
        string SourceLanguage, string TargetLanguage, string Background, RegionFixture[] Regions,
        ContainerFixture[] TextContainers, ProtectedFixture[] ProtectedArtwork, PassageFixture[] IntendedPassages,
        bool ExpectMarginCaption = false, bool ExpectColoredCaption = false, Box[]? DetectedRegions = null,
        string? CapturedView = null, bool AutoStyle = false, string[]? LocalHeadings = null,
        Box[]? SourceInkPoints = null)
    {
        public int SchemaVersion => 1;
    }
    private sealed record RegionFixture(string Id, Box Bounds, string RecognizedText, string Translation);
    private sealed record ContainerFixture(string RegionId, Box Bounds, bool RequireCaptionInside = false);
    private sealed record ProtectedFixture(string Id, Box Bounds);
    private sealed record PassageFixture(string Id, string SourceText, string? RegionId, string ExpectedStage, Box? Bounds = null);
    private sealed record ProcessingInput(string Id, string CapturedView, double Dpi, double Zoom,
        string SourceLanguage, string TargetLanguage, PassageFixture[] IntendedPassages, ProtectedFixture[] ProtectedArtwork,
        bool AutoStyle = false);
    private sealed record Box(int X, int Y, int Width, int Height)
    {
        public Drawing.Rectangle Drawing => new(X, Y, Width, Height);
        public static Box From(Drawing.Rectangle value) => new(value.X, value.Y, value.Width, value.Height);
    }
    private sealed class BoxConverter : JsonConverter<Box>
    {
        public override Box Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("A box must be [x,y,width,height].");
            reader.Read(); int x = reader.GetInt32(); reader.Read(); int y = reader.GetInt32();
            reader.Read(); int width = reader.GetInt32(); reader.Read(); int height = reader.GetInt32();
            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw new JsonException("A box needs four values.");
            return new Box(x, y, width, height);
        }

        public override void Write(Utf8JsonWriter writer, Box value, JsonSerializerOptions options)
        {
            writer.WriteStartArray(); writer.WriteNumberValue(value.X); writer.WriteNumberValue(value.Y);
            writer.WriteNumberValue(value.Width); writer.WriteNumberValue(value.Height); writer.WriteEndArray();
        }
    }
    private sealed record ReplayResult(IReadOnlyList<SceneItem> Items, string RenderHash, BitmapSource Rendered);
    private sealed record SceneItem(string Kind, string? RegionId, Box Bounds, string? Text, string[] Lines,
        string Typeface, string FontWeight, double FontSize, string Foreground, string Background,
        string? FallbackReason)
    {
        public static SceneItem From(Border border, ReplayCase fixture, Drawing.Rectangle capture,
            Drawing.Rectangle desktop, Matrix transform)
        {
            int x = (int)Math.Round(Canvas.GetLeft(border) / transform.M11) + desktop.X - capture.X;
            int y = (int)Math.Round(Canvas.GetTop(border) / transform.M22) + desktop.Y - capture.Y;
            int width = (int)Math.Round(border.Width / transform.M11);
            int height = (int)Math.Round(border.Height / transform.M22);
            int index = border.Tag is int value ? value : -1;
            string? regionId = index >= 0 && index < fixture.Regions.Length ? fixture.Regions[index].Id : null;
            var text = border.Child as TextBlock;
            string kind = text is null ? "cover"
                : index >= 0 && index < fixture.Regions.Length && CaptionText(text.Text) == fixture.Regions[index].Translation
                    ? "caption" : "association-badge";
            var bounds = new Box(x, y, width, height);
            string? fallback = kind == "caption" && index >= 0
                && !bounds.Drawing.IntersectsWith(fixture.Regions[index].Bounds.Drawing) ? "readable-margin" : null;
            return new SceneItem(kind, regionId, bounds, text?.Text,
                text?.Text.Replace("\r", "").Split('\n') ?? Array.Empty<string>(),
                text?.FontFamily.Source ?? "", text?.FontWeight.ToString() ?? "", text?.FontSize ?? 0,
                text?.Foreground?.ToString() ?? "", border.Background?.ToString() ?? "", fallback);
        }
    }
}
