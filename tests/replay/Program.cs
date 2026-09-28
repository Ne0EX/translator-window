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
            int? pendingRegion = fixture.ProgressiveRegion is { } pendingId
                ? Array.FindIndex(fixture.Regions, region => region.Id == pendingId) : null;
            Require(pendingRegion is null || pendingRegion.Value >= 0,
                $"{fixture.Id}: progressive region must identify an existing text region.");
            var first = Replay(fixture, frame, performance);
            // Compare fresh all-ready placement with completion through the same live renderer instance.
            var second = Replay(fixture, frame, pendingRegionIndex: pendingRegion);
            if (diagnostics is not null && pendingRegion is not null)
                WriteDiagnostics(diagnostics, fixture with { Id = fixture.Id + "-progressive" }, frame, second, "fixed-text");
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
                var pending = Replay(guarded, frame, allowMissing: true, allowLayoutRejection: true);
                int offset = (205 * fixture.Width + 225) * 4;
                Require(Pixels(frame).AsSpan(offset, 4).SequenceEqual(Pixels(pending.Rendered).AsSpan(offset, 4)),
                    "Heading cover must preserve ink belonging to a pending overlapping region.");
            }
            if (fixture.Background == "small-bubble")
            {
                var ellipsis = fixture with { Regions = new[] {
                    fixture.Regions[0] with { Translation = "…" } } };
                var ellipsisResult = Replay(ellipsis, frame);
                Verify(ellipsis, ellipsisResult);
                var caption = ellipsisResult.Items.Single(item => item.Kind == "caption").Bounds.Drawing;
                var ellipsisPixels = Pixels(ellipsisResult.Rendered);
                var dotWidths = new List<int>();
                int dotWidth = 0;
                for (int x = caption.Left; x < caption.Right; x++)
                {
                    bool ink = false;
                    for (int y = caption.Top; y < caption.Bottom; y++)
                        ink |= ellipsisPixels[(y * fixture.Width + x) * 4] < 128;
                    Require(!ink || x > caption.Left && x < caption.Right - 1,
                        "Ellipsis ink must leave an empty pixel at both horizontal caption edges.");
                    if (ink) dotWidth++;
                    else if (dotWidth > 0) { dotWidths.Add(dotWidth); dotWidth = 0; }
                }
                Require(dotWidths.Count == 3 && dotWidths.Max() - dotWidths.Min() <= 1,
                    "A single ellipsis glyph in a narrow bubble must render three complete dots of matching width.");
                foreach (var sample in new[] {
                    (Translation: "...・・・", Expected: "...・・・"),
                    (Translation: "゠", Expected: "゠"),
                    (Translation: "日本語", Expected: "แปลไม่สำเร็จ"),
                    (Translation: "ไทย日本語", Expected: "ไทย") })
                {
                    var punctuation = fixture with { Regions = new[] {
                        fixture.Regions[0] with { Translation = sample.Translation } } };
                    var punctuationResult = Replay(punctuation, frame);
                    var actual = punctuationResult.Items.Single(item => item.Kind == "caption").Text!
                        .Replace("\r", "").Replace("\n", "");
                    Require(actual == sample.Expected,
                        $"A Thai caption must preserve punctuation without accepting untranslated Japanese: {sample.Translation}");
                }
                var questionWord = fixture with { Id = "thai-question-word", Regions = new[] {
                    fixture.Regions[0] with { Bounds = new Box(278, 195, 41, 77),
                        Translation = "5...แค่นั้นเหรอ?" } },
                    TextContainers = new[] { new ContainerFixture("short", new Box(250, 150, 100, 167), true) } };
                var questionFrame = BuildFrame(questionWord);
                var questionResult = Replay(questionWord, questionFrame);
                if (diagnostics is not null)
                    WriteDiagnostics(diagnostics, questionWord, questionFrame, questionResult, "fixed-text");
                Verify(questionWord, questionResult);
                var questionCaption = questionResult.Items.Single(item => item.Kind == "caption");
                Require(questionCaption.FontSize >= 20 && questionCaption.Text!.Contains("เหรอ"),
                    "A Thai question must keep เหรอ together without shrinking its 20-DIP caption.");
                var openBubble = fixture with { Background = "open-bubble", Regions = new[] {
                    fixture.Regions[0] with { Bounds = new Box(275, 120, 21, 86), Translation = "ได้เลย..." } },
                    // Transparent padding may overlap the outline; the pixel checks below protect the actual border.
                    TextContainers = new[] { new ContainerFixture("short", new Box(235, 100, 101, 171), true) } };
                var openPixels = Pixels(BuildFrame(openBubble));
                Paint(openPixels, fixture.Width, fixture.Height, new Drawing.Rectangle(235, 100, 101, 171),
                    (x, y) => {
                        double radius = y < 220 ? Math.Pow((x - 285d) / 45, 2)
                            : Math.Pow((x - 285d) / 45, 2) + Math.Pow((y - 220d) / 45, 2);
                        return radius > 1 ? ((byte)90, (byte)120, (byte)150)
                            : radius >= 0.94 ? ((byte)18, (byte)18, (byte)18)
                            : (openPixels[(y * fixture.Width + x) * 4 + 2],
                                openPixels[(y * fixture.Width + x) * 4 + 1], openPixels[(y * fixture.Width + x) * 4]);
                    });
                Paint(openPixels, fixture.Width, fixture.Height, new Drawing.Rectangle(296, 128, 1, 1),
                    (_, _) => ((byte)80, (byte)80, (byte)80));
                var openFrame = BitmapSource.Create(fixture.Width, fixture.Height, fixture.Dpi, fixture.Dpi,
                    PixelFormats.Bgra32, null, openPixels, fixture.Width * 4);
                openFrame.Freeze();
                var openResult = Replay(openBubble, openFrame);
                if (diagnostics is not null) WriteDiagnostics(diagnostics,
                    openBubble with { Id = "open-narrow-bubble" }, openFrame, openResult, "fixed-text");
                Verify(openBubble, openResult);
                var openRendered = Pixels(openResult.Rendered);
                Require(Math.Abs(openRendered[(126 * fixture.Width + 280) * 4] - 248) <= 1,
                    "An open narrow bubble must conceal original lettering while fitting the complete caption locally.");
                for (int y = 100; y < 271; y++)
                for (int x = 235; x < 336; x++)
                    if (!openBubble.Regions[0].Bounds.Drawing.Contains(x, y)
                        && openPixels[(y * fixture.Width + x) * 4] != 248)
                        Require(openPixels.AsSpan((y * fixture.Width + x) * 4, 4)
                            .SequenceEqual(openRendered.AsSpan((y * fixture.Width + x) * 4, 4)),
                            "Open bubble placement must preserve its curved border and exterior artwork.");

                var curvedOpen = fixture with { Background = "curved-open-bubble", Regions = new[] {
                    fixture.Regions[0] with { Bounds = new Box(270, 158, 47, 97),
                        Translation = "...เรื่องในอดีตนั่นแหละ ฉันยังคงกังวลอยู่เลย" } },
                    TextContainers = new[] { new ContainerFixture("short", new Box(239, 125, 108, 167), true) } };
                var curvedPixels = Pixels(BuildFrame(curvedOpen));
                Paint(curvedPixels, fixture.Width, fixture.Height, new Drawing.Rectangle(235, 120, 116, 180),
                    (x, y) => {
                        double radius = Math.Pow((x - 291d) / 49, 2)
                            + (y < 176 ? Math.Pow((y - 176d) / 49, 2)
                                : y > 234 ? Math.Pow((y - 234d) / 49, 2) : 0);
                        if (y >= 279 && Math.Abs(x - 291) < 14) return ((byte)248, (byte)248, (byte)248);
                        return radius > 1 ? ((byte)90, (byte)120, (byte)150)
                            : radius >= 0.94 ? ((byte)18, (byte)18, (byte)18)
                            : (curvedPixels[(y * fixture.Width + x) * 4 + 2],
                                curvedPixels[(y * fixture.Width + x) * 4 + 1], curvedPixels[(y * fixture.Width + x) * 4]);
                    });
                var curvedFrame = BitmapSource.Create(fixture.Width, fixture.Height, fixture.Dpi, fixture.Dpi,
                    PixelFormats.Bgra32, null, curvedPixels, fixture.Width * 4);
                curvedFrame.Freeze();
                var curvedResult = Replay(curvedOpen, curvedFrame);
                if (diagnostics is not null) WriteDiagnostics(diagnostics,
                    curvedOpen with { Id = "curved-open-bubble" }, curvedFrame, curvedResult, "fixed-text");
                Verify(curvedOpen, curvedResult);
                var curvedRendered = Pixels(curvedResult.Rendered);
                Require(Math.Abs(curvedRendered[(163 * fixture.Width + 274) * 4] - 248) <= 1,
                    "An open curved bubble must conceal its original ink with the sampled paper fill.");
                for (int y = 120; y < 300; y++)
                for (int x = 235; x < 351; x++)
                    if (!curvedOpen.Regions[0].Bounds.Drawing.Contains(x, y)
                        && curvedPixels[(y * fixture.Width + x) * 4] != 248)
                        Require(curvedPixels.AsSpan((y * fixture.Width + x) * 4, 4)
                            .SequenceEqual(curvedRendered.AsSpan((y * fixture.Width + x) * 4, 4)),
                            "Complete local lines in an open curved bubble must preserve its border and exterior artwork.");

                // A panel crop ends both flanks together; its white gutter is not extra speech space.
                var croppedBubble = fixture with { Id = "panel-cropped-open-bottom-bubble", Background = "panel-cropped-bubble",
                    Regions = new[] { fixture.Regions[0] with { Bounds = new Box(265, 134, 88, 127),
                        RecognizedText = "今更だけど謝っておきたかったんだ",
                        Translation = "ถึงตอนนี้ก็สายไปแล้ว แต่ฉันอยากจะขอโทษจริงๆ" } },
                    TextContainers = new[] { new ContainerFixture("short", new Box(228, 107, 147, 174), true) },
                    ProtectedArtwork = Array.Empty<ProtectedFixture>() };
                var croppedPixels = Pixels(BuildFrame(croppedBubble));
                Paint(croppedPixels, fixture.Width, fixture.Height,
                    new Drawing.Rectangle(0, 0, fixture.Width, fixture.Height), (x, y) => {
                        if (x < 120 || x >= 375 || y < 107 || y >= 281)
                            return ((byte)255, (byte)255, (byte)255);
                        int left = 228 + (int)Math.Round(21 * Math.Pow((y - 197d) / 90, 2));
                        if (y < 109 || x >= 373 || x >= left && x < left + 2)
                            return ((byte)18, (byte)18, (byte)18);
                        if (x < left) return ((byte)180, (byte)180, (byte)180);
                        byte paperOrInk = croppedPixels[(y * fixture.Width + x) * 4] < 128 ? (byte)18 : (byte)255;
                        return (paperOrInk, paperOrInk, paperOrInk);
                    });
                var croppedFrame = BitmapSource.Create(fixture.Width, fixture.Height, fixture.Dpi, fixture.Dpi,
                    PixelFormats.Bgra32, null, croppedPixels, fixture.Width * 4);
                croppedFrame.Freeze();
                var croppedResult = Replay(croppedBubble, croppedFrame);
                if (diagnostics is not null) WriteDiagnostics(diagnostics,
                    croppedBubble, croppedFrame, croppedResult, "fixed-text");
                var croppedRendered = Pixels(croppedResult.Rendered);
                for (int y = 107; y < fixture.Height; y++)
                for (int x = 0; x < fixture.Width; x++)
                    if (y >= 281 || !croppedBubble.Regions[0].Bounds.Drawing.Contains(x, y)
                        && croppedPixels[(y * fixture.Width + x) * 4] != 255)
                        Require(croppedPixels.AsSpan((y * fixture.Width + x) * 4, 4)
                            .SequenceEqual(croppedRendered.AsSpan((y * fixture.Width + x) * 4, 4)),
                            "A panel-cropped bubble must preserve every gutter, outline and exterior artwork pixel.");
                Verify(croppedBubble, croppedResult);
            }
            if (fixture.Background == "bubble")
            {
                var largeBubble = fixture with { Id = "large-capture-closed-bubble", Background = "private",
                    Width = 1800, Height = 1200, Dpi = 144,
                    Regions = new[] { fixture.Regions[0] with { Bounds = new Box(875, 355, 150, 622),
                        Translation = "ฉันจะใช้ทุกสิ่งที่สามารถใช้ได้เพื่อช่วยเหลือ ไม่ว่าจะยากแค่ไหนก็ตาม!" } },
                    TextContainers = new[] { new ContainerFixture(fixture.Regions[0].Id, new Box(800, 250, 300, 820), true) },
                    ProtectedArtwork = Array.Empty<ProtectedFixture>() };
                var largePixels = new byte[largeBubble.Width * largeBubble.Height * 4];
                Paint(largePixels, largeBubble.Width, largeBubble.Height,
                    new Drawing.Rectangle(0, 0, largeBubble.Width, largeBubble.Height), (x, y) => {
                        double radius = Math.Pow((x - 950d) / 150, 2) + Math.Pow((y - 660d) / 410, 2);
                        return radius > 1 ? ((byte)90, (byte)120, (byte)150)
                            : radius >= 0.97 ? ((byte)18, (byte)18, (byte)18)
                            : ((byte)248, (byte)248, (byte)248);
                    });
                for (int y = 370; y < 970; y += 50)
                foreach (int x in new[] { 916, 970 })
                    Paint(largePixels, largeBubble.Width, largeBubble.Height, new Drawing.Rectangle(x, y, 4, 13),
                        (_, _) => ((byte)18, (byte)18, (byte)18));
                Paint(largePixels, largeBubble.Width, largeBubble.Height, new Drawing.Rectangle(948, 286, 4, 13),
                    (_, _) => ((byte)18, (byte)18, (byte)18));
                var largeFrame = BitmapSource.Create(largeBubble.Width, largeBubble.Height, largeBubble.Dpi, largeBubble.Dpi,
                    PixelFormats.Bgra32, null, largePixels, largeBubble.Width * 4);
                largeFrame.Freeze();
                var largeResult = Replay(largeBubble, largeFrame);
                if (diagnostics is not null) WriteDiagnostics(diagnostics, largeBubble, largeFrame, largeResult, "fixed-text");
                var largeRendered = Pixels(largeResult.Rendered);
                Require(largeRendered[(290 * largeBubble.Width + 949) * 4] == 255,
                    "A large captured view must still cover source lettering throughout its closed bubble.");
                Verify(largeBubble, largeResult);
                for (int y = 0; y < largeBubble.Height; y++)
                for (int x = 0; x < largeBubble.Width; x++)
                    if (Math.Pow((x - 950d) / 150, 2) + Math.Pow((y - 660d) / 410, 2) >= 0.97)
                        Require(largePixels.AsSpan((y * largeBubble.Width + x) * 4, 4)
                            .SequenceEqual(largeRendered.AsSpan((y * largeBubble.Width + x) * 4, 4)),
                            "Bounded bubble search must preserve every outline and exterior artwork pixel.");
                var distantTop = fixture with { Id = "closed-bubble-with-distant-top", Background = "private",
                    Width = 1200, Height = 1000, Dpi = 144,
                    Regions = new[] { fixture.Regions[0] with { Bounds = new Box(550, 620, 100, 150),
                        Translation = "ไม่ว่าคู่ต่อสู้จะยากที่จะเอาชนะแค่ไหนก็ตาม!" } },
                    TextContainers = new[] { new ContainerFixture(fixture.Regions[0].Id, new Box(480, 400, 240, 440), true) },
                    ProtectedArtwork = Array.Empty<ProtectedFixture>(),
                    WhiteSourceCoverPoints = new[] { new Box(552, 750, 1, 1) } };
                var distantPixels = new byte[distantTop.Width * distantTop.Height * 4];
                Paint(distantPixels, distantTop.Width, distantTop.Height,
                    new Drawing.Rectangle(0, 0, distantTop.Width, distantTop.Height), (x, y) => {
                        double radius = Math.Pow((x - 600d) / 120, 2) + Math.Pow((y - 620d) / 220, 2);
                        return radius > 1 ? ((byte)90, (byte)120, (byte)150)
                            : radius >= 0.97 ? ((byte)18, (byte)18, (byte)18)
                            : ((byte)255, (byte)255, (byte)255);
                    });
                foreach (int y in new[] { 642, 682, 742 })
                foreach (int x in new[] { 551, 611 })
                {
                    Paint(distantPixels, distantTop.Width, distantTop.Height, new Drawing.Rectangle(x, y, 8, 17),
                        (_, _) => ((byte)238, (byte)238, (byte)238));
                    Paint(distantPixels, distantTop.Width, distantTop.Height, new Drawing.Rectangle(x + 3, y + 3, 2, 11),
                        (_, _) => ((byte)18, (byte)18, (byte)18));
                }
                var distantPending = new Box(580, 460, 40, 40);
                Paint(distantPixels, distantTop.Width, distantTop.Height, new Drawing.Rectangle(597, 470, 3, 12),
                    (_, _) => ((byte)18, (byte)18, (byte)18));
                var distantFrame = BitmapSource.Create(distantTop.Width, distantTop.Height, distantTop.Dpi, distantTop.Dpi,
                    PixelFormats.Bgra32, null, distantPixels, distantTop.Width * 4);
                distantFrame.Freeze();
                var distantWithPending = distantTop with { Regions = distantTop.Regions.Concat(new[] {
                    new RegionFixture("pending", distantPending, "pending", "") }).ToArray() };
                var distantResult = Replay(distantWithPending, distantFrame, allowMissing: true);
                if (diagnostics is not null) WriteDiagnostics(diagnostics, distantWithPending,
                    distantFrame, distantResult, "fixed-text");
                var distantRendered = Pixels(distantResult.Rendered);
                Verify(distantTop, distantResult);
                Require(distantResult.Items.Single(item => item.Kind == "caption").FontSize >= 24,
                    "Using the full closed bubble must retain a readable complete local caption.");
                for (int y = 0; y < distantTop.Height; y++)
                for (int x = 0; x < distantTop.Width; x++)
                    if (distantPending.Drawing.Contains(x, y)
                        || Math.Pow((x - 600d) / 120, 2) + Math.Pow((y - 620d) / 220, 2) >= 0.97)
                        Require(distantPixels.AsSpan((y * distantTop.Width + x) * 4, 4)
                            .SequenceEqual(distantRendered.AsSpan((y * distantTop.Width + x) * 4, 4)),
                            "Recovering a distant bubble boundary must preserve pending text, its outline and exterior artwork.");
                var fringePixels = (byte[])distantPixels.Clone();
                Paint(fringePixels, distantTop.Width, distantTop.Height, new Drawing.Rectangle(647, 699, 3, 1),
                    (_, _) => ((byte)18, (byte)18, (byte)18));
                Paint(fringePixels, distantTop.Width, distantTop.Height, new Drawing.Rectangle(650, 699, 3, 1),
                    (_, _) => ((byte)209, (byte)209, (byte)209));
                var fringeFrame = BitmapSource.Create(distantTop.Width, distantTop.Height, distantTop.Dpi, distantTop.Dpi,
                    PixelFormats.Bgra32, null, fringePixels, distantTop.Width * 4);
                fringeFrame.Freeze();
                foreach (int sourceLeft in new[] { 551, 550 })
                {
                    var fringeDialogue = distantTop.Regions[0] with { Bounds = new Box(sourceLeft, 620, 100, 150) };
                    var fringeFixture = distantWithPending with { Id = $"distant-bubble-source-fringe-{sourceLeft}",
                        Regions = new[] { fringeDialogue, distantWithPending.Regions[1] },
                        WhiteSourceCoverPoints = new[] { new Box(552, 750, 1, 1), new Box(651, 699, 1, 1) } };
                    var fringeResult = Replay(fringeFixture, fringeFrame, allowMissing: true);
                    Verify(distantTop with { Regions = new[] { fringeDialogue } }, fringeResult);
                    if (diagnostics is not null) WriteDiagnostics(diagnostics, fringeFixture,
                        fringeFrame, fringeResult, "fixed-text");
                    Require(fringeResult.Items.Single(item => item.Kind == "caption").FontSize >= 24,
                        "A one-pixel detection shift beside antialiased source ink must retain complete local bubble text.");
                    var fringeRendered = Pixels(fringeResult.Rendered);
                    for (int y = 0; y < distantTop.Height; y++)
                    for (int x = 0; x < distantTop.Width; x++)
                        if (distantPending.Drawing.Contains(x, y)
                            || Math.Pow((x - 600d) / 120, 2) + Math.Pow((y - 620d) / 220, 2) >= 0.97)
                            Require(fringePixels.AsSpan((y * distantTop.Width + x) * 4, 4)
                                .SequenceEqual(fringeRendered.AsSpan((y * distantTop.Width + x) * 4, 4)),
                                "Ignoring a source-letter fringe in paper sampling must preserve pending text, the outline and exterior artwork.");
                }
                var distantOpenPixels = (byte[])distantPixels.Clone();
                Paint(distantOpenPixels, distantTop.Width, distantTop.Height, new Drawing.Rectangle(596, 0, 8, 420),
                    (_, _) => ((byte)255, (byte)255, (byte)255));
                var distantOpenFrame = BitmapSource.Create(distantTop.Width, distantTop.Height, distantTop.Dpi, distantTop.Dpi,
                    PixelFormats.Bgra32, null, distantOpenPixels, distantTop.Width * 4);
                distantOpenFrame.Freeze();
                var distantOpenFixture = distantWithPending with { Id = "distant-open-bubble", WhiteSourceCoverPoints = null };
                var distantOpenResult = Replay(distantOpenFixture, distantOpenFrame, allowMissing: true);
                Verify(distantTop with { WhiteSourceCoverPoints = null }, distantOpenResult);
                var distantOpenRendered = Pixels(distantOpenResult.Rendered);
                Require(distantOpenRendered[(750 * distantTop.Width + 552) * 4] == 238,
                    "A paper corridor reaching the capture exterior must not qualify as a closed bubble.");
                for (int y = 0; y < distantTop.Height; y++)
                for (int x = 0; x < distantTop.Width; x++)
                    if (distantPending.Drawing.Contains(x, y)
                        || Math.Pow((x - 600d) / 120, 2) + Math.Pow((y - 620d) / 220, 2) >= 0.97)
                        Require(distantOpenPixels.AsSpan((y * distantTop.Width + x) * 4, 4)
                            .SequenceEqual(distantOpenRendered.AsSpan((y * distantTop.Width + x) * 4, 4)),
                            "An open paper corridor must preserve pending text and all remaining outline and exterior pixels.");
                var dottedPixels = (byte[])distantPixels.Clone();
                for (int y = 430; y < 820; y += 10)
                for (int x = 485; x < 716; x += 15)
                    Paint(dottedPixels, distantTop.Width, distantTop.Height, new Drawing.Rectangle(x, y, 6, 3),
                        (px, py) => Math.Pow((px - 600d) / 120, 2) + Math.Pow((py - 620d) / 220, 2) < 0.90
                            ? ((byte)205, (byte)205, (byte)205)
                            : (dottedPixels[(py * distantTop.Width + px) * 4 + 2],
                                dottedPixels[(py * distantTop.Width + px) * 4 + 1], dottedPixels[(py * distantTop.Width + px) * 4]));
                var dottedFrame = BitmapSource.Create(distantTop.Width, distantTop.Height, distantTop.Dpi, distantTop.Dpi,
                    PixelFormats.Bgra32, null, dottedPixels, distantTop.Width * 4);
                dottedFrame.Freeze();
                var dottedFixture = distantWithPending with { Id = "text-on-halftone-paper", WhiteSourceCoverPoints = null };
                var dottedResult = Replay(dottedFixture, dottedFrame, allowMissing: true, allowLayoutRejection: true);
                if (diagnostics is not null) WriteDiagnostics(diagnostics, dottedFixture,
                    dottedFrame, dottedResult, "fixed-text");
                var dottedRendered = Pixels(dottedResult.Rendered);
                Require(dottedRendered[(611 * distantTop.Width + 532) * 4] == 205,
                    "Extending a search around text must not erase halftone dots outside its source region.");
                for (int y = 0; y < distantTop.Height; y++)
                for (int x = 0; x < distantTop.Width; x++)
                    if (distantPending.Drawing.Contains(x, y)
                        || Math.Pow((x - 600d) / 120, 2) + Math.Pow((y - 620d) / 220, 2) >= 0.97)
                        Require(dottedPixels.AsSpan((y * distantTop.Width + x) * 4, 4)
                            .SequenceEqual(dottedRendered.AsSpan((y * distantTop.Width + x) * 4, 4)),
                            "Textured paper reconstruction must preserve pending text and exterior artwork.");
                var diagramPixels = (byte[])distantPixels.Clone();
                var diagramPath = new[] { new Drawing.Point(525, 519), new Drawing.Point(556, 510),
                    new Drawing.Point(564, 542), new Drawing.Point(590, 546), new Drawing.Point(585, 585),
                    new Drawing.Point(567, 605), new Drawing.Point(554, 582), new Drawing.Point(532, 594),
                    new Drawing.Point(520, 560), new Drawing.Point(525, 519) };
                var diagramInk = new HashSet<Drawing.Point>();
                for (int segment = 1; segment < diagramPath.Length; segment++)
                {
                    var start = diagramPath[segment - 1];
                    var end = diagramPath[segment];
                    int steps = Math.Max(Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
                    for (int step = 0; step <= steps; step++)
                    {
                        int x = (int)Math.Round(start.X + (end.X - start.X) * step / (double)steps);
                        int y = (int)Math.Round(start.Y + (end.Y - start.Y) * step / (double)steps);
                        Set(diagramPixels, distantTop.Width, x, y, 18, 18, 18);
                        diagramInk.Add(new Drawing.Point(x, y));
                    }
                }
                var diagramFrame = BitmapSource.Create(distantTop.Width, distantTop.Height, distantTop.Dpi, distantTop.Dpi,
                    PixelFormats.Bgra32, null, diagramPixels, distantTop.Width * 4);
                diagramFrame.Freeze();
                var diagramFixture = distantWithPending with { Id = "diagram-beside-distant-text",
                    WhiteSourceCoverPoints = new[] { new Box(554, 648, 1, 1) } };
                var diagramResult = Replay(diagramFixture, diagramFrame, allowMissing: true);
                if (diagnostics is not null) WriteDiagnostics(diagnostics, diagramFixture,
                    diagramFrame, diagramResult, "fixed-text");
                Verify(distantTop with { WhiteSourceCoverPoints = null }, diagramResult);
                var diagramRendered = Pixels(diagramResult.Rendered);
                foreach (var point in diagramInk)
                    Require(diagramPixels.AsSpan((point.Y * distantTop.Width + point.X) * 4, 4)
                        .SequenceEqual(diagramRendered.AsSpan((point.Y * distantTop.Width + point.X) * 4, 4)),
                        "Recovering a closed background around text must preserve disconnected diagram strokes outside the text regions.");
                for (int y = distantPending.Y; y < distantPending.Y + distantPending.Height; y++)
                for (int x = distantPending.X; x < distantPending.X + distantPending.Width; x++)
                    Require(diagramPixels.AsSpan((y * distantTop.Width + x) * 4, 4)
                        .SequenceEqual(diagramRendered.AsSpan((y * distantTop.Width + x) * 4, 4)),
                        "Protecting a neighboring diagram must also leave pending text untouched.");
                Require(first.Items.Single(item => item.Kind == "caption").FontSize >= 28,
                    "A roomy bubble must use a larger readable caption instead of the fixed 24-DIP ceiling.");
                var cornerNeighbor = fixture with { Regions = fixture.Regions.Concat(new[] {
                    new RegionFixture("neighbor", new Box(225, 110, 12, 15), "pending", "") }).ToArray() };
                Verify(fixture, Replay(cornerNeighbor, frame, allowMissing: true));
                var pendingNeighbor = new Box(245, 270, 110, 18);
                var neighborPixels = Pixels(frame);
                foreach (int x in new[] { 280, 288 })
                    Paint(neighborPixels, fixture.Width, fixture.Height, new Drawing.Rectangle(x, 278, 3, 3),
                        (_, _) => ((byte)18, (byte)18, (byte)18));
                var neighborFrame = BitmapSource.Create(fixture.Width, fixture.Height, fixture.Dpi, fixture.Dpi,
                    PixelFormats.Bgra32, null, neighborPixels, fixture.Width * 4);
                neighborFrame.Freeze();
                var neighborDialogue = fixture with { Background = "neighbor-bubble", Regions = new[] {
                    fixture.Regions[0] with { Translation = "ฉันจะใช้ทุกสิ่งที่สามารถใช้ได้เพื่อช่วยเหลือ!" } } };
                var neighborFixture = neighborDialogue with { Regions = neighborDialogue.Regions.Concat(new[] {
                    new RegionFixture("pending", pendingNeighbor, "…", "") }).ToArray() };
                var neighborResult = Replay(neighborFixture, neighborFrame, allowMissing: true);
                if (diagnostics is not null) WriteDiagnostics(diagnostics,
                    neighborFixture with { Id = "bubble-beside-pending-text" }, neighborFrame, neighborResult, "fixed-text");
                Verify(neighborDialogue, neighborResult);
                var neighborRendered = Pixels(neighborResult.Rendered);
                Require(neighborRendered[(125 * fixture.Width + 292) * 4] == 255,
                    "A neighboring pending passage must not prevent full bubble coverage of original lettering.");
                for (int y = 105; y < 300; y++)
                for (int x = 220; x < 375; x++)
                    if (pendingNeighbor.Drawing.Contains(x, y)
                        || Math.Pow((x - 297.5) / 77.5, 2) + Math.Pow((y - 202.5) / 97.5, 2) >= 0.94)
                        Require(neighborPixels.AsSpan((y * fixture.Width + x) * 4, 4)
                            .SequenceEqual(neighborRendered.AsSpan((y * fixture.Width + x) * 4, 4)),
                            "Fitting beside pending text must preserve that entire region and the bubble outline and exterior.");
                var roundedDetection = fixture with { Regions = new[] {
                    fixture.Regions[0] with { Bounds = new Box(235, 145, 120, 120) } } };
                Verify(roundedDetection, Replay(roundedDetection, frame));
                var punctuationPixels = Pixels(frame);
                Paint(punctuationPixels, fixture.Width, fixture.Height, new Drawing.Rectangle(272, 155, 5, 4),
                    (_, _) => ((byte)18, (byte)18, (byte)18));
                var punctuationFrame = BitmapSource.Create(fixture.Width, fixture.Height, fixture.Dpi, fixture.Dpi,
                    PixelFormats.Bgra32, null, punctuationPixels, fixture.Width * 4);
                punctuationFrame.Freeze();
                var punctuationResult = Replay(fixture, punctuationFrame);
                Verify(fixture, punctuationResult);
                Require(punctuationResult.Items.Single(item => item.Kind == "caption").FontSize >= 28
                    && Pixels(punctuationResult.Rendered)[(156 * fixture.Width + 273) * 4] == 255,
                    "A punctuation-sized compact mark inside detected text must be covered and retain roomy bubble layout.");
                var shortUtterance = fixture with { Regions = new[] {
                    fixture.Regions[0] with { Bounds = new Box(287, 165, 24, 74) } },
                    TextContainers = new[] { new ContainerFixture("dialogue", new Box(236, 118, 126, 172), true) } };
                var texturedPixels = Pixels(BuildFrame(shortUtterance));
                Paint(texturedPixels, fixture.Width, fixture.Height, new Drawing.Rectangle(256, 240, 84, 25),
                    (x, y) => (x - 256) % 10 < 2 && (y - 240) % 10 < 2
                        ? ((byte)225, (byte)225, (byte)225)
                        : (texturedPixels[(y * fixture.Width + x) * 4 + 2],
                            texturedPixels[(y * fixture.Width + x) * 4 + 1], texturedPixels[(y * fixture.Width + x) * 4]));
                var texturedFrame = BitmapSource.Create(fixture.Width, fixture.Height, fixture.Dpi, fixture.Dpi,
                    PixelFormats.Bgra32, null, texturedPixels, fixture.Width * 4);
                texturedFrame.Freeze();
                var shortUtteranceResult = Replay(shortUtterance, texturedFrame);
                Verify(shortUtterance, shortUtteranceResult);
                var shortRendered = Pixels(shortUtteranceResult.Rendered);
                Require(Math.Abs(shortRendered[(261 * fixture.Width + 337) * 4] - 255) <= 1,
                    "A short utterance must use its full closed bubble with solid white paper over faint neutral texture.");
                for (int y = 118; y < 290; y++)
                for (int x = 236; x < 362; x++)
                    if (Math.Pow((x - 299d) / 63, 2) + Math.Pow((y - 204d) / 86, 2) >= 0.94)
                        Require(texturedPixels.AsSpan((y * fixture.Width + x) * 4, 4)
                            .SequenceEqual(shortRendered.AsSpan((y * fixture.Width + x) * 4, 4)),
                            "Filling a roomy bubble around short text must preserve its outline and exterior artwork.");
                var grayCapPixels = Pixels(frame);
                for (int y = 105; y < 300; y++)
                for (int x = 220; x < 375; x++)
                {
                    double radius = Math.Pow((x - 297.5) / 77.5, 2) + Math.Pow((y - 202.5) / 97.5, 2);
                    if (radius >= 0.90 && radius < 0.94)
                        Set(grayCapPixels, fixture.Width, x, y, 151, 151, 151);
                    else if (radius >= 0.94 && radius <= 1 && x >= 320 && x < 324 && y < 140)
                        Set(grayCapPixels, fixture.Width, x, y, 130, 130, 130);
                    else if (radius < 0.90 && y < 153 && grayCapPixels[(y * fixture.Width + x) * 4] == 248)
                        Set(grayCapPixels, fixture.Width, x, y, 211, 211, 211);
                }
                Paint(grayCapPixels, fixture.Width, fixture.Height, new Drawing.Rectangle(375, 120, 40, 28),
                    (x, y) => {
                        double radius = Math.Pow((x - 395d) / 20, 2) + Math.Pow((y - 134d) / 14, 2);
                        return radius < 0.78 ? ((byte)211, (byte)211, (byte)211)
                            : radius <= 1 ? ((byte)18, (byte)18, (byte)18)
                            : ((byte)248, (byte)248, (byte)248);
                    });
                var grayCapPending = new Box(270, 269, 55, 19);
                Paint(grayCapPixels, fixture.Width, fixture.Height, new Drawing.Rectangle(278, 274, 3, 10),
                    (_, _) => ((byte)18, (byte)18, (byte)18));
                var grayCapFrame = BitmapSource.Create(fixture.Width, fixture.Height, fixture.Dpi, fixture.Dpi,
                    PixelFormats.Bgra32, null, grayCapPixels, fixture.Width * 4);
                grayCapFrame.Freeze();
                var grayCapFixture = fixture with { Id = "gray-cap-above-detected-text", Background = "gray-cap",
                    Regions = fixture.Regions.Concat(new[] {
                        new RegionFixture("pending", grayCapPending, "pending", "") }).ToArray(),
                    WhiteSourceCoverPoints = new[] { new Box(297, 118, 1, 1), new Box(280, 140, 1, 1),
                        new Box(315, 135, 1, 1), new Box(293, 128, 1, 1), new Box(265, 165, 1, 1) } };
                var grayCapResult = Replay(grayCapFixture, grayCapFrame, allowMissing: true);
                Verify(fixture with { Background = "gray-cap" }, grayCapResult);
                if (diagnostics is not null) WriteDiagnostics(diagnostics, grayCapFixture,
                    grayCapFrame, grayCapResult, "fixed-text");
                Require(grayCapResult.Items.Single(item => item.Kind == "caption").FontSize >= 28,
                    "A gray cap above detected lettering must use coherent white bubble paper and readable complete local text.");
                var grayCapRendered = Pixels(grayCapResult.Rendered);
                for (int y = 0; y < fixture.Height; y++)
                for (int x = 0; x < fixture.Width; x++)
                    if (grayCapPending.Drawing.Contains(x, y)
                        || Math.Pow((x - 297.5) / 77.5, 2) + Math.Pow((y - 202.5) / 97.5, 2) >= 0.90)
                        Require(grayCapPixels.AsSpan((y * fixture.Width + x) * 4, 4)
                            .SequenceEqual(grayCapRendered.AsSpan((y * fixture.Width + x) * 4, 4)),
                            "Recovering a gray cap must preserve every outline and antialias fringe pixel, exterior art pocket and pending passage.");
                var highlightedPixels = Pixels(frame);
                for (int y = 105; y < 300; y++)
                for (int x = 220; x < 375; x++)
                    if (Math.Pow((x - 297.5) / 77.5, 2) + Math.Pow((y - 202.5) / 97.5, 2) < 0.94
                        && highlightedPixels[(y * fixture.Width + x) * 4] == 248)
                    {
                        bool highlight = y >= 175 && y < 189;
                        Set(highlightedPixels, fixture.Width, x, y, highlight ? (byte)253 : (byte)238,
                            highlight ? (byte)252 : (byte)237, highlight ? (byte)255 : (byte)242);
                    }
                Paint(highlightedPixels, fixture.Width, fixture.Height, new Drawing.Rectangle(220, 195, 32, 12),
                    (x, y) => Math.Pow((x - 297.5) / 77.5, 2) + Math.Pow((y - 202.5) / 97.5, 2) < 0.94
                        ? ((byte)255, (byte)255, (byte)255)
                        : (highlightedPixels[(y * fixture.Width + x) * 4 + 2],
                            highlightedPixels[(y * fixture.Width + x) * 4 + 1], highlightedPixels[(y * fixture.Width + x) * 4]));
                var highlightedFrame = BitmapSource.Create(fixture.Width, fixture.Height, fixture.Dpi, fixture.Dpi,
                    PixelFormats.Bgra32, null, highlightedPixels, fixture.Width * 4);
                highlightedFrame.Freeze();
                var highlightedFixture = fixture with { Background = "highlighted-bubble" };
                var highlightedResult = Replay(highlightedFixture, highlightedFrame);
                if (diagnostics is not null) WriteDiagnostics(diagnostics,
                    highlightedFixture with { Id = "highlighted-bubble" }, highlightedFrame, highlightedResult, "fixed-text");
                Verify(highlightedFixture, highlightedResult);
                var highlightedRendered = Pixels(highlightedResult.Rendered);
                foreach (var point in new[] { new Drawing.Point(244, 200), new Drawing.Point(244, 210),
                    new Drawing.Point(293, 128) })
                {
                    int pixel = (point.Y * fixture.Width + point.X) * 4;
                    Require(highlightedRendered[pixel] == 255 && highlightedRendered[pixel + 1] == 255
                        && highlightedRendered[pixel + 2] == 255,
                        "Neutral near-white bubble paper must use a coherent white cover across highlights, adjacent fill and original lettering.");
                }
                foreach (var point in new[] { new Drawing.Point(293, 128), new Drawing.Point(293, 277) })
                {
                    int pixel = (point.Y * fixture.Width + point.X) * 4;
                    Require(highlightedRendered[pixel] == 255 && highlightedRendered[pixel + 1] == 255
                        && highlightedRendered[pixel + 2] == 255,
                        "A pale bubble highlight must not split source coverage or leave original lettering visible.");
                }
                for (int y = 0; y < fixture.Height; y++)
                for (int x = 0; x < fixture.Width; x++)
                    if (Math.Pow((x - 297.5) / 77.5, 2) + Math.Pow((y - 202.5) / 97.5, 2) >= 0.94)
                        Require(highlightedPixels.AsSpan((y * fixture.Width + x) * 4, 4)
                            .SequenceEqual(highlightedRendered.AsSpan((y * fixture.Width + x) * 4, 4)),
                            "Crossing an interior highlight must preserve the closed bubble outline and exterior artwork.");
                var variablePaperPixels = Pixels(frame);
                for (int y = 174; y < 186; y++)
                for (int x = 220; x < 320; x++)
                {
                    double radius = Math.Pow((x - 297.5) / 77.5, 2) + Math.Pow((y - 202.5) / 97.5, 2);
                    if (radius < 0.94 && variablePaperPixels[(y * fixture.Width + x) * 4] == 248)
                        Set(variablePaperPixels, fixture.Width, x, y, 232, 232, 232);
                    else if (radius >= 0.94 && radius <= 1)
                        Set(variablePaperPixels, fixture.Width, x, y, 218, 218, 218);
                }
                var variablePaperFrame = BitmapSource.Create(fixture.Width, fixture.Height, fixture.Dpi, fixture.Dpi,
                    PixelFormats.Bgra32, null, variablePaperPixels, fixture.Width * 4);
                variablePaperFrame.Freeze();
                var variablePaperFixture = fixture with { Id = "variable-white-paper", Background = "variable-white-paper",
                    WhiteSourceCoverPoints = new[] { new Box(250, 178, 1, 1), new Box(261, 178, 1, 1),
                        new Box(265, 178, 1, 1), new Box(293, 128, 1, 1) } };
                var variablePaperResult = Replay(variablePaperFixture, variablePaperFrame);
                Verify(variablePaperFixture, variablePaperResult);
                if (diagnostics is not null) WriteDiagnostics(diagnostics, variablePaperFixture,
                    variablePaperFrame, variablePaperResult, "fixed-text");
                Require(variablePaperResult.Items.Single(item => item.Kind == "caption").FontSize >= 28,
                    "A sampled gray patch inside white bubble paper must retain complete, readable local text.");
                var variablePaperRendered = Pixels(variablePaperResult.Rendered);
                for (int y = 0; y < fixture.Height; y++)
                for (int x = 0; x < fixture.Width; x++)
                    if (Math.Pow((x - 297.5) / 77.5, 2) + Math.Pow((y - 202.5) / 97.5, 2) >= 0.94)
                        Require(variablePaperPixels.AsSpan((y * fixture.Width + x) * 4, 4)
                            .SequenceEqual(variablePaperRendered.AsSpan((y * fixture.Width + x) * 4, 4)),
                            "Covering translucent gray paper must preserve its faint outline and every exterior pixel.");
                var exposedInkPixels = Pixels(frame);
                for (int y = 105; y < 300; y++)
                for (int x = 220; x < 375; x++)
                    if (Math.Pow((x - 297.5) / 77.5, 2) + Math.Pow((y - 202.5) / 97.5, 2) < 0.94
                        && exposedInkPixels[(y * fixture.Width + x) * 4] == 248)
                        Set(exposedInkPixels, fixture.Width, x, y, 238, 237, 242);
                Paint(exposedInkPixels, fixture.Width, fixture.Height, new Drawing.Rectangle(220, 184, 106, 10),
                    (x, y) => Math.Pow((x - 297.5) / 77.5, 2) + Math.Pow((y - 202.5) / 97.5, 2) < 0.94
                        && exposedInkPixels[(y * fixture.Width + x) * 4] != 18
                        ? ((byte)255, (byte)255, (byte)255)
                        : (exposedInkPixels[(y * fixture.Width + x) * 4 + 2],
                            exposedInkPixels[(y * fixture.Width + x) * 4 + 1], exposedInkPixels[(y * fixture.Width + x) * 4]));
                Set(exposedInkPixels, fixture.Width, 269, 187, 227, 221, 213);
                Set(exposedInkPixels, fixture.Width, 277, 190, 219, 236, 232);
                var exposedInkFrame = BitmapSource.Create(fixture.Width, fixture.Height, fixture.Dpi, fixture.Dpi,
                    PixelFormats.Bgra32, null, exposedInkPixels, fixture.Width * 4);
                exposedInkFrame.Freeze();
                var exposedInkFixture = fixture with { Background = "source-ink-beside-highlight",
                    WhiteSourceCoverPoints = new[] { new Box(265, 165, 1, 1), new Box(297, 221, 1, 1),
                        new Box(269, 187, 1, 1), new Box(277, 190, 1, 1) } };
                var exposedInkResult = Replay(exposedInkFixture, exposedInkFrame);
                Verify(exposedInkFixture, exposedInkResult);
                var exposedInkRendered = Pixels(exposedInkResult.Rendered);
                for (int y = 0; y < fixture.Height; y++)
                for (int x = 0; x < fixture.Width; x++)
                    if (Math.Pow((x - 297.5) / 77.5, 2) + Math.Pow((y - 202.5) / 97.5, 2) >= 0.94)
                        Require(exposedInkPixels.AsSpan((y * fixture.Width + x) * 4, 4)
                            .SequenceEqual(exposedInkRendered.AsSpan((y * fixture.Width + x) * 4, 4)),
                            "Covering lettering beside a pale highlight must preserve the outline and exterior artwork.");
                var balancedSourcePixels = Pixels(frame);
                Paint(balancedSourcePixels, fixture.Width, fixture.Height, new Drawing.Rectangle(220, 105, 155, 195),
                    (x, y) => x < 222 || x >= 373 || y < 107 || y >= 298
                        ? ((byte)18, (byte)18, (byte)18) : ((byte)238, (byte)237, (byte)242));
                for (int x = 264; x < 329; x += 8)
                    Paint(balancedSourcePixels, fixture.Width, fixture.Height, new Drawing.Rectangle(x, 160, 3, 82),
                        (_, _) => ((byte)18, (byte)18, (byte)18));
                Paint(balancedSourcePixels, fixture.Width, fixture.Height, new Drawing.Rectangle(222, 184, 104, 10),
                    (x, y) => balancedSourcePixels[(y * fixture.Width + x) * 4] == 18
                        ? ((byte)18, (byte)18, (byte)18) : ((byte)255, (byte)255, (byte)255));
                var balancedFrame = BitmapSource.Create(fixture.Width, fixture.Height, fixture.Dpi, fixture.Dpi,
                    PixelFormats.Bgra32, null, balancedSourcePixels, fixture.Width * 4);
                balancedFrame.Freeze();
                var balancedFixture = fixture with { Background = "centered-pale-bubble" };
                var balancedResult = Replay(balancedFixture, balancedFrame);
                Verify(balancedFixture, balancedResult);
                if (diagnostics is not null) WriteDiagnostics(diagnostics,
                    balancedFixture with { Id = "centered-pale-bubble" }, balancedFrame, balancedResult, "fixed-text");
                var balancedCaption = balancedResult.Items.Single(item => item.Kind == "caption");
                Require(balancedCaption.FontSize >= 43,
                    "A complete centered caption must retain a readable font in the available bubble area.");
                var balancedPixels = Pixels(balancedResult.Rendered);
                for (int line = 0; line < balancedCaption.Lines.Length; line++)
                {
                    int firstRow = balancedCaption.Bounds.Y + balancedCaption.Bounds.Height * line / balancedCaption.Lines.Length;
                    int lastRow = balancedCaption.Bounds.Y + balancedCaption.Bounds.Height * (line + 1) / balancedCaption.Lines.Length;
                    int leftInk = fixture.Width, rightInk = -1;
                    for (int y = firstRow; y < lastRow; y++)
                    for (int x = balancedCaption.Bounds.X; x < balancedCaption.Bounds.X + balancedCaption.Bounds.Width; x++)
                    {
                        int balancedPixel = (y * fixture.Width + x) * 4;
                        if (balancedSourcePixels[balancedPixel] < 180 || balancedPixels[balancedPixel] >= 128) continue;
                        leftInk = Math.Min(leftInk, x); rightInk = Math.Max(rightInk, x);
                    }
                    Require(rightInk >= leftInk && Math.Abs((leftInk + rightInk) / 2d - 297.5) <= 6,
                        "Complete caption lines must share the bubble center when its pale interior admits a centered arrangement.");
                }
                for (int y = 0; y < fixture.Height; y++)
                for (int x = 0; x < fixture.Width; x++)
                    if (!new Drawing.Rectangle(222, 107, 151, 191).Contains(x, y))
                        Require(balancedSourcePixels.AsSpan((y * fixture.Width + x) * 4, 4)
                            .SequenceEqual(balancedPixels.AsSpan((y * fixture.Width + x) * 4, 4)),
                            "A centered paragraph must preserve the bubble outline and exterior artwork.");
                var notchPixels = (byte[])exposedInkPixels.Clone();
                for (int y = 105; y <= 210; y++)
                for (int x = 248; x <= 330; x++)
                {
                    double t = Math.Clamp(((x - 260d) * 55 + (y - 105d) * 100) / 13025, 0, 1);
                    double distance = Math.Sqrt(Math.Pow(x - (260 + t * 55), 2) + Math.Pow(y - (105 + t * 100), 2));
                    if (distance < 7) Set(notchPixels, fixture.Width, x, y,
                        distance < 4 ? (byte)255 : (byte)218,
                        distance < 4 ? (byte)255 : (byte)218,
                        distance < 4 ? (byte)255 : (byte)218);
                }
                var notchFrame = BitmapSource.Create(fixture.Width, fixture.Height, fixture.Dpi, fixture.Dpi,
                    PixelFormats.Bgra32, null, notchPixels, fixture.Width * 4);
                notchFrame.Freeze();
                var notchFixture = fixture with { Background = "pale-diagonal-notch",
                    WhiteSourceCoverPoints = new[] { new Box(293, 128, 1, 1) } };
                var notchResult = Replay(notchFixture, notchFrame);
                Verify(notchFixture, notchResult);
                var notchRendered = Pixels(notchResult.Rendered);
                foreach (var point in new[] { new Drawing.Point(284, 159), new Drawing.Point(303, 173),
                    new Drawing.Point(312, 189), new Drawing.Point(316, 209) })
                    for (int channel = 0; channel < 3; channel++)
                        Require(notchRendered[(point.Y * fixture.Width + point.X) * 4 + channel] == 218,
                            "A pale diagonal outline connected to the exterior must stay visible within the detected text rectangle.");
                var tintedPixels = Pixels(frame);
                for (int y = 105; y < 300; y++)
                for (int x = 220; x < 375; x++)
                    if (Math.Pow((x - 297.5) / 77.5, 2) + Math.Pow((y - 202.5) / 97.5, 2) < 0.94
                        && tintedPixels[(y * fixture.Width + x) * 4] == 248)
                        Set(tintedPixels, fixture.Width, x, y, 250, 233, 247);
                var tintedFrame = BitmapSource.Create(fixture.Width, fixture.Height, fixture.Dpi, fixture.Dpi,
                    PixelFormats.Bgra32, null, tintedPixels, fixture.Width * 4);
                tintedFrame.Freeze();
                var tintedFixture = fixture with { Background = "pale-tinted-bubble" };
                var tintedResult = Replay(tintedFixture, tintedFrame);
                Verify(tintedFixture, tintedResult);
                var tintedRendered = Pixels(tintedResult.Rendered);
                foreach (var point in new[] { new Drawing.Point(293, 128), new Drawing.Point(293, 277) })
                {
                    int pixel = (point.Y * fixture.Width + point.X) * 4;
                    Require(tintedRendered[pixel] == 247 && tintedRendered[pixel + 1] == 233
                        && tintedRendered[pixel + 2] == 250,
                        "A genuinely tinted pale bubble must retain its sampled color over original lettering.");
                }
                for (int y = 0; y < fixture.Height; y++)
                for (int x = 0; x < fixture.Width; x++)
                    if (Math.Pow((x - 297.5) / 77.5, 2) + Math.Pow((y - 202.5) / 97.5, 2) >= 0.94)
                        Require(tintedPixels.AsSpan((y * fixture.Width + x) * 4, 4)
                            .SequenceEqual(tintedRendered.AsSpan((y * fixture.Width + x) * 4, 4)),
                            "Retaining a pale bubble tint must preserve its outline and exterior artwork.");
                var weakBoundaryPixels = Pixels(frame);
                Paint(weakBoundaryPixels, fixture.Width, fixture.Height, new Drawing.Rectangle(185, 100, 195, 205),
                    (x, y) => Math.Pow((x - 297.5) / 77.5, 2) + Math.Pow((y - 202.5) / 97.5, 2) > 1
                        ? ((byte)90, (byte)120, (byte)150)
                        : (weakBoundaryPixels[(y * fixture.Width + x) * 4 + 2],
                            weakBoundaryPixels[(y * fixture.Width + x) * 4 + 1], weakBoundaryPixels[(y * fixture.Width + x) * 4]));
                Paint(weakBoundaryPixels, fixture.Width, fixture.Height, new Drawing.Rectangle(190, 192, 30, 22),
                    (_, _) => ((byte)248, (byte)248, (byte)248));
                Paint(weakBoundaryPixels, fixture.Width, fixture.Height, new Drawing.Rectangle(220, 199, 3, 8),
                    (_, _) => ((byte)234, (byte)234, (byte)234));
                Paint(weakBoundaryPixels, fixture.Width, fixture.Height, new Drawing.Rectangle(197, 199, 2, 2),
                    (_, _) => ((byte)225, (byte)225, (byte)225));
                var weakBoundaryFrame = BitmapSource.Create(fixture.Width, fixture.Height, fixture.Dpi, fixture.Dpi,
                    PixelFormats.Bgra32, null, weakBoundaryPixels, fixture.Width * 4);
                weakBoundaryFrame.Freeze();
                var weakBoundaryResult = Replay(fixture, weakBoundaryFrame);
                var weakRendered = Pixels(weakBoundaryResult.Rendered);
                Verify(fixture with { Background = "weak-boundary" }, weakBoundaryResult);
                Require(weakRendered[(128 * fixture.Width + 293) * 4] == 255,
                    "A faint boundary must retain full bubble coverage of original lettering.");
                for (int y = 100; y < 305; y++)
                for (int x = 185; x < 380; x++)
                    if (Math.Pow((x - 297.5) / 77.5, 2) + Math.Pow((y - 202.5) / 97.5, 2) >= 0.94)
                        for (int channel = 0; channel < 4; channel++)
                            Require(Math.Abs(weakBoundaryPixels[(y * fixture.Width + x) * 4 + channel]
                                - weakRendered[(y * fixture.Width + x) * 4 + channel]) <= 1,
                                "A faint bubble boundary must preserve its outline and adjacent floor within one compositor rounding level.");
                foreach (byte exteriorPaper in new byte[] { 248, 240 })
                {
                    var shadedWeakBoundaryPixels = (byte[])weakBoundaryPixels.Clone();
                    for (int y = 192; y < 214; y++)
                    for (int x = 190; x < 220; x++)
                        if (shadedWeakBoundaryPixels[(y * fixture.Width + x) * 4] == 248)
                            Set(shadedWeakBoundaryPixels, fixture.Width, x, y, exteriorPaper, exteriorPaper, exteriorPaper);
                    for (int y = 174; y < 186; y++)
                    for (int x = 220; x < 320; x++)
                        if (Math.Pow((x - 297.5) / 77.5, 2) + Math.Pow((y - 202.5) / 97.5, 2) < 0.94
                            && shadedWeakBoundaryPixels[(y * fixture.Width + x) * 4] == 248)
                            Set(shadedWeakBoundaryPixels, fixture.Width, x, y, 232, 232, 232);
                    var shadedWeakBoundaryFrame = BitmapSource.Create(fixture.Width, fixture.Height, fixture.Dpi, fixture.Dpi,
                        PixelFormats.Bgra32, null, shadedWeakBoundaryPixels, fixture.Width * 4);
                    shadedWeakBoundaryFrame.Freeze();
                    var shadedWeakBoundaryFixture = fixture with { Id = $"shaded-paper-faint-boundary-{exteriorPaper}",
                        Background = "shaded-paper-faint-boundary" };
                    var shadedWeakBoundaryResult = Replay(shadedWeakBoundaryFixture, shadedWeakBoundaryFrame);
                    Verify(shadedWeakBoundaryFixture, shadedWeakBoundaryResult);
                    if (diagnostics is not null) WriteDiagnostics(diagnostics, shadedWeakBoundaryFixture,
                        shadedWeakBoundaryFrame, shadedWeakBoundaryResult, "fixed-text");
                    var shadedWeakBoundaryRendered = Pixels(shadedWeakBoundaryResult.Rendered);
                    for (int y = 100; y < 305; y++)
                    for (int x = 185; x < 380; x++)
                        if (Math.Pow((x - 297.5) / 77.5, 2) + Math.Pow((y - 202.5) / 97.5, 2) >= 0.94)
                            for (int channel = 0; channel < 4; channel++)
                                Require(Math.Abs(shadedWeakBoundaryPixels[(y * fixture.Width + x) * 4 + channel]
                                    - shadedWeakBoundaryRendered[(y * fixture.Width + x) * 4 + channel]) <= 1,
                                    "Observed gray paper must not admit a faint outline or paint the exterior floor beyond it.");
                }
                var weakDarkPixels = (byte[])weakBoundaryPixels.Clone();
                for (int pixel = 0; pixel < weakDarkPixels.Length; pixel += 4)
                    if (weakDarkPixels[pixel] == weakDarkPixels[pixel + 1]
                        && weakDarkPixels[pixel] == weakDarkPixels[pixel + 2])
                        weakDarkPixels[pixel] = weakDarkPixels[pixel + 1] = weakDarkPixels[pixel + 2]
                            = (byte)Math.Clamp(272 - weakDarkPixels[pixel], 0, 255);
                var weakDarkFrame = BitmapSource.Create(fixture.Width, fixture.Height, fixture.Dpi, fixture.Dpi,
                    PixelFormats.Bgra32, null, weakDarkPixels, fixture.Width * 4);
                weakDarkFrame.Freeze();
                var weakDarkResult = Replay(fixture with { Background = "weak-dark-boundary" }, weakDarkFrame);
                Verify(fixture with { Background = "weak-dark-boundary" }, weakDarkResult);
                var weakDarkRendered = Pixels(weakDarkResult.Rendered);
                Require(weakDarkRendered[(128 * fixture.Width + 293) * 4] == 24,
                    "A dark bubble must still cover original lettering inside a faint light boundary.");
                for (int y = 100; y < 305; y++)
                for (int x = 185; x < 380; x++)
                    if (Math.Pow((x - 297.5) / 77.5, 2) + Math.Pow((y - 202.5) / 97.5, 2) >= 0.94)
                        for (int channel = 0; channel < 4; channel++)
                            Require(Math.Abs(weakDarkPixels[(y * fixture.Width + x) * 4 + channel]
                                - weakDarkRendered[(y * fixture.Width + x) * 4 + channel]) <= 1,
                                "A faint light outline around a dark bubble must preserve adjacent artwork.");
                var counterPixels = Pixels(frame);
                Paint(counterPixels, fixture.Width, fixture.Height, new Drawing.Rectangle(310, 258, 16, 17),
                    (x, y) => x < 312 || x >= 324 || y < 260 || y >= 273
                        ? ((byte)18, (byte)18, (byte)18) : ((byte)248, (byte)248, (byte)248));
                var counterFrame = BitmapSource.Create(fixture.Width, fixture.Height, fixture.Dpi, fixture.Dpi,
                    PixelFormats.Bgra32, null, counterPixels, fixture.Width * 4);
                counterFrame.Freeze();
                Verify(fixture, Replay(fixture, counterFrame));
                Paint(counterPixels, fixture.Width, fixture.Height, new Drawing.Rectangle(310, 258, 16, 17),
                    (x, y) => x >= 315 && x < 321 && y >= 263 && y < 267
                        ? ((byte)248, (byte)248, (byte)248) : ((byte)18, (byte)18, (byte)18));
                counterFrame = BitmapSource.Create(fixture.Width, fixture.Height, fixture.Dpi, fixture.Dpi,
                    PixelFormats.Bgra32, null, counterPixels, fixture.Width * 4);
                counterFrame.Freeze();
                Verify(fixture, Replay(fixture, counterFrame));
                Paint(counterPixels, fixture.Width, fixture.Height, new Drawing.Rectangle(310, 258, 16, 17),
                    (_, _) => ((byte)18, (byte)18, (byte)18));
                var compactArtwork = BitmapSource.Create(fixture.Width, fixture.Height, fixture.Dpi, fixture.Dpi,
                    PixelFormats.Bgra32, null, counterPixels, fixture.Width * 4);
                compactArtwork.Freeze();
                var protectedCompactArtwork = Replay(fixture, compactArtwork);
                var protectedPixels = Pixels(protectedCompactArtwork.Rendered);
                for (int y = 258; y < 275; y++)
                for (int x = 310; x < 326; x++)
                    Require(protectedPixels[(y * fixture.Width + x) * 4] == 18,
                        "A solid compact artwork component outside the text region must not be filled as lettering.");
                var connectedArtwork = Pixels(frame);
                Paint(connectedArtwork, fixture.Width, fixture.Height, new Drawing.Rectangle(252, 160, 44, 84),
                    (x, y) => Math.Abs(x - 252 - (y - 160) / 2) <= 1
                        ? ((byte)18, (byte)18, (byte)18)
                        : (connectedArtwork[(y * fixture.Width + x) * 4 + 2],
                            connectedArtwork[(y * fixture.Width + x) * 4 + 1],
                            connectedArtwork[(y * fixture.Width + x) * 4]));
                var connectedArtworkFrame = BitmapSource.Create(fixture.Width, fixture.Height, fixture.Dpi, fixture.Dpi,
                    PixelFormats.Bgra32, null, connectedArtwork, fixture.Width * 4);
                connectedArtworkFrame.Freeze();
                var connectedArtworkResult = Replay(fixture with { Background = "artwork" }, connectedArtworkFrame,
                    allowLayoutRejection: true);
                Require(Pixels(connectedArtworkResult.Rendered)[(160 * fixture.Width + 252) * 4] == 18,
                    "Artwork connected through lettering must remain visible outside the text region.");
                Require(Pixels(connectedArtworkResult.Rendered)[(215 * fixture.Width + 280) * 4] == 18,
                    "A rejected source cover must not authorize caption paint over artwork inside the text region.");
                foreach (bool narrowGlyph in new[] { false, true })
                {
                    var glyphPixels = Pixels(frame);
                    Paint(glyphPixels, fixture.Width, fixture.Height, fixture.Regions[0].Bounds.Drawing,
                        (_, _) => ((byte)248, (byte)248, (byte)248));
                    var glyph = narrowGlyph ? new Drawing.Rectangle(294, 151, 8, 100)
                        : new Drawing.Rectangle(275, 172, 40, 40);
                    Paint(glyphPixels, fixture.Width, fixture.Height, glyph,
                        (x, y) => narrowGlyph || x < glyph.Left + 4 || x >= glyph.Right - 4
                            || y < glyph.Top + 4 || y >= glyph.Bottom - 4
                            ? ((byte)18, (byte)18, (byte)18) : ((byte)248, (byte)248, (byte)248));
                    var glyphFrame = BitmapSource.Create(fixture.Width, fixture.Height, fixture.Dpi, fixture.Dpi,
                        PixelFormats.Bgra32, null, glyphPixels, fixture.Width * 4);
                    glyphFrame.Freeze();
                    var glyphFixture = narrowGlyph ? fixture with { Regions = new[] {
                        fixture.Regions[0] with { Bounds = new Box(282, 143, 32, 120) } } } : fixture;
                    Verify(glyphFixture, Replay(glyphFixture, glyphFrame));
                }
                var narrowDetection = fixture with { Regions = new[] {
                    fixture.Regions[0] with { Bounds = new Box(282, 143, 32, 120),
                        Translation = "ฉันจะใช้ความสามารถทั้งหมดที่มี เพื่อทำให้ความปรารถนาของฉันเป็นจริง" } } };
                var narrowResult = Replay(narrowDetection, BuildFrame(narrowDetection));
                Verify(narrowDetection, narrowResult);
                var joined = narrowDetection with { Background = "joined-bubble", Regions = new[] {
                    narrowDetection.Regions[0] with { Translation = "ฉันจะใช้ความสามารถทั้งหมดที่มี" } } };
                var joinedPixels = Pixels(BuildFrame(narrowDetection));
                Paint(joinedPixels, fixture.Width, fixture.Height, narrowDetection.Regions[0].Bounds.Drawing,
                    (_, _) => ((byte)248, (byte)248, (byte)248));
                foreach (int inkTop in new[] { 160, 232 })
                    Paint(joinedPixels, fixture.Width, fixture.Height, new Drawing.Rectangle(292, inkTop, 4, 12),
                        (_, _) => ((byte)18, (byte)18, (byte)18));
                Paint(joinedPixels, fixture.Width, fixture.Height, new Drawing.Rectangle(220, 196, 65, 16),
                    (_, _) => ((byte)18, (byte)18, (byte)18));
                Paint(joinedPixels, fixture.Width, fixture.Height, new Drawing.Rectangle(307, 196, 68, 16),
                    (_, _) => ((byte)18, (byte)18, (byte)18));
                var joinedFrame = BitmapSource.Create(fixture.Width, fixture.Height, fixture.Dpi, fixture.Dpi,
                    PixelFormats.Bgra32, null, joinedPixels, fixture.Width * 4);
                joinedFrame.Freeze();
                var joinedResult = Replay(joined, joinedFrame);
                Verify(joined, joinedResult);
                var shortJoined = joined with { Regions = new[] {
                    joined.Regions[0] with { Translation = "ไป มา" } } };
                var shortJoinedResult = Replay(shortJoined, joinedFrame);
                Verify(shortJoined, shortJoinedResult);
                Require(shortJoinedResult.Items.Single(item => item.Kind == "caption").FontSize >= 28,
                    "A short joined caption must use both roomy lobes even when its neck needs more rows than there are words.");
                var joinedRendered = Pixels(joinedResult.Rendered);
                var shortJoinedRendered = Pixels(shortJoinedResult.Rendered);
                for (int y = 0; y < fixture.Height; y++)
                for (int x = 0; x < fixture.Width; x++)
                    if (Math.Pow((x - 297.5) / 77.5, 2) + Math.Pow((y - 202.5) / 97.5, 2) >= 0.94
                        || y >= 196 && y < 212 && (x < 285 || x >= 307))
                    {
                        int pixel = (y * fixture.Width + x) * 4;
                        Require(joinedPixels.AsSpan(pixel, 4).SequenceEqual(joinedRendered.AsSpan(pixel, 4))
                            && joinedPixels.AsSpan(pixel, 4).SequenceEqual(shortJoinedRendered.AsSpan(pixel, 4)),
                            "Joined bubble fitting must preserve its narrow neck and exterior artwork.");
                    }
                Require(first.Items.Single(item => item.Kind == "caption").Lines.All(line => !string.IsNullOrWhiteSpace(line)),
                    "An ordinary oval must not acquire spare blank caption rows.");
                var offsetBubble = fixture with { Background = "offset-bubble", Regions = new[] {
                    fixture.Regions[0] with { Bounds = new Box(277, 132, 50, 122) } },
                    TextContainers = new[] { new ContainerFixture("dialogue", new Box(220, 105, 155, 205), true) } };
                var offsetPixels = Pixels(BuildFrame(offsetBubble));
                bool InsideOffsetBubble(int x, int y) =>
                    Math.Pow((x - 326d) / 34, 2) + Math.Pow((y - 152d) / 45, 2) <= 1
                    || Math.Pow((x - 285d) / 58, 2) + Math.Pow((y - 240d) / 62, 2) <= 1;
                Paint(offsetPixels, fixture.Width, fixture.Height, new Drawing.Rectangle(220, 105, 155, 205),
                    (x, y) => {
                        if (!InsideOffsetBubble(x, y)) return ((byte)90, (byte)120, (byte)150);
                        for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                            if (!InsideOffsetBubble(x + dx, y + dy)) return ((byte)18, (byte)18, (byte)18);
                        return ((byte)248, (byte)248, (byte)248);
                    });
                foreach (var ink in new[] { new Drawing.Rectangle(320, 136, 4, 12),
                    new Drawing.Rectangle(277, 232, 4, 12) })
                    Paint(offsetPixels, fixture.Width, fixture.Height, ink,
                        (_, _) => ((byte)18, (byte)18, (byte)18));
                var offsetFrame = BitmapSource.Create(fixture.Width, fixture.Height, fixture.Dpi, fixture.Dpi,
                    PixelFormats.Bgra32, null, offsetPixels, fixture.Width * 4);
                offsetFrame.Freeze();
                var offsetResult = Replay(offsetBubble, offsetFrame);
                Verify(offsetBubble, offsetResult);
                var offsetRendered = Pixels(offsetResult.Rendered);
                foreach (var lobe in new[] { new Drawing.Rectangle(304, 126, 40, 48),
                    new Drawing.Rectangle(248, 218, 74, 58) })
                {
                    int translatedInk = 0;
                    for (int y = lobe.Top; y < lobe.Bottom; y++)
                    for (int x = lobe.Left; x < lobe.Right; x++)
                        if (offsetPixels[(y * fixture.Width + x) * 4] == 248
                            && offsetRendered[(y * fixture.Width + x) * 4] < 128) translatedInk++;
                    Require(translatedInk >= 8,
                        "Offset joined captions must use both lobes, with translated glyphs following each lobe's horizontal position.");
                }
                Require(offsetRendered[(140 * fixture.Width + 321) * 4] == 255,
                    "Offset joined bubbles must cover the upper lobe's source ink even when the OCR corners lie outside.");
                for (int y = 105; y < 310; y++)
                for (int x = 220; x < 375; x++)
                    if (!InsideOffsetBubble(x, y) || offsetPixels[(y * fixture.Width + x) * 4] == 18
                        && !new Drawing.Rectangle(320, 136, 4, 12).Contains(x, y)
                        && !new Drawing.Rectangle(277, 232, 4, 12).Contains(x, y))
                        for (int channel = 0; channel < 4; channel++)
                            Require(Math.Abs(offsetPixels[(y * fixture.Width + x) * 4 + channel]
                                - offsetRendered[(y * fixture.Width + x) * 4 + channel]) <= 1,
                                "Offset joined bubble outlines and exterior artwork must remain unchanged within one compositor rounding level.");
                var whiteOffset = offsetBubble with { Id = "white-offset-bubble", Regions = new[] {
                    offsetBubble.Regions[0] with { Bounds = new Box(277, 132, 66, 122) } } };
                var whiteOffsetPixels = (byte[])offsetPixels.Clone();
                Paint(whiteOffsetPixels, fixture.Width, fixture.Height, new Drawing.Rectangle(155, 10, 310, 366),
                    (x, y) => InsideOffsetBubble(x, y)
                        ? (whiteOffsetPixels[(y * fixture.Width + x) * 4 + 2],
                            whiteOffsetPixels[(y * fixture.Width + x) * 4 + 1], whiteOffsetPixels[(y * fixture.Width + x) * 4])
                        : ((byte)248, (byte)248, (byte)248));
                var whiteOffsetFrame = BitmapSource.Create(fixture.Width, fixture.Height, fixture.Dpi, fixture.Dpi,
                    PixelFormats.Bgra32, null, whiteOffsetPixels, fixture.Width * 4);
                whiteOffsetFrame.Freeze();
                var whiteOffsetResult = Replay(whiteOffset, whiteOffsetFrame);
                if (diagnostics is not null) WriteDiagnostics(diagnostics, whiteOffset, whiteOffsetFrame,
                    whiteOffsetResult, "fixed-text");
                Verify(whiteOffset, whiteOffsetResult);
                var whiteOffsetRendered = Pixels(whiteOffsetResult.Rendered);
                Require(whiteOffsetResult.Items.Single(item => item.Kind == "caption").FallbackReason is null
                    && whiteOffsetRendered[(140 * fixture.Width + 321) * 4] == 255
                    && whiteOffsetRendered[(236 * fixture.Width + 278) * 4] == 255,
                    "A joined bubble on matching exterior paper must conceal both source passages and retain its caption locally.");
                var whiteOffsetRepeated = Replay(whiteOffset, whiteOffsetFrame);
                Require(whiteOffsetRepeated.RenderHash == whiteOffsetResult.RenderHash,
                    "A joined bubble with matching exterior paper must render stable repeated pixels.");
                for (int y = 105; y < 310; y++)
                for (int x = 220; x < 375; x++)
                    if (!InsideOffsetBubble(x, y) || whiteOffsetPixels[(y * fixture.Width + x) * 4] == 18
                        && !new Drawing.Rectangle(320, 136, 4, 12).Contains(x, y)
                        && !new Drawing.Rectangle(277, 232, 4, 12).Contains(x, y))
                        for (int channel = 0; channel < 4; channel++)
                            Require(Math.Abs(whiteOffsetPixels[(y * fixture.Width + x) * 4 + channel]
                                - whiteOffsetRendered[(y * fixture.Width + x) * 4 + channel]) <= 1,
                                "Retrying a bubble seed must preserve the outline and matching exterior paper.");
                var openedOffsetPixels = (byte[])whiteOffsetPixels.Clone();
                Paint(openedOffsetPixels, fixture.Width, fixture.Height, new Drawing.Rectangle(225, 238, 12, 5),
                    (_, _) => ((byte)248, (byte)248, (byte)248));
                var openedOffsetFrame = BitmapSource.Create(fixture.Width, fixture.Height, fixture.Dpi, fixture.Dpi,
                    PixelFormats.Bgra32, null, openedOffsetPixels, fixture.Width * 4);
                openedOffsetFrame.Freeze();
                var openedOffset = Replay(whiteOffset, openedOffsetFrame, allowLayoutRejection: true);
                var openedOffsetRendered = Pixels(openedOffset.Rendered);
                for (int y = 105; y < 310; y++)
                for (int x = 220; x < 375; x++)
                    if (openedOffsetPixels[(y * fixture.Width + x) * 4] == 18
                        && !whiteOffset.Regions[0].Bounds.Drawing.Contains(x, y))
                        Require(openedOffsetPixels.AsSpan((y * fixture.Width + x) * 4, 4)
                            .SequenceEqual(openedOffsetRendered.AsSpan((y * fixture.Width + x) * 4, 4)),
                            "Retrying an open joined bubble must not paint over its remaining outline.");
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
                var wideBubble = fixture with { Regions = new[] {
                        fixture.Regions[0] with { Bounds = new Box(310, 155, 45, 130) } },
                    TextContainers = new[] { fixture.TextContainers[0] with { Bounds = new Box(190, 115, 200, 220) } } };
                var wideFrame = BuildFrame(wideBubble);
                wideBubble = wideBubble with { Background = "wide-bubble" };
                var wideResult = Replay(wideBubble, wideFrame);
                Verify(wideBubble, wideResult);
                Require(Pixels(wideResult.Rendered)[(128 * fixture.Width + 293) * 4] == 255,
                    "A tall narrow detection must find the full wider bubble and cover its original lettering.");
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

    private static ReplayResult Replay(ReplayCase fixture, BitmapSource frame, bool performance = false,
        bool allowMissing = false, bool allowLayoutRejection = false, int? pendingRegionIndex = null)
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
            if (pendingRegionIndex is int pendingIndex)
            {
                var partialTranslations = fixture.Regions.Select((region, index) =>
                    index == pendingIndex ? "" : region.Translation).ToArray();
                overlay.Render(capture, regions, partialTranslations, SubtitleStyle.Overwrite, 6, frame,
                    japaneseToThai, allowMissingTranslations: true, captionStyle: captionStyle);
                overlay.UpdateLayout();
                var pendingTransform = PresentationSource.FromVisual(overlay)?.CompositionTarget?.TransformFromDevice
                    ?? throw new InvalidOperationException("Subtitle overlay has no display transform.");
                var pendingVisuals = ((Canvas)overlay.Content).Children.OfType<Border>().ToArray();
                var pendingItems = pendingVisuals.Select(border => SceneItem.From(border, fixture, capture,
                    Forms.SystemInformation.VirtualScreen, pendingTransform)).ToArray();
                Require(pendingItems.All(item => item.RegionId != fixture.Regions[pendingIndex].Id),
                    $"{fixture.Id}: a pending passage must not receive a caption or source cover.");
                var partial = Pixels(RenderCapturedView(frame, pendingVisuals, pendingItems,
                    fixture.Width, fixture.Height));
                var original = Pixels(frame);
                var pendingBounds = fixture.Regions[pendingIndex].Bounds.Drawing;
                for (int y = pendingBounds.Top; y < pendingBounds.Bottom; y++)
                {
                    int offset = (y * fixture.Width + pendingBounds.Left) * 4;
                    Require(original.AsSpan(offset, pendingBounds.Width * 4)
                        .SequenceEqual(partial.AsSpan(offset, pendingBounds.Width * 4)),
                        $"{fixture.Id}: another caption must preserve every pending source pixel.");
                }
            }
            var firstRender = Stopwatch.StartNew();
            try
            {
                overlay.Render(capture, regions, fixture.Regions.Select(region => region.Translation).ToArray(),
                    SubtitleStyle.Overwrite, 6, frame, japaneseToThai, allowMissingTranslations: allowMissing,
                    captionStyle: captionStyle);
            }
            catch (SubtitleLayoutException) when (allowLayoutRejection)
            {
                // These two protection cases have no safe margin; rejection must leave the source untouched.
                Require(((Canvas)overlay.Content).Children.Count == 0,
                    "Rejected artwork or pending-ink placement must not leave a partial caption layer.");
            }
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
            if (fixture.SourceInkPoints is not null || fixture.WhiteSourceCoverPoints is not null)
            {
                var coverIndices = Enumerable.Range(0, items.Length).Where(i => items[i].Kind == "cover").ToArray();
                var covered = Pixels(RenderCapturedView(frame, coverIndices.Select(i => visuals[i]).ToArray(),
                    coverIndices.Select(i => items[i]).ToArray(), fixture.Width, fixture.Height));
                foreach (var point in fixture.SourceInkPoints ?? Array.Empty<Box>())
                    Require(!(covered[(point.Y * fixture.Width + point.X) * 4 + 1] > 175
                        && covered[(point.Y * fixture.Width + point.X) * 4 + 1]
                            > covered[(point.Y * fixture.Width + point.X) * 4] + 60),
                        $"{fixture.Id}: original heading ink remains at {point.X},{point.Y} before drawing Thai: "
                        + string.Join(",", covered.Skip((point.Y * fixture.Width + point.X) * 4).Take(4)));
                foreach (var point in fixture.WhiteSourceCoverPoints ?? Array.Empty<Box>())
                {
                    int pixel = (point.Y * fixture.Width + point.X) * 4;
                    Require(covered[pixel] == 255 && covered[pixel + 1] == 255 && covered[pixel + 2] == 255,
                        $"{fixture.Id}: original bubble lettering remains at {point.X},{point.Y} before drawing captions.");
                }
            }
            var rendered = RenderCapturedView(frame, visuals, items, fixture.Width, fixture.Height);
            if (pendingRegionIndex is not null)
            {
                overlay.Render(capture, regions, fixture.Regions.Select(region => region.Translation).ToArray(),
                    SubtitleStyle.Overwrite, 6, frame, japaneseToThai, captionStyle: captionStyle);
                overlay.UpdateLayout();
                var repeatedVisuals = canvas.Children.OfType<Border>().ToArray();
                var repeatedItems = repeatedVisuals.Select(border => SceneItem.From(border, fixture, capture,
                    desktop, transform)).ToArray();
                Require(JsonSerializer.Serialize(items) == JsonSerializer.Serialize(repeatedItems),
                    $"{fixture.Id}: repeated completed captions changed geometry, text or style.");
                Require(Pixels(rendered).AsSpan().SequenceEqual(Pixels(RenderCapturedView(frame,
                    repeatedVisuals, repeatedItems, fixture.Width, fixture.Height))),
                    $"{fixture.Id}: repeated completed captions changed rendered pixels.");
            }
            if (fixture.Background == "bubble" && !allowMissing)
            {
                var obstructed = BuildFrame(fixture with { Background = "dense-margins" });
                var translations = fixture.Regions.Select(region => region.Translation).ToArray();
                overlay.Render(capture, regions, translations, SubtitleStyle.Overwrite, 6, obstructed,
                    japaneseToThai, captionStyle: captionStyle);
                overlay.UpdateLayout();
                Require(canvas.Children.OfType<Border>().Any(border => border.Uid == "association-badge"),
                    "Text over artwork must first use a readable margin in the changed-view check.");
                overlay.Render(capture, regions, translations, SubtitleStyle.Overwrite, 6, frame,
                    japaneseToThai, captionStyle: captionStyle);
                overlay.UpdateLayout();
                var restored = canvas.Children.OfType<Border>()
                    .Select(border => SceneItem.From(border, fixture, capture, desktop, transform)).ToArray();
                Require(JsonSerializer.Serialize(restored) == JsonSerializer.Serialize(items),
                    "A changed captured view with available bubble space must restore complete captions inside the bubble.");
            }
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
                Require(pixels[offset] == 255 && pixels[offset + 1] == 255 && pixels[offset + 2] == 255,
                    "Enclosed bubble: source lettering outside the detection must be covered with white paper.");
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
        Box[]? SourceInkPoints = null, Box[]? WhiteSourceCoverPoints = null, string? ProgressiveRegion = null)
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
                : border.Uid == "association-badge" ? "association-badge" : "caption";
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
