using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Translumo.Local;

if (args.Length != 1) throw new ArgumentException("Pass the local Python executable path.");
string worker = Path.Combine(Path.GetTempPath(), "translator-check-" + Guid.NewGuid() + ".py");
try
{
    File.WriteAllText(worker, """
import json, os, sys, time
sys.stdin.reconfigure(encoding="utf-8")
sys.stdout.reconfigure(encoding="utf-8")
for line in sys.stdin:
    request = json.loads(line)
    texts = request["texts"]
    translations = [f'{text}|{os.getpid()}' for text in texts]
    progress = request.get("progress", False)
    if texts == ["progress-a", "progress-b", "progress-c"] and progress:
        print(json.dumps({"index": 2, "translation": translations[2]}), flush=True)
        print(json.dumps({"index": 0, "translation": translations[0]}), flush=True)
        time.sleep(0.35)
        print(json.dumps({"index": 1, "translation": translations[1]}), flush=True)
    elif texts == ["wait-a", "wait-b"] and progress:
        print(json.dumps({"index": 1, "translation": translations[1]}), flush=True)
        time.sleep(0.5)
    elif texts == ["callback-error"] and progress:
        print(json.dumps({"index": 0, "translation": translations[0]}), flush=True)
        time.sleep(0.25)
    elif texts == ["duplicate"] and progress:
        print(json.dumps({"index": 0, "translation": translations[0]}), flush=True)
        print(json.dumps({"index": 0, "translation": translations[0]}), flush=True)
    elif texts == ["mismatch"] and progress:
        print(json.dumps({"index": 0, "translation": "wrong"}), flush=True)
    elif texts == ["worker-error"]:
        print(json.dumps({"error": "forced error"}), flush=True)
        continue
    elif texts == ["legacy-final"]:
        print(json.dumps({"translations": translations}), flush=True)
        continue
    print(json.dumps({"translations": translations, "parallelism": 4}), flush=True)
""");
    using var translator = new LocalTranslator(args[0], worker, Path.GetTempPath());
    string[] empty = await translator.TranslateAsync(Array.Empty<string>(), "ja", "th", default);
    if (empty.Length != 0 || translator.Parallelism != 4) throw new Exception("Empty prewarm capability mismatch.");

    string? legacyProgress = null;
    string[] legacy = await translator.TranslateProgressiveAsync(new[] { "legacy-final" }, "ja", "th", (_, value) =>
    {
        legacyProgress = value;
        return Task.CompletedTask;
    }, default);
    if (legacyProgress != legacy[0] || translator.Parallelism != 1)
        throw new Exception("A legacy final-only response did not synthesize progress with serial capability.");
    empty = await translator.TranslateAsync(Array.Empty<string>(), "ja", "th", default);
    if (empty.Length != 0 || translator.Parallelism != 4) throw new Exception("Native capability did not recover after legacy output.");

    var progressOrder = new List<int>();
    var firstProgress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<string[]> progressive = translator.TranslateProgressiveAsync(
        new[] { "progress-a", "progress-b", "progress-c" }, "ja", "th", (index, _) =>
        {
            lock (progressOrder) progressOrder.Add(index);
            firstProgress.TrySetResult();
            return Task.CompletedTask;
        }, default);
    await firstProgress.Task.WaitAsync(TimeSpan.FromSeconds(2));
    if (progressive.IsCompleted) throw new Exception("Progress was not visible before the final response.");
    string[] first = await progressive;
    int workerPid = int.Parse(first[0].Split('|')[1]);
    lock (progressOrder)
        if (!progressOrder.SequenceEqual(new[] { 2, 0, 1 }))
            throw new Exception("Out-of-order progress was not delivered by requested index.");

    string? canceledProgress = null;
    using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
    {
        try
        {
            await translator.TranslateProgressiveAsync(new[] { "wait-a", "wait-b" }, "ja", "th", (_, value) =>
            {
                canceledProgress = value;
                return Task.CompletedTask;
            }, cancellation.Token);
            throw new Exception("Cancellation did not interrupt the progressive caller.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    string[] next = await translator.TranslateAsync(new[] { "next response" }, "ja", "th", timeout.Token);
    if (canceledProgress != $"wait-b|{workerPid}" || next[0] != $"next response|{workerPid}")
        throw new Exception("Canceled progress was not drained or the warm worker was restarted.");

    var callbackTimer = Stopwatch.StartNew();
    try
    {
        await translator.TranslateProgressiveAsync(new[] { "callback-error" }, "ja", "th", (_, _) =>
            Task.FromException(new CallbackFailure()), default);
        throw new Exception("Progress callback failure was swallowed.");
    }
    catch (CallbackFailure) { }
    if (callbackTimer.ElapsedMilliseconds < 200)
        throw new Exception("Callback failure escaped before the final response was drained.");
    string callbackRecovery = (await translator.TranslateAsync(new[] { "after callback" }, "ja", "th", default))[0];
    if (callbackRecovery != $"after callback|{workerPid}")
        throw new Exception("A callback failure restarted a valid warm worker.");

    await ExpectInvalid(() => translator.TranslateProgressiveAsync(new[] { "duplicate" }, "ja", "th",
        (_, _) => Task.CompletedTask, default), "duplicate progress");
    string restarted = (await translator.TranslateAsync(new[] { "after duplicate" }, "ja", "th", default))[0];
    int restartedPid = int.Parse(restarted.Split('|')[1]);
    if (restartedPid == workerPid) throw new Exception("Malformed progress did not restart the worker.");

    await ExpectInvalid(() => translator.TranslateProgressiveAsync(new[] { "mismatch" }, "ja", "th",
        (_, _) => Task.CompletedTask, default), "mismatched final response");
    string afterMismatch = (await translator.TranslateAsync(new[] { "after mismatch" }, "ja", "th", default))[0];
    int finalPid = int.Parse(afterMismatch.Split('|')[1]);
    if (finalPid == restartedPid) throw new Exception("A final/progress mismatch did not restart the worker.");

    await ExpectInvalid(() => translator.TranslateAsync(new[] { "worker-error" }, "ja", "th", default), "worker error");
    string afterError = (await translator.TranslateAsync(new[] { "after error" }, "ja", "th", default))[0];
    int afterErrorPid = int.Parse(afterError.Split('|')[1]);
    if (afterErrorPid == finalPid) throw new Exception("A worker error did not restart the worker.");
    finalPid = afterErrorPid;
    using var workerProcess = Process.GetProcessById(finalPid);
    translator.Dispose();
    if (!workerProcess.WaitForExit(5000)) throw new Exception("Dispose did not stop the translation worker.");
    Console.WriteLine("PASS: progressive ordering, final validation, cancel/drain reuse, callback drain, protocol restart, and disposal.");
}
finally { File.Delete(worker); }

static async Task ExpectInvalid(Func<Task<string[]>> action, string behavior)
{
    try { await action(); throw new Exception($"Invalid {behavior} was accepted."); }
    catch (InvalidOperationException) { }
}

sealed class CallbackFailure : Exception { }
