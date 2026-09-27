# Captured-view replay

This check replays redistribution-safe synthetic captured views through the real
`SubtitleOverlay` renderer. The default run uses fixed recognized text and fixed
translations, renders every case twice, and compares geometry, line breaks,
font sizes, colors, and output pixels.

```powershell
.\.tools\dotnet\dotnet.exe run --project tests\replay\ReplayCheck.csproj
```

The committed cases cover caption containment, dense-page readable margins,
colored covers, a sparse passing control, and stylized-text completeness. Text
containers and protected artwork are annotations independent of OCR regions.
The stylized fixture deliberately records one heading as
`unresolved-before-recognized-text`; it verifies that the evidence keeps the
missing passage separate from a successfully rendered body caption, rather than
claiming support or assigning an unevidenced detector fault.

## Repeated-view performance

For the opt-in repeated-view rendering budget:

```powershell
.\.tools\dotnet\dotnet.exe run --project tests\replay\ReplayCheck.csproj -c Release -- --performance
```

Each case renders nine newly allocated, frozen copies of the same captured view
through public `SubtitleOverlay.Render`, including WPF layout. The median must
fit 16.67 ms, one 60 Hz frame; worst time is also printed. This is a development
machine target, not a hardware-independent CI requirement. Existing assertions
compare the final geometry, full caption text and pixels with a fresh render.
Capture, OCR, translation and compositor presentation are outside this timer;
passing does not establish smooth live scrolling in Comet.

## Opt-in diagnostics

Normal runs write no files. Pass a directory to record the generated captured
view, rendered PNG, commit, capture metadata, recognition and translation data,
caption geometry/font/lines/color, stage outcomes, and fallback reasons. Reports
record `captureTargetKind` as `selected-area`, `window`, `screen`, or `unknown`,
separately from the `capturedViewEvidence` image filename. Complete-processing
manifests currently lack the selected target kind, so their reports use `unknown`.

```powershell
.\.tools\dotnet\dotnet.exe run --project tests\replay\ReplayCheck.csproj -- --diagnostics .cache\replay
```

## Complete local processing

Use a local manifest to run a captured image through comic text detection,
recognition, the local translation worker, and the caption renderer. This mode
requires the normal local models and runtime. Images and generated reports stay
under ignored local paths.

```powershell
.\.tools\dotnet\dotnet.exe run --project tests\replay\ReplayCheck.csproj -- `
  --processing .cache\replay\ex12.json --root . --diagnostics .cache\replay\result
```

Example local manifest:

```json
{
  "id": "ex12-current-view",
  "capturedView": "ex12.png",
  "dpi": 144,
  "zoom": 1.25,
  "sourceLanguage": "ja-comic",
  "targetLanguage": "th",
  "protectedArtwork": [
    { "id": "face", "bounds": [120, 300, 180, 220] }
  ],
  "intendedPassages": [
    {
      "id": "heading",
      "sourceText": "expected source characters",
      "regionId": null,
      "bounds": [300, 80, 240, 100],
      "expectedStage": "detection-miss"
    }
  ]
}
```

The processing report uses the domain outcomes `detection-miss`,
`region-grouping-error`, `unreadable-region`, `recognition-error`,
`caption-placement-failure`, and `caption-complete`. Without an intended-passage
rectangle, missing text remains `unresolved-before-recognized-text`; the check
does not guess which earlier stage failed.

## Local evidence inventory and limit

Ignored local evidence already includes `.cache/diagnostics/manga-preview/`
frames and OCR output, page 7 and spread 16 OCR/translation/render outputs,
`dense-margin-fallback.png`, and the provenance-controlled inputs described in
`docs/verification-fixtures.md`. These publisher/user images and their
derivatives are not copied into this fixture.

Those files do not identify a current-build EX12 captured view plus target,
DPI/zoom, intended-passage rectangles, and detector-stage output as one matched
record. They therefore cannot establish whether the reported EX12 heading or
the shortened middle-left color passage is a current detection miss,
grouping/recognition problem, translation issue, or old render. A complete local
processing manifest is still required before making or rejecting a bounded OCR
correction for issue #12.
