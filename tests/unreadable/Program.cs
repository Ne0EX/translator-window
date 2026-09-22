using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Translumo.Local;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try { Run(args).GetAwaiter().GetResult(); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static async Task Run(string[] args)
    {
        string root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
        string output = Path.Combine(root, "artifacts/unreadable-check");
        Directory.CreateDirectory(output);
        string worker = Path.Combine(output, "stub_worker.py");
        // Two detector blocks: one readable and one empty crop. No models/GPU/network.
        File.WriteAllText(worker, """
import json, sys
for line in sys.stdin:
    request = json.loads(line)
    if 'image' in request:
        reply = {'regions': [{'x':25,'y':25,'width':250,'height':75,'vertical':False},
                             {'x':400,'y':25,'width':250,'height':75,'vertical':False}]}
    else:
        reply = {'translations': ['translated text' if text.strip() else '' for text in request['texts']]}
    print(json.dumps(reply), flush=True)
""");
        var statuses = new List<string>();
        var overlay = new SubtitleOverlay(); // No handle, Show, capture, or desktop mutation.
        using var translator = new LocalTranslator(Path.Combine(root, ".venv/Scripts/python.exe"), worker, output);
        using var ocr = new SpatialOcr(Path.Combine(root, "models/tessdata"),
            Path.Combine(root, ".venv/Scripts/python.exe"), worker, worker);
        var session = new LiveTranslationSession(translator, overlay, statuses.Add, ocr);
        var completeFrame = typeof(LiveTranslationSession).GetMethod("TranslateFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

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
                "en-comic", "th", SubtitleStyle.Overwrite, 6, CancellationToken.None, true })!;
            await task;
            return task.GetType().GetProperty("Result")!.GetValue(task);
        }

        var mixed = await Process(true) ?? throw new Exception("A readable neighbor must still translate.");
        var type = mixed.GetType();
        var regions = (IReadOnlyList<TextRegion>)type.GetProperty("Regions")!.GetValue(mixed)!;
        var captions = (IReadOnlyList<string>)type.GetProperty("Translations")!.GetValue(mixed)!;
        Require(regions.Count == 2 && regions[1].Text == "", "Detected unreadable region was discarded.");
        Require(regions[1].Bounds == new Rectangle(400, 25, 250, 75), "Unreadable source mask lost its detected bounds.");
        Require(captions.SequenceEqual(new[] { "translated text", "\u2026" }), "Unreadable crop must get an ellipsis beside its translated neighbor.");
        Require((int)type.GetProperty("UnreadableCount")!.GetValue(mixed)! == 1, "Status must retain the unreadable count.");
        Require(type.GetProperty("Frame")!.GetValue(mixed) is not null, "A successful mixed frame must retain its captured page.");
        Require(await Process(false) is null, "An entirely unreadable page must keep the previous cover.");
        Require(statuses.Any(text => text.Contains("could not be read") && text.Contains("stays covered")),
            "Unreadable frame must explain the held view.");
        Console.WriteLine("PASS: unreadable source bounds retained, ellipsis caption emitted, readable neighbor translated, all-unreadable frame held. No desktop or GPU used.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
