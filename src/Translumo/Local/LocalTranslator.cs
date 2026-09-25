using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Translumo.Local;

public sealed class LocalTranslator : IDisposable
{
    private readonly string _pythonPath;
    private readonly string _workerPath;
    private readonly string _modelPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _processLock = new();
    private Process? _process;
    private Task<string>? _standardError;
    private Task<TranslationReply>? _pendingResponse;
    private int _parallelism = 1;
    private bool _disposed;

    public LocalTranslator(string pythonPath, string workerPath, string modelPath)
    {
        _pythonPath = Path.GetFullPath(pythonPath);
        _workerPath = Path.GetFullPath(workerPath);
        _modelPath = Path.GetFullPath(modelPath);
    }

    internal int Parallelism => Volatile.Read(ref _parallelism);

    public Task<string[]> TranslateAsync(IReadOnlyList<string> texts, string source, string target, CancellationToken token)
        => TranslateAsync(texts, source, target, null, token);

    internal Task<string[]> TranslateProgressiveAsync(IReadOnlyList<string> texts, string source, string target,
        Func<int, string, Task> progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return TranslateAsync(texts, source, target, progress, token);
    }

    private async Task<string[]> TranslateAsync(IReadOnlyList<string> texts, string source, string target,
        Func<int, string, Task>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        bool responseComplete = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Keep the JSON-lines protocol aligned: a canceled caller leaves its remaining replies to drain.
            if (_pendingResponse is not null)
            {
                var abandoned = _pendingResponse;
                await abandoned.WaitAsync(token).ConfigureAwait(false);
                if (ReferenceEquals(_pendingResponse, abandoned)) _pendingResponse = null;
            }
            token.ThrowIfCancellationRequested();
            Process process = StartWorker();
            string request = progress is null
                ? JsonSerializer.Serialize(new { texts, source, target })
                : JsonSerializer.Serialize(new { texts, source, target, progress = true });
            await process.StandardInput.WriteLineAsync(request).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            var responseTask = ReadResponseAsync(process, texts.Count, progress, token);
            _pendingResponse = responseTask;
            var response = await responseTask.WaitAsync(token).ConfigureAwait(false);
            if (ReferenceEquals(_pendingResponse, responseTask)) _pendingResponse = null;
            responseComplete = true;
            token.ThrowIfCancellationRequested();
            if (response.CallbackError is not null)
                ExceptionDispatchInfo.Capture(response.CallbackError).Throw();
            return response.Translations;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch when (responseComplete) { throw; }
        catch
        {
            StopWorker();
            token.ThrowIfCancellationRequested();
            throw;
        }
        finally { _gate.Release(); }
    }

    private async Task<TranslationReply> ReadResponseAsync(Process process, int count,
        Func<int, string, Task>? progress, CancellationToken callerToken)
    {
        var received = new string?[count];
        var seen = new bool[count];
        Exception? callbackError = null;
        while (true)
        {
            string? response = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
            if (response is null) throw await WorkerStoppedAsync().ConfigureAwait(false);
            using var reply = JsonDocument.Parse(response);
            var root = reply.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw InvalidResponse();
            if (root.TryGetProperty("error", out var error))
            {
                string? detail = error.ValueKind == JsonValueKind.String ? error.GetString() : null;
                throw new InvalidOperationException("Local translation: " + (detail ?? "unknown worker error"));
            }
            if (root.TryGetProperty("translations", out var final))
            {
                string[]? translations = final.Deserialize<string[]>();
                int parallelism = 1;
                if (translations is null || translations.Length != count
                    || Array.Exists(translations, text => text is null)
                    || root.TryGetProperty("parallelism", out var capability)
                    && (!capability.TryGetInt32(out parallelism) || parallelism is not (1 or 4)))
                    throw InvalidResponse();
                for (int index = 0; index < count; index++)
                    if (seen[index] && !string.Equals(received[index], translations[index], StringComparison.Ordinal))
                        throw new InvalidOperationException("Local translation worker final response did not match its progress.");
                Volatile.Write(ref _parallelism, parallelism);
                if (progress is not null && callbackError is null)
                {
                    for (int index = 0; index < count; index++)
                    {
                        if (callerToken.IsCancellationRequested) break;
                        if (seen[index]) continue;
                        try { await progress(index, translations[index]).ConfigureAwait(false); }
                        catch (Exception errorFromCallback) { callbackError = errorFromCallback; break; }
                    }
                }
                return new TranslationReply(translations, callbackError);
            }
            if (progress is null || !root.TryGetProperty("index", out var indexElement)
                || !indexElement.TryGetInt32(out int responseIndex) || responseIndex < 0 || responseIndex >= count
                || !root.TryGetProperty("translation", out var translationElement)
                || translationElement.ValueKind != JsonValueKind.String)
                throw InvalidResponse();
            string? translation = translationElement.GetString();
            if (translation is null || seen[responseIndex])
                throw new InvalidOperationException(seen[responseIndex]
                    ? "Local translation worker returned duplicate progress."
                    : "Local translation worker returned an invalid response.");
            seen[responseIndex] = true;
            received[responseIndex] = translation;
            if (callbackError is null && !callerToken.IsCancellationRequested)
            {
                try { await progress(responseIndex, translation).ConfigureAwait(false); }
                catch (Exception errorFromCallback) { callbackError = errorFromCallback; }
            }
        }
    }

    private static InvalidOperationException InvalidResponse()
        => new("Local translation worker returned an invalid response.");

    private async Task<InvalidOperationException> WorkerStoppedAsync()
    {
        string detail = await _standardError!.ConfigureAwait(false);
        return new InvalidOperationException("Local translation worker stopped. " + detail[..Math.Min(detail.Length, 1000)]);
    }

    private Process StartWorker()
    {
        lock (_processLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process != null && !_process.HasExited) return _process;
            _process?.Dispose();
            if (!File.Exists(_pythonPath) || !File.Exists(_workerPath))
                throw new FileNotFoundException("Local translation runtime is missing. Run local-model/setup.ps1 first.");
            var info = new ProcessStartInfo(_pythonPath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(_workerPath)!
            };
            info.ArgumentList.Add("-u");
            info.ArgumentList.Add(_workerPath);
            info.ArgumentList.Add("--model");
            info.ArgumentList.Add(_modelPath);
            info.Environment["HF_HUB_OFFLINE"] = "1";
            info.Environment["TRANSFORMERS_OFFLINE"] = "1";
            info.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
            _process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the local translation worker.");
            _standardError = _process.StandardError.ReadToEndAsync();
            return _process;
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    private void StopWorker()
    {
        lock (_processLock)
        {
            if (_process == null)
            {
                _pendingResponse = null;
                return;
            }
            Kill(_process);
            _process.Dispose();
            _process = null;
            _pendingResponse = null;
            Volatile.Write(ref _parallelism, 1);
        }
    }

    public void Dispose()
    {
        lock (_processLock) _disposed = true;
        StopWorker();
    }

    private sealed record TranslationReply(string[] Translations, Exception? CallbackError);
}
