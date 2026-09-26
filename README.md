# Translumo Local

**Version 0.2.0 release candidate · Windows x64 · source build**

[Candidate release notes](docs/releases/0.2.0.md) · [Changelog](CHANGELOG.md) ·
[Problem and decision ledger](docs/project-ledger.md) · [Domain glossary](CONTEXT.md)

[v0.2.0 implementation plan](docs/plans/0.2.0.md) ·
[Tracking issue](https://github.com/Ne0EX/translator-window/issues/1) ·
[Reader feedback and evidence](docs/feedback/2026-09-25-v0.2.0.md) ·
[Published v0.1.0 notes](docs/releases/0.1.0.md)

A Windows fork of [ramjke/Translumo](https://github.com/ramjke/Translumo).
OCR and translation run on your computer. The executable excludes the upstream
cloud translators, proxy tools, updater, and automatic runtime downloads.

Choose **selected area**, **window**, or **screen**, independently of presentation:

- **Subtitle overlay** places translations beside recognized text.
- **Subtitle overwrite** covers recognized text with opaque translated captions.

Korean, Japanese, English, and Thai are selectable as source and target languages.
The default is **Japanese → Thai**, with **Manga / webtoon text detection** and
**Subtitle overwrite** enabled. Japanese manga recognition handles both vertical
and horizontal dialogue automatically.

The v0.2.0 candidate keeps caption placement independent of source-cover padding,
uses a 12-DIP Thai readability floor, and adds automatic heading emphasis plus
dialogue, narration, and emphasis overrides. Plain fills and smooth linear gradients
can use bounded source-text cover reconstruction. Backgrounds rejected by that
bounded qualifier, including the tested decorated and detailed-artwork cases, keep
the source visible and use a readable caption fallback.

## Run

In a configured checkout, double-click **Start Translator.cmd**.

On another Windows 10 (2004+) / Windows 11 x64 machine, install Python 3.12 and the
[Microsoft Visual C++ x64 runtime](https://aka.ms/vs/17/release/vc_redist.x64.exe), then run:

```powershell
.\scripts\setup.ps1
# For the tested NVIDIA CUDA configuration:
.\scripts\setup.ps1 -Cuda
# Or specify Python explicitly:
.\scripts\setup.ps1 -Python 'C:\path\to\python.exe' -Cuda
```

Setup downloads the workspace .NET SDK, Python dependencies, OCR data, and pinned
local model weights. Allow several GB for installed models and caches. Runtime
recognition and translation work offline. CPU inference is available; NVIDIA CUDA
is recommended for manga reading.

Thai caption wrapping uses the ICU word breaker included with supported Windows
versions; installing a Thai language pack is not required.

Keep the project folder together: the app is in `artifacts/app`, Python in `.venv`,
and weights in `models/hy-mt2`, `models/tessdata`, `models/comic-text-detector`, and
`models/manga-ocr`. A Python virtual environment is machine-specific: run setup
on each machine instead of copying its environment.

Choose a capture mode, select its area/window/screen, then start. Window mode
tracks the visible window as it moves and pauses when it is minimized. Keep the
source visible; this is desktop capture. Clicks and scrolling pass through subtitles.
Escape cancels area selection. Stop removes subtitles and releases the models.
For manga in a browser, try a selected page area if tabs or controls are detected as text.

Manga overwrite leaves the selected page visible while OCR and translation run.
Translated captions replace each detected source region as its translation arrives; source
text remains visible until its caption is ready. During navigation, captions hide and
new capture work pauses briefly. After motion settles, fresh pixel and OCR checks
determine whether captions can be restored or the page needs new recognition.
A changed or text-free page stays visible while it is scanned. Blocks that cannot
be read remain uncovered; the status reports the unreadable count. Literal ellipses
remain ellipses rather than being labeled translation failures.
With Japanese Windows OCR installed, a dense vertical contents page can use a
second recognition pass and display its headings in two readable page columns.
The controls remain visible so you can stop or change the selection.

Cover padding adjusts the mask around detected text. Disable manga detection for
ordinary desktop text; that path uses installed Windows OCR with local Tesseract
fallback. Ordinary overwrite covers recognized text while translation runs.

## Build and verify

```powershell
.\scripts\build.ps1
.\.venv\Scripts\python.exe local-model/check.py --model models/hy-mt2
.\.tools\dotnet\dotnet.exe run --project local-model/check/BridgeCheck.csproj -- .venv/Scripts/python.exe
.\.tools\dotnet\dotnet.exe run --project tests/OcrSmoke/OcrSmoke.csproj -- models/tessdata
.\.tools\dotnet\dotnet.exe run --project tests/unreadable/UnreadableCheck.csproj -- .
.\.tools\dotnet\dotnet.exe run --project tests/layout-hold/LayoutHoldCheck.csproj
.\.tools\dotnet\dotnet.exe run --project tests/overlay/OverlayCheck.csproj -- --visual
.\.tools\dotnet\dotnet.exe run --project tests/replay/ReplayCheck.csproj -c Release
.\.tools\dotnet\dotnet.exe run --project tests/color-style/ColorStyleCheck.csproj -c Release
.\.tools\dotnet\dotnet.exe run --project tests/scroll-alignment/ScrollAlignmentCheck.csproj -c Release
.\.tools\dotnet\dotnet.exe run --project tests/integration/IntegrationCheck.csproj -- .
```

The fixed replay, color/style, scroll-alignment, and unreadable-region checks use
redistribution-safe inputs and do not load GPU models. Replay diagnostics are
opt-in and stay in ignored local folders.
On the 2026-09-26 working candidate, the Release build, default and vertical
overlay checks, layout/cancellation check, fixed replay (all five cases twice),
color/style check including SRC01, scroll-alignment check, and unreadable-region
check passed locally. The full visual check passed its feature phases, then its
final native screenshot failed with an invalid handle. Direct capture was blocked
by DXGI access denied (`0x80070005`) in the automation environment. Exact-commit
hosted, private full-processing, reader, matched-v0.1.0, and fresh-machine gates
remain pending; see the [candidate release notes](docs/releases/0.2.0.md).
Run visual checks one at a time on an unlocked desktop. They temporarily display
test windows. The integration check exercises all six capture/style combinations
with real Japanese OCR and Thai translation, then changed text, moved/resized
windows, minimize/restore, and cancellation. Generated evidence stays in ignored
`artifacts/` and `.cache/verification/` folders.

The optional real manga check needs the locally acquired, unscaled 1273 × 1800
fixture and a display that fits it. Fixture attribution and acquisition details are
in [verification fixture provenance](docs/verification-fixtures.md); copyrighted
fixture files are not in the repo.

```powershell
.\.venv\Scripts\python.exe local-ocr/check_scroll.py --model models/comic-text-detector/comictextdetector.onnx --image artifacts/manga-test/page12.png
.\.tools\dotnet\dotnet.exe run --project tests/manga/MangaCheck.csproj -- . artifacts/manga-test/page12.png
```

## Current limits

The local model can mistranslate proper names, omitted subjects, and short dialogue.
Family chart detection now separates the reported merged labels, but translated
names, generation numbers, and parenthetical status notes can still be wrong.
Narrow labels may use numbered captions in a side margin.
The [measured model comparison](local-model/QUALITY.md) records these limits; successful
OCR and target-language output do not establish human-quality translation.
Seamless manga/webtoon reading remains an acceptance goal, not a guarantee for all
pages: stylized text can evade detection, small text needs zooming, and detailed
artwork is outside bounded color-cover support. This does not perform image
inpainting. Dense pages can run out of readable caption space; failed layouts clear
stale captions and report fitting guidance. Larger animated areas can prevent a view
from settling; mixed-DPI multiple-monitor behavior needs a physical hardware test.
Native-feeling scrolling and page-turn animations remain under manual evaluation.
The 450 ms navigation pause is provisional; no one-second end-to-end latency or
universal smoothness guarantee is made. GPU/CPU memory pressure affects both startup
and reading performance. Fresh-machine setup has not been qualified for this release.
Translation APIs and LLM providers are deferred to the
[API backlog](docs/plans/0.2.0.md#deferred-remote-translation-backlog); this
candidate performs OCR and translation locally and contains no API-key integration.

See [translation runtime](local-model/README.md), [manga OCR](local-ocr/README.md),
[implementation and evidence](docs/implementation-plan.md), and
[upstream README](docs/upstream-README.md). Upstream source remains for history and
reference; only the local application project is built.

## Licenses

The upstream application retains [Apache-2.0](LICENSE). The separate comic detector
component follows [dmMaze/comic-text-detector](https://github.com/dmMaze/comic-text-detector)
and carries [GPL-3.0](local-ocr/LICENSE.comic-text-detector); retain that license and
its corresponding source when distributing that component. The Japanese recognition
model [manga-ocr-base](https://huggingface.co/kha-white/manga-ocr-base) is Apache-2.0.
The translation model [HY-MT2](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF) is Apache-2.0.
Translation model provenance and runtime details are in [local-model](local-model/README.md).
Model setup retains the model cards and notices beside the downloaded weights.
