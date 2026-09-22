# Local translation runtime

Run `./scripts/setup.ps1 -Cuda` for an NVIDIA GPU, or omit `-Cuda` for CPU.
The standalone `local-model/setup.ps1` installs only translation; CUDA DLLs also
require `local-ocr/setup.ps1 -Cuda` or a compatible system CUDA toolkit.
Setup creates a Python 3.12 `.venv` inside the repository, installs a pinned
llama-cpp-python runtime, and downloads the verified Hy-MT2 model into
`models/hy-mt2`. Internet is needed only for setup. Recreate `.venv` with setup
if the project is moved or copied to another machine; virtual environments are
not portable. Rerunning default setup preserves an existing verified CUDA runtime.

The app launches `.venv/Scripts/python.exe local-model/worker.py --model models/hy-mt2`.
One persistent process reads JSON lines from stdin and writes one JSON reply per
line. It opens no server and sends no screen content or text over a network.
An empty text list loads the model without generating text. C# serializes
requests; cancellation kills the process so an old reply cannot corrupt a new
request. The next request starts a fresh worker.

Example request and reply:

```json
{"texts":["Hello."],"source":"en","target":"th"}
{"translations":["สวัสดี"]}
```

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

GPU model allocation or inference failures fall back to CPU in `auto` mode.
For GPU use, run the full `scripts/setup.ps1 -Cuda`: its OCR Torch wheel also
provides the CUDA runtime DLLs. The translation worker loads those DLLs from the
workspace without importing Torch; a separate system CUDA toolkit is not needed. Set
`LOCAL_TRANSLATOR_DEVICE=cpu` to force CPU, or `cuda` to require CUDA and surface
an error if it is unavailable. On the tested RTX 3060 laptop, eight uncached
Japanese manga regions took about 1.7 seconds to translate and loading the
translation model took about 1.4 seconds. OCR and capture add their own latency.
The same cached regions return immediately. See [the measured comparison](QUALITY.md).

Checks:

```powershell
.venv/Scripts/python.exe local-model/check.py --model models/hy-mt2
.tools/dotnet/dotnet.exe run --project local-model/check/BridgeCheck.csproj -- .venv/Scripts/python.exe
.tools/dotnet/dotnet.exe run --project tests/integration/IntegrationCheck.csproj -- .
.tools/dotnet/dotnet.exe run --project tests/manga/MangaCheck.csproj -- . artifacts/manga-test/page12.png
```

The Python check blocks socket connections in the actual inference process. It
checks all twelve directions between the four UI languages, malformed requests,
missing files, cache behavior, simulated CUDA failure, and output truncation.
The bridge check verifies prewarming, in-flight cancellation, worker restart,
and response isolation. The WPF manga check needs an unscaled local page fitting
within 1300 by 1800 pixels and an unobstructed desktop. Its captures verify
cold-start covering, translated captions, scroll hold, a new translated frame,
blank-page protection, and clearing on minimize and stop. These are rendering
and lifecycle assertions, not a claim of human-quality translation.

The model is [Tencent Hy-MT2-1.8B-GGUF](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF)
Q8_0, Apache 2.0, revision `a0c709d9fac510f2c807aa3af52872340dc37a4a`.
Provisioning verifies its SHA256. Exact source card, copyright, license, file hash,
and pinned runtime wheel hashes are retained in [attribution](attribution/ATTRIBUTION.md)
and copied into the model folder. The runtime is llama-cpp-python 0.3.35, using
an official Windows CPU or CUDA 12.5 wheel whose SHA256 setup verifies.
