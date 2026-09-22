using System;
using System.IO;
using System.Threading;
using Translumo.Local;

if (args.Length != 1) throw new ArgumentException("Pass the local Python executable path.");
string worker = Path.Combine(Path.GetTempPath(), "translator-check-" + Guid.NewGuid() + ".py");
try
{
    File.WriteAllText(worker, """
import json, sys, time
sys.stdin.reconfigure(encoding="utf-8")
sys.stdout.reconfigure(encoding="utf-8")
for line in sys.stdin:
    texts = json.loads(line)["texts"]
    if texts == ["wait"]:
        time.sleep(30)
    print(json.dumps({"translations": texts}), flush=True)
""");
    using var translator = new LocalTranslator(args[0], worker, Path.GetTempPath());
    string[] empty = await translator.TranslateAsync(Array.Empty<string>(), "ja", "th", default);
    if (empty.Length != 0) throw new Exception("Empty prewarm response mismatch.");
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
    try
    {
        await translator.TranslateAsync(new[] { "wait" }, "ja", "th", cancellation.Token);
        throw new Exception("Cancellation did not interrupt the worker.");
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    string[] next = await translator.TranslateAsync(new[] { "next response" }, "ja", "th", timeout.Token);
    if (next.Length != 1 || next[0] != "next response") throw new Exception("Canceled response corrupted the next request.");
    Console.WriteLine("PASS: prewarm, in-flight cancellation, worker restart, and response isolation.");
}
finally { File.Delete(worker); }
