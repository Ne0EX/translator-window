# Local comic text detection

`setup.ps1` installs pinned OpenCV and ONNX Runtime dependencies into the project's
Python environment and downloads the SHA256-verified detector into
`models/comic-text-detector`. Run `local-model/setup.ps1` first.
The worker has no downloader and uses only local model files.

The persistent JSON-lines protocol accepts `{"image":"<base64 PNG>"}` for diagnostics.
The Windows app sends a `mapping` object with a unique memory mapping `name`,
`width`, `height`, packed `stride`, `length`, and `pixelFormat: "bgra32"` instead.
Both paths return
`{"regions":[{"x":0,"y":0,"width":100,"height":40,"vertical":false}]}` or an
`error`. Coordinates are physical pixels in the input frame. A 1024-pixel inference
canvas is resized with preserved aspect ratio and bottom/right padding, matching
the official model. Bounding box confidence threshold is 0.4; nonmaximum suppression
uses 0.35 IoU plus containment suppression to avoid covering touching bubbles twice.
When a confident box encloses two separated bubble proposals, those smaller boxes
replace the union. Only this paired recovery accepts confidence down to 0.3; isolated
weak proposals remain excluded. This preserves bubbles when scrolling changes their
relative detector scores.

Frame copying and request serialization run off the UI thread. The app keeps one
mapping alive until its reply is drained, including when the caller cancels.
The worker copies BGRA into the same BGR pixels the PNG path supplies. A 3840 × 2088
transport benchmark reduced preparation and transfer from 351 to 47 ms, with exact
RGB output. This excludes detection and recognition; the complete app timings are
recorded in [the implementation measurements](../docs/implementation-plan.md).

An optional `"progress":true` request emits one `{"detected":[...]}` geometry
header, followed by `{"recognized":[{"index":0,"text":"..."}]}` chunks after
each existing eight-crop Japanese recognition batch. The usual `regions`/`reused`
final response remains unchanged. The app validates indices, geometry, and final
text agreement, and starts translating recognized blocks before the final reply.
Cancellation drains the remaining reply before another request reuses the worker.
Pages that may need the Windows vertical-column replacement wait for that final
geometry so captions cannot appear over boxes that will be discarded.

Japanese comic crops use the local manga-ocr recognizer with its published beam search settings; other languages use Windows OCR or local Tesseract on detected, padded crops. This avoids
interpreting manga line art as hundreds of text regions. Normal desktop OCR remains
available by disabling manga/webtoon detection.
If one Japanese crop reaches the recognizer's 300-token limit, the worker leaves
its text empty so the app can try its Windows/Tesseract crop fallback and keep the
source visible if it is still unreadable. Other crops in the batch continue.

Before Japanese recognition and reuse reconciliation, a bounded chart pass can
split merged vertical labels hanging from a shared ruled connector and recover
nearby labels with matching stems. It preserves source pixels and connector
lines. This targets thin ruled family charts; it is not a general diagram parser.
The page 10/21 verification and remaining translation defects are recorded in
the [0.1.0 release notes](../docs/releases/0.1.0.md).

Source and model: [dmMaze/comic-text-detector](https://github.com/dmMaze/comic-text-detector),
source revision `440b978563c71b758e31aaa315d100faba1efa2f`, GPL-3.0.
The ONNX weights are the official `comictextdetector.pt.onnx` release asset from
[manga-image-translator beta-0.2.1](https://github.com/zyddnys/manga-image-translator/releases/tag/beta-0.2.1).
SHA256: `1a86ace74961413cbd650002e7bb4dcec4980ffa21b2f19b86933372071d718f`.
The detector adapter follows that project's preprocessing and output interpretation;
retain `LICENSE.comic-text-detector` when distributing this component or its model.
OpenCV is Apache-2.0; ONNX Runtime is MIT.

The installed `comictextdetector.onnx` is the 94,669,756-byte comic-text-detector
release model. Its YOLOv5s block head emits `blk [1,64512,7]`; the same graph also
emits a U-Net `seg [1,1,1024,1024]` mask and a DBNet `det [1,2,1024,1024]` line map.
Setup extracts the block output into `comictextdetector-blocks.onnx` (29,127,212
bytes, SHA256 `d92958fc0e73fbde5d26e4f8512b2298a7308cb70d2ba1303c4eec67d58aff03`).
The worker prefers this verified graph and falls back to the original if it is
missing or has a different hash. It applies confidence filtering and NMS to get
comic text blocks. Requesting only `blk` from the original graph still executes
the unused heads in this ONNX Runtime inference build; extraction removes them.
Across 27 reader captures, median CPU detection fell from 1,283 to 282 ms with
bit-exact block tensors and selected boxes. ONNX Runtime executes these weights;
it does not select the model architecture.
RapidOCR 3.9.2 defaults to the `PP-OCRv6_det_small.onnx` text-line detector. On the
3840 × 2088 Comet spread 8 capture, the current model returned 29 blocks in about
1.3 seconds warm on CPU before extraction; RapidOCR detection-only returned 143 line boxes in about
1.0–1.1 seconds. A direct swap would pass many more crops to manga-ocr and change
caption grouping. The RapidOCR trial lives only in ignored benchmark files.

Run `python local-ocr/check.py --model models/comic-text-detector/comictextdetector.onnx --image <test-page.png>`
to verify offline protocol recovery, region coordinates, and repeated-frame stability. The
OCR smoke test also exercises all four languages with detection enabled.

Japanese recognition model: [kha-white/manga-ocr-base](https://huggingface.co/kha-white/manga-ocr-base),
Apache-2.0, revision `aa6573bd10b0d446cbf622e29c3e084914df9741`. Setup downloads it to
`models/manga-ocr` with its card and license. The JSON request can include
`"language":"ja"` to add recognized `text` to each region. Other supported language
codes are `ko`, `en`, and `th`; recognition for these stays in the Windows app.

Setup exports the same pinned weights into `encoder_model.onnx`,
`decoder_init.onnx`, and `decoder_with_past.onnx` in that folder. The preferred
runtime uses ONNX Runtime, NumPy, Pillow, and the local character vocabulary;
it imports neither Torch nor Transformers. It retains four beams, length penalty
2, no repeated trigrams, and the 300-token limit. Decoder attention caches stay
on the GPU between tokens. If the exported assets are absent, the original
Torch loader remains available. Exports are local setup work, never a runtime
download. `check_onnx_recognizer.py` checks beam termination, configuration,
input limits, and CPU fallback; its optional image/regions arguments verify
real transcripts while blocking network access.

CUDA setup also exports the three `_fp16.onnx` variants. CUDA selects those
when the complete set is present; CPU and CUDA failure recovery use the original
FP32 graphs and inputs. Beam scores are computed in float32, matching Transformers
even when model inference uses float16. In the Q8-resident comparison this freed
about 639 MiB of GPU memory. The author fixture at four scroll offsets and the
dense 29-region fixture stayed exact. Across 388 saved crops, FP16 matched current
FP32 on 387; the difference was a giant crop merging several contents-page columns
whose OCR was already incoherent. The app's vertical Windows OCR fallback handles
that layout when it detects enough separate columns; the full-window native check
returns all 19 columns even with additional small detector boxes.

CUDA setup also rewrites the encoder's single 16×16 patch convolution as a
reshape plus biased matrix multiplication, preserving its weights and patch
order. The Windows runtime loads core cuDNN and lets it load further libraries
only when needed. The rewritten encoder avoids the convolution engine libraries;
original exports still work and load those libraries on demand. In the native
translation-resident comparison, OCR working set fell by about 148 MiB and
private commit by 175 MiB. Warm-cache setup plus first recognition improved by
117–149 ms; dense recognition throughput stayed about the same.

Compared with current FP16, 385/388 saved crops were identical. Two differences
were invalid contents-page unions: the app returned exactly the same 19 Windows
OCR columns and emitted no intermediate captions for either encoder. The third
crop corrected a printed parenthesis and kanji. CPU graphs and four-beam
generation remain unchanged. This qualification does not establish perfect OCR
on other pages; detailed outputs are recorded in the implementation measurements.

The CUDA encoder uses [cuDNN's heuristic convolution selection](https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html#cudnn_conv_algo_search) for its single patch
projection, avoiding the default exhaustive search on first use. Decoder settings
stay unchanged. In a Q8-resident comparison, initialization plus first recognition
fell from 2.98 to 2.56 seconds; the eight-region and 29-region transcripts stayed exact.

Scroll rescans still run the full detector. Recognition can reuse a known block only
after its entire original crop, including eight pixels of padding, matches exactly
at the new position. New, split, merged, or expanded detections are recognized again.
Failed proof stays invalid across snapshot updates and is checked again against the
actual requested frame. This removed all eight repeated recognitions in the native
scroll fixture; OCR took 515 ms, including the detector and bridge.

Run `powershell -File local-ocr/setup.ps1 -Cuda` for the tested NVIDIA configuration.
It installs PyTorch 2.6.0+cu126 and ONNX Runtime GPU 1.23.2, replacing CPU ONNX Runtime.
The default setup uses CPU inference and preserves an already-installed GPU runtime.
Comic detection uses CPU by default to leave GPU memory for translation and manga
recognition. Set `LOCAL_OCR_DETECTOR_DEVICE=cuda` to opt the detector into CUDA;
session creation and inference fall back to CPU if CUDA fails. `LOCAL_OCR_DEVICE`
continues to control manga recognition. The worker uses no cloud inference,
disables ONNX telemetry, and loads only local model assets. The Torch fallback
also sets `local_files_only=True`.

On the author-authorized Black Jack page 12 fixture, this changed recognition from
198 mostly incorrect regions to the seven dialogue bubbles plus the hospital sign.
All eight Japanese texts matched the manually checked transcript, ignoring punctuation.
Earlier GPU-detector measurements on the RTX 3060 Laptop took 75–87 ms for warm
detection and 775–798 ms for C# detection plus recognition. The current CPU block
detector and cached ONNX recognizer avoid that configuration's slow detector start.
In the latest Release native fixture, full OCR took 5.41 seconds cold and 744 ms
on a warm restore while the translation model shared GPU memory. These timings exclude
translation and overlay rendering; isolated recognizer timings are faster than
the complete app. See [the implementation measurements](../docs/implementation-plan.md).

The exact fixture regression (fixture acquisition and attribution are recorded in
[verification fixture provenance](../docs/verification-fixtures.md)) is:

```powershell
.tools/dotnet/dotnet.exe run --project tests/OcrSmoke/OcrSmoke.csproj -- models/tessdata artifacts/manga-test/page12.png --black-jack-page12
.venv/Scripts/python.exe local-ocr/check.py --model models/comic-text-detector/comictextdetector.onnx --image artifacts/manga-test/page12.png --language ja
.venv/Scripts/python.exe local-ocr/check_scroll.py --model models/comic-text-detector/comictextdetector.onnx --image artifacts/manga-test/page12.png
```

Both Python checks block socket connections in the real worker process. The scroll
check verifies all eight transcripts at offsets 0, 60, 100, and 120 pixels, plus
proposal-only regressions for isolated weak detections and adjacent text columns. These are
checks of one real manga page and synthetic multilingual bubbles; they do not establish
perfect detection, recognition, or translation for every comic layout and art style.
