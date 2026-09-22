using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Drawing;
using ImageFormat = System.Drawing.Imaging.ImageFormat;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tesseract;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Translumo.Local;

public sealed class SpatialOcr : IDisposable
{
    private OcrEngine? engine;
    private string? engineLanguage;
    private TesseractEngine? fallbackEngine;
    private string? fallbackLanguage;
    private readonly string dataPath;
    private readonly SemaphoreSlim recognitionGate = new(1, 1);

    private readonly string? detectorPython, detectorWorker, detectorModel;
    private Process? detectorProcess;
    private Task<string>? detectorErrors;

    public SpatialOcr(string? dataPath = null, string? pythonPath = null, string? detectorWorkerPath = null, string? detectorModelPath = null)
    {
        this.dataPath = dataPath ?? Path.Combine(AppContext.BaseDirectory, "tessdata");
        detectorPython = pythonPath;
        detectorWorker = detectorWorkerPath;
        detectorModel = detectorModelPath;
    }

    public static IReadOnlyList<OcrLanguage> AvailableLanguages => OcrEngine.AvailableRecognizerLanguages
        .Select(language => new OcrLanguage(language.LanguageTag, language.DisplayName)).ToArray();

    public async Task<IReadOnlyList<TextRegion>> RecognizeAsync(Bitmap bitmap, string language, CancellationToken token)
    {
        await recognitionGate.WaitAsync(token);
        try
        {
            var vertical = language.Equals("ja-vert", StringComparison.OrdinalIgnoreCase);
            var baseLanguage = language.Split('-')[0];
            var ocrLanguage = new Language(baseLanguage);
            if (vertical || language.EndsWith("-comic", StringComparison.OrdinalIgnoreCase))
                return await RecognizeComicAsync(bitmap, baseLanguage, vertical, token);
            if (!OcrEngine.IsLanguageSupported(ocrLanguage))
                return await Task.Run(() => RecognizeTesseract(bitmap, language, vertical, token), token);
            return await RecognizeWindowsAsync(bitmap, language, token);
        }
        finally { recognitionGate.Release(); }
    }

    private async Task<IReadOnlyList<TextRegion>> RecognizeComicAsync(Bitmap bitmap, string language, bool verticalJapanese, CancellationToken token)
    {
        if (!File.Exists(detectorPython) || !File.Exists(detectorWorker) || !File.Exists(detectorModel))
            throw new InvalidOperationException("Local manga detector is missing. Run local-ocr/setup.ps1, then select the installed Python runtime.");
        try
        {
            if (detectorProcess is null || detectorProcess.HasExited)
            {
                detectorProcess?.Dispose();
                var info = new ProcessStartInfo(detectorPython!)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                info.ArgumentList.Add("-u"); info.ArgumentList.Add(detectorWorker!);
                info.ArgumentList.Add("--model"); info.ArgumentList.Add(detectorModel!);
                detectorProcess = Process.Start(info) ?? throw new InvalidOperationException("Could not start local manga detector.");
                detectorErrors = detectorProcess.StandardError.ReadToEndAsync();
            }
            var process = detectorProcess;
            using var canceled = token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(true); }
                catch (InvalidOperationException) { }
            });
            using var encoded = new MemoryStream();
            bitmap.Save(encoded, ImageFormat.Png);
            var request = JsonSerializer.Serialize(new { image = Convert.ToBase64String(encoded.ToArray()), language });
            await process.StandardInput.WriteLineAsync(request.AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
            var reply = await process.StandardOutput.ReadLineAsync(token);
            if (reply is null)
                throw new InvalidOperationException("Local manga detector stopped. " + await detectorErrors!);
            using var document = JsonDocument.Parse(reply);
            if (document.RootElement.TryGetProperty("error", out var error))
                throw new InvalidOperationException("Local manga detector: " + error.GetString());
            var result = new List<TextRegion>();
            var imageBounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            foreach (var item in document.RootElement.GetProperty("regions").EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                var detected = new Rectangle(item.GetProperty("x").GetInt32(), item.GetProperty("y").GetInt32(),
                    item.GetProperty("width").GetInt32(), item.GetProperty("height").GetInt32());
                if (detected.Width <= 0 || detected.Height <= 0 || !imageBounds.Contains(detected))
                    throw new InvalidOperationException("Manga detector returned invalid image coordinates.");
                if (item.TryGetProperty("text", out var recognized))
                {
                    var recognizedText = recognized.GetString();
                    if (string.IsNullOrWhiteSpace(recognizedText))
                        throw new InvalidOperationException("Local manga recognizer returned empty text.");
                    result.Add(new TextRegion(recognizedText, detected));
                    continue;
                }
                var cropBounds = Rectangle.Intersect(imageBounds, Rectangle.Inflate(detected, 8, 8));
                using var crop = bitmap.Clone(cropBounds, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var vertical = verticalJapanese && item.GetProperty("vertical").GetBoolean();
                var words = vertical || !OcrEngine.IsLanguageSupported(new Language(language))
                    ? await Task.Run(() => RecognizeTesseract(crop, language, vertical, token), token)
                    : await RecognizeWindowsAsync(crop, language, token);
                var text = string.Join(Joiner(language), words.Select(word => word.Text));
                // Preserve detected blocks even when OCR cannot read them, so overwrite
                // can keep their source covered instead of exposing an untranslated gap.
                result.Add(new TextRegion(text, detected));
            }
            return result;
        }
        catch
        {
            StopDetector();
            token.ThrowIfCancellationRequested();
            throw;
        }
    }

    private void StopDetector()
    {
        if (detectorProcess is null) return;
        try { if (!detectorProcess.HasExited) detectorProcess.Kill(true); }
        catch (InvalidOperationException) { }
        detectorProcess.Dispose();
        detectorProcess = null;
    }
    private async Task<IReadOnlyList<TextRegion>> RecognizeWindowsAsync(Bitmap bitmap, string language, CancellationToken token)
    {
        if (engineLanguage != language)
        {
            engine = OcrEngine.TryCreateFromLanguage(new Language(language))
                ?? throw new InvalidOperationException($"Windows OCR for '{language}' is not installed.");
            engineLanguage = language;
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Bmp);
        stream.Position = 0;
        using var randomStream = stream.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(randomStream).AsTask(token);
        var scale = Math.Min(1, OcrEngine.MaxImageDimension / (double)Math.Max(bitmap.Width, bitmap.Height));
        var width = Math.Max(1, (uint)Math.Floor(bitmap.Width * scale));
        var height = Math.Max(1, (uint)Math.Floor(bitmap.Height * scale));
        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            new BitmapTransform { ScaledWidth = width, ScaledHeight = height },
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(token);
        var result = await engine!.RecognizeAsync(softwareBitmap).AsTask(token);


        var lines = new List<TextRegion>();
        var joiner = Joiner(language);
        var imageBounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        foreach (var line in result.Lines)
        {
            TextRegion? segment = null;
            foreach (var word in line.Words)
            {
                var rect = word.BoundingRect;
                var bounds = OcrGeometry.ToSourceBounds(new System.Windows.Rect(rect.X, rect.Y, rect.Width, rect.Height),
                    new Size((int)width, (int)height), bitmap.Size, result.TextAngle ?? 0);
                AppendWord(lines, ref segment, new TextRegion(word.Text, bounds), joiner, false);
            }
            if (segment is not null)
                lines.Add(segment);
        }
        token.ThrowIfCancellationRequested();
        return GroupLines(lines, joiner);
    }

    private IReadOnlyList<TextRegion> RecognizeTesseract(Bitmap bitmap, string language, bool vertical, CancellationToken token)
    {
        var model = language.ToLowerInvariant().Split('-')[0] switch
        {
            "ja" => vertical ? "jpn_vert" : "jpn",
            "ko" => "kor",
            "th" => "tha",
            "en" => "eng",
            _ => throw new InvalidOperationException($"No local OCR model is configured for '{language}'.")
        };
        if (!File.Exists(Path.Combine(dataPath, model + ".traineddata")))
            throw new InvalidOperationException($"Local OCR model '{model}' is missing. Run scripts/setup-ocr.ps1, then rebuild, or copy {model}.traineddata into {dataPath}.");
        if (fallbackLanguage != model)
        {
            fallbackEngine?.Dispose();
            fallbackEngine = null;
            fallbackLanguage = null;
            fallbackEngine = new TesseractEngine(dataPath, model, EngineMode.LstmOnly);
            fallbackLanguage = model;
        }
        token.ThrowIfCancellationRequested();
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Bmp);
        using var pix = Pix.LoadFromMemory(stream.ToArray());
        // Explicit vertical layout preserves top-to-bottom glyph order. Spatial grouping below separates bubbles.
        using var page = fallbackEngine!.Process(pix, vertical ? PageSegMode.SingleBlockVertText : PageSegMode.SparseText);
        using var iterator = page.GetIterator();
        var lines = new List<TextRegion>();
        var joiner = Joiner(language);
        var imageBounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        TextRegion? segment = null;
        iterator.Begin();
        do
        {
            token.ThrowIfCancellationRequested();
            if (iterator.IsAtBeginningOf(PageIteratorLevel.TextLine) && segment is not null)
            {
                lines.Add(segment);
                segment = null;
            }
            var text = iterator.GetText(PageIteratorLevel.Word)?.Trim();
            if (!string.IsNullOrWhiteSpace(text) && iterator.TryGetBoundingBox(PageIteratorLevel.Word, out var rect))
            {
                var bounds = Rectangle.Intersect(imageBounds, new Rectangle(rect.X1, rect.Y1, rect.Width, rect.Height));
                AppendWord(lines, ref segment, new TextRegion(text, bounds), joiner, vertical);
            }
        } while (iterator.Next(PageIteratorLevel.Word));
        if (segment is not null)
            lines.Add(segment);
        token.ThrowIfCancellationRequested();
        return GroupLines(lines, joiner, vertical);
    }

    private static string Joiner(string language) => language.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
        || language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
        || language.StartsWith("th", StringComparison.OrdinalIgnoreCase) ? "" : " ";

    private static void AppendWord(List<TextRegion> lines, ref TextRegion? segment, TextRegion word, string joiner, bool vertical)
    {
        var bounds = word.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0 || string.IsNullOrWhiteSpace(word.Text))
            return;
        // Both engines can emit two speech bubbles as one line. Split large whitespace gaps.
        if (segment is not null)
        {
            var previous = segment.Bounds;
            var gap = vertical ? Math.Max(bounds.Top - previous.Bottom, previous.Top - bounds.Bottom)
                : Math.Max(bounds.Left - previous.Right, previous.Left - bounds.Right);
            var thickness = vertical ? Math.Max(previous.Width, bounds.Width) : Math.Max(previous.Height, bounds.Height);
            if (gap > 1.5 * thickness)
            {
                lines.Add(segment);
                segment = null;
            }
        }
        segment = segment is null ? word : new TextRegion(segment.Text + joiner + word.Text, Rectangle.Union(segment.Bounds, bounds));
    }

    public static IReadOnlyList<TextRegion> GroupLines(IEnumerable<TextRegion> lines, string joiner = " ", bool vertical = false)
    {
        // Rotate geometry for right-to-left vertical columns, then restore physical image bounds.
        if (vertical)
            return GroupLines(lines.Select(line => new TextRegion(line.Text,
                    new Rectangle(line.Bounds.Top, -line.Bounds.Right, line.Bounds.Height, line.Bounds.Width))), joiner)
                .Select(block => new TextRegion(block.Text,
                    new Rectangle(-block.Bounds.Bottom, block.Bounds.Left, block.Bounds.Height, block.Bounds.Width))).ToArray();
        var blocks = new List<TextRegion>();
        var lastLines = new List<Rectangle>();
        // ponytail: conservative O(n^2) geometry handles separated text; touching speech bubbles need image-based bubble segmentation.
        foreach (var line in lines.Where(line => !string.IsNullOrWhiteSpace(line.Text)
                     && line.Bounds.Width > 0 && line.Bounds.Height > 0).OrderBy(line => line.Bounds.Top).ThenBy(line => line.Bounds.Left))
        {
            var match = -1;
            var closestGap = double.MaxValue;
            for (var i = 0; i < blocks.Count; i++)
            {
                var previous = lastLines[i];
                var gap = line.Bounds.Top - previous.Bottom;
                var minHeight = Math.Min(previous.Height, line.Bounds.Height);
                var overlap = Math.Min(previous.Right, line.Bounds.Right) - Math.Max(previous.Left, line.Bounds.Left);
                var centerDifference = Math.Abs((previous.Left + previous.Width / 2.0) - (line.Bounds.Left + line.Bounds.Width / 2.0));
                if (gap >= -0.15 * minHeight && gap <= 0.8 * minHeight && gap < closestGap
                    && Math.Max(previous.Height, line.Bounds.Height) <= minHeight * 1.6
                    && overlap >= Math.Min(previous.Width, line.Bounds.Width) * 0.55
                    && (centerDifference <= Math.Max(minHeight * 0.8, Math.Max(previous.Width, line.Bounds.Width) * 0.25)
                        || Math.Abs(previous.Left - line.Bounds.Left) <= minHeight * 0.5
                        || Math.Abs(previous.Right - line.Bounds.Right) <= minHeight * 0.5))
                {
                    match = i;
                    closestGap = gap;
                }
            }
            if (match < 0)
            {
                blocks.Add(line);
                lastLines.Add(line.Bounds);
            }
            else
            {
                var block = blocks[match];
                blocks[match] = new TextRegion(block.Text + joiner + line.Text, Rectangle.Union(block.Bounds, line.Bounds));
                lastLines[match] = line.Bounds;
            }
        }
        return blocks;
    }

    public static void SelfCheck()
    {
        OcrGeometry.SelfCheck();
        var grouped = GroupLines(new[]
        {
            new TextRegion("left one", new Rectangle(20, 20, 120, 20)),
            new TextRegion("right one", new Rectangle(250, 20, 120, 20)),
            new TextRegion("left two", new Rectangle(30, 48, 100, 20)),
            new TextRegion("right two", new Rectangle(260, 48, 100, 20)),
            new TextRegion("separate below", new Rectangle(20, 150, 120, 20)),
            new TextRegion("", new Rectangle(20, 70, 120, 20))
        });
        if (grouped.Count != 3 || grouped[0].Text != "left one left two"
            || grouped[1].Text != "right one right two" || grouped[0].Bounds != new Rectangle(20, 20, 120, 48))
            throw new InvalidOperationException("OCR grouping mixed separate bubbles or lost text bounds.");
        var japanese = GroupLines(new[]
        {
            new TextRegion("\u3053\u3093\u306b\u3061\u306f", new Rectangle(-100, 20, 100, 20)),
            new TextRegion("\u4e16\u754c", new Rectangle(-90, 45, 80, 20))
        }, "");
        if (japanese.Count != 1 || japanese[0].Text != "\u3053\u3093\u306b\u3061\u306f\u4e16\u754c" || japanese[0].Bounds.Left != -100)
            throw new InvalidOperationException("OCR grouping corrupted Japanese spacing or negative coordinates.");
        var vertical = GroupLines(new[]
        {
            new TextRegion("second", new Rectangle(70, 20, 20, 100)),
            new TextRegion("first", new Rectangle(100, 20, 20, 100)),
            new TextRegion("other bubble", new Rectangle(250, 200, 20, 100))
        }, "", true);
        if (!vertical.Any(region => region.Text == "firstsecond" && region.Bounds == new Rectangle(70, 20, 50, 100))
            || vertical.Count != 2)
            throw new InvalidOperationException("Vertical OCR grouping lost right-to-left reading order.");
        var split = new List<TextRegion>();
        TextRegion? segment = null;
        AppendWord(split, ref segment, new TextRegion("one", new Rectangle(10, 10, 30, 20)), " ", false);
        AppendWord(split, ref segment, new TextRegion("two", new Rectangle(200, 10, 30, 20)), " ", false);
        if (split.Count != 1 || split[0].Text != "one" || segment?.Text != "two")
            throw new InvalidOperationException("OCR word segmentation joined separate bubbles.");
    }

    public void Dispose()
    {
        StopDetector();
        fallbackEngine?.Dispose();
        recognitionGate.Dispose();
    }
}


