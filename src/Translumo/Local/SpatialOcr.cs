using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Drawing;
using System.Drawing.Imaging;
using ImageFormat = System.Drawing.Imaging.ImageFormat;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
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
    private readonly object detectorLock = new();
    private Process? detectorProcess;
    private Task<string>? detectorErrors;
    private Task<ComicReply?>? pendingDetectorReply;
    private bool disposed;

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
        => (await RecognizeFrameAsync(bitmap, language, token, Array.Empty<TextRegion>())).Regions;

    internal async Task<(IReadOnlyList<TextRegion> Regions, bool HasOcrInputProof)> RecognizeFrameAsync(
        Bitmap bitmap, string language, CancellationToken token, IReadOnlyList<TextRegion> knownRegions)
        => await RecognizeFrameCoreAsync(bitmap, language, token, knownRegions, null);

    internal async Task<(IReadOnlyList<TextRegion> Regions, bool HasOcrInputProof)> RecognizeFrameProgressiveAsync(
        Bitmap bitmap, string language, CancellationToken token, IReadOnlyList<TextRegion> knownRegions,
        Func<IReadOnlyList<TextRegion>, Task> progress)
        => await RecognizeFrameCoreAsync(bitmap, language, token, knownRegions,
            progress ?? throw new ArgumentNullException(nameof(progress)));

    private async Task<(IReadOnlyList<TextRegion> Regions, bool HasOcrInputProof)> RecognizeFrameCoreAsync(
        Bitmap bitmap, string language, CancellationToken token, IReadOnlyList<TextRegion> knownRegions,
        Func<IReadOnlyList<TextRegion>, Task>? progress)
    {
        await recognitionGate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var vertical = language.Equals("ja-vert", StringComparison.OrdinalIgnoreCase);
            var baseLanguage = language.Split('-')[0];
            var ocrLanguage = new Language(baseLanguage);
            if (vertical || language.EndsWith("-comic", StringComparison.OrdinalIgnoreCase))
                return await RecognizeComicAsync(bitmap, baseLanguage, vertical, token, knownRegions, progress);
            if (!OcrEngine.IsLanguageSupported(ocrLanguage))
                return (await Task.Run(() => RecognizeTesseract(bitmap, language, vertical, token), token), false);
            return (await RecognizeWindowsAsync(bitmap, language, token), false);
        }
        finally { recognitionGate.Release(); }
    }

    private async Task<(IReadOnlyList<TextRegion> Regions, bool HasOcrInputProof)> RecognizeComicAsync(Bitmap bitmap,
        string language, bool verticalJapanese, CancellationToken token, IReadOnlyList<TextRegion> knownRegions,
        Func<IReadOnlyList<TextRegion>, Task>? progress)
    {
        if (!File.Exists(detectorPython) || !File.Exists(detectorWorker) || !File.Exists(detectorModel))
            throw new InvalidOperationException("Local manga detector is missing. Run local-ocr/setup.ps1, then select the installed Python runtime.");
        var imageBounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        if (knownRegions.Count > 4096 || knownRegions.Any(region => region.Bounds.Width <= 0 || region.Bounds.Height <= 0
            || region.Bounds.X < 0 || region.Bounds.Y < 0
            || (long)region.Bounds.X + region.Bounds.Width > imageBounds.Width
            || (long)region.Bounds.Y + region.Bounds.Height > imageBounds.Height
            || string.IsNullOrWhiteSpace(region.Text) || region.Text.Length > 4096))
            throw new ArgumentException("Known OCR regions must have valid image bounds and nonempty text.", nameof(knownRegions));
        try
        {
            // Keep the one-line protocol aligned: a canceled caller leaves its reply for the next request to drain.
            if (pendingDetectorReply is not null)
            {
                var abandoned = pendingDetectorReply;
                if (await abandoned.WaitAsync(token) is null)
                    throw new InvalidOperationException("Local manga detector stopped. " + await detectorErrors!);
                if (ReferenceEquals(pendingDetectorReply, abandoned)) pendingDetectorReply = null;
            }
            token.ThrowIfCancellationRequested();
            Process process;
            lock (detectorLock)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
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
                process = detectorProcess;
            }
            ComicRequest? request = await Task.Run(() => EncodeComicRequest(bitmap, language, knownRegions,
                progress is not null), token);
            ComicReply? response;
            try
            {
                token.ThrowIfCancellationRequested();
                await process.StandardInput.WriteLineAsync(request.Payload);
                await process.StandardInput.FlushAsync();
                Task<ComicReply?> replyTask;
                lock (detectorLock)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    replyTask = ReadDetectorReplyAsync(process, request, bitmap.Size,
                        !verticalJapanese && language == "ja" && OcrEngine.IsLanguageSupported(new Language("ja")),
                        progress, token);
                    pendingDetectorReply = replyTask;
                    request = null; // The response task owns the mapping until this protocol reply is drained.
                }
                try { response = await replyTask.WaitAsync(token); }
                finally
                {
                    if (replyTask.IsCompleted && ReferenceEquals(pendingDetectorReply, replyTask))
                        pendingDetectorReply = null;
                }
            }
            finally { request?.Dispose(); }
            if (response is null)
                throw new InvalidOperationException("Local manga detector stopped. " + await detectorErrors!);
            if (response.CallbackError is not null)
                throw new ComicProgressCallbackException(response.CallbackError);
            using var document = JsonDocument.Parse(response.FinalLine);
            if (document.RootElement.TryGetProperty("error", out var error))
                throw new InvalidOperationException("Local manga detector: " + error.GetString());
            var result = new List<TextRegion>();
            foreach (var item in document.RootElement.GetProperty("regions").EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                var detected = new Rectangle(item.GetProperty("x").GetInt32(), item.GetProperty("y").GetInt32(),
                    item.GetProperty("width").GetInt32(), item.GetProperty("height").GetInt32());
                if (detected.Width <= 0 || detected.Height <= 0 || detected.X < 0 || detected.Y < 0
                    || (long)detected.X + detected.Width > imageBounds.Width
                    || (long)detected.Y + detected.Height > imageBounds.Height)
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
            if (!verticalJapanese && language == "ja" && ShouldTryVerticalComicColumns(result, bitmap.Size)
                && OcrEngine.IsLanguageSupported(new Language("ja")))
            {
                var columns = VerticalComicColumns(await RecognizeWindowsAsync(bitmap, "ja", token), bitmap.Size);
                if (columns.Length >= 12) return (columns, false);
            }
            return (result, true);
        }
        catch (ComicProgressCallbackException error)
        {
            ExceptionDispatchInfo.Capture(error.InnerException!).Throw();
            throw;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch
        {
            StopDetector();
            token.ThrowIfCancellationRequested();
            throw;
        }
    }

    private static ComicRequest EncodeComicRequest(Bitmap bitmap, string language,
        IReadOnlyList<TextRegion> knownRegions, bool progressive)
    {
        if (bitmap.Width < 4 || bitmap.Height < 4 || bitmap.Width > 20000 || bitmap.Height > 20000
            || (long)bitmap.Width * bitmap.Height > 100_000_000)
            throw new ArgumentOutOfRangeException(nameof(bitmap), "Image dimensions must be 4..20000 pixels and at most 100 megapixels.");
        int stride = checked(bitmap.Width * 4);
        long length = checked((long)stride * bitmap.Height);
        string name = "Local\\TranslumoOcr-" + Guid.NewGuid().ToString("N");
        var mapped = MemoryMappedFile.CreateNew(name, length, MemoryMappedFileAccess.ReadWrite);
        try
        {
            using var view = mapped.CreateViewStream(0, length, MemoryMappedFileAccess.Write);
            var pixels = ArrayPool<byte>.Shared.Rent(stride);
            BitmapData? data = null;
            try
            {
                data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                // LockBits exposes BGRA. Copy logical rows into a packed positive-stride mapping,
                // including alpha, even if the source bitmap has a negative stride.
                for (int y = 0; y < bitmap.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, 0, stride);
                    view.Write(pixels, 0, stride);
                }
                view.Flush();
            }
            finally
            {
                if (data is not null) bitmap.UnlockBits(data);
                ArrayPool<byte>.Shared.Return(pixels);
            }
            var known = knownRegions.Select(region => new { x = region.Bounds.X, y = region.Bounds.Y,
                width = region.Bounds.Width, height = region.Bounds.Height, text = region.Text }).ToArray();
            var mapping = new { name, width = bitmap.Width, height = bitmap.Height, stride, length,
                pixelFormat = "bgra32" };
            string payload = progressive
                ? JsonSerializer.Serialize(new { mapping, language, known, progress = true })
                : JsonSerializer.Serialize(new { mapping, language, known });
            return new ComicRequest(payload, mapped);
        }
        catch
        {
            mapped.Dispose();
            throw;
        }
    }

    private static async Task<ComicReply?> ReadDetectorReplyAsync(Process process, ComicRequest request, Size image,
        bool suppressGiantProgress, Func<IReadOnlyList<TextRegion>, Task>? progress, CancellationToken callerToken)
    {
        try
        {
            TextRegion[]? detected = null;
            bool[]? recognized = null;
            int initialReused = 0;
            bool suppressProgress = false;
            Exception? callbackError = null;
            while (true)
            {
                string? line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line is null) return null;
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("Local manga detector returned a malformed response.");
                bool hasRegions = root.TryGetProperty("regions", out var finalRegions);
                bool hasError = root.TryGetProperty("error", out var error);
                bool hasDetected = root.TryGetProperty("detected", out var header);
                bool hasRecognized = root.TryGetProperty("recognized", out var chunk);
                if ((hasRegions ? 1 : 0) + (hasError ? 1 : 0) + (hasDetected ? 1 : 0) + (hasRecognized ? 1 : 0) != 1)
                    throw new InvalidOperationException("Local manga detector returned an ambiguous response.");
                if (hasRegions || hasError)
                {
                    if (hasError)
                    {
                        if (error.ValueKind != JsonValueKind.String || root.EnumerateObject().Count() != 1)
                            throw new InvalidOperationException("Local manga detector returned invalid error metadata.");
                    }
                    else
                    {
                        if (root.EnumerateObject().Any(property => property.Name is not ("regions" or "reused"))
                            || finalRegions.ValueKind != JsonValueKind.Array)
                            throw new InvalidOperationException("Local manga detector returned invalid final metadata.");
                        if (root.TryGetProperty("reused", out var reused)
                            && (!reused.TryGetInt32(out int reusedCount) || reusedCount < 0
                                || reusedCount > finalRegions.GetArrayLength()))
                            throw new InvalidOperationException("Local manga detector returned invalid reuse metadata.");
                    }
                    if (hasRegions && detected is not null)
                    {
                        if (!root.TryGetProperty("reused", out var reused)
                            || reused.GetInt32() != initialReused)
                            throw new InvalidOperationException("Local manga detector final reuse count did not match detection progress.");
                        ValidateFinalProgress(finalRegions, detected, recognized!, image);
                    }
                    return new ComicReply(line, callbackError);
                }
                if (progress is null)
                    throw new InvalidOperationException("Local manga detector returned unexpected progress.");
                if (hasDetected)
                {
                    if (root.EnumerateObject().Count() != 1 || detected is not null || header.ValueKind != JsonValueKind.Array
                        || header.GetArrayLength() > 4096)
                        throw new InvalidOperationException("Local manga detector returned invalid detection progress.");
                    detected = header.EnumerateArray().Select(item => ParseProgressRegion(item, image, true)).ToArray();
                    recognized = detected.Select(region => !string.IsNullOrWhiteSpace(region.Text)).ToArray();
                    initialReused = recognized.Count(value => value);
                    suppressProgress = suppressGiantProgress && ShouldTryVerticalComicColumns(detected, image);
                    if (!suppressProgress && recognized.Any(value => value))
                        callbackError = await InvokeComicProgressAsync(progress, detected, callerToken, callbackError);
                    continue;
                }
                if (!hasRecognized || root.EnumerateObject().Count() != 1 || detected is null
                    || chunk.ValueKind != JsonValueKind.Array || chunk.GetArrayLength() is < 1 or > 8)
                    throw new InvalidOperationException("Local manga detector returned invalid recognition progress.");
                foreach (var item in chunk.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("index", out var indexValue)
                        || !indexValue.TryGetInt32(out int index) || index < 0 || index >= detected.Length
                        || recognized![index] || !item.TryGetProperty("text", out var textValue)
                        || textValue.ValueKind != JsonValueKind.String)
                        throw new InvalidOperationException("Local manga detector returned an invalid recognition index.");
                    string? text = textValue.GetString();
                    if (string.IsNullOrWhiteSpace(text) || text.Length > 4096)
                        throw new InvalidOperationException("Local manga detector returned invalid recognition text.");
                    detected[index] = detected[index] with { Text = text };
                    recognized[index] = true;
                }
                if (!suppressProgress)
                    callbackError = await InvokeComicProgressAsync(progress, detected, callerToken, callbackError);
            }
        }
        finally { request.Dispose(); }
    }

    private static TextRegion ParseProgressRegion(JsonElement item, Size image, bool requireVertical)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("x", out var xValue)
            || !item.TryGetProperty("y", out var yValue) || !item.TryGetProperty("width", out var widthValue)
            || !item.TryGetProperty("height", out var heightValue)
            || !xValue.TryGetInt32(out int x) || !yValue.TryGetInt32(out int y)
            || !widthValue.TryGetInt32(out int width) || !heightValue.TryGetInt32(out int height))
            throw new InvalidOperationException("Local manga detector returned invalid detection progress metadata.");
        var bounds = new Rectangle(x, y, width, height);
        if (width <= 0 || height <= 0 || x < 0 || y < 0
            || (long)x + width > image.Width || (long)y + height > image.Height)
            throw new InvalidOperationException("Local manga detector returned invalid detection progress coordinates.");
        string text = "";
        if (item.TryGetProperty("text", out var textValue))
        {
            if (textValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(textValue.GetString())
                || textValue.GetString()!.Length > 4096)
                throw new InvalidOperationException("Local manga detector returned invalid reused progress text.");
            text = textValue.GetString()!;
        }
        if (item.TryGetProperty("vertical", out var vertical))
        {
            if (vertical.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException("Local manga detector returned invalid vertical progress metadata.");
        }
        else if (requireVertical && string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Local manga detector omitted vertical progress metadata.");
        return new TextRegion(text, bounds);
    }

    private static async Task<Exception?> InvokeComicProgressAsync(Func<IReadOnlyList<TextRegion>, Task> progress,
        TextRegion[] detected, CancellationToken callerToken, Exception? previousError)
    {
        if (previousError is not null || callerToken.IsCancellationRequested) return previousError;
        try { await progress(detected.ToArray()).ConfigureAwait(false); }
        catch (Exception) when (callerToken.IsCancellationRequested) { return previousError; }
        catch (Exception error) { return error; }
        return null;
    }

    private static void ValidateFinalProgress(JsonElement finalRegions, TextRegion[] detected, bool[] recognized, Size image)
    {
        if (finalRegions.ValueKind != JsonValueKind.Array || finalRegions.GetArrayLength() != detected.Length)
            throw new InvalidOperationException("Local manga detector final response did not match detection progress.");
        int index = 0;
        foreach (var item in finalRegions.EnumerateArray())
        {
            var final = ParseProgressRegion(item, image, false);
            bool finalRecognized = !string.IsNullOrWhiteSpace(final.Text);
            if (final.Bounds != detected[index].Bounds || recognized[index] != finalRecognized
                || recognized[index] && final.Text != detected[index].Text)
                throw new InvalidOperationException("Local manga detector final response did not match recognition progress.");
            index++;
        }
    }

    internal static bool ShouldTryVerticalComicColumns(IReadOnlyList<TextRegion> regions, Size image)
        => regions.Count == 0 || regions.Any(region => region.Bounds.Width > image.Width / 6
            && region.Bounds.Height > image.Height / 6);

    private void StopDetector()
    {
        lock (detectorLock)
        {
            if (detectorProcess is null)
            {
                pendingDetectorReply = null;
                return;
            }
            try { if (!detectorProcess.HasExited) detectorProcess.Kill(true); }
            catch (InvalidOperationException) { }
            detectorProcess.Dispose();
            detectorProcess = null;
            pendingDetectorReply = null;
        }
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

    internal static TextRegion[] VerticalComicColumns(IReadOnlyList<TextRegion> regions, Size image)
        => regions.Where(region => region.Bounds.Width >= image.Width / 110
                && region.Bounds.Width <= image.Width / 35
                && region.Bounds.Height >= Math.Max(image.Height / 18, region.Bounds.Width * 2)
                && region.Text.Count(character => character is >= '\u3040' and <= '\u30ff'
                    or >= '\u3400' and <= '\u9fff') >= 2)
            .OrderByDescending(region => region.Bounds.Left).ToArray();

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
        var indexColumns = Enumerable.Range(0, 13)
            .Select(i => new TextRegion("目次", new Rectangle(100 + i * 50, 100, 48, 300)))
            .Concat(Enumerable.Range(0, 13).Select(i => new TextRegion("ふりがな", new Rectangle(110 + i * 50, 100, 12, 180))))
            .ToArray();
        if (VerticalComicColumns(indexColumns, new Size(3840, 2088)).Length != 13)
            throw new InvalidOperationException("Vertical index fallback must exclude ruby annotations.");
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
        lock (detectorLock)
        {
            if (disposed) return;
            disposed = true;
        }
        StopDetector();
        fallbackEngine?.Dispose();
        fallbackEngine = null;
    }

    private sealed record ComicRequest(string Payload, MemoryMappedFile Mapping) : IDisposable
    {
        public void Dispose() => Mapping.Dispose();
    }

    private sealed record ComicReply(string FinalLine, Exception? CallbackError);

    private sealed class ComicProgressCallbackException(Exception innerException) : Exception(null, innerException);
}


