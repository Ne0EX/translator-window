using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    private bool _disposed;

    public LocalTranslator(string pythonPath, string workerPath, string modelPath)
    {
        _pythonPath = Path.GetFullPath(pythonPath);
        _workerPath = Path.GetFullPath(workerPath);
        _modelPath = Path.GetFullPath(modelPath);
    }

    public async Task<string[]> TranslateAsync(IReadOnlyList<string> texts, string source, string target, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Process process = StartWorker();
            // A canceled response must never be mistaken for the next request's response.
            using var registration = token.Register(() => Kill(process));
            token.ThrowIfCancellationRequested();
            string request = JsonSerializer.Serialize(new { texts, source, target });
            await process.StandardInput.WriteLineAsync(request.AsMemory(), token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
            string? response = await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false);
            if (response == null)
            {
                string detail = await _standardError!.ConfigureAwait(false);
                throw new InvalidOperationException("Local translation worker stopped. " + detail[..Math.Min(detail.Length, 1000)]);
            }
            using var reply = JsonDocument.Parse(response);
            if (reply.RootElement.TryGetProperty("error", out var error))
                throw new InvalidOperationException("Local translation: " + error.GetString());
            string[]? translated = reply.RootElement.GetProperty("translations").Deserialize<string[]>();
            if (translated == null || translated.Length != texts.Count || Array.Exists(translated, text => text == null))
                throw new InvalidOperationException("Local translation worker returned an invalid response.");
            token.ThrowIfCancellationRequested();
            return translated;
        }
        catch
        {
            StopWorker();
            token.ThrowIfCancellationRequested();
            throw;
        }
        finally { _gate.Release(); }
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
            if (_process == null) return;
            Kill(_process);
            _process.Dispose();
            _process = null;
        }
    }

    public void Dispose()
    {
        lock (_processLock) _disposed = true;
        StopWorker();
    }
}
