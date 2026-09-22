# Local comic text detection

`setup.ps1` installs pinned OpenCV and ONNX Runtime dependencies into the project's
Python environment and downloads the SHA256-verified detector into
`models/comic-text-detector`. Run `local-model/setup.ps1` first.
The worker has no downloader and uses only local model files.

The persistent JSON-lines protocol accepts `{"image":"<base64 PNG>"}` and returns
`{"regions":[{"x":0,"y":0,"width":100,"height":40,"vertical":false}]}` or an
`error`. Coordinates are physical pixels in the input frame. A 1024-pixel inference
canvas is resized with preserved aspect ratio and bottom/right padding, matching
the official model. Bounding box confidence threshold is 0.4; nonmaximum suppression
uses 0.35 IoU plus containment suppression to avoid covering touching bubbles twice.
When a confident box encloses two separated bubble proposals, those smaller boxes
replace the union. Only this paired recovery accepts confidence down to 0.3; isolated
weak proposals remain excluded. This preserves bubbles when scrolling changes their
relative detector scores.

Japanese comic crops use the local manga-ocr recognizer with its published beam search settings; other languages use Windows OCR or local Tesseract on detected, padded crops. This avoids
interpreting manga line art as hundreds of text regions. Normal desktop OCR remains
available by disabling manga/webtoon detection.

Source and model: [dmMaze/comic-text-detector](https://github.com/dmMaze/comic-text-detector),
source revision `440b978563c71b758e31aaa315d100faba1efa2f`, GPL-3.0.
The ONNX weights are the official `comictextdetector.pt.onnx` release asset from
[manga-image-translator beta-0.2.1](https://github.com/zyddnys/manga-image-translator/releases/tag/beta-0.2.1).
SHA256: `1a86ace74961413cbd650002e7bb4dcec4980ffa21b2f19b86933372071d718f`.
The detector adapter follows that project's preprocessing and output interpretation;
retain `LICENSE.comic-text-detector` when distributing this component or its model.
OpenCV is Apache-2.0; ONNX Runtime is MIT.

Run `python local-ocr/check.py --model models/comic-text-detector/comictextdetector.onnx --image <test-page.png>`
to verify offline protocol recovery, region coordinates, and repeated-frame stability. The
OCR smoke test also exercises all four languages with detection enabled.

Japanese recognition model: [kha-white/manga-ocr-base](https://huggingface.co/kha-white/manga-ocr-base),
Apache-2.0, revision `aa6573bd10b0d446cbf622e29c3e084914df9741`. Setup downloads it to
`models/manga-ocr` with its card and license. The JSON request can include
`"language":"ja"` to add recognized `text` to each region. Other supported language
codes are `ko`, `en`, and `th`; recognition for these stays in the Windows app.

Run `powershell -File local-ocr/setup.ps1 -Cuda` for the tested NVIDIA configuration.
It installs PyTorch 2.6.0+cu126 and ONNX Runtime GPU 1.23.2, replacing CPU ONNX Runtime.
The default setup uses CPU inference and preserves an already-installed GPU runtime.
Set `LOCAL_OCR_DEVICE=cpu` to force CPU. Automatic GPU inference falls back to CPU
if CUDA is unavailable or inference fails. The worker uses no cloud inference,
disables ONNX telemetry, and loads transformer assets with `local_files_only=True`.

On the author-authorized Black Jack page 12 fixture, this changed recognition from
198 mostly incorrect regions to the seven dialogue bubbles plus the hospital sign.
All eight Japanese texts matched the manually checked transcript, ignoring punctuation.
On the RTX 3060 Laptop test machine, warmed detector inference took 75–87 ms and full
C# detector plus manga recognition took 775–798 ms; initial process/model loading took
about 10 seconds. These timings exclude translation and overlay rendering.

The exact fixture regression (fixture acquisition/attribution is recorded in
`artifacts/manga-test/source.md`) is:

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