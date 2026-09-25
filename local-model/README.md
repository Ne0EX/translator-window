# Local translation runtime

Run `./scripts/setup.ps1 -Cuda` for an NVIDIA GPU, or omit `-Cuda` for CPU.
The standalone `local-model/setup.ps1 -Cuda` installs only translation: a pinned
native llama.cpp CUDA runtime under `.tools/llama-server`, a Python 3.12 `.venv`,
the legacy llama-cpp-python fallback, and the verified Hy-MT2 model under
`models/hy-mt2`. Internet is needed only for setup. Recreate `.venv` with setup if
the project is moved or copied to another machine; virtual environments are not
portable. Rerunning setup without `-Cuda` neither downloads nor removes an installed
native CUDA runtime.

The app launches `.venv/Scripts/python.exe local-model/worker.py --model models/hy-mt2`.
The persistent worker reads JSON lines from stdin and writes progress events followed
by one final reply. With CUDA setup it prefers the bundled `llama-server.exe`, started
as a hidden child on a random `127.0.0.1` port with a per-process API key, proxies
disabled, and network downloads disabled. Captured text stays on the computer: it
passes through the worker pipe and authenticated local loopback only. No service or
global `PATH` entry is created. If the native executable is absent or fails in `auto`
mode, the worker uses the embedded Python runtime instead.

On Windows the native child loads the GGUF with `--load-mode none`. The pinned
runtime cannot release offloaded ranges from a memory-mapped file until shutdown.
On the target machine, direct loading reduced the native working set from
1.86-1.90 GiB to about 0.62 GiB and improved startup in both AB/BA orderings. It
increased private commit by about 248 MiB for the host model buffer. GPU allocation,
generation throughput, and effective model settings were unchanged. Automatic
memory fitting remains enabled.

The native server schedules at most four independent region translations together.
Each completed region becomes a progress event so its caption can render without
waiting for the other regions; the final reply must match every emitted event.
Its RAM prompt-state cache is disabled because requests do not reuse server prompts;
the worker keeps completed translations in its own bounded cache instead.
An empty text list loads the model without generating text. A cancelled frame stops
new caption callbacks and its remaining reply is drained before another request.
Stop, worker EOF, and app close release the worker and its native child. Protocol
failures restart the worker.

Example request and reply:

```json
{"texts":["Hello."],"source":"en","target":"th","progress":true}
{"index":0,"translation":"สวัสดี"}
{"translations":["สวัสดี"],"parallelism":4}
```

The legacy fallback reports `"parallelism":1`; the final reply therefore identifies
the active capability instead of making callers assume native execution.

Supported UI codes are `ja`, `ko`, `en`, and `th`; tags such as `ja-vert`,
`en-US`, and `th-TH` normalize to their language prefix. Each request supports
up to 64 regions. Each region is limited to 4,000 characters and 512 model tokens.
The model folder must contain exactly one `.gguf` file. The worker keeps at most
2,048 translations, keyed by language pair, normal/comics prompt, and source text. A generation that
reaches its token limit returns an error and never enters the cache.

The model uses source-language-aware translation prompts, with
no page-specific glossary or replacement text. Tags ending in `-comic` or `-vert`
use the measured manga prompt; ordinary text uses a general translation prompt.
Requests translate each region
separately, preserving one output per input. Full-page context and JSON generation
were slower or less reliable in the local comparison and are not used.

GPU model allocation or inference failures fall back to the legacy runtime and then
CPU in `auto` mode. Set `LOCAL_TRANSLATOR_DEVICE=cpu` to force the embedded CPU
runtime, or `cuda` to require GPU acceleration and surface an error if it is
unavailable. The current source-aware natural manga prompt and output limits are the
same on both paths. The approved four-slot native trial reduced dense translation
time, but one saved phrase meaning "father to child" changed to "father to student."
This is a known fidelity tradeoff, and no one-second end-to-end result is claimed.
OCR, capture, and rendering add their own latency. Cached regions return immediately.
See [the measured comparison](QUALITY.md).

Checks:

```powershell
.venv/Scripts/python.exe local-model/check.py --model models/hy-mt2
.tools/dotnet/dotnet.exe run --project local-model/check/BridgeCheck.csproj -- .venv/Scripts/python.exe
.tools/dotnet/dotnet.exe run --project tests/integration/IntegrationCheck.csproj -- .
.tools/dotnet/dotnet.exe run --project tests/manga/MangaCheck.csproj -- . artifacts/manga-test/page12.png
```

The Python worker rejects non-loopback connections, and the native child is launched
with its web UI, proxies, and network downloads disabled. The check covers all twelve
directions between the four UI languages, malformed requests, missing files, cache
behavior, simulated CUDA failure, and output truncation.
The bridge check verifies prewarming, in-flight cancellation, worker reuse,
response isolation, and process cleanup on disposal. The WPF manga check needs an unscaled local page fitting
within 1300 by 1800 pixels and an unobstructed desktop. Its captures verify
cold-start page visibility, translated captions, live scrolling, a new translated view,
blank-page visibility, and clearing on minimize and stop. These are rendering
and lifecycle assertions, not a claim of human-quality translation.

The model is [Tencent Hy-MT2-1.8B-GGUF](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF)
Q8_0, Apache 2.0, revision `a0c709d9fac510f2c807aa3af52872340dc37a4a`.
Provisioning verifies its SHA256. Exact source card, copyright, license, file hash,
and pinned runtime wheel hashes are retained in [attribution](attribution/ATTRIBUTION.md)
and copied into the model folder. The runtime is llama-cpp-python 0.3.35, using
an official Windows CPU or CUDA 12.5 wheel whose SHA256 setup verifies.
The preferred CUDA server is official llama.cpp build `b11146`, commit
`7fe450e19305b828c199d602c23a8337aaa1f03b`. Setup verifies both release archives
before staged extraction and retains their licenses and
[native runtime provenance](attribution/NATIVE-RUNTIME.md) beside the installed files.
