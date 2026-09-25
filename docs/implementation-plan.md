# Local Translumo implementation and acceptance

The local branch starts from ramjke/Translumo commit
`dc7fe322d68ec5d7d2664a0fa044770bdfe0141d` and retains upstream history, licensing,
and the area-selection UI. The .NET 8 Windows executable compiles only the new
local pipeline. Legacy cloud adapters remain as source reference and are excluded
from the executable.

## Behavior

- Capture selected area, visible window, or selected screen in physical pixels.
- Choose beside-text subtitles or opaque overwrite independently of capture mode.
- Select Korean, Japanese, English, or Thai for either language; default Japanese → Thai.
- Manga detection separates dialogue from artwork; local manga OCR handles Japanese
  columns and furigana. Windows OCR and local Tesseract serve other text paths.
- Run OCR and translation in persistent local processes. Installation downloads
  models; runtime uses local files without cloud fallback.
- Reject results from obsolete frames. Manga overwrite leaves the live page visible
  and adds captions in four-block batches as translations finish.
- Keep controls accessible; subtitles neither intercept input nor appear in OCR captures.
- Stop/cancellation clears presentation, terminates active workers, and releases models.

The implementation deliberately uses whole-frame stability with a small pixel-noise tolerance and rectangular masks.
Larger animated areas need per-region tracking; artwork restoration would need inpainting.
Neither is claimed by the current implementation.

## Recorded checks on 2026-09-22

Test machine: Windows, Ryzen 7 5800H, RTX 3060 Laptop, one 4K display at 150% scaling.

| Check | Evidence |
| --- | --- |
| Local Windows build and self-contained publish | Workspace .NET SDK 8.0.425; only the known WPF/WinForms DPI manifest analyzer warning |
| Four source-language OCR paths | `tests/OcrSmoke`: Windows/Tesseract, comic crops, Japanese vertical text, oversized images, physical capture |
| Local translation protocol | `local-model/check.py`: blocked sockets, all twelve directions among four languages, comic/plain prompts, input/output limits, CUDA fallback, cache, missing assets |
| Cancellation and restart | `local-model/check/BridgeCheck.csproj`: canceled reply cannot contaminate a later request |
| Subtitle rendering | `tests/overlay`: geometry, actual source-pixel masking, capture exclusion, click-through, partial captions and moving control cutout |
| All six capture/style combinations | `tests/integration`: real Japanese OCR → Thai translation, content changes, moved/resized window, minimize/restore, stop |
| Real manga OCR | Eight exact Japanese transcripts, ignoring punctuation, including hospital sign; source and 60/100/120 px scroll offsets |
| Offline manga scroll regression | `local-ocr/check_scroll.py`: same eight transcripts with worker socket connections blocked |

The real fixture is a privately downloaded author-provided *Give My Regards to Black
Jack* page by SHUHO SATO. Attribution and acquisition terms are recorded in
[verification fixture provenance](verification-fixtures.md). No manga imagery is
included in the repo.

## Real manga acceptance

`tests/manga/MangaCheck.csproj` passed using the final Hy-MT2 1.8B Q8 model:
cold startup remained covered; all eight blocks received Thai captions; the native
translated image followed a small scroll while the newly revealed strip stayed
covered; the completed new view replaced it; a blank/no-detection view retained the
previous image; minimize/restore and stopping cleared the presentation correctly.
This records the older held-frame behavior, replaced in the current build below.

That native run measured 20.9 seconds for first-page OCR and 2.9 seconds for
translation; the scrolled view took 4.6 seconds for OCR and 0.3 seconds for
translation while the already translated content moved immediately. These are local
measurements on one page, not a throughput guarantee. Evidence remains in the ignored
`artifacts/manga-test/live-manga-check.json` and `live-*.png` files.

Thai captions preserve Unicode grapheme clusters and use Windows dictionary boundaries,
balanced wrapping, and fitting
checks. Unreadable detected blocks retain their masks. At that point, a layout that
could not fit kept the last translated view and continued watching for another frame; the
focused `tests/unreadable` and `tests/layout-hold` regressions cover those paths.

Translation fidelity is separate from rendering. The selected small model improves
on the initial M2M100 baseline but can still mistranslate names, omitted subjects,
and dialogue fragments. The [measured comparison](../local-model/QUALITY.md) records
those limitations and why the much slower 7B candidate was rejected. Rendering
checks establish source coverage on the tested page, not human-quality translation
or perfect OCR on arbitrary comics.

Physical mixed-DPI multi-monitor testing remains unavailable on this single-display
machine; negative-coordinate and scale geometry checks do not substitute for it.

## QA continuation on 2026-09-24

The self-contained app publish, offline translation, real manga OCR at four scroll offsets,
bridge cancellation, subtitle layout, unreadable blocks, and changed-page hold checks
pass. A changed page arriving during a no-text OCR result no longer stalls scanning.
A BrowserOS neo session opened the publisher preview at
`https://nc.tameshiyo.me/9784094066371`, navigated all 27 reader positions, and confirmed
that page images load through the final spread. That browser session cannot run the
Windows subtitle overlay. Offline replays of all 27 reconstructed 3840 x 2088 reader
positions ran the production detector, OCR, translator, and renderer: 388 regions were
translated and rendered, and all 388 detected source regions were masked across every
spread. The densest spread had 29 captions; the replay found no caption below 12 DIP
after moving narrow chart text to numbered side gutters. At that point, manga overwrite
reveals the captured page under masks as OCR completes,
then adds Thai captions in four-block batches; a failed partial layout restores the
last readable page. Reused line measurements and mask brushes reduced the dense
spread's later partial redraws from roughly 0.8–1.1 seconds to 0.45–0.6 seconds.
OCR and translation still make errors with names and small text,
and increased cover padding damages artwork. Focused overlay and layout-hold checks
pass. The unlocked desktop allowed `tests/manga` to verify native screen pixels,
scroll movement, new-strip coverage, no-text hold, minimize, and stop. A direct
run on Comet's live reader at spread 8 produced 28 Thai captions and 28 source
masks, including eight numbered side-gutter captions; all were at least 12 DIP.
That run measured 6.2 seconds for OCR, 12.4 seconds for translation with partial
updates, and 0.6 seconds for final layout. The contents spread exposed a detector
gap: it found only two large blocks and left some Japanese headings visible.
The full live reader journey was checked on 2026-09-25 below.

## Live reader continuation on 2026-09-25

Two Comet captures of the same spread differed at 34 pixels, and a visible
terminal added 52 blinking pixels. Exact whole-frame hashes treated those as
new pages and repeatedly restarted OCR. Captures now need at least 128 pixels
with a channel difference of 16 before a page-change rescan; the result check
uses the same tolerance. A focused layout-hold regression checks 100 noisy
pixels against a 144-pixel text-sized change. The unobstructed live Comet
spread 8 completed with 28 captions, 28 masks, no caption below 12 DIP, and
eight numbered margin captions. It measured 6.4 seconds for OCR, 13.1 seconds
for translation, and 30 ms for final layout. A same-frame caption cache reduces
repeated partial redraw work. The comic detector misses most narrow Japanese
headings on the contents spread. A second Windows OCR pass now selects 19 main
columns while excluding ruby text, and a two-column white-panel layout keeps
the headings readable without covering the two figures or the page numbers.
Live Comet spread 4 completed with 19 captions, 19 source masks, none below
12 DIP; the completed pass measured 4.5 seconds for OCR, 5.5 seconds for
translation, and 36 ms for final layout. Some names and short headings still
mistranslate; the small local model does not establish human-quality Japanese-
to-Thai translation.

A persistent session then advanced through all 27 live Comet reader positions.
The completed views had 404 captions and 404 matching source masks, with no
caption below 12 DIP. The longest page completion was 34.4 seconds; most warm
pages completed in about 5–18 seconds. At page 21, one crop exhausted the
manga recognizer's 300-token limit and initially aborted the scan. The worker
now leaves that crop unreadable and lets the existing Windows/Tesseract fallback
and mask path handle it; the resumed live run completed pages 21–27. The
author-provided eight-text fixture still passes at 0, 60, 100, and 120-pixel
scroll offsets, and a focused check covers a mixed readable/overlong batch.
Visual review of rendered dense spreads and the contents page found their
source text covered; Japanese OCR on translated Thai produces false Japanese
matches, so it cannot prove perfect coverage on every page. Reader counter
OCR also misread some positions; the sequential navigation and inspected
counter images support the 27-position journey.

## Speed and live-page update on 2026-09-25

The active build does not invoke EasyOCR; it uses the comic-text-detector ONNX block
head and MangaOCR for Japanese crops. A detection-only trial of RapidOCR 3.9.2's
default PP-OCRv6 small detector found 143 line boxes on Comet spread 8 versus 29
comic blocks from the current detector. Warm CPU detection was about 1.0–1.1 seconds
versus 1.3 seconds, but sending five times as many crops to MangaOCR would change
grouping and increase recognition work. The 6 GB GPU instead completed the same 29
MangaOCR crops in 3.03 seconds with batches of eight versus 3.88 seconds with batches
of four, with identical outputs; it falls back to batches of four on CUDA failure.
Using the comic detector on CUDA took 28 seconds for the first native page despite
faster warm detection, so CPU remains the default for that model.

Manga overwrite now leaves browser artwork visible during OCR and translation.
Captions and their source masks appear in four-region batches. Untranslated regions
stay untouched until ready. Existing captions follow small scrolls while the live
page remains visible; page changes, no-text scans, and layout failures clear stale
captions. The updated native manga check validates visible screen pixels during cold
start, scroll, no-text, minimize, and stop. It still receives eight Thai captions.
The tested dense Comet spread still needs several seconds of OCR and translation;
this change improves responsiveness and crop throughput, not a one-second completion.

## Remaining performance plan

The target remains fast, lightweight, local manga/webtoon reading with readable
translations. The live page must stay interactive while captions arrive. Measure
first usable captions, complete-page latency, repeated scroll/page-change latency,
and memory, including a genuinely new worker. A warm cache is not cold-start evidence.

1. Measure the current worker startup, detector, recognition, and translation stages
   separately on the eight-block fixture and the dense Comet spread. Keep the input
   images and model settings identical between comparisons.
2. Test whether obsolete-frame cancellation destroys loaded workers. Preserve warm
   models across page changes while preventing stale responses from reaching the
   overlay. Explicit Stop and app close must still release worker processes.
3. Profile the exact comic detector graph. Requesting only the block output does not
   by itself prove unused branches are skipped. Use ONNX Runtime's existing execution
   controls if they reduce work with identical detected boxes.
4. Attribute cold OCR time to imports, model loading, CUDA initialization, and first
   inference. Change the measured bottleneck; prewarming alone does not establish a
   faster cold start. Research smaller local inference paths if the same-model fixes
   cannot meet the reading target.
5. Delegate implementation to GPT-5.6-sol, retain output-equivalence checks for speed
   changes, and verify cancellation, rapid navigation, local-only execution, caption
   readability, and the native app before publishing another build.

The one-second reference remains the working latency target for a small changed
view. Dense-page completion and cold startup must be reported separately; passing
rendering tests alone does not complete this performance goal.

## Same-model performance work, 2026-09-25

The original Comic Text Detector graph contains YOLOv5s block detection, U-Net
segmentation, and a DBNet text-line map. Requesting only its block output still
executed all three branches in the installed inference runtime. Setup now extracts
a verified block-only graph: 94.7 MB becomes 29.1 MB, and median CPU detection over
27 captured reader positions falls from 1,283 to 282 ms. Raw block tensors and
selected boxes match exactly, including the four fixture scroll offsets.

Cold MangaOCR profiling attributed about 19 seconds to Torch and Transformers
imports. A local export of the same pinned recognition weights now runs through
ONNX Runtime with GPU attention caches and the existing four-beam settings.
The production export matches all 29 dense-spread transcripts twice, plus the
eight fixture transcripts at four scroll offsets, with sockets blocked. Torch
remains an asset-missing fallback. Setup generates the graphs from local weights.

Cancellation now keeps loaded workers and drains an abandoned response before
sending another request. Explicit Stop and disposal still release processes.
Manga captions arrive after each region; scroll rescans retain exact known source
text matches and translate only newly recognized text. Focused regression checks
cover worker reuse, stale-response isolation, first-caption delivery, and retaining
known captions while newly revealed text is translated.

The first native Debug-build run of these changes still exposed a gap from isolated model
measurements. Eight captions appeared at 15.9 seconds cold and completed at 17.9
seconds; warm OCR took 1.7–1.9 seconds. A dense 29-region view completed in 29.3
seconds cold, a changed 20-region view in 10.6 seconds, and a cached revisit in 6.6
seconds. Its cached translation/render loop alone took 3.4 seconds. These runs
passed live-page visibility, readable captions, source masks, scrolling, and
cleanup; they do not meet the one-second target. Further profiling is checking
shared GPU startup and repeated full-frame image copies during partial renders.

The official Q4 translation candidate was about 27% faster but changed sentence
meaning, so Q8 remains selected. See `local-model/QUALITY.md` for that comparison.

### Published Release verification

The overlay now reuses the frozen frame's BGRA bytes instead of copying about
32 MB on each dense-page partial render. First-caption delivery stays immediate;
later partial redraws are coalesced for 100 ms after the previous render finishes,
and completion always renders the final result. This removes repeated redraws
when many translations return immediately from cache.

The final Release native fixture passed eight Thai captions, live source pixels,
scroll reuse, blank-page clearing, minimize, and stop. Its cold first caption took
10.26 seconds and all eight completed at 11.97 seconds; OCR accounted for 8.40
seconds. Warm scroll OCR took 1.31 seconds, and restore showed its first caption
in 1.60 seconds. On dense reader captures, the 29-region cold view showed its first
caption at 12.35 seconds and completed at 22.52 seconds. A changed 20-region view
took 4.63 seconds to first caption and 9.83 seconds to completion; a cached
29-region revisit completed in 4.89 seconds, with 33 ms in its translation/partial
render loop. Every completed dense caption had a matching mask and at least 12 DIP
type. These are fresh-process and warm-session results, not reboot-cold tests.

Shared GPU profiling found only about 380–406 MiB free on the 6 GiB laptop GPU
with both models resident. Arena limits and disabling ONNX Runtime thread spinning
showed no reliable improvement and were not adopted. Cold startup and new dense
views still miss the working latency target. The updated app was published to
`artifacts/app` and reopened for the user's manual check; human-quality translation
remains deferred as requested.

### FP16 recognition, exact scroll reuse, and lower translation memory

The CUDA recognizer now loads locally exported FP16 versions of the same pinned
weights. CPU inference and CUDA recovery retain FP32 assets. Beam probability
arithmetic stays float32, matching Transformers. This saved about 639 MiB of GPU
memory. The encoder's single convolution now uses cuDNN heuristic selection;
initialization plus first recognition fell from 2.98 to 2.56 seconds in the
controlled Q8-resident comparison. Eight author-fixture regions at four scroll
positions and all 29 dense-page regions stayed exact. Across 388 saved crops, one
FP16/FP32 difference remained in an already incoherent merged contents-column crop.
The native contents fallback now returns 19 separate columns even when small
extra detections accompany the giant unions.

Recognition reuse retains hashes of the original padded OCR inputs. A scroll must
preserve those exact pixels, and the actual next request must pass the same check.
The full detector still runs. Only unambiguous matches inside the proved footprint
reuse text; new, split, merged, and expanded detections use normal recognition.
Checks cover changed padding, stale snapshots, detector misses, and nearby new text.
All 24 retained crops across three fixture scrolls passed the exact proof. The native
scroll requested zero new translations and completed OCR in 515 ms.

Translation now starts the next region while the current caption is rendered,
keeping one request pending. The Q8 runtime's batch sizes changed from 512/512 to
128/64 after two uncached passes over the eight-region and 29-region fixtures.
All 37 outputs matched across configurations and repeats. Warm timing differed by
less than 1%; GPU allocation fell by 210 MiB and private commit by about 404 MiB.
This is a memory improvement, not evidence of a faster model load. Startup profiling
attributes the isolated load primarily to GGUF loading (1.3–3.2 seconds), with only
tens of milliseconds in context/batch creation. Native concurrent startup remains
slower and variable under the machine's memory and compute pressure.

The combined Release native checks passed live artwork visibility, scrolling,
blank-page clearing, minimize, Stop, Thai caption presence, matching source masks,
and minimum 12 DIP type:

| Native scenario | First caption | Complete view | OCR |
| --- | ---: | ---: | ---: |
| Eight-region fresh process | 7.88 s | 9.33 s | 5.41 s |
| Eight-region scroll, captions retained | Already visible | 1.17 s | 0.52 s |
| Eight-region restore after blank view | 1.20 s | — | 0.74 s |
| 29-region fresh process | 10.07 s | 19.26 s | 8.02 s |
| 19-column contents page | 2.33 s | 6.38 s | 1.79 s |
| New 20-region dense page | 2.42 s | 7.20 s | 1.56 s |
| Cached 29-region revisit | 2.39 s | 2.93 s | 1.95 s |

These are fresh-process/warm-filesystem and warm-session measurements, not reboot
cold tests. The prior published Release measured 3.16 seconds for the fixture
scroll, 4.63/9.83 seconds for a new dense page's first/complete captions, and 4.89
seconds for the cached dense revisit. The goal remains active: cold startup and
new dense-page completion still miss the desired reading speed. The next measured
bottleneck is serial translation of independent regions; a maintained local runtime
with native request batching is being investigated before changing that path.

The combined build was published to `artifacts/app` and reopened as `Translumo Local`
for manual checking. The process has a responding main window. Native fixture
screenshots were inspected; live Comet foreground verification remains unavailable
through the current computer-use bridge.

The next bounded experiment is upstream `llama-server` with two, then four parallel
slots and the existing Q8 weights. Its native continuous batching can process
independent region requests together; the installed Python server serializes them.
This needs a pinned official Windows CUDA archive and matching runtime DLLs,
same-prompt output comparison, first-caption/total timing, and peak GPU/private
memory alongside FP16 OCR. No server assets were downloaded or production protocol
changed. Preserve per-region progress and explicit Stop cleanup if the experiment
justifies replacing the serial worker. See the upstream
[server documentation](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
and [batch scheduler notes](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README-dev.md).

### Native batching qualification and cold OCR isolation

The scratch native trial used official Windows CUDA 12.4 release `b11146`, commit
`7fe450e19305b828c199d602c23a8337aaa1f03b`. Both server and runtime archives matched
their published SHA256 digests; provenance and complete outputs are saved under
`.cache/native-llama-server/`. A single native slot took 8.24 seconds for the dense
29-region page. Four slots took 3.85 seconds with all requests queued, or 4.19 seconds
with at most four requests active. First completion in the latter was 711 ms.
The four-slot configuration added about 362 MiB of GPU allocation over one slot.

Batching also changed the Japanese phrase meaning "father to child" into Thai
meaning "father to student." The native and Python prompts had identical rendered
token IDs, and their effective sampler settings, output limits, and cache settings
matched. Disabling flash attention did not resolve the error and slowed dense
translation to 10.63 seconds. Production retains the current serial runtime;
the faster candidate remains available for a user-selected manual speed trial.
The user has been asked which tradeoff they prefer. There is no native production
integration yet.

A native OCR-only harness then removed translator loading while retaining the
production .NET image/pipe path. Frame 8 took 7.21 seconds on its first request and
1.50 seconds warm; padded input hashing added only 4–6 ms. Cold stages included
1.06 seconds before Python entry, 0.60 seconds of imports, 0.18 seconds of detector
initialization, 0.44 seconds of detection, 2.90 seconds of recognizer initialization,
and 1.87 seconds of recognition. Consequently concurrent Q8 startup does not explain
most of the cold delay. Warm transport and decoding alone accounted for about
414 ms, which warrants a bounded lossless-codec comparison.

Changing CUDA OCR intra-op threads from four to one was not adopted. All 29
transcripts stayed exact, but successive first-request timings of 7.21, 5.49, and
3.06 seconds tracked progressive filesystem/driver warming rather than the thread
setting. Warm times of 1.45–1.62 seconds did not establish a stable gain. The latest
isolated checks live in `.cache/verification/native-ocr-only/`.

The existing CUDA-to-CPU translation retry now preserves the caller's output-token
limit. A fake-engine regression verifies that an explicit limit of 137 reaches the
CPU retry; no model or desktop test is needed for this parameter-forwarding fix.

Further bounded candidates were measured and left out of production:

- The same official `b11146` Vulkan build was verified by SHA256 and explicitly
  selected the RTX 3060. Even one slot changed "father to child" into "father to
  student," so the remaining corpus and concurrency trials stopped at that gate.
  Readiness took 7.68 seconds and the isolated first completion took 3.27 seconds.
  Its smaller download did not justify the translation regression.
- Locally optimized ORT-format FP16 OCR graphs were compared with ONNX in fresh
  processes in ONNX/ORT/ORT/ONNX order. Mean initialization was 921/951 ms and
  session creation 734/758 ms. Texts matched between formats, but there was no
  stable speed benefit; the ORT assets were also 214 KB larger and coupled to
  runtime/provider/hardware settings. Original ONNX assets remain in use.
- A production .NET 8 image-pipe comparison found PNG24 preserved RGB, including
  synthetic partial/zero-alpha pixels. It saved only about 23 ms (380.5 to
  357.5 ms) while increasing the request size by 32% and managed allocations from
  113 to 212 MB. PNG32 remains in use. BMP was slower and TIFF offered no gain.

Scratch reports remain under `.cache/native-llama-server/` and
`.cache/verification/`; these are measurements, not runtime dependencies.

Startup tracing confirmed that Q8 loading already overlaps OCR: saved timestamps
show about 6.7 seconds of overlap, with Q8 ready before OCR returns text. Starting
workers earlier would hold model memory while the app is idle. Progressive OCR
could expose the first eight regions sooner, but the measured warm recognition
gap is about 0.69 seconds, and safely streaming partial results would require a
new response/cancellation contract. Both changes are deferred while the existing
PNG request preparation is moved off the UI thread.

The partial-caption dispatcher callbacks now recheck page version and cancellation
when they execute; layout rollback does the same. A deterministic `layout-hold`
regression queues cancellation before the pending caption callback. It failed on
the old code when the canceled caption became visible and passes with the guard.
The full Release layout check also passes progressive rendering and scroll reuse.

PNG encoding and JSON preparation now run on the thread pool under the existing
OCR request gate. Native `ReadOnlyMemory<byte>` serialization avoids both the raw
PNG copy and the intermediate base64 string; it also avoids escaping base64 `+`
characters. On the same PNG bytes, the final typed comparison reduced the JSON
request from 4.639 to 4.227 MB and median serialization plus pipe/decode from
145.1 to 116.8 ms. This does not include PNG encoding time. The main benefit is
that encoding no longer stalls the UI.

The existing Release `unreadable` check now exercises the actual OCR bridge with
a fake local worker, verifies every decoded pixel of a 3840 × 2088 image and known
text containing quotes and `+`, and verifies that the caller regains the UI before
the request reaches the worker. Cancellation reply draining, worker PID reuse,
and explicit disposal also pass. No model or protocol replacement was required.

The Release build published successfully with no warnings or errors and was reopened
as `Translumo Local`; the replacement process has a responding main window. The
overall speed goal remains active, with cold startup and dense-page translation
still above the desired reading latency.

### Authorized native speed build

The user selected the faster runtime for manual testing and asked to continue
optimizing the other delays. Implement the measured four-slot CUDA runtime with
the existing Q8 weights and custom prompt. The known father-to-student error is
accepted for this speed trial; this does not establish human translation quality.

The official default prompt was separately rejected: controlled Python runs were
no faster, native dense translation was slightly slower, and the outputs reversed
a negation, confused older and younger relatives, and invented a shogun. Full
comparisons are in `.cache/native-llama-server/default-prompt/`. Slot context and
RoPE settings matched, so they do not explain the native batching difference.

Implementation is split among the requested GPT-5.6-sol agents:

1. Provision SHA256-verified official `b11146` Windows CUDA binaries during setup,
   retaining provenance and licenses. Runtime performs no downloads.
2. Let the Python worker own an authenticated, hidden child bound to `127.0.0.1`.
   Keep the current prompt, limits, cache, validation, and legacy CPU fallback.
   Independent native requests run at a maximum concurrency of four.
3. Add optional indexed progress replies, followed by one final response. Keep
   final-only callers compatible. The C# bridge drains abandoned requests and
   validates partial/final agreement; obsolete results cannot render.
4. Send bounded manga batches using the worker's reported parallelism. Render
   each completed caption immediately, with the existing render coalescing, while
   leaving the source page live. Stop and EOF must release the child process.
5. Verify protocol, cancellation, cache/duplicates, CPU fallback, child cleanup,
   and native page timing with OCR resident before publishing the build.

Progressive OCR is deferred until the native translation build is measured. The
separate DLL-loading experiment omitted the unused 244 MB cuDNN advanced library
with exact OCR, but mixed end-to-end timing did not justify a production change.

The pre-integration coexistence check kept production FP16 OCR resident while the
native four-slot translator ran. Global GPU use reached 5,186 MiB, including a
1,536 MiB desktop baseline. Dense OCR took 873 ms and stayed exact before/after
the translator loaded. Translation took 1.21 seconds for eight regions and
4.18 seconds for 29; first results arrived at 419 and 801 ms. This validates
memory coexistence and translation throughput, not the final app's page latency.
Native outputs vary with batch scheduling: the known father/child phrase happened
to be correct in this run, while names and wording varied. The earlier error is
still a known limitation. Results are in `.cache/verification/native-ocr-resident/`.

The native worker, progress bridge, fallback, token limits, and child lifecycle
checks passed. The real worker explicitly reported four slots; twelve language
directions and the Japanese work/hospital phrase passed. The WPF check verified
eight Thai captions, live scrolling, a blank page, minimize, and Stop. Review also
caught and fixed a WPF thread-affinity bug in progress callbacks and an in-flight
first-page scroll that could otherwise stop scheduling recognition.

The first integration measurement exposed extra app overhead. With FP16 OCR
resident, the actual worker took 4.683 seconds for one rolling 29-region request
and 5.337 seconds for eight bounded requests of four. Cached bounded requests
took only 1.5 ms. The app measured 10.104 seconds for translation plus progressive
layout, and 1.020 seconds for cached translations plus layout. Keep the bounded
requests for navigation responsiveness; remove unnecessary redraws before
considering a larger protocol change. The raw measurements are in
`.cache/verification/native-worker-waves/`.

Cold OCR remains variable: the initial author-page app run took 12.543 seconds,
then one instrumented run took 6.648 seconds. That run spent 1.23 seconds importing,
1.45 seconds loading CUDA libraries, 1.20 seconds creating ORT sessions, and
1.72 seconds recognizing the first page; pipe and image decoding took 58 ms.
The 12.543-second outlier has not been attributed conclusively. Avoid presenting
warm measurements as a cold-start guarantee. Detailed stages are recorded in
`.cache/verification/performance/native-ocr-pipeline.jsonl`.

The manual speed build is published with native four-slot inference, indexed
caption progress, and redraw coalescing across batch boundaries. Focused bridge
and layout checks pass, including worker cleanup, dispatcher affinity, and
scrolling during the first translation. The Release publish completed without
warnings or errors.

Final native desktop timing explicitly asserted four-slot capability and verified
that each fixture had painted the expected pixels before starting its timer:

| View | First caption | Complete | OCR | Translation including progressive layout |
| --- | ---: | ---: | ---: | ---: |
| Fresh worker, 29 regions | 10.478 s | 16.980 s | 7.991 s | 8.359 s |
| New view, 19 regions | 2.295 s | 9.025 s | 1.386 s | 7.204 s |
| Cached view, 29 regions | 2.058 s | 2.748 s | 1.606 s | 0.595 s |

Cached translation/layout improved from 1.020 to 0.595 seconds after removing
forced intermediate batch redraws. The earlier multi-page scratch harness could
capture the previous DWM image during a page transition, so its warm-page
figures are not a reliable direct baseline. Final results and exact captures
are under `.cache/verification/dense-performance/`. All captions contained Thai,
had corresponding masks, and stayed at least 12 DIP in that check.

The overall speed goal remains active. The worker-level speedup is real, but
end-to-end latency still includes cold OCR and substantial capture/dispatcher/
progressive-layout work. Further optimization should time those boundaries in
the app before changing the pipeline again. Human translation quality remains
deferred by the user's choice for this manual trial.

### Remaining UI and image-transfer delays

Screen capture plus fingerprinting now runs on the thread pool. The dispatcher
still reads window bounds and owns session state and rendering. The same 4K
capture cost roughly 87–96 ms after warming; moving it reduced the measured
maximum 20 ms UI heartbeat interval from 173 to 22 ms. A blocked-capture
regression verifies that the dispatcher stays available and Stop cannot publish
a late caption. Hashing itself costs about 20 ms; avoiding its buffer copy saved
only about 3 ms, so that additional change was omitted.

A caption-only trace attributed 1.6 seconds of the translation path to its first
synchronous render. Fresh-process render profiling found first-use initialization
of the hidden overlay handle, Windows Thai segmentation, and font shaping.
Preparing these with the generic text `ภาษาไทย` reduced isolated rendering to
110–124 ms in a controlled comparison. Segmentation and font measurement use one background
STA with local objects; the empty, hidden handle stays on the UI dispatcher.
Preparation starts after the translator's empty request starts model loading.
These isolated figures exclude preparation and do not establish total page time.
An actual model-overlap check finished layout preparation at 398 ms and model
initialization at 6,436 ms, preserving focus and an empty hidden overlay. Its
first render took 684 ms with the model resident. The background preparation
overlap is verified; a fixed first-render latency is not guaranteed.

A .NET 8 transport benchmark compared the actual PNG/base64 request with a named
Windows memory mapping containing raw BGRA pixels. Median transfer time on the
3840 × 2088 capture fell from 351 to 47 ms with the safe production row-copy path;
managed allocations fell from 16.9 MB to about 2.6 KB. The mapping uses 32.1 MB until its reply completes.
Every decoded RGB pixel matched, including synthetic transparent pixels, and
quoted Japanese known-region text survived unchanged. Production integration
retains the map through canceled-response draining and closes it on worker
failure or disposal. The handoff to the response reader is atomic with Dispose;
the reader releases its mapping without waiting for the UI dispatcher. Focused
checks cover exact pixels, invalid metadata, legacy PNG input, same-worker reuse,
and mapping release after completion, cancellation, and Stop. The benchmark is in
`.cache/verification/transport-codecs/results-mmap.json`.

The combined desktop check passed with the uninstrumented production worker:

| View | First caption | Complete | OCR | Translation including progressive layout |
| --- | ---: | ---: | ---: | ---: |
| Fresh workers, 29 regions | 9.640 s | 15.280 s | 7.703 s | 7.057 s |
| New view, 19 regions | 1.938 s | 4.872 s | 1.120 s | 3.212 s |
| Cached view, 29 regions | 1.905 s | 2.415 s | 1.562 s | 0.437 s |

All views explicitly used four native slots, retained exact underlying source
pixels, displayed Thai captions with corresponding masks at least 12 DIP, and
cleared captions on Stop. The preceding traced run measured 5.057 seconds for
the new view and 2.196 seconds for the cached view. These are local fixture
measurements, not a guarantee for live Comet or every manga page.

An earlier attempt took 21.837 seconds on the first view and aborted its next
page because DWM still exposed the old fixture pixels. The harness now waits
until the requested pixels actually appear instead of assuming a fixed paint
delay. Its failed cold result remains recorded; startup is still variable.
The traced run measured 8.873 seconds for the empty translator warmup request,
longer than OCR, so that remaining wait was counted in translation time. A sample
during that run showed only 157 MiB of free system RAM out of 13.85 GiB and
5,470 MiB of GPU memory in use. Memory pressure is a candidate contributor, not
a proven explanation for every slow run.

No duplicate full model was found: native and legacy translation are mutually
exclusive, while OCR loads one detector and three required FP16 sessions. A
future bounded memory experiment can disable the recognizer's CPU arena and
compare exact transcripts, resident memory, and latency. No memory setting was
changed without that comparison. Cold startup and the one-second reading goal
remain unfinished; human translation quality polishing remains deferred.

The Release build was published and reopened successfully as `Translumo Local`.
Its main window responds, and the verification model workers have exited. The
published build includes background capture, Thai layout preparation, and the
memory mapped image transport described above.

### Next latency experiments

The recognizer CPU-arena A/B was rejected. Four fresh processes in AB/BA order
kept native translation resident and recognized the author eight-region and
dense 29-region fixtures exactly. Disabling the arena changed mean peak private
bytes from 2,996.7 to 2,998.4 MiB and peak working set from 978.7 to 987.5 MiB,
with no stable latency improvement. One default run initialized in 5.00 seconds
with only 152 MiB free RAM; the reversed pair initialized in 1.16/1.10 seconds.
Private bytes include GPU-backed allocations and do not equal physical RAM.
The option remains at its default. Results are in
`.cache/verification/ocr-cpu-arena/results.json`; the independent CPU arena and
memory-pattern controls are documented in the
[ONNX Runtime Python API](https://onnxruntime.ai/docs/api/python/api_summary.html#onnxruntime.SessionOptions.enable_cpu_mem_arena).

A model-free progressive OCR contract check demonstrates a caption before the
final OCR response, with batches capped at four, canceled replies drained, old
coordinates suppressed during scrolling, and giant-column fallback kept atomic.
The proposed implementation sends detected geometry once, then indexed recognized
text after each existing eight-crop recognition batch, followed by the unchanged
final result. An awaited consumer and the existing pipe provide backpressure;
no new channel or queue is needed. Real OCR/translation concurrency and page
latency still require verification before publication.

### Windows model loading

The pinned native runtime's Windows mapping keeps the GGUF mapped after GPU
upload: its fragment-unmap implementation is empty. Four OCR-resident runs in
default/none/none/default order compared direct loading (`--load-mode none`)
with the default mapping. Ready working set fell from 1,858–1,899 to 622 MiB;
available physical RAM improved by 677–1,189 MiB. Startup improved in both
orderings, by 0.75 and 4.55 seconds. Dense translation averaged 4.542 seconds
with direct loading versus 4.723 seconds with mapping, and OCR remained exact.
The tradeoff is about 248 MiB more private commit for the retained CUDA host
buffer. The same Q8 weights, 33 GPU layers, four slots, context, batching, flash
attention, and GPU allocation were verified. Direct loading is now selected.
Results are in `.cache/verification/native-startup-attribution/results-load-mode.json`;
the cause is visible in the pinned [Windows mapper](https://github.com/ggml-org/llama.cpp/blob/7fe450e19305b828c199d602c23a8337aaa1f03b/src/llama-mmap.cpp#L577)
and [model loader](https://github.com/ggml-org/llama.cpp/blob/7fe450e19305b828c199d602c23a8337aaa1f03b/src/llama-model-loader.cpp).

Disabling automatic fit was rejected: the measured fit pass cost only 280 ms,
changed no parameters, and the trial's dense translation was slower. Keeping it
retains the upstream memory guard. Prompt-prefix caching and smaller quantizations
remain unqualified; neither was added to this build.

### Progressive OCR acceptance

The app now consumes indexed OCR chunks for a fresh Japanese manga overwrite
frame. Translation starts after the first eight recognized crops; each completed
caption renders immediately, in translation batches of at most four. The geometry
and backing frame stay stable across chunks. A scroll suppresses progress at the
old coordinates and the final result uses the existing alignment path. Giant
column candidates wait for the final Windows replacement geometry. Source pixels
and interaction remain live throughout.

The real worker produced exactly the same final geometry, text, and reuse count
with and without progress on the author eight-region and dense 29-region fixtures.
The dense fixture emitted its first recognized chunk at 882 ms and final OCR at
1,389 ms, with chunks of 8/8/8/5. This worker-only check excludes translation and
rendering. Results are in `.cache/verification/ocr-progress-real/results.json`.

The combined desktop run used the production worker and direct model loading:

| View | First caption | Complete | Processing including OCR, translation, and progressive layout |
| --- | ---: | ---: | ---: |
| Fresh workers, 29 regions | 9.709 s | 15.088 s | 13.915 s |
| New view, 19 regions | 1.600 s | 4.596 s | 4.176 s |
| Cached view, 29 regions | 1.110 s | 2.194 s | 1.585 s |

The preceding build measured 1.938/4.872 seconds for the new view and
1.905/2.415 seconds for the cached view. The cold first-caption result did not
improve in this integrated run; cold OCR remains a bottleneck despite the
independently measured native-loading improvement. These are fixture measurements,
not a live Comet reading-quality assessment or a one-second guarantee.

All views retained four native slots, exact underlying pixels, Thai captions
with matching masks at least 12 DIP, and zero captions after Stop. Focused checks
also passed for malformed protocol events, bounds overflow, cancellation after
a callback failure followed by same-worker reuse, memory-map lifetime, dispatcher
ownership, and scrolling between OCR chunks. First-progress bitmap access stays
on the dispatcher so cancellation cannot dispose the image during a background
read. The UI reports one processing duration because OCR progress now overlaps
translation; callback time is counted once.

The Release publish passed and the updated `Translumo Local` window was reopened
and verified responding. All benchmark model workers exited. This build includes
progressive OCR and direct native model loading; manual reading-quality feedback
and further cold-start optimization remain outstanding.

### Remaining cold OCR attribution

A fresh production OCR worker with the direct-load native translator already
resident reproduced 6.333 seconds through the first eight recognized crops,
with exact text. The measured stages were 569 ms for imports, 173 ms for detector
initialization, 38 ms for image decoding, 268 ms for detection, 52 ms for the
recognizer module import, 3,072 ms for recognizer initialization, and 2,157 ms
for its first batch. CUDA DLL preloading alone took 1,416 ms; the three sessions
took 766/666/220 ms. Host available RAM was 840 MiB with native translation
resident. This confirms recognizer setup and first inference dominate the cold
path; results are in `.cache/verification/ocr-startup-current/result-current.json`.

The warm UI gap is accounted for by capture, the 160 ms page-settling gate,
transport, the first progress snapshot/layout, and uncached translation.
Removing the first-visible settling check could save about 190 ms, but can
start stale OCR on a partially painted window and delay the correct page behind
that work. It remains unchanged. Cached ORT graphs and reduced intra-op threads
also remain rejected on their earlier measurements.

Selective CUDA/core-cuDNN preloading was retested with native Q8 resident in
full/selective/selective/full order. Initialization plus the author eight-crop
batch took 3,574/1,925/1,624/1,817 ms. The final full-load arm erased the apparent
gain, indicating common library/file-cache warming. All author and dense texts
stayed exact. The selective path left the 244 MB `cudnn_adv` file unmapped, but
its final working set/private commit (977–984/2,980–3,005 MiB) was effectively
the same as the final full-load arm (985/2,989 MiB). File size did not represent
resident memory savings. This candidate remains rejected; results are in
`.cache/verification/performance/manga-ocr-preload-resident-ab.json`.

Recognizing four crops first, then batches of eight, was also rejected after a
native-resident AB/BA comparison. All 37 author/dense texts stayed exact. Dense
first recognized text improved by only 65 ms and first native caption by an
inconsistent 35 ms, while final OCR regressed by 656 ms (41%). Complete-page time
changed by less than 1%. The reversed, warmed author baseline also eliminated
the apparent cold gain. The current eight-crop chunks remain; results are in
`.cache/verification/ocr-first4/results.json`.

### Encoder patch projection without convolution engines

The FP16 encoder has one non-overlapping 16×16 patch projection. A scratch graph
rewrites it as patch reshape/transpose and one biased `Gemm`, preserving the
coefficient bytes, patch order, four-beam generation, and the remaining encoder.
Combined with loading the core cuDNN library on demand, it avoids the convolution
engine, heuristic, and `adv` DLLs through complete OCR. Base cuDNN remains required
by the CUDA provider. The [`Gemm` operation](https://onnx.ai/onnx/operators/onnx__Gemm.html)
includes the bias in the matrix operation; a separate FP16 addition would introduce
another rounding step.

With Q8 resident, production/candidate/candidate/production measurements showed
about 148 MiB less OCR working set and 175 MiB less private commit. Cache-warm
setup plus the first author batch improved by 117–149 ms (6–8%); dense throughput
was neutral. The first production arm's additional two seconds were file-cache
warming and are not credited to this change. Results are in
`.cache/verification/performance/manga-ocr-patch-gemm-ab.json`.

The full saved corpus matched current FP16 on 385/388 crops. Two differences
occurred in the known invalid page 4 giant unions. The remaining page 10 crop
corrected the closing parenthesis in `(死去)` and `橋` to the visibly printed `橘`;
the rest of that imperfect transcription was unchanged. This is not a general
translation-quality claim. The 388-crop run took 17.994 versus 17.651 seconds
sequentially, consistent with the neutral dense AB/BA result. The full transcripts,
boxes, and mismatch images are in
`.cache/verification/performance/manga-ocr-patch-gemm-corpus-full.json`.

The production OCR boundary verified the page 4 exception: original and optimized
encoders both returned the same 19 Windows OCR columns, `HasOcrInputProof=false`,
and zero progressive callbacks. Final geometry/text SHA256 was
`0CA2AB037BA4CC6D02062C26B46E89C78245866D2DFF55B17124432A8538693C`.
The check is in `.cache/verification/encoder-fallback-check/`; its sequential
timings are not a controlled speed comparison.

Setup now atomically converts the existing FP16 encoder in place, guarded by
source/output SHA256 checks. It handles cached and fresh exports without adding
another model variant. A fresh encoder export reproduced the pinned source hash,
and repeated optimization was byte idempotent. Production CUDA recognized all
29 dense-page crops exactly twice with sockets blocked and no Torch/Transformers
runtime imports. The loader retains automatic FP32 CPU fallback.

The first integrated run exposed a startup ordering bug: OCR could finish before
the empty translator warmup reply, so the first batch used the provisional
parallelism of one. The following batches used four, changing grouping and
losing the initial batching benefit. The frame now awaits the existing warmup
task before choosing its first nonempty batch. No extra warmup request is sent.
A delayed-readiness regression proves no early request and a first batch of four;
version, cancellation, protocol-drain, and scroll checks remain green.

The final desktop check used the optimized encoder and readiness fix:

| View | First caption | Complete |
| --- | ---: | ---: |
| Fresh workers, 29 regions | 9.278 s | 14.546 s |
| New view, 19 regions | 1.655 s | 4.737 s |
| Cached view, 29 regions | 1.140 s | 2.383 s |

All views used four native slots, preserved exact source pixels, retained Thai
captions and masks at least 12 DIP, and cleared on Stop. The preceding run before
the readiness fix took 7.549/19.441 seconds on the fresh view; it is preserved as
`patch-gemm-before-readiness-results.json`. Cold startup remains variable and
unresolved. The reliable gains from this iteration are reduced OCR memory and
correct initial batch scheduling; the end-to-end numbers do not establish a
large or consistent cold-start speedup.

The combined Release build was published successfully and reopened as a
responding `Translumo Local` main window. Verification workers have exited.
The overall speed goal remains active; human translation-quality polishing
remains deferred and live reading feedback is still pending.

### Capture hashing and a smaller-context trial

Whole-frame SHA256 now consumes one signed bitmap stride at a time through
`IncrementalHash`. At 3840 × 2088, the pooled buffer falls from 32 MiB to 16 KiB.
Six alternating rounds of 30 hashes measured median 19.423 ms with the whole
buffer and 17.808 ms with rows. This is an isolated hashing measurement, not an
end-to-end speed claim. The focused layout check preserves the prior positive
stride digest, detects changed pixels, and checks equivalent negative-stride
logical rows. Evidence: `.cache/verification/capture-loop-pressure/results.json`.

A four-slot native context trial used 2048/1280/1280/2048 tokens per slot with
the same Q8 weights and generation settings. CUDA KV allocation fell from 512
to 320 MiB, whole GPU use fell about 189–192 MiB, and native private commit fell
about 192 MiB; working set stayed around 622 MiB. Translation of the 37 saved
regions took 5.748/5.854 seconds at 2048 and 5.404/5.499 at 1280. All 24 current
chat-templated language/prompt boundary cases fit: at most 548 prompt tokens plus
512 output tokens. These limits follow the pinned runtime's
[per-slot context option](https://github.com/ggml-org/llama.cpp/blob/7fe450e19305b828c199d602c23a8337aaa1f03b/common/arg.cpp#L1542-L1549).

The smaller context was not adopted. One candidate output changed the same-mother
sentence's negation and action. A similar negation error already exists in a
prior 2048-context native trace, so this does not establish that reducing context
caused the error. The trial did not establish stable meaning for the proposed
change. Production remains at 2048. The original ABBA harness also had a late-bound
generator bug that repeated the final author crop eight times; translation inputs
and dense OCR were unaffected. Corrected independent OCR checks matched all 37
regions with each context. Both the original results and corrected gate remain in
`.cache/verification/native-context-abba/`.

The first detailed full-app resource trace is excluded from timing comparisons:
its process-enumerating sampler cost about 65 ms every 50 ms and resumed on the
WPF dispatcher. It also observed nearly exhausted host RAM, but the fixture,
sampling, and pixel assertions contributed memory pressure. Its 10.289/21.781
second first/complete times are not a production performance baseline. OCR stage
logs placed most startup work in CUDA DLL loading, session setup, and first
recognition; native translation was ready earlier. A corrected sampler runs in
the background with cached process handles and deferred output.

The corrected fresh-worker trace measured 8.872 seconds to the first caption and
17.450 seconds to all 29 captions. Cached memory samples took median 0.072 ms;
process discovery ran once per second off the UI thread. CUDA preload took 1.858
seconds, the three OCR sessions 2.395 seconds, and the first eight crops 1.382
seconds. Native translation was ready at 4.939 seconds, before OCR's first chunk.
Available host RAM fell from 1,272 MiB to a minimum 4.1 MiB. The test harness itself
peaked at 546 MiB working set, versus about 92 MiB for the idle app, so this is
evidence of pressure during the combined fixture workload, not a measurement of
the user's live Comet session. Cold startup remains variable and unresolved.

This run verified four native slots, 29 Thai captions and matching masks at least
12 DIP, and clearing on Stop. It did not repeat the earlier post-overlay source
pixel assertion; the changed hashing path has byte-equivalence coverage and the
overlay code is unchanged. Results are in `.cache/verification/startup-resource/`;
the intrusive earlier trace is retained in its `perturbed-ui-sampler/` directory.
The row-hashing change was published, and the reopened `Translumo Local` window
was verified responding. All benchmark model workers exited.

### Larger translation batches and shared OCR resources

Sending eight texts per native request was rejected after a fresh-worker,
OCR-resident 4/8/8/4 comparison. Complete-page time improved only 221 ms (4.3%) in
one ordering and regressed 156 ms (2.9%) in the other. After canceling at the first
caption, the next page's first caption moved from 378 to 1,695 ms: an additional
1.317 seconds of obsolete work. Both eight-item runs also rendered a proper name
as a literal “light king.” Current four-item requests remain. Results and gate
evaluation are in `.cache/verification/rolling-schedule/native_batch8_*.json`.

Two memory candidates remain under qualification, without production changes:

- Load OCR's CUDA trio from the existing native runtime directory so both workers
  map the same files, retaining the current cuFFT and cuDNN. All 91 directly
  imported CUDA-trio symbols were present, and the four-run baseline/shared/shared/
  baseline check preserved all 37 OCR results. Same-file resident-page intersection
  and the 388-crop corpus still need qualification; timing and available-RAM changes
  were too noisy for a startup claim. ONNX Runtime documents
  [compatibility within CUDA 12 and the required cuDNN major version](https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html#requirements).
- Merge the two FP16 decoder graphs into one conditional session. The scratch graph
  deduplicates 53,945,856 bytes (51.45 MiB) of identical initializers, passes the
  ONNX checker, and builds deterministically. Runtime speed, memory, and output are
  unverified; a conditional branch on every token could slow recognition.

The published app subsequently began a live session with its own model workers.
GPU benchmarks were suspended to avoid competing with that session. Scratch work
is preserved; neither candidate is advertised or enabled in the app.

Both resource candidates were subsequently rejected after the user authorized
benchmarking. CUDA sharing preserved all 388 transcripts, but the same-file,
same-RVA concurrently resident shared-page intersection was only 2.3 MiB after
recognition: Windows had trimmed the idle native server's CUDA image. The earlier
86 MiB estimate was transient, not sustained savings. The merged decoder also
preserved all 388 transcripts and four-beam generation, but its repeat comparison
showed a 29.6% dense-recognition slowdown and roughly 265 MiB more GPU memory in
one comparable warm pair. The initial apparent memory saving did not reproduce.
Production retains the original loader and separate decoder sessions. Evidence:
`.cache/verification/cuda-runtime-sharing/qualification-results.json` and
`.cache/verification/performance/manga-ocr-merged-qualification.json`.

### Native page animation and scrolling

Manual feedback says captions now arrive in an acceptable time, but scrolling and
page-turn animations still feel worse than the native reader. Smooth source motion
is therefore the immediate priority.

A separate-process 4K animated fixture isolated two causes. Static subtitles alone
did not harm source timing. Screen capture produced occasional source gaps up to
108 ms. Alignment on the translator dispatcher produced heartbeat p95/max of
80/136 ms, versus 20/24 ms when that same work ran in the background. Captions
moved only every 250–290 ms, making retained masks visibly lag source movement.
The existing 160 ms gate also measured time from the first changed frame, rather
than a quiet period, allowing OCR to start during an ongoing page animation.
Evidence: `.cache/verification/animation-smoothness/`.

Reusing a bitmap/DC reduced 4K capture from median 64.27 to 55.86 ms; direct
persistent DIB/BitBlt reached 44.80 ms. Pixel hashes and resource cleanup passed,
but full display readback remains the dominant capture cost. No capture backend
change is justified solely as a complete smoothness fix.

Direct reads from locked bitmaps preserved the scroll estimator's results while
avoiding its two complete image copies. The 4K shift case fell from 35.51 to
11.08 ms and from 61.44 to 0.27 MiB allocated; an unrelated page comparison fell
from 22.60 to 6.43 ms. The implementation now reads BGRA pixels directly from each
locked bitmap, retaining the same luminance weights, sampling, and acceptance
thresholds. The Release scroll check passes for signed negative stride, a 4K
vertical shift, and rejection of an unrelated 4K page.

Scroll analysis now runs off the Dispatcher with cancellation, version, frozen
frame identity, and region bounds checked before applying its result. Restoring
the frozen image writes directly into the locked bitmap instead of allocating a
second full-size byte array. Captions hide during text motion and return after two
quiet captures and at least 160 ms; background-only animation preserves them. The
quiet gate now resets for every changed text frame, preventing OCR from starting
mid-animation. Pending progress and final results cannot reshow stale captions.

The Release layout-hold check and production build pass. The check covers 800 ms
of continuous changes without starting OCR, cached scroll restoration and reuse,
progress arriving during motion, unrelated animation, and Stop during background
analysis. Independent code review found no blocking ownership or stale callback
issue. A separate-process animation retime remains necessary before making a
source-smoothness claim; the GDI capture backend is unchanged.

The first completed live retime failed motion acceptance despite the focused
checks: captions hid after 189 ms, then briefly reappeared during continuous
oscillation, and one OCR call started during motion. Final captions arrived after
400 ms through a fresh scan rather than cached restoration. A repeated sampled
position can pass a time-only quiet gate when one capture interval already
exceeds 160 ms. This candidate is not published; the failed measurement is saved
as `.cache/verification/animation-smoothness/motion-single-sample-failure.json`.

The follow-up fix requires two quiet observations, counted before scroll alignment
can clear the motion flag. This also prevents an analysis taking longer than
160 ms from starting OCR in its own moving-frame iteration. The final OCR gate
checks the current counter after pending-result invalidation. A regression case
with repeated sampled positions and delayed captures now stays hidden and starts
no OCR throughout motion, then restores after two quiet captures. The full focused
check and both Release builds pass.

The corrected separate-process retime passed motion suppression: one hide after
197 ms, zero OCR calls and no caption reappearance during the 2.5-second moving
phase. The translator Dispatcher heartbeat was 20.2 ms p95 / 27.3 ms maximum,
with no gaps above 33 ms. Source rendering was 8.0 ms p95 / 36.6 ms maximum, with
one gap above 33 ms. Full capture still produced occasional source stalls in its
isolated phase, so native-equivalent animation is not established.

Captions returned 654 ms after settling through a fresh scan. This harness uses
fake OCR and translation; that number measures capture/settling/render overhead,
not production caption latency. Cached restoration did not validate on this live
composited sequence, so the exact pixel proof correctly fell back to recognition.
The focused check separately verifies cached restoration during a pending rescan.
No alignment threshold was relaxed. Raw measurements are in
`.cache/verification/animation-smoothness/results.json`; focused commands and
results are in its `focused-check.txt`.

The Release self-contained app was published and reopened. PID 137008 at
`artifacts/app/Translumo.exe` was verified visible, not minimized, and responding
as `Translumo Local`. Benchmark helpers are closed. Manual comparison in the real
reader is pending; caption fidelity polishing remains deferred as requested.

### Remaining capture stalls and interrupted scroll reuse

A GDI preview experiment rejects downsampling as the capture-stall fix. Two full
4K arms took 48.38/51.83 ms for capture plus about 20 ms for hashing. Capturing
320- or 640-pixel-wide previews still took 43.42 ms; source frame gaps above
33 ms remained at six per preview phase versus six/seven for full capture. The
preview reduced hashing, not the dominant display readback. Synthetic pixels and
integer preview reduction matched. The initial cleanup assertion rejected a
decrease in USER handles (7 to 6); it was corrected to reject increases only,
without rerunning timing. Results: `.cache/verification/capture-preview/`.

The next bounded experiment uses DXGI Desktop Duplication with crop-sized GPU
staging and a fresh owned bitmap. Microsoft documents zero-timeout acquisition,
explicit recreation after access loss, and BGRA8 desktop pixels. It also
recommends [holding the acquired frame until the next capture](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgioutputduplication-releaseframe)
to avoid redundant OS copies between polls. Capture time alone is insufficient:
the same separate-process animation fixture must show reduced source stalls, and
overlay exclusion must return underlying pixels rather than a black rectangle.
No production capture backend has changed. The scratch prototype is limited to a
containing unrotated output; unsupported geometry must retain correct behavior
before any production integration.

The lost-cache case was also narrowed. An offscreen WPF rendering of the same
fixture at 96 DPI preserves exact pixels, every 15-pixel shift, and both padded
OCR proofs, including the final 120-pixel shift. Coordinate conversion is not the
cause. The live comparison matched only five of twelve intermediate frames; the
first failed proof clears the working translation, so it cannot be reconsidered
once motion ends. Stage telemetry is insufficient to attribute those failures
to the estimator versus nonuniform composited pixels. Evidence:
`.cache/verification/scroll-cache-diagnosis/`.

A bounded fix retains one hidden last-trusted translated frame through those
intermediate failures. After two quiet captures it must pass the existing shift,
exact region, and padded OCR proofs against the settled image before reuse.
Unverified content stays hidden, and a failed final proof falls back to OCR.
Newly revealed text must still be scanned after cached captions return. This
The production implementation and focused Release checks now pass, including a
failed intermediate capture, return to an earlier scroll position while the scan
hash is stale, padding-only glyph changes, page replacement, and Stop. The scan
hash remains the identity of the OCR scan; restoration compares actual frozen
pixels. Two sandboxed attempts failed at the first GDI capture with an invalid
handle before reaching the live phase; the elevated interactive run subsequently
passed. Cached captions returned after 659 ms, with zero OCR requests or caption
reappearance during motion. The following scan still started after restoration.
This is a model-free cache result, not production translation latency. Source
frame gaps still reached 75 ms (the same run's baseline reached 59 ms), and the
controller had one 48 ms gap during motion, so this does not establish native
animation smoothness. The raw run is preserved as
`.cache/verification/animation-smoothness/hidden-cache-gdi-results.json`.

Independent review found another hidden-caption edge case: a completed frame
accepted by the small-change tolerance retained old exact region fingerprints,
so its suppressed captions could stay hidden indefinitely. Exact region mismatch
now rejects that pending result and allows fresh OCR. The regression failed with
the three changed lines reverted and passed after restoring the fix; the full
layout-hold check and production Release build pass.

The self-contained update was published and reopened as PID 142372 at
`artifacts/app/Translumo.exe`. Its `Translumo Local` window was verified visible,
not minimized, and responding. No model worker or benchmark helper remained at
that check. The publish succeeded with the offline NuGet advisory lookup warning.

### DXGI color correctness qualification

The capture prototype initially produced overbright pixels. Per-frame diagnostics
showed that the overlay frame used BGRA8 automatic conversion, while a later
recreation produced FP16; reporting only the final format obscured the cause.
Requesting FP16 exclusively and converting with the queried SDR white scale
passed the full synthetic pixel comparison, fresh underlying pixels beneath an
excluded overlay, cached capture, crop resize, duplication recreation, and owned
resource disposal. The display's measured SDR white multiplier was 3.

At 640 by 360, acquisition took 0.56 ms, crop-copy submission 0.01 ms, and mapping,
CPU color conversion, and creating the returned bitmap took 11.94 ms. These are
small-image correctness measurements, not evidence of a 4K speed improvement.
Representative 4K animation timing and production lifecycle handling remain
required; the app continues to use its existing capture backend. Evidence:
`.cache/verification/dxgi-capture/self-check-fp16.json`.

The representative 3840 by 2088 trial showed that scalar FP16 conversion was too
slow: capture took roughly 398–406 ms, mostly CPU conversion. A 65,536-byte lookup
table built from the same conversion formula reduced capture to 44.7 ms in both
repeat arms, versus 63.6–65.2 ms for GDI. Both backends then achieved about 30
captures per three-second phase. The independent animated source had zero gaps
above 33 ms with DXGI (maximum 8.4–9.5 ms), versus 10/12 gaps with GDI (maximum
36.1–44.2 ms). All 8,017,920 synthetic pixels, including the excluded overlay's
underlay, matched exactly after a forced source present.

This establishes a useful capture candidate, not a shipped backend. The original
static first acquisition returned an all-zero frame; forcing a new source present
made the same full-image proof pass. Pointer-only first acquisitions must not seed
the cache. The subsequent unforced check skipped one resource with
`LastPresentTime=0`, accepted a desktop frame with `AccumulatedFrames=1`, and
matched every pixel. This verifies the first-frame correction independently of
the forced repaint. Production still needs bounded access-loss recovery and
correct fallback for unsupported display geometry. Raw results are preserved
separately as `scalar-results.json`, `scalar-forced-present-proof.json`,
`lut-results.json`, and `lut-unnudged-proof.json` in
`.cache/verification/dxgi-4k-animation/`. Reported memory is whole-process context,
not an isolated backend delta; disposal cleared owned COM pointers, while total
GDI/USER counts included first-use WPF/window initialization.

The benchmark milestone is complete. Both DXGI arms sustained the requested
cadence, and the independent source/controller timing gates passed. All scratch
helpers exited; the published scroll-cache update remains visible and responding
as PID 142372. DXGI is ready for production integration work and is not enabled
in that published build.

Production integration is now in progress. One disposable capture session will
own the native device for a reading session; capture remains on the existing
background task, and the static GDI helper continues to serve one-off captures.
Same-output crop changes reuse the device. Unsupported geometry and native
environment failures fall back to GDI, with bounded retries. The native path must
bound first-frame waits and access-loss recovery, re-enumerate changed outputs,
refresh the actual SDR white level, and release every partial allocation.
Verification will exercise production pixels, crop changes, cancellation,
fallback, disposal, and the existing live 4K motion fixture before publication.

The production path now passes those checks. The capture session owns one native
device, reuses same-output crops, and retries expected failures at most once per
10 seconds for the same target, with immediate retry when the target output
changes. This bounded retry also permits recovery from same-size display-setting
changes without retaining a permanent failure flag. The LUT follows the queried
white level on every capture. First-frame waits poll cancellation every 25 ms;
access loss permits one full adapter/output re-enumeration per capture.

`tests/capture/CaptureCheck.csproj` passes exact initial, static, changed, resized,
and moved pixels; it confirms the production session actually owns the native
backend. Stop of a continuous full-output capture loop took 46.96 ms. Forced
failure state exercised GDI cooldown and the real retry-expiry branch. Disposal
and rejected geometry also passed. Actual hardware access loss was not induced;
the recovery path received independent code review. The full layout-hold check
passes with exactly one capture session created and disposed per reading session.

The integrated 4K live loop hid captions after 159 ms, started no OCR and never
reshowed captions during motion, then restored cached captions 634 ms after
settling. During motion the source's maximum frame gap was 20.33 ms and the
controller's was 25.52 ms, with no gaps over 33 ms. This remains a synthetic,
model-free timing check; settling return time is not translation latency. The
final static restoration had one 50 ms controller gap. Evidence:
`.cache/verification/animation-smoothness/production-dxgi-results.json`.

The real-model manga acceptance check also passed all eight Thai captions,
uncovered/interactable cold startup, cached scrolling, no-text, minimize, and
Stop. Cold first caption was 8.523 seconds, complete page 9.424 seconds, and warm
restored first caption 1.420 seconds. Capture was 17–24 ms for that smaller page.
Cold startup remains unfinished; the capture improvement does not claim to
resolve it. Evidence: `.cache/verification/production-dxgi-live-manga-check.json`.

The self-contained app was published with native capture enabled, then reopened
as PID 140912. Its `Translumo Local` window was verified visible, not minimized,
and responding. The publish passed with the existing offline NuGet advisory
lookup warning; all model/test helpers had exited before publication.

The next bounded startup candidate is process-only OCR warmup at the start of a
reading session. Current `SpatialOcr` starts its Python worker only after the first
frame passes the quiet gate. That worker loads imports and the detector before
reading stdin, while manga recognition remains lazy until actual Japanese
regions exist. Starting the same process beside translation warmup could overlap
roughly 250–400 ms of otherwise idle startup without a new protocol or an idle-app
worker. Concurrent loading could erase that gain, so a controlled comparison is
required before changing production. The later process-only trial below records
the result; this proposal does not resolve the full cold-start delay.

### Parallel OCR session construction trial

Constructing the same three OCR sessions through a three-worker thread pool did
not reliably improve initialization. In a sequential/parallel/parallel/sequential
trial, warm initialization was 1303 ms sequential versus 1276/1360 ms parallel.
All 37 transcripts matched. Parallel initialization plus the first eight crops
was 260–339 ms faster in that run order, but retained about 63 MiB more private
memory and dense recognition was slightly slower. Cache and first-inference
effects remain confounded; this does not justify a production change.

The [ONNX Runtime 1.23.2 Python binding](https://github.com/microsoft/onnxruntime/blob/v1.23.2/onnxruntime/python/onnxruntime_pybind_state.cc)
holds the GIL during session construction and initialization, limiting overlap
even when called through a thread pool. The measured constructors were mostly
serialized. Results and the exact-output gates are preserved in
`.cache/verification/ocr-parallel-sessions/`; benchmark helpers are closed.

### Process-only OCR early-start trial

A scratch candidate starts the existing OCR process beside translation warmup
when a comic reading session begins. It reuses the locked worker-start path,
sends no warm request, and observes the startup task before disposing OCR.
Production was unchanged during the fresh-process ABBA comparison:

| Run | First caption | Complete page |
| --- | ---: | ---: |
| A1 baseline | 8,166 ms | 9,030 ms |
| B1 early start | 7,374 ms | 8,178 ms |
| B2 early start | 6,168 ms | 7,149 ms |
| A2 baseline | 6,269 ms | 7,311 ms |

All four runs passed eight Thai captions, text fit, live original pixels during
startup, scrolling, blank-page behavior, minimize, and Stop. Caption hashes varied
between both baseline runs as well as the candidate runs; this is not an exact
translation-output equivalence result. The warm comparison saved only 101 ms to
first caption and 162 ms overall, while within-mode run-order variation was
1.2–1.9 seconds. The gain is not established, so the candidate is not promoted.
Sources, commands, and results remain in `.cache/verification/ocr-early-start/`.

The existing combined startup trace bounds the process-launch head start near
449 ms. Eager recognizer construction before reading stdin merely reorders serial
work: imports and detector setup already outlast the initial settled-frame wait.
A one-second translation-start delay would still overlap the expensive OCR load.
Neither variant justifies another configuration trial from the current evidence.
Cold-start latency remains unresolved; the published native-capture build stays
in place.

### OCR GPU allocation investigation

The decoder graphs contain 51.45 MiB of identical initializer payload. Pinned
[ONNX Runtime 1.23.2 source](https://github.com/microsoft/onnxruntime/blob/v1.23.2/onnxruntime/core/framework/session_state_utils.cc#L225-L250)
permits sharing an already resident initializer when its device matches the
planned device. That would require parsing weights, uploading and retaining
shared tensors, and handling CPU fallback separately. The limited saving does
not justify that additional runtime path here; no sharing code was added.

The simpler CUDA arena trial changed only `arena_extend_strategy` to
`kSameAsRequested` for all three OCR sessions. This is an existing
[CUDA provider option](https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html#arena_extend_strategy).
Four fresh OCR processes ran in default/same/same/default order with the same
native translator resident. All 37 texts matched in both cold and warm passes,
and effective provider options were checked for each session.

Requested-size growth saved 227–232 MiB of whole-device GPU use and 225–236 MiB
of process private commit after dense inference. Working-set savings were only
4–14 MiB; private commit is not physical RAM. Warm inference was effectively
unchanged, but first inference was slower in both neighboring comparisons. In
the final pair, initialization plus first eight crops took 2,295 ms versus
1,660 ms with default growth. Initialization also varied with cache order.
The option is not promoted; raw stages and findings remain in
`.cache/verification/ocr-cuda-arena/`.

One narrower follow-up retains default transient arena growth and allocates
fixed initializers through the device allocator. ORT 1.23.2 exposes this through
`session.use_device_allocator_for_initializers`; it changes allocation, not
weights or generation. The bounded comparison must establish memory savings
without the first-inference slowdown before any production change.

That initializer-allocation comparison is also rejected. All texts matched, but
device allocation increased peak private commit by 63–74 MiB and whole-device
GPU use by 74–75 MiB. The final fresh-process pair took 2,315 versus 1,657 ms for
initialization plus the first eight crops, with no stable warm speed gain. Results
are in `.cache/verification/ocr-initializer-allocator/`; production is unchanged.

The final candidate in this allocation branch uses one CUDA arena across the
three sequential OCR sessions. It follows the stock
[ORT 1.23.2 Python CUDA allocator test](https://github.com/microsoft/onnxruntime/blob/v1.23.2/onnxruntime/test/python/onnxruntime_test_python.py#L1509-L1524),
registering once after DLL loading and opting only the CUDA sessions into the
environment allocator. The worker's process lifetime bounds the shared arena.
Growth strategy, fixed-weight allocation, CPU fallback, model weights, and beam
search remain at their existing settings. A fresh-process ABBA must establish
an actual memory saving without a speed regression before promotion.

The shared arena with default geometric growth also failed. All transcripts
matched, but peak private commit rose from 2,809–2,832 MiB to 3,869–3,878 MiB.
One shared run reached only 7.2 MiB of available physical RAM and its warm dense
recognition stalled for 2.16 seconds. The shared pool's growth history spans all
three sessions; reusing a pool did not itself reduce reservation. Results are in
`.cache/verification/ocr-shared-arena/`. A partial CUDA failure can also leave an
environment-owned pool resident during CPU fallback until the worker exits;
Python has no corresponding unregister path here.

One final interaction check combines shared allocation with requested-size
growth, using the stock `OrtArenaCfg(0, 1, -1, -1)`. This targets the observed
global geometric overshoot while retaining reuse across sequential stages.
Acceptance requires memory below the original separate-session default and no
material first-recognition regression. If it fails either, close this allocation
branch without trying chunk-size or memory-cap variants.

The combined shared/requested-size trial matched every transcript but also
missed that gate. It saved 340–357 MiB of whole-device GPU use, 353–369 MiB of
peak private commit, and 18–20 MiB of working set. The comparable cached pair
took 2,203 versus 1,724 ms for initialization plus the first eight crops, a
480 ms regression. Warm eight-crop recognition regressed by 20–25 ms in both
neighboring pairs, and warm dense recognition by 29–65 ms. The environment
allocator's CPU-fallback retention caveat also remains. Raw results and option
proof are in `.cache/verification/ocr-shared-requested-arena/`.

This allocation branch is closed without production changes. Every arm in the
four allocation comparisons preserved the 37 author/dense transcripts in cold
and warm passes. Those are focused inference checks, not full-reader latency or
human translation-quality evidence. All benchmark workers exited; the published
native-capture build remains available for manual reading. No chunk-size,
memory-cap, or custom allocator variants are planned from these results.

### Real-model motion check under memory pressure

The earlier animation check used simulated inference. A separate scratch check
in `.cache/verification/real-model-motion/` now runs the production assembly,
actual `ja-comic` progressive OCR, and native Japanese-to-Thai translation. A
separate process displays the saved 3840×2088 pages. Both pages must match the
saved RGB pixels before models start; the fixture accounts for the display's
150% scaling. Controls include the same full-page movement without models.

Two real-model runs passed caption correctness: 19 initial Thai captions,
visible incomplete translation before the second movement, no visible caption
return after motion suppression until settling, all 29 final source text/bounds
tuples equal to `ocr-08.json`, and empty captions after Stop. `Passed` in these
artifacts describes those assertions, not a smoothness acceptance result.

| Measurement | First run | Resource-sampled run |
| --- | ---: | ---: |
| Cold first / all captions | 10.64 / 14.13 s | 11.59 / 16.88 s |
| New-page first captions | 2.75 s | 3.41 s |
| Settled first / all captions | 2.02 / 6.18 s | 2.65 / 7.64 s |
| Initial movement request to hide observed by controller | 1.61 s | 0.88 s |
| In-flight movement request to hide and scrolling status | 2.08 s | 0.74 s |
| Separate source's largest cold rendering-callback gap | 2.61 s | 4.09 s |

These are separate observations, not an optimization comparison. In the first
run, the in-flight visibility-false event occurred 1.56 s after the source ACK;
the following scrolling status arrived another 0.52 s later. The start of
`Window.Hide()` was not instrumented, so those events do not further divide its
cost. Before models, full-motion source callback gaps stayed below 9.3 ms and
controller heartbeat gaps below 20.7 ms in both runs.

The resource run began with only about 1.0–1.1 GiB of physical memory available
and reached 11 MiB available after models loaded. Peak sampled working sets
were 939 MiB for OCR, 744 MiB for the native translator, 500 MiB for the test
controller, and 172 MiB for the source fixture. The controller includes saved
image/proof data and is not a measurement of the published app's normal memory
use. Private commit is distinct from resident RAM. This establishes severe RAM
pressure; paging and exclusive attribution of stalls were not measured.

PresentMon captured valid source display events in the failed initial pixel
preflight only. It did not produce a valid trace for either real-model run;
subsequent launch checks refused to start models without trace output. The
repair attempts were stopped and the final run used resource sampling alone.
The GPU sampler produced no samples. Rendering callbacks are therefore a source
responsiveness proxy, not proof of physical display cadence. A tiny moving
marker also forces full-frame difference checks during otherwise static phases;
that cost limits comparison with a completely static reader page.

Artifacts: `results-20260925-093454.json`,
`results-20260925-094228.json`, and `resources-20260925-094228.json` in the scratch
directory. No production change is justified from these timings alone. The next
performance comparison needs sufficient memory headroom or direct evidence of
avoidable app memory/work; do not repeat allocator variants or treat a
Dispatcher-only change as a demonstrated fix for the independent source stalls.
Live Comet automation remains unavailable: the browser inventory is empty and
the Windows automation helper fails to launch. No desktop-unlock assumption is
made from that tooling failure.

After cleanup, the authoritative process inventory contained no benchmark,
OCR, translation, PresentMon, or NVIDIA sampler children. The published app
remained visible, unminimized, and responsive. The host has 14,188 MiB usable
physical RAM; 1,653 MiB was available after cleanup. The resource minimum occurred
4.15 s into the cold phase, during model loading/recognition. The next memory
investigation should distinguish resident model costs from this fixture's
overhead and measure hard faults before attributing stalls specifically to
paging. Cleanup and compact summaries are saved beside the raw artifacts as
`post-cleanup-20260925-094228.json` and `analysis-20260925-094228.json`.

### Remove a changed-frame copy; qualify pageable native embeddings

`LiveTranslationSession` now transfers the captured bitmap into its retained
comparison frame after the iteration finishes reading it and creating the
independent scan/worker copies. Previously it cloned that bitmap and disposed
the original. Cancellation and exceptions dispose an untransferred capture;
replacement and Stop dispose the retained one. This avoids a 32,071,680-byte
(30.586 MiB) allocation and pixel copy per changed 3840×2088 frame. The retained
frame count is unchanged, so this is allocation/bandwidth relief, not a claimed
steady RAM saving.

The new lifetime check in `tests/layout-hold` failed against the old code and
passed after the transfer, together with the full existing motion, stale-result,
scroll restoration, and cancellation checks. Separate review confirmed that
pending OCR owns its own clone and callbacks retain frozen snapshots. Baseline
files and red/green output are under
`.cache/verification/frame-ownership-transfer/`.

The existing model-free 4K animation check also passed against the new source:
326 ms to hide, 443 ms cached restoration, zero OCR during motion, no caption
reappearance before settling, and clean Stop. During motion, source callback
p95/max were 8.11/19.61 ms and controller p95/max were 20.15/22.16 ms, with no gaps
over 33 ms. These are integration observations with simulated inference, not a
claim that real-model stalling is solved. Artifact:
`.cache/verification/animation-smoothness/bitmap-ownership-results.json`.

A separate native-runtime candidate was not promoted. The pinned runtime's
[`--no-host` flag](https://github.com/ggml-org/llama.cpp/blob/7fe450e19305b828c199d602c23a8337aaa1f03b/common/arg.cpp#L2320-L2326)
bypasses its preferred CUDA host buffer for CPU weights. The Q8 token embedding
is exactly 250.721 MiB; logs confirmed its model buffer changed from `CUDA_Host`
to `CPU`, while all 33 GPU layers and the 1,815.26 MiB CUDA model buffer stayed
unchanged. The working hypothesis was that pageable embeddings could relieve
physical RAM pressure.

The bounded current/no-host comparison kept real FP16 OCR resident and used the
production four-text translation waves. Ready working set was essentially equal
(621.0/620.3 MiB); after inference it was worse with the candidate (794.1/841.4
MiB). Startup took 3,006/3,449 ms. Dense translation was 5,143/5,172 ms and the
warm repeat 5,418/5,093 ms. Starting host free RAM differed by 306 MiB, so the
candidate's 27 MiB higher ending availability is not evidence of a saving.
No reverse-order runs or variants followed because the initial memory gate did
not hold. Translation strings varied; the complete source/output comparisons
are retained, and no quality improvement is claimed. Artifacts and buffer proof
are in `.cache/verification/native-no-host/`; the production native command is
unchanged.

The copy fix was published successfully and the app reopened. Process 141584
was verified visible, unminimized, and responsive, with no benchmark or model
workers remaining. A preliminary sandboxed build had an unavailable NuGet audit
endpoint and the existing DPI warning; the final Release publish completed
without reported warnings. Real-model memory pressure and manual Comet
smoothness remain open acceptance items.

The official Q6_K model was then qualified separately because the measured RAM
pressure makes its smaller weights relevant. Its pinned size/SHA and header
were verified before inference. A Q8/Q6 pair with the same four-text waves and
OCR resident demonstrated about 49 MiB lower native working set and 394–414 MiB
lower whole-device GPU use. Private commit fell about 466 MiB; that is not the
host RAM saving. Dense translation was essentially unchanged in the first pass
(4.75/4.92 s) and 5.29/4.93 s in the warm repeat. Different initial pressure
prevents a startup-speed conclusion.

Q6 failed the protected meaning checks: hospital duty became generic patient
treatment, the shared-mother/fault relationship was corrupted in both dense
passes, and eldest brother became younger brother. Independent review ignored
name/prose variations and separated errors already present in Q8. No reverse
pair followed this failure. The app stays on Q8; Q6 remains a verified scratch
artifact only. See `local-model/QUALITY.md` and
`.cache/verification/native-q6/{results-q8-q6,summary-q8-q6,semantic-review,gguf-header}.json`.
All model workers exited after this trial; app 141584 remained visible and
responsive. This closes Q6 qualification without a model or runtime change.

### Repair status text; reduce benchmark setup overhead

The indentation cleanup after bitmap ownership transfer had decoded the source
with Windows PowerShell's default encoding. Five status strings acquired
garbled middle dots or ellipses, including the completion string used by the
real-model harness. Their original UTF-8 literals were restored from the saved
pre-change source; all five now match exactly. The focused verifier is
`.cache/verification/frame-ownership-transfer/verify_utf8_literals.py`.
The Release layout-hold checks passed, including capture lifetime and Stop.
The repaired app was published and reopened as process 126604, verified visible,
unminimized, and responsive before further model runs.

The real-model fixture also retained two decoded 4K images throughout its async
controller method after using them to validate dimensions. Dimension validation
now disposes those images in a scoped helper and retains only their size. Exact
pixel proofs remain required. A one-time fixture-setup collection releases the
proof arrays before baseline/model timing; no production collection or loop
collection was introduced. The source fixture still retains the two images it
needs for page changes. Both comparison arms use this same corrected setup.
The model-free preflight passed exact RGB checks for both pages.

### Windows model memory priority: reject after reverse comparison

A scratch-only comparison set OCR and native translation process memory
priority to below-normal (4), with a normal-priority (5) control. Both arms used
identical wrappers and the corrected fixture. OCR set and queried its priority
before production imports. Native set and queried priority immediately after
`Popen`, then queried it again at readiness; this is not an atomic guarantee for
the child's earliest loader pages. Controller and source were independently
verified at priority 5. CPU priority, inference settings, and production code
were unchanged.

| In run order | Control 5 | Candidate 4 | Candidate 4 | Control 5 |
| --- | ---: | ---: | ---: | ---: |
| Starting available RAM (MiB) | 455.6 | 1,495.3 | 1,627.3 | 1,396.8 |
| Minimum available RAM (MiB) | 7.2 | 252.4 | 58.6 | 111.9 |
| Cold first / all captions (s) | 12.48 / 24.81 | 9.90 / 12.88 | 10.73 / 13.81 | 12.34 / 16.10 |
| Initial motion request to hide observed (s) | 4.10 | 0.85 | 0.90 | 0.95 |
| In-flight motion request to hide and scrolling status (s) | 9.20 | 0.87 | 15.19 | 1.50 |
| Settled first / all captions (s) | 2.01 / 5.18 | 2.45 / 7.23 | 2.95 / 4.58 | 2.26 / 8.05 |

The first pair's apparent gain was confounded by about 1 GiB more available RAM
before the candidate started. The reverse pair then showed an unacceptable
15.19-second in-flight hide delay with priority 4 despite its higher starting
headroom. This fails the responsiveness requirement; no priority change was
promoted and no further arms were run. These observations do not establish
that priority 4 caused that outlier, or a reliable startup benefit.

In the reverse candidate's in-flight phase, the source acknowledged motion in
3.1 ms, but its rendering callback p95/max reached 312 ms/8.77 s, versus
36.5 ms/1.02 s in the control. The controller's largest heartbeat gap was
4.68 s versus 277 ms. A rate of long gaps alone would obscure this failure:
long stalls suppress callbacks and lengthen the phase used as the denominator.

All four runs passed exact source pixel checks, Thai caption completion, final
29-region source text/bounds identity, suppression after hide, and Stop cleanup.
Those correctness checks do not imply smooth interaction. Source rendering
callbacks and controller dispatcher callbacks remain proxies for responsiveness,
not physical presentation measurements. Hard-page read/input rates were sampled
system-wide; they cannot identify a faulting process and lack a shared phase QPC
origin. No per-process paging cause is claimed. Results, resource samples,
priority readbacks, and hard-page rates are saved under
`.cache/verification/real-model-motion/priority-{control-a,candidate-b,candidate-b2,control-a2}-*`.
The compact comparison is `memory-priority-summary.json` in that directory.
After cleanup, authoritative inventory showed no model or harness workers;
app 126604 remained visible and responsive, with 1,612 MiB host RAM available.
Real-model scrolling smoothness remains unresolved. The accepted bitmap-copy
optimization and repaired status strings remain in the published app.

### Reuse row storage for region fingerprints

`FingerprintRegions` allocated a contiguous pixel array for every clipped text
region before hashing it. The 29-region 4K page allocated 3,283,928 pixel bytes
per call, including 13 large-object-heap arrays. The shared helper is used by
motion/result comparisons, progressive frame publication, final frames, and
background scroll analysis; some motion iterations call it more than once.

The helper now hashes the same rows in the same order with one pooled row
buffer and one resettable SHA-256 instance. The largest row in this fixture is
1,892 bytes. Region coordinates, clipping, empty-region digests, and callers are
unchanged. This avoids new asynchronous frame-lifetime or version handling.

Eight alternating-order microbenchmark rounds preserved all hashes. Per-call
allocation fell from about 3,289,000 bytes to 2,241–2,317 bytes; elapsed time fell
from 2.96–3.57 ms to 2.24–2.84 ms. The old path recorded 20 generation-2
collections per 30 calls under the observed host pressure, versus zero for the
new path. This is an allocation/CPU measurement, not proof that independent
reader stalls have been fixed. Exact timing and GC counts depend on host state.

The focused regression check failed on the old code at 2,558,712 allocated
bytes, then passed normal, clipped, outside-region digest and allocation checks.
The full Release layout-hold suite passed, including motion suppression,
restoration, cancellation, and capture ownership. Original UTF-8 status literals
still match the saved snapshot. Evidence and the benchmark are in
`.cache/verification/region-fingerprint/`.

### Q8 KV cache: publish the measured GPU-memory reduction

The pinned native runtime supports symmetric Q8 flash-attention caches. The
production command now adds `--cache-type-k q8_0 --cache-type-v q8_0`; model
weights remain Q8, with four 2048-token slots and all other inference settings
unchanged. Logs verified CUDA flash attention, all 33 GPU layers, and KV
allocation decreasing from 512 to 272 MiB. Native per-process NVIDIA dedicated
memory peaks fell by 224–226 MiB across the isolated and motion comparisons.
This reduces allocation; WDDM residency still depends on the
[process memory budget](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgiadapter3-queryvideomemoryinfo).

The isolated F16/Q8-cache pair kept OCR resident. Author-page translation took
1.28/1.52 seconds; the dense page took 5.32/5.24 seconds and its warm repeat
4.75/5.45 seconds. Review of the source and outputs found no new material meaning
failure beyond established F16 scheduling variants. Names and prose differences
were not rejection criteria. The real-motion final captions were also reviewed.
See `local-model/QUALITY.md` and `.cache/verification/native-q8-kv/`.

Both arms of the live comparison used the rebuilt region-hashing code, normal
process memory priority, identical fixtures, and GPU/host telemetry. All four
runs passed exact pixel checks, 19 initial Thai captions, final 29-region source
identity, suppression after hide, and Stop cleanup.

| In run order | Q8 cache | F16 control | F16 control | Q8 cache |
| --- | ---: | ---: | ---: | ---: |
| Starting available RAM (MiB) | 338.5 | 1,532.8 | 1,647.9 | 1,453.7 |
| Cold first / all captions (s) | 11.21 / 23.76 | 10.14 / 13.97 | 10.67 / 22.24 | 11.14 / 14.80 |
| Initial motion request to hide observed (s) | 6.54 | 0.86 | 8.89 | 0.87 |
| In-flight motion request to hide and status (s) | 2.84 | 2.37 | 2.29 | 0.65 |
| Settled first / all captions (s) | 1.95 / 6.68 | 1.83 / 5.53 | 1.55 / 10.40 | 0.68 / 7.81 |

The first pair cannot attribute responsiveness differences to cache precision:
the candidate began with roughly 1.2 GiB less host headroom. In the reverse pair,
the candidate began with 194 MiB less headroom yet improved hide and completion
delays. Its first cold caption was 0.47 seconds slower, and callback tails were
mixed: source new-page and settle gaps still reached 1.82 and 1.33 seconds.
The repeatable GPU saving and useful reverse-pair motion gains justify promotion,
with cold-start and overall smoothness still incomplete.

The new scratch NVML/PDH sampler records QPC timestamps shared with the fixture.
The reverse pair's aggregate NVIDIA dedicated peaks fell from 5,761 to 5,460 MiB;
raw NVML free-memory minima improved from 244 to 545 MiB. Raw NVML used memory
includes driver-reserved memory and differs from `nvidia-smi memory.used`.
Its [memory-utilization percentage](https://docs.nvidia.com/deploy/nvml-api/api/structnvmlUtilization__t.html)
reports time with memory activity, not peak bandwidth saturation. Per-process counters identify OCR/native NVIDIA usage;
the WPF source/controller adapter was not established. Callbacks are not display
presentations, and system hard-page counters cannot identify a faulting process.
These measurements do not establish an exclusive GPU cause for the stalls.
Full comparison: `.cache/verification/real-model-motion/kv-motion-summary.json`.

The Release publish completed successfully. App 128780 was reopened and verified
visible, unminimized, and responsive, with no benchmark/model workers remaining.
The app now includes both row hashing and Q8 KV. Manual Comet feedback is pending.

Further cancellation work requires a separate proof. The current version guards
stop new waves while draining an already submitted wave/chunk. Pinned native
server cancellation on HTTP disconnect preserves its model, but the non-stream
handler polls disconnect only once per second; the current blocking Python
client exposes no closable connection or out-of-band page-cancel signal. OCR
could use per-request ONNX RunOptions termination, but same-session reuse and
CUDA termination latency remain unverified. No new cancellation protocol or
process suspension was introduced.

### Cache bitmap dimensions in the frame-comparison loop

Tracing the remaining controller pauses found that `MeaningfullyDifferent`
read `first.Width` in the condition of its per-pixel loop. A near-static
3840×2088 comparison made roughly eight million dimension-getter calls.
[`Image.Width`](https://github.com/dotnet/winforms/blob/main/src/System.Drawing.Common/src/System/Drawing/Image.cs#L689-L723)
calls native GDI+; it is not a stored field that the JIT can safely hoist like a
pure expression. This gave a concrete explanation to test for the recurring
250–300 ms UI pauses.

The only production change caches width, height, and row length before the
loops. Pixel order, RGB threshold of 16, the 128-pixel change cutoff, alpha
handling, locking, and bitmap ownership remain identical.

| Release microbenchmark, two alternating orders | Before | Cached dimensions |
| --- | ---: | ---: |
| Identical 4K frames | 238–278 ms | 14.7–14.8 ms |
| 127 changed pixels | 236–237 ms | 15.0–15.6 ms |
| Actual frame08-to-frame16 change | 21.1 ms | 1.36–1.39 ms |

Differential checks covered identical frames, the exact 127/128 change boundary,
RGB differences of 15/16, alpha-only changes, size mismatch, and the actual page
pair. All decisions matched. The existing Release layout-hold suite passed;
independent review confirmed only the dimension substitutions and no UTF-8 or
ownership changes. Artifacts and the immediate baseline are under
`.cache/verification/meaningfully-different-width/`.

The motion fixture's 4×4 marker deliberately changes the full-frame hash while
remaining below the visible-change cutoff. It therefore exercises this full
scan every capture. Truly static reader frames stop at the equal-hash check,
so the microbenchmark is not an end-to-end page-speed claim. It directly removes
CPU work for small animations/noise and improves the measured actual page-change
comparison as well.

The follow-up real-model motion check stopped at its exact-pixel preflight,
before starting any models. A centered obstruction covered the same 749-by-414
rectangle on both saved pages. Reasserting
the owned fixture window's placement did not remove the mismatch. No external
window was closed and the pixel gate was preserved. This run supplies no live
latency or smoothness result; promotion relies on the differential measurement,
existing session regression checks, and review above. The failed preflight is
recorded under `.cache/verification/real-model-motion/priority-width-hoist-q8kv-*`.

Release publish succeeded. App PID 142612 was reopened and verified visible,
unminimized, and responsive (window 5965604), with no active translation or
benchmark workers before replacement. This build includes the dimension fix.

### Warm saved-caption replay isolates the next rendering cost

The saved 29-region Thai outputs were replayed with their real completion order
and observed progressive counts (1, 10, 11, 14, 17, 21, 22, 25, 28, 29), using
the production Thai warmup before the baseline. Warmup took 505 ms; first-caption
render took 139 ms. The earlier 1.64-second replay first render omitted warmup
and does not describe current production behavior.

The largest progressive render took 292 ms. Caption placement and text layout
dominated the slow updates: 266 ms at 10 captions, 165 ms at 17, 118 ms at 25,
and 164 ms at 28. Thai margin rendering added 32-50 ms from 17 captions onward
and still took 38 ms on the fully cached final render. Warm native positioning
took at most 0.45 ms, ruling out the earlier proposal to optimize repeated
`SetWindowPos` calls on this representative replay. Source-mask work was mostly
1-5 ms after the first render; deferred Render-priority drains were 1.7-12.4 ms.

Controller heartbeat p95/max were 77/297 ms, with 11 gaps above 33 ms. The separate
source's rendering callbacks had p95/max of 6.64/22.3 ms and none above 33 ms.
External occlusion and callback-only measurement prevent a live display
smoothness claim. Suspend took 3.9 ms and cached restore 24.3 ms. No overlay
production change was made; text layout is the next measured target. Reusing
the existing brush cache for contained caption backgrounds is semantically
safe but prior traces suggest only 1-2 ms potential savings, so it was deferred.
Scratch instrumentation and results: `.cache/verification/real-caption-layout/`.

The focused replay split 957 ms of aggregate main-caption layout into 418 ms
of word wrapping, 350 ms of placement/background checks, 106 ms of final text
measurement, and 43 ms of Thai segmentation. There were 201 caption-cache hits,
35 rebuilds, and only six invalidated suffix entries; changing suffix
invalidation is therefore not the first target. Margin work totaled 319 ms,
including 182.5 ms finding plain margins and 90.1 ms wrapping words. Final and
restore renders had all 29 main-cache entries hit yet still rebuilt nine
margin captions, taking 48.3 and 44.6 ms with identical visual signatures.
The next change caches the plain-margin scan for the same frozen page and
actual capture/mask geometry. Baseline: `results-substages.json` and
`substage-summary.json` in the replay directory.

Replacing WPF text measurement is deferred. `FormattedText` still invokes the
dispatcher text formatter per candidate, and matching `TextBlock` metrics
requires preserving culture, typeface, formatting mode, DPI and number
substitution. A custom `TextFormatter` client also requires its own text store.
Neither is justified before measuring a faithful small alternative. Sources:
[TextBlock](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/TextBlock.cs),
[FormattedText](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/Media/FormattedText.cs),
[WPF text-store requirements](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/advanced-text-formatting#implementing-the-client-text-store).

A separate possible placement optimization remains unmeasured: a background
scan can reject a candidate once its RGB range exceeds 32 and observed
nonbright samples exceed 5% of the maximum grid-point count, including masked
points. The range and nonbright count cannot shrink; the final unmasked sample
count cannot exceed that bound, so neither the 95%-white exception nor a flat
color can succeed. Long integer arithmetic avoids count overflow. Before
changing this path, shadow-count eligible calls, skipped grid positions, and
their existing final outcomes; every eligible call must already return null.
The current 350 ms measurement includes placement as well as background scans,
so it does not yet establish the saving. No early rejection code was added.

### Published: reuse the frozen page's plain-margin scan

`SubtitleOverlay` now retains the small rectangle results from
`FindPlainMargins`. Reuse requires a frozen page, exact capture bounds, and
value-equal mask geometry. The existing frame-reference invalidation and
`Clear()` release the cache; mutable frames always recompute. Padding and
in-place region changes affect the masks and invalidate by value. Recursive
layout fallback can share the result because its scan inputs are identical.
No additional pixel buffer, whole-caption layout cache, or text-measurement
algorithm was introduced.

The focused `tests/overlay` margin-preview check passed. Each invalidation case
starts from an identical baseline and changes just one input: padding, region
bounds, capture bounds, frozen frame identity, or mutable-frame eligibility.
Existing dense-page source coverage and readability checks still pass.
Independent review found no lifetime or cache-validity issue; UTF-8 literals
matched the immediate baseline.

The warmed saved-page replay preserved all 13 visual signatures and render
order. Expensive margin scans fell from eight to one, with seven cache hits;
scan time fell from 182.51 to 22.87 ms across the replay. The first uncached scan
was stable at 23.00 versus 22.87 ms. Final confirmation render improved from
48.32 to 14.56 ms, and cached restoration from 44.58 to 15.91 ms. Total margin
stage time fell from 319.04 to 144.18 ms. The initial caption-placement and
wrapping costs remain, so this is not an overall page-translation latency or
live display smoothness claim. The deterministic scan reduction and identical
layouts support promotion without another timing repeat. Comparison, exact
baselines, binaries, and manifests are in
`.cache/verification/real-caption-layout/candidate/comparison.json` and its
neighboring `baseline` directory.

Release publish succeeded with the existing WFAC010 DPI-manifest warning.
An authoritative process check found no active models or benchmark helpers
before replacing the idle app. PID 52176 was reopened and verified visible,
unminimized, and responsive (window 7147904). Additional margin word-wrap caching
was deferred after repeated final and restore rendering reached 15-16 ms;
first-time caption placement/wrapping is the next larger target.

### First-time placement: background rejection proof

A shadow replay using the published margin cache recorded 7,013 background
scans. The conservative early-rejection condition became true in 6,723 scans;
every one completed with the existing null result. It projects 16,557,283 fewer
grid visits and 10,693,616 fewer sampled pixel reads. The measured work after
eligibility totaled 90.86 ms of 294.46 ms scan time. All 13 render signatures
still matched. These shadow timings include instrumentation; actual promotion
requires a normal baseline/candidate comparison. Evidence:
`.cache/verification/real-caption-layout/background-scan-summary.json`,
`background-scan-corpus.json`, and `results-background-shadow.json`.

Exact dynamic-program pruning in `WrapWords` was also considered and rejected.
Each candidate width determines the existing oversized-line break before the
suffix is tested. Skipping measurement for an unreachable or worse suffix
could visit candidates the current algorithm never reaches. Preserving that
behavior would require an unproved monotonic shaped-width assumption. Reordered
ends also affect strict tie resolution. Post-measure pruning only saves trivial
arithmetic, and the existing width cache already shares measurements across
width trials at a given font size. No wrapping algorithm change was made.

### Published: stop background scans once rejection is certain

The production scan now computes the maximum number of grid samples using long
arithmetic. After updating the sampled colors, it returns null when a channel's
range exceeds 32 and nonbright samples already exceed 5% of that maximum. The
strict threshold preserves the existing exactly-95%-white exception; including
masked grid points makes the bound conservative. No scan can later recover
from this condition, so accepted backgrounds and final colors are unchanged.

The independent old/new evaluator matched all 7,022 recorded calls and reduced
grid visits from 33,169,636 to 16,612,353. The actual production method was also
invoked for exact-95% and below-95% white, per-channel range 32/33, uniform
colors, partial and containing masks, and zero unmasked samples. Those boundary
cases matched null/white/exact RGB outputs. Runnable check and evidence:
`.cache/verification/background-brush-early-null/`. Independent review and
UTF-8 comparison passed.

The clean replay used the published margin-cache baseline, not the slower
shadow-instrumented run. All 13 layout signatures and render order matched.
Aggregate caption placement/background time fell from 363.49 to 282.63 ms
(22.2%). Progressive render time fell from 1,219.93 to 1,173.51 ms across the
sequence; unrelated final/restore timings varied even though this stage did
no work there. The deterministic visit reduction and targeted stage improvement
support promotion; these data do not claim an equivalent whole-page translation
or live smoothness improvement. Exact comparison:
`.cache/verification/real-caption-layout/background-early-candidate/comparison.json`.

Release publish succeeded with the existing WFAC010 warning. No models or
benchmark helpers were active before replacing the idle app. PID 144136 was
reopened and verified visible, unminimized, and responsive (window 7213440).

The read-only desktop inventory corrected the earlier window attribution:
Comet PID 46240 has the manga book-page title, while the separate visible
Windows Terminal PID 25440 is titled
`[ . ] Action Required | Verify Comet desktop access | translater-window`.
That window was not touched. The user was asked to clear the access prompt;
live verification remains pending, and no preflight was retried solely because
time had passed.

### Measured Thai substring-width replacement

A bounded `FormattedText` probe at the current 144 DPI compared the saved 29
Thai captions across 377 font cases and 1,417 wrap attempts. All 38,947 substring
widths were bit exact against `TextBlock.Measure(Infinity)`, with zero width-fit
or newline differences. Both use the same typeface, culture, flow direction,
font size, number-substitution properties, text-formatting mode, and the live
TextBlock's pixels per DIP. This does not assume proportional font scaling.

The warmed same-work A/B then B/A comparison recreated measurement properties
per wrap invocation and fresh per-font width caches. TextBlock took
1,437.62/1,615.42 ms; FormattedText took 932.11/914.34 ms. Means were
1,526.52 versus 923.23 ms (39.5% reduction), with identical output checksums.
Managed allocations fell from about 203,953,152 to 156,854,896 bytes (23.1%);
these are allocations across the benchmark, not process working-set savings.
The fixed baseline results are `.cache/verification/thai-measurement/fidelity-results.json`
and `timing-results.json`.

The proposed production patch changes only candidate substring measurement.
The dynamic program, width cache, and final TextBlock height measurement/render
remain. All three callers ignore intermediate text on a failed wrap; only the
successful complete text is consumed. Before promotion, the probe must compare
the actual changed production method with an independently saved old TextBlock
oracle, cover numbered margins, wrapping/alignment roles and fallback sizes,
and pass the existing grapheme checks and full caption replay. The current
144-DPI evidence is not a test of every display configuration.

The promotion gates passed against the saved old TextBlock method, not an
oracle that changed along with production. The actual production replacement
matched all 38,947 widths and 612 additional caller cases covering numbered
margin captions, Wrap/NoWrap, index alignment, 8-11 DIP fallback text, shared
width caches, and Thai combining marks. Existing Thai/grapheme checks passed;
review confirmed that the only production change is matching-property setup
and the substring-width measurement call. Final text and rendering remain
TextBlock based. Evidence: `production-fidelity-results.json` in the probe
directory.

In the clean full replay, all 13 visual signatures and render order matched the
published early-rejection baseline. Main wrapping fell from 420.39 to 295.46 ms
(29.7%), and margin wrapping from 99.06 to 59.60 ms (39.8%). Progressive render
time across the sequence fell from 1,173.51 to 1,055.12 ms (10.1%); the slowest
update fell from 285.25 to 222.36 ms. Other stages varied, so the isolated 39.5%
measurement improvement is not a whole-page speed claim. Comparison:
`.cache/verification/real-caption-layout/formatted-text-candidate/comparison.json`.

The replacement was published successfully with the existing WFAC010 warning.
An authoritative process guard found no active model or benchmark session.
App PID 129180 was reopened and verified visible, unminimized, and responsive
(window 31722816). This build contains the frame-dimension hoist, margin-scan
cache, early background rejection, and FormattedText measurement, in addition
to the earlier row hashing and Q8 KV change.

The remaining required verification is the current full capture/model/reader
flow after the centered obstruction is cleared. Saved-page replays establish
layout fidelity and CPU cost, but cannot establish current native scrolling,
cold-start delay, or the interaction of concurrent models with browser rendering.

### Live preflight: actual obstruction identified

The earlier access-prompt attribution was incorrect. A bounded model-free
preflight now saves the captured PNG, which visibly identifies the obstruction
as `MeaningfullyDifferentWidthCheck.exe - Application Error`: a stale crash
dialog from our earlier dimension-hoist benchmark. DPI-aware window inventory
identifies HWND 6033602, class `#32770`, hosted by csrss PID 74620, with physical
bounds 1618,960,625x260. The Windows Terminal access-prompt title is unrelated
to this captured obstruction and requires no action for this test.

Both page captures reproduced the same mismatch footprint
(1557,917,749x414, including the composited dialog footprint); frame 08 differed
in 286,824 RGB pixels and frame 16 in 280,426. Exact capture validation stayed
red, so no models loaded. All source/harness helpers exited; the published
Translumo app remained visible and responsive. Evidence:
`.cache/verification/real-model-motion/preflight-actual-frame-08.png`,
`preflight-actual-frame-16.png`, `preflight-saved-20260925-122139.json`, and
`preflight-window-inventory-dpi-aware.json` in the same directory.

The supported Computer Use runtime exited during initialization on both the
initial attempt and recovery attempt. The user was asked to click OK on this
specific benchmark error dialog, superseding the mistaken access-prompt request.
Current combined model/motion timing remains pending; this preflight provides
obstruction evidence, not a new performance result.

### Ready captions only on mixed readable/unreadable pages

The final frame previously replaced empty OCR entries with an ellipsis, which
could become a Thai failure caption and mask source text that had never been
read. Empty OCR entries now keep their blank translations. The shared overlay
renderer creates masks only for nonblank translations and skips blank entries
only when their OCR text is also blank (or the existing progressive-render
policy applies). Readable OCR with an empty model response still fails before
visual-tree replacement. Detected unreadable bounds remain placement blockers
so neighboring captions cannot occupy that source area.

The vertical contents layout now requires every translation to be ready, since
its opaque full panel would otherwise conceal unreadable text. Mixed pages use
ordinary caption placement. Final rendering and stable/motion restoration all
use the same rules; entirely unreadable pages still clear the overlay.

The focused headless check and optional actual-render check passed:
`.tools/dotnet/dotnet.exe run -c Release --no-restore --project tests/unreadable/UnreadableCheck.csproj`
and the same command followed by `-- . --visual`. The latter verified one
ready caption and its mask, no unreadable-region visual, identical behavior
after hide/restore, and atomic rejection of an empty response for readable OCR.
Independent review, UTF-8 checks, and diff whitespace checks passed. These are
correctness checks with stub workers, not new inference performance results.

Red capability was executed against the saved pre-fix production files: the
finalized headless check exited 1 with `Unreadable crop must stay uncovered
beside its translated neighbor.` A finally block restored both current source
files byte-for-byte, verified by SHA-256; the identical command then exited 0.
The already published app was not changed by this baseline check.

Release publish succeeded and the app was reopened visibly as PID 136404,
HWND 4854064, responsive and not minimized. Authoritative process guards before
and after publish found no model session or benchmark helper. The specific
benchmark crash dialog remained visible, so no further preflight or real-model
run was attempted. The prepared motion command uses memory priority 5 (Windows
normal/default), not the rejected priority-4 experiment; CPU priority is unchanged.

### Current user feedback and full-run pressure failure

The user cleared the benchmark crash dialog and reports scrolling/page turns
still feel noticeably laggy, including after all captions finish. The current
full real-model replay passed both exact 3840x2088 pixel gates, then timed out
waiting 90 seconds for the first cold caption. No caption visuals appeared.
Native readiness took 45.17 seconds; OCR emitted no progressive region text
before the timeout. A second recognition status appeared near 96.90 seconds,
but no page switch was commanded. This is a reproduced failure under pressure,
not a successful current-build latency or smoothness acceptance result.

Host available memory started at 417 MiB and reached 21.9 MiB; total commit
peaked at 54.82 GiB. OCR peaked at 656 MiB working set / 3,127 MiB private commit
and used 91.1 CPU-seconds. Native peaked at 569 MiB working set / 2,937 MiB private
commit. GPU free memory reached 407 MiB; per-process dedicated allocation peaked
near 2,227 MiB native and 1,191 MiB OCR. Systemwide page reads averaged 2,452/s
and page input averaged 30,065 pages/s. These counters show paging pressure but
do not identify which process caused individual hard faults.

The separate source's rendering callbacks had a maximum gap of 11.74 ms during
the pre-model motion baseline and 8.13 seconds during cold loading; the
controller heartbeat maximum was 354 ms. No visible captions means caption
layout/the visible subtitle overlay cannot explain this particular cold pause.
Callbacks remain distinct from physical presentation. The source retains two
decoded pages (about 61.17 MiB raw pixels); this fixture overhead can amplify
paging near 22 MiB of headroom, so the absolute stall is not a measurement of
the user's Comet tab. All owned workers exited; the app remained responsive.
Evidence prefix: `.cache/verification/real-model-motion/priority-post-unreadable-q8kv-`.

The next bounded attribution test keeps the same source and idle resident
workers while independently toggling overlay visibility and capture/hash
polling, followed by worker disposal. This targets the user's post-caption
report. Production still converts/maps the full FP16 capture and creates a
30.59 MiB native-backed Bitmap even on a DXGI no-new-frame timeout, then hashes
the full BGRA image before its 100 ms delay. This is a candidate cause to measure,
not yet a proven fix. No further layout or backend configuration change is
justified by the failed cold run alone.

### Remove unused native RAM prompt caching

A separate source-and-log audit found an active memory cost unrelated to the
zero-caption cold failure. In pinned llama.cpp b11146, request
`cache_prompt:false` disables in-slot prefix reuse, while server-level RAM
prompt caching still saves and reloads old states on slot replacement and idle
slot handling. Its default limit is 8 GiB. The application already disables
prefix reuse, so retaining those states provides no intended benefit here.
Sources: pinned [server context](https://github.com/ggml-org/llama.cpp/blob/7fe450e19305b828c199d602c23a8337aaa1f03b/tools/server/server-context.cpp)
and [defaults](https://github.com/ggml-org/llama.cpp/blob/7fe450e19305b828c199d602c23a8337aaa1f03b/common/common.h).

The bounded comparison changed only `--cache-ram 0`, retaining production Q8
weights/KV, context, four slots, and request parameters with OCR resident.
Current/candidate then candidate/current used the same author eight, dense 29,
and repeated dense 29 source texts. Current retained 87.579/89.539 MiB across
33/34 prompt states, with 124 saves each; candidate logged both caches disabled
and zero saves. Current private commit grew 97.3/99.5 MiB from readiness, while
candidate changed -9.7/+9.8 MiB. End private-commit reductions were 108.1 and
89.5 MiB. Working-set differences changed sign across orders, so this does not
establish a repeatable resident-RAM reduction. Process fault counts include soft
faults and are not evidence of per-process disk paging.

| Order | Dense current / candidate | Repeated dense current / candidate |
| --- | ---: | ---: |
| Current then candidate | 8.59 / 7.48 s | 12.90 / 7.33 s |
| Candidate then current | 7.03 / 5.96 s | 9.38 / 5.99 s |

Startup varied with order, and these throughput results remain sensitive to
scheduling and host pressure. Both orders support removing the unused work;
they are not native scrolling or overall cold-start acceptance results.
The protected meaning checks passed in all four arms. Exact strings vary with
continuous batching: the known father-to-student error occurred in both modes,
and no new protected meaning failure appeared with caching disabled. Weights,
context and generation settings remain unchanged; human translation polishing
is still deferred. Evidence: `.cache/verification/native-cache-ram/`, including
both result JSON files, per-stage memory snapshots and server logs. The one-flag
production change is accepted after the current baseline attribution run ends.

The production flag and its exact mocked command-line assertion are implemented.
The finalized `local-model/check.py` check failed when the flag was absent and
passed after the change, without loading a model. Release publish succeeded;
app PID 150276 was reopened visible, unminimized and responsive (HWND 262456).
The app resolves the repo worker root because its published directory has no
`.venv`; the effective `local-model/native_server.py` includes `--cache-ram 0`
(SHA-256 `1BDE86BC7759A03720FE7C97C8248D5C31E7AD92ACBB82D19D0E006B4CCFFCD3`).
The final authoritative process check found no model or benchmark helpers.

### Initial steady-state attribution attempts

The first scratch factorial attempt exhausted memory before reaching ready;
the test source orphan was identified and cleaned up. The revised setup removed
the duplicate decoded page, disposed the small OCR input before translator
loading, and loaded both workers before opening the extra 4K source window.
It then passed eight-region OCR/translation, worker-idle, exact-pixel and
29-caption overlay gates. The first visible-overlay/capture arm completed no
capture within four seconds and was canceled, so later control arms did not run.

That first call also lazily initialized DXGI. It is not a measurement of warmed
capture/hash cost. Similarly, the transition's long source gaps include initial
overlay presentation and full-page motion; they do not isolate resident memory
or overlay composition. Source callback maximum was 7.57 seconds in transition
and 1.89 seconds in the first arm. GPU free memory reached 326 MiB, with mean
systemwide page reads 1,402/s and page input 13,906 pages/s. These are pressure
diagnostics, not physical Comet frame-rate or completed factorial results.
Artifacts: `.cache/verification/steady-state-attribution/{results,gpu,hard-pages}.json`.

After the user freed memory, setup and capture warmup passed, but the borrowed
source fixture's unconditional `DwmFlush` blocked a transition acknowledgement
before any factorial arm started. That artifact has zero capture attempts and
does not measure warmed capture cost. It is preserved under
`.cache/verification/steady-state-attribution/failed-dwmflush-20260925/`.
Steady timing commands now omit that non-evidentiary flush; initial exact-pixel
qualification retains it. The scratch harness also bounds polling drain and
cleanup, emits phase QPC boundaries, and has an independent wrapper deadline.

### Completed steady-state comparison after memory was freed

The corrected run completed all ten arms with actual idle OCR/translation
workers, the same 29-caption visual tree, and exact 3840x2088 source pixels.
`results.json` reports `Passed=true`, `FrameExact=true`, `WorkersExited=true`,
no capture cleanup error, and source exit 0. All owned helpers exited. The
wrapper subsequently mishandled a blank PowerShell process ExitCode property;
its summary/resource serialization did not run. The harness result, phase
stdout, GPU sampler, and hard-page sampler artifacts completed successfully.

Each arm ran for approximately four seconds. Polling performs production
capture and hashing followed by the production 100 ms delay. A-D were repeated
in reverse order; S used a static source and E followed worker disposal.

| Arm | Completed / attempted captures | Controller CPU | Capture mean / p95 | Source callback p95 |
| --- | ---: | ---: | ---: | ---: |
| A1 visible, polling | 19 / 19 | 1,563 ms | 107 / 119 ms | 8.2 ms |
| B1 hidden, polling | 6 / 7 | 500 ms | 106 / 119 ms | 8.0 ms |
| C1 hidden, paused | 0 / 0 | 94 ms | — | 7.8 ms |
| D1 visible, paused | 0 / 0 | 94 ms | — | 8.7 ms |
| D2 visible, paused | 0 / 0 | 94 ms | — | 8.0 ms |
| C2 hidden, paused | 0 / 0 | 78 ms | — | 8.8 ms |
| B2 hidden, polling | 14 / 15 | 3,172 ms | 162 / 547 ms | 9.0 ms |
| A2 visible, polling | 13 / 13 | 2,922 ms | 209 / 994 ms | 8.9 ms |
| S hidden, static, polling | 12 / 13 | 2,906 ms | 177 / 529 ms | 8.6 ms |
| E hidden, paused, workers disposed | 0 / 0 | 31 ms | — | 7.2 ms |

Capture summaries exclude canceled attempts, so B1's small completed-call
mean must not conceal its canceled attempt. Polling adds substantial CPU cost
even with static pixels, but did not consistently worsen source callback
cadence. Overlay visibility likewise did not materially change typical cadence.
Visible arms had some additional rare source callback gaps, while hidden/paused
C1 had a 772 ms outlier. These mixed tails do not establish a scrolling cause.

Available physical memory fell from about 1,028 MiB in A1 to 485 MiB after S.
Idle workers held about 1,423 MiB working set and 5,885 MiB private commit.
Disposal recovered about 1.2 GiB physical headroom during E and reduced commit
by about 5.4 GiB. That final comparison is order-confounded. GPU free memory
reached 620 MiB; systemwide page reads averaged 651/s, with a 16,362/s maximum.
GPU samples can align with phase QPC boundaries; hard-page samples have no
matching QPC timestamps and remain run-wide evidence.

This fixture measures WPF callbacks, not actual Comet presentation. It also
isolates capture/hash and static overlay costs rather than executing the full
live scroll-alignment loop. No further production change follows from this
comparison alone. In particular, neither hiding the overlay nor disposing
workers is established as a fix. Distinguishing stuttering source artwork from
captions trailing smooth artwork is the next user-visible diagnostic.
The user's scrolling complaint remains unresolved. Translumo PID 150276 was
left visible, unminimized, responsive, and idle for review.

### User screenshots: failed captions and family charts

The user clarified that the source artwork itself still stutters, although the
latest build is more readable and smoother than before. They then supplied four
photos of reader positions 21, 14, 10, and 12. These expose separate correctness
failures in rendering and OCR:

- Positions 12 and 14 show a Thai failure message in silent speech bubbles.
  Saved OCR and translation are both `...` (12 region 14, 14 region 12).
  `SafeThaiTranslation` rejected punctuation-only output because it contained
  no letter or digit. The correction retains nonempty punctuation when no
  untranslated Japanese was removed. The same function serves normal captions,
  margin captions, and the contents layout; Japanese-only responses still
  produce the existing failure indication.
- Dark horizontal streaks come from mask background interpolation. On position
  14, regions 2 and 5 sample a dark border on one side while the opposite side,
  top, and bottom are nearly white. The correction rejects that one inconsistent
  side only when those three independent samples agree. It retains the gradient
  and dark-background behavior covered by the existing check.
- Position 10 merges nine family names into the single detected rectangle
  `[1246,1692,529,207]`. OCR corrupts the combined names and the renderer receives
  one long translation for the entire row. Position 21 both merges and misses
  genealogy labels; its user photo includes an unrelated Thai paragraph. Saved
  generations differ from the user's photo, so the exact photographed Thai
  wording is not attributed to a particular cached model response.

Both renderer fixes have independent red/green checks. Removing only the
punctuation correction fails the inline ellipsis assertion; removing only the
mask correction fails the single-edge streak assertion. With the changes,
`.tools/dotnet/dotnet.exe run -c Release --no-restore --project tests/overlay/OverlayCheck.csproj`
and the same command followed by `-- --vertical` pass, including margin
punctuation and the Japanese-only failure indication. No model was loaded.
Rendering the saved full frames with their cached OCR and translations also
passes visual review: page 12 renders 15 captions and page 14 renders 14,
including the two literal ellipses. The reported dark horizontal mask streaks
are removed in these reproductions. Composites are retained under
`.cache/verification/chart-render/page12-composite.png` and
`page14-composite.png`; this does not certify all translation wording.

Whitespace splitting alone was rejected because it also splits ordinary
vertical dialogue. The Japanese OCR path now qualifies a merged region using
multiple label stems ending below the same thin horizontal connector bar.
Within that bounded context it recovers individual labels, including adjacent
ordinal/ruby text, and keeps connecting lines outside their bounds. Recovery
runs before known-region reconciliation and progressive detection output, so
OCR crops, cache proofs, and displayed geometry share the corrected bounds.
This deliberately covers thin ruled charts with vertical text, not general
diagrams; it adds no model or dependency and leaves source pixels unchanged.

The final geometry audit changes only positions 10 and 21 among all 27 saved
frames at 0.75, 1, and 1.25 image scales, recovering nine child labels and ten
chart labels respectively. A separate fixed-viewport zoom check at 0.75 and
1.25 covers positions 10, 12, 14, and 21. It pans the enlarged charts fully into
view and clips transformed detector boxes to the capture, matching the real
detector's bounds contract. Earlier center-cropped fixtures incorrectly
expected recovery of text outside the viewport and were corrected.

The final five-call in-process medians are 122.52 ms on position 10, 107.20 ms
on position 21, and 49.39 ms on ordinary position 12. Earlier runs ranged up to
about 250 ms on the charts and 135 ms on position 12. These are added geometry
processing costs on this loaded machine, not a stable end-to-end speedup.
Artifacts are in `.cache/verification/chart-label-split/`. The focused worker
check goes red when the splitter is replaced in-process with the prior
pass-through behavior: the merged chart remains one region. The production
implementation passes `.venv/Scripts/python.exe -X utf8 local-ocr/check.py
--device-self-check`, including source-pixel preservation and ordinary-text
controls. No production source was changed for that red check.

Actual OCR and translation ran sequentially and the model helpers exited.
They produced 30 full-page regions on position 10 and 24 on position 21, with
all 19 recovered chart labels recognized separately. Full-page composites in
`.cache/verification/chart-render/` render all 30/24 captions, retain connector
lines, and contain no contents/index rewrite. Position 10 uses ten numbered
margin associations for narrow captions; position 21 uses one. The former
merged chart paragraph no longer appears.

Translation fidelity remains unfinished. Position 10's `白日子王` gains a Thai
"Title:" prefix, and `木梨之軽王(死去)` loses its deceased status. Position 21's
generation 20 and 22 captions both produce incomplete `รุ่นที่ยี่` wording;
proper-name transliterations are also unreliable. These defects remain visible
in the verification artifacts. No prompt, name glossary, or model change was
made in this structural/rendering fix, consistent with the earlier deferral of
human-quality translation work. Chart layout passing does not certify these
meanings.

### Navigation-triggered capture pause candidate

The candidate observes navigation through Windows Raw Input on the existing
controls window. It registers mouse and keyboard input sinks without replacing
normal browser input delivery. Only wheel, left-button navigation/drag, and
page-navigation keys affect the session; ordinary pointer movement and other
keys are ignored, and no input history is retained. Mouse signals use the
window under the pointer; keyboard signals use the foreground window. Both
are scoped to the selected capture target and exclude the controls window.

Navigation suspends captions immediately, invalidates pending output, and
pauses new capture/hash work for a 450 ms quiet interval. This is a provisional
settling interval, not a measurement of the reader's page-turn animation. Generation checks
discard an already-running capture or alignment result invalidated by input.
Restoration still requires fresh captures and existing pixel/OCR proofs.
Registration failure retains the prior pixel-based detection path.

The focused `tests/layout-hold` regression goes red when the session observer
subscription is removed and green with the candidate. It covers immediate
caption suspension, an in-flight capture being discarded, no further capture
while navigation is active, rejection of stale progressive output, and fresh
restoration. Main Release build passes. A native fixture using owned windows
also passes the input ABI, safe transparent-overlay traversal, opaque-window
rejection, irrelevant-input filtering, registration cleanup, and quiet deadline
checks. It injects no system input. User reader acceptance remains required;
this is not a claim of measured Comet frame-rate improvement.

The user uses both a mouse wheel and laptop touchpad, and reports stutter during
the paper-like page-turn animation. The observer additionally registers the
optional Precision Touchpad collection (usage page `0x0D`, usage `0x05`) and reads
only its Raw Input header. It retains no contact payload or input history.
Microsoft documents [touchpad wheel routing](https://learn.microsoft.com/windows/win32/input-precisiontouchpad/precision-touchpad-portal)
and the [touchpad collection](https://learn.microsoft.com/windows-hardware/design/component-guidelines/touchpad-windows-precision-touchpad-collection);
generic mouse Raw Input alone does not establish coverage of Precision Touchpad
gestures. An aggregate-only native observation found one matching physical
touchpad, successful registration, and zero matching HID headers during a
10-second idle interval. Actual two-finger delivery still needs the user's
manual check. Optional touchpad registration and cleanup are separate from
the mouse/keyboard registration so failure preserves that working path.

### Combined screenshot-fix build

Release build, both overlay checks, and the layout/session lifecycle check
pass after the combined changes. The self-contained publish to `artifacts/app`
passes with the existing offline NuGet advisory-feed warning. An ownership and
child-process guard verified the previous app was idle before replacing it.
The updated app was reopened as PID 69044, title `Translumo Local`, and verified
visible, unminimized, and responding. Physical two-finger scrolling and the
paper-like animation still require the user's manual check; neither synthetic
input tests nor these static composites establish native Comet smoothness.
