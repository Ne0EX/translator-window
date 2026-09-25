using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Translumo.Local;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var task = Run(args);
            if (!task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                task.ContinueWith(_ => frame.Continue = false, CancellationToken.None,
                    TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
                Dispatcher.PushFrame(frame);
            }
            task.GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static async Task Run(string[] args)
    {
        bool visual = args.Contains("--visual", StringComparer.Ordinal);
        string root = Path.GetFullPath(args.FirstOrDefault(argument => argument != "--visual") ?? ".");
        var contentsFallback = typeof(SpatialOcr).GetMethod("ShouldTryVerticalComicColumns",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var detectorMix = new[] {
            new TextRegion("giant", new Rectangle(0, 0, 400, 300)),
            new TextRegion("small-1", new Rectangle(500, 20, 30, 80)),
            new TextRegion("small-2", new Rectangle(550, 20, 30, 80)),
            new TextRegion("small-3", new Rectangle(600, 20, 30, 80)),
            new TextRegion("small-4", new Rectangle(650, 20, 30, 80))
        };
        Require((bool)contentsFallback.Invoke(null, new object[] { detectorMix, new Size(1200, 900) })!,
            "A giant detector block plus small extras must still try the Japanese contents-column fallback.");
        string output = Path.Combine(root, "artifacts/unreadable-check");
        Directory.CreateDirectory(output);
        string transportMarker = Path.Combine(output, "ui-returned.marker");
        File.Delete(transportMarker);
        const string knownText = "\"quoted\" + plus / slash";
        using var transportBitmap = new Bitmap(3840, 2088, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(transportBitmap)) graphics.Clear(Color.FromArgb(255, 17, 83, 201));
        var alphaPixels = new[] {
            Color.FromArgb(0, 31, 41, 59), Color.FromArgb(1, 61, 71, 89),
            Color.FromArgb(127, 97, 101, 103), Color.FromArgb(254, 107, 109, 127)
        };
        for (int index = 0; index < alphaPixels.Length; index++) transportBitmap.SetPixel(index, 0, alphaPixels[index]);
        string transportWorker = Path.Combine(output, "transport_worker.py");
        File.WriteAllText(transportWorker, $$"""
import json, mmap, os, sys
import cv2, numpy as np
marker = {{JsonSerializer.Serialize(transportMarker)}}
expected_text = {{JsonSerializer.Serialize(knownText)}}
count = 0
for line in sys.stdin:
    count += 1
    request = json.loads(line)
    frame = request['mapping']
    with mmap.mmap(-1, frame['length'], tagname=frame['name'], access=mmap.ACCESS_READ) as shared:
        bgra = np.ndarray((frame['height'], frame['width'], 4), dtype=np.uint8, buffer=shared,
                          strides=(frame['stride'], 4, 1))
        image = cv2.cvtColor(bgra, cv2.COLOR_BGRA2BGR)
    if count == 2:
        assert os.path.exists(marker), 'UI caller did not regain control before detector I/O'
        assert request['language'] == 'ja' and request['known'][0]['text'] == expected_text, request
        assert image.shape == (2088, 3840, 3), image.shape
        assert image[0, :4].tolist() == [[59,41,31],[89,71,61],[103,101,97],[127,109,107]], image[0, :4]
        assert np.all(image[:, 4:] == np.array([201, 83, 17], dtype=np.uint8))
    print(json.dumps({'regions': [{'x':10,'y':10,'width':100,'height':40,
                                  'vertical':False,'text':'transport-ok'}]}), flush=True)
""");
        var recognizeFrame = typeof(SpatialOcr).GetMethod("RecognizeFrameAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        using (var transportOcr = new SpatialOcr(Path.Combine(root, "models/tessdata"),
            Path.Combine(root, ".venv/Scripts/python.exe"), transportWorker, transportWorker))
        {
            using var warm = new Bitmap(200, 100);
            await transportOcr.RecognizeAsync(warm, "ja-comic", CancellationToken.None);
            var known = new[] { new TextRegion(knownText, new Rectangle(20, 20, 80, 40)) };
            var returnTimer = Stopwatch.StartNew();
            var transportTask = (Task)recognizeFrame.Invoke(transportOcr,
                new object[] { transportBitmap, "ja-comic", CancellationToken.None, known })!;
            returnTimer.Stop();
            Require(!transportTask.IsCompleted && returnTimer.Elapsed < TimeSpan.FromMilliseconds(75),
                $"Mapped frame copying blocked the UI caller for {returnTimer.ElapsedMilliseconds} ms.");
            File.WriteAllText(transportMarker, "returned");
            await transportTask;
        }
        string worker = Path.Combine(output, "stub_worker.py");
        // Two detector blocks: one readable and one empty crop. No models/GPU/network.
        File.WriteAllText(worker, """
import json, sys
for line in sys.stdin:
    request = json.loads(line)
    if 'mapping' in request:
        reply = {'regions': [{'x':25,'y':25,'width':250,'height':75,'vertical':False},
                             {'x':400,'y':25,'width':250,'height':75,'vertical':False}]}
    else:
        reply = {'translations': ['translated text' if text.strip() else '' for text in request['texts']]}
    print(json.dumps(reply), flush=True)
""");
        string bridgeWorker = Path.Combine(output, "bridge_worker.py");
        string mappingLog = Path.Combine(output, "mapping-lifetime.txt");
        File.Delete(mappingLog);
        File.WriteAllText(bridgeWorker, $$"""
import json, mmap, os, sys, time
mapping_log = {{JsonSerializer.Serialize(mappingLog)}}
count = 0
for line in sys.stdin:
    request = json.loads(line)
    count += 1
    frame = request['mapping']
    with open(mapping_log, 'a', encoding='utf-8') as log:
        print(frame['name'], file=log, flush=True)
    if count == 2:
        time.sleep(0.5)
    if count == 4:
        time.sleep(5)
    with mmap.mmap(-1, frame['length'], tagname=frame['name'], access=mmap.ACCESS_READ) as shared:
        assert shared[0] == 149
    print(json.dumps({'regions': [{'x':10,'y':10,'width':100,'height':40,
                                  'vertical':False,'text':f'{count}|{os.getpid()}|{frame["name"]}'}]}), flush=True)
""");
        using (var bridgeOcr = new SpatialOcr(Path.Combine(root, "models/tessdata"),
            Path.Combine(root, ".venv/Scripts/python.exe"), bridgeWorker, bridgeWorker))
        using (var bitmap = new Bitmap(200, 100))
        {
            bitmap.SetPixel(0, 0, Color.FromArgb(255, 47, 83, 149));
            string first = (await bridgeOcr.RecognizeAsync(bitmap, "en-comic", CancellationToken.None))[0].Text;
            string[] firstParts = first.Split('|');
            int workerPid = int.Parse(firstParts[1]);
            Require(!MappingExists(firstParts[2]), "Completed OCR retained its named mapping.");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            try
            {
                await bridgeOcr.RecognizeAsync(bitmap, "en-comic", cancellation.Token);
                throw new Exception("OCR cancellation did not interrupt the caller.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            string next = (await bridgeOcr.RecognizeAsync(bitmap, "en-comic", timeout.Token))[0].Text;
            string[] nextParts = next.Split('|');
            Require(nextParts[0] == "3" && nextParts[1] == workerPid.ToString(),
                "Canceled OCR response was reused or the warm worker was restarted.");
            Require(!MappingExists(nextParts[2]), "Completed OCR retained its named mapping.");
            string[] drainedMappings = File.ReadAllLines(mappingLog);
            Require(drainedMappings.Length == 3 && drainedMappings.All(name => !MappingExists(name)),
                "A completed or drained OCR request retained its named mapping.");
            using var workerProcess = System.Diagnostics.Process.GetProcessById(workerPid);
            using var stopCancellation = new CancellationTokenSource();
            var stopped = bridgeOcr.RecognizeAsync(bitmap, "en-comic", stopCancellation.Token);
            await Until(() => File.Exists(mappingLog) && File.ReadAllLines(mappingLog).Length >= 4);
            string stoppedMapping = File.ReadAllLines(mappingLog)[3];
            Require(MappingExists(stoppedMapping), "In-flight OCR mapping closed before its worker response.");
            stopCancellation.Cancel();
            try { await stopped; throw new Exception("Stopped OCR did not cancel its caller."); }
            catch (OperationCanceledException) when (stopCancellation.IsCancellationRequested) { }
            bridgeOcr.Dispose();
            Require(workerProcess.WaitForExit(5000), "Dispose did not stop the OCR worker.");
            await Until(() => !MappingExists(stoppedMapping));
        }
        string progressWorker = Path.Combine(output, "progress_worker.py");
        File.WriteAllText(progressWorker, """
import json, os, sys, time
for line in sys.stdin:
    request = json.loads(line)
    assert request.get('progress') is True
    mode = request.get('known', [{'text':'valid'}])[0]['text']
    pid = os.getpid()
    if mode == 'ambiguous':
        print(json.dumps({'detected': [], 'recognized': []}), flush=True)
        continue
    if mode == 'overflow':
        print(json.dumps({'detected':[{'x':2147483647,'y':0,'width':2,'height':2,'vertical':False}]}), flush=True)
        continue
    header = {'x':10,'y':10,'width':100,'height':40,'vertical':False}
    if mode == 'next':
        reused = {'x':10,'y':10,'width':100,'height':40,'text':f'next|{pid}'}
        print(json.dumps({'detected':[reused]}), flush=True)
        print(json.dumps({'regions':[reused], 'reused':1}), flush=True)
        continue
    print(json.dumps({'detected':[header]}), flush=True)
    text = f'{mode}|{pid}'
    if mode == 'duplicate':
        print(json.dumps({'recognized':[{'index':0,'text':text},{'index':0,'text':text}]}), flush=True)
        continue
    if mode != 'missing-progress':
        print(json.dumps({'recognized':[{'index':0,'text':text}]}), flush=True)
    if mode == 'callback-error':
        time.sleep(0.5)
    final_text = 'different' if mode == 'mismatch' else text
    print(json.dumps({'regions':[dict(header, text=final_text)], 'reused':1 if mode == 'bad-reused' else 0}), flush=True)
""");
        var recognizeProgressive = typeof(SpatialOcr).GetMethod("RecognizeFrameProgressiveAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        using (var progressOcr = new SpatialOcr(Path.Combine(root, "models/tessdata"),
            Path.Combine(root, ".venv/Scripts/python.exe"), progressWorker, progressWorker))
        using (var bitmap = new Bitmap(200, 100))
        {
            Task<(IReadOnlyList<TextRegion> Regions, bool HasOcrInputProof)> Start(string mode,
                CancellationToken token, Func<IReadOnlyList<TextRegion>, Task> callback)
                => (Task<(IReadOnlyList<TextRegion>, bool)>)recognizeProgressive.Invoke(progressOcr,
                    new object[] { bitmap, "en-comic", token,
                        new[] { new TextRegion(mode, new Rectangle(120, 10, 40, 30)) }, callback })!;

            var earlyProgress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var earlyTask = Start("valid", CancellationToken.None, regions =>
            {
                if (regions[0].Text.StartsWith("valid|", StringComparison.Ordinal)) earlyProgress.TrySetResult();
                return Task.CompletedTask;
            });
            await earlyProgress.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Require(!earlyTask.IsCompleted, "Progressive OCR did not expose its recognized chunk before the final reply.");
            var earlyResult = await earlyTask;
            int workerPid = int.Parse(earlyResult.Regions[0].Text.Split('|')[1]);

            var callbackReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var callbackCancellation = new CancellationTokenSource();
            var canceled = Start("callback-error", callbackCancellation.Token, regions =>
            {
                if (!regions[0].Text.StartsWith("callback-error|", StringComparison.Ordinal)) return Task.CompletedTask;
                callbackReached.TrySetResult();
                throw new InvalidOperationException("expected callback failure");
            });
            await callbackReached.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(100);
            callbackCancellation.Cancel();
            try { await canceled; throw new Exception("Progressive OCR cancellation did not interrupt the caller."); }
            catch (OperationCanceledException) when (callbackCancellation.IsCancellationRequested) { }
            var resumed = await Start("next", CancellationToken.None, _ => Task.CompletedTask);
            Require(resumed.Regions[0].Text == $"next|{workerPid}",
                "A canceled callback failure poisoned the next reply or restarted the detector.");

            foreach (string mode in new[] { "duplicate", "mismatch", "missing-progress", "bad-reused", "ambiguous", "overflow" })
            {
                try
                {
                    await Start(mode, CancellationToken.None, _ => Task.CompletedTask);
                    throw new Exception($"Malformed progressive OCR mode '{mode}' was accepted.");
                }
                catch (InvalidOperationException) { }
            }
        }
        var statuses = new List<string>();
        var overlay = new SubtitleOverlay(); // No handle, Show, capture, or desktop mutation.
        using var translator = new LocalTranslator(Path.Combine(root, ".venv/Scripts/python.exe"), worker, output);
        using var ocr = new SpatialOcr(Path.Combine(root, "models/tessdata"),
            Path.Combine(root, ".venv/Scripts/python.exe"), worker, worker);
        var session = new LiveTranslationSession(translator, overlay, statuses.Add, ocr);
        var completeFrame = typeof(LiveTranslationSession).GetMethod("TranslateFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var recognizedParameter = Expression.Parameter(completeFrame.GetParameters()[14].ParameterType.GenericTypeArguments[0]);
        var recognizedCallback = Expression.Lambda(completeFrame.GetParameters()[14].ParameterType,
            Expression.Empty(), recognizedParameter).Compile();

        async Task<object?> Process(bool readable)
        {
            var bitmap = new Bitmap(800, 200);
            using (var graphics = Graphics.FromImage(bitmap))
            using (var font = new Font("Arial", 32, GraphicsUnit.Pixel))
            {
                graphics.Clear(Color.White);
                if (readable) graphics.DrawString("Hello world.", font, Brushes.Black, 40, 40);
            }
            var task = (Task)completeFrame.Invoke(session, new object[] { bitmap, new Rectangle(0, 0, 800, 200), 0,
                "en-comic", "th", SubtitleStyle.Overwrite, 6, CancellationToken.None, true, null!, false,
                0L, Array.Empty<byte>(), 0L, recognizedCallback,
                (Func<System.Windows.Media.Imaging.BitmapSource?, bool>)(_ => true), Task.CompletedTask })!;
            await task;
            return task.GetType().GetProperty("Result")!.GetValue(task);
        }

        var mixed = await Process(true) ?? throw new Exception("A readable neighbor must still translate.");
        var type = mixed.GetType();
        var regions = (IReadOnlyList<TextRegion>)type.GetProperty("Regions")!.GetValue(mixed)!;
        var captions = (IReadOnlyList<string>)type.GetProperty("Translations")!.GetValue(mixed)!;
        Require(regions.Count == 2 && regions[1].Text == "", "Detected unreadable region was discarded.");
        Require(regions[1].Bounds == new Rectangle(400, 25, 250, 75), "Unreadable source mask lost its detected bounds.");
        Require(captions.Count == 2 && captions[0] == "translated text" && string.IsNullOrWhiteSpace(captions[1]),
            "Unreadable crop must stay uncovered beside its translated neighbor.");
        Require((int)type.GetProperty("UnreadableCount")!.GetValue(mixed)! == 1, "Status must retain the unreadable count.");
        var heldFrame = (BitmapSource?)type.GetProperty("Frame")!.GetValue(mixed);
        Require(heldFrame is not null, "A successful mixed frame must retain its captured page.");
        if (visual)
        {
            var renderOverlay = new SubtitleOverlay();
            try
            {
                void AssertReadyOnly(string phase)
                {
                    var visuals = ((Canvas)renderOverlay.Content).Children.OfType<Border>().ToArray();
                    Require(visuals.Count(border => border.Child is null) == 1
                        && visuals.Count(border => border.Child is TextBlock) == 1
                        && visuals.All(border => Equals(border.Tag, 0)),
                        $"{phase} must render only the translated region, without an unreadable mask or placeholder.");
                }

                renderOverlay.Render(new Rectangle(0, 0, 800, 200), regions, captions,
                    SubtitleStyle.Overwrite, 6, heldFrame, japaneseToThai: true);
                AssertReadyOnly("The completed frame");
                renderOverlay.Hide();
                renderOverlay.Render(new Rectangle(0, 0, 800, 200), regions, captions,
                    SubtitleStyle.Overwrite, 6, heldFrame, japaneseToThai: true);
                AssertReadyOnly("The restored frame");

                var readableRegions = regions.Select((region, index) => index == 1
                    ? region with { Text = "readable text" } : region).ToArray();
                try
                {
                    renderOverlay.Render(new Rectangle(0, 0, 800, 200), readableRegions, captions,
                        SubtitleStyle.Overwrite, 6, heldFrame, japaneseToThai: true);
                    throw new Exception("A missing readable translation did not fail.");
                }
                catch (InvalidOperationException error) when (error.Message.Contains("empty translation", StringComparison.Ordinal)) { }
                AssertReadyOnly("A rejected empty model reply");
            }
            finally
            {
                renderOverlay.Clear();
                renderOverlay.Close();
            }
        }
        Require(await Process(false) is null, "An entirely unreadable page must not publish a translated frame.");
        Require(statuses.Any(text => text.Contains("could not be read") && text.Contains("try zooming in")),
            "Unreadable frame must explain why the changed view was cleared.");
        Console.WriteLine("PASS: mapped BGRA transport and progressive OCR preserve protocol alignment, drain canceled callback failures on the same worker, reject malformed progress, release mappings, and retain unreadable source bounds. "
            + (visual ? "The test-owned desktop overlay rendered ready captions only; no GPU used."
                : "No desktop or GPU used."));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static bool MappingExists(string name)
    {
        try { using var mapping = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read); return true; }
        catch (FileNotFoundException) { return false; }
    }

    private static async Task Until(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Expected worker state was not reached.");
            await Task.Delay(20);
        }
    }
}
