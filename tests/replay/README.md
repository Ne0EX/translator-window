# Captured-view replay

This check replays redistribution-safe synthetic captured views through the real
`SubtitleOverlay` renderer. The default run uses fixed recognized text and fixed
translations, renders every case twice, and compares geometry, line breaks,
font sizes, colors, and output pixels.

```powershell
.\.tools\dotnet\dotnet.exe run --project tests\replay\ReplayCheck.csproj
```

The committed cases cover caption containment, dense-page readable margins
(including a 701-pixel view and gutters inside dark window borders),
colored covers, a sparse passing control, and stylized-text completeness. Text
containers and protected artwork are annotations independent of OCR regions.
The readability cases require large black-on-white margin cards, complete opaque
fill inside an enclosed bubble (including pale lettering outside its detection),
unchanged bubble outlines and exterior pixels, preservation of a second passage
awaiting translation, light captions on dark fills, and dominant source-ink color
when the Auto preset is selected. These checks use the public renderer only.
The readability cases require large black-on-white margin cards, complete opaque
fill inside an enclosed bubble (including pale lettering outside its detection),
unchanged bubble outlines and exterior pixels, preservation of a second passage
awaiting translation, light captions on dark fills, and dominant source-ink color
when the Auto preset is selected. These checks use the public renderer only.
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

`--fixtures` also accepts local cases with `capturedView` (a path relative to the
fixture file or an absolute path), `autoStyle: true`, and `localHeadings` containing
region IDs required to stay at their source locations. Use fixed translations to
isolate presentation on private source images; retain those manifests locally.
Diagnostics are written before semantic verification so failed assertions retain
their rendered evidence. The reference-page baseline is currently failing; the
default synthetic suite does not claim that page's visual acceptance.

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

### Colored words inside a pale speech bubble

This opt-in regression uses the approved complete-processing boundary. Supply the
private 1524 × 810 Senmanga captured view as
`.cache/replay/senmanga-acceptance/45-color-dialogue.png`; publisher images are not
included in the repository. Stop the live translation session before running it
so the replay has the local models to itself.

The independently read source passage is `たとえどんなに攻略困難な相手でもね!`.
The original recognizer skipped `攻略困難な`. The following manifest supplies
only the required source literal and location: detection, recognized text,
translation and caption placement all come from the actual processing pipeline.
It preserves the whole captured view and all detected regions.

Run from the repository root. Use a fresh diagnostics directory for each RED or
GREEN attempt; keep both reports and console output.

```powershell
$evidence = ".cache/replay/colored-word"
$phase = "red" # Change to green after the implementation.
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
@'
{
  "id": "45-colored-word-complete",
  "capturedView": "../senmanga-acceptance/45-color-dialogue.png",
  "sourceLanguage": "ja-comic", "targetLanguage": "th",
  "autoStyle": true, "dpi": 96, "zoom": 0.8,
  "protectedArtwork": [],
  "intendedPassages": [{
    "id": "colored-opponent",
    "sourceText": "たとえどんなに攻略困難な相手でもね!",
    "regionId": null, "expectedStage": "caption-complete",
    "bounds": [388, 485, 134, 167]
  }]
}
'@ | Set-Content -Encoding UTF8 "$evidence/processing.json"
if (Test-Path "$evidence/$phase") { throw "Use a fresh diagnostics directory." }
New-Item -ItemType Directory -Path "$evidence/$phase" | Out-Null
.\.tools\dotnet\dotnet.exe run --project tests/replay/ReplayCheck.csproj -c Release -- `
  --processing "$evidence/processing.json" --root . --diagnostics "$evidence/$phase" `
  2>&1 | Tee-Object -FilePath "$evidence/$phase/console.log"
$fullHarnessExit = $LASTEXITCODE
Write-Output "Full harness exit: $fullHarnessExit"
$report = Get-Content -Raw -Encoding UTF8 "$evidence/$phase/45-colored-word-complete.json" | ConvertFrom-Json
$literal = "たとえどんなに攻略困難な相手でもね!"
$stage = @($report.stageOutcomes | Where-Object id -eq "colored-opponent")
$region = @($report.regions | Where-Object recognizedText -eq $literal)
if ($stage.Count -ne 1 -or $stage[0].observedStage -ne "caption-complete" -or $region.Count -ne 1) {
  throw "Complete colored source passage did not reach a caption."
}
$caption = @($report.captions | Where-Object { $_.kind -eq "caption" -and $_.regionId -eq $region[0].id })
if ($caption.Count -ne 1 -or $null -ne $caption[0].fallbackReason -or `
    $caption[0].text.Replace("`r", "").Replace("`n", "") -ne $region[0].translation) {
  throw "The complete translation must be shown locally without shortening."
}
Write-Output "PASS targeted colored passage; full harness exit remains $fullHarnessExit."
```

The recorded RED reports `recognition-error`; GREEN reports `caption-complete`
and shows `ไม่ว่าคู่ต่อสู้จะยากที่จะเอาชนะแค่ไหนก็ตาม!` locally at 24 DIP. Both
full-harness attempts still fail on unrelated toolbar region 5 having no caption.
Keep that failure visible; the focused passage assertion does not establish a
passing whole view. All 13 detected regions retain their geometry and the other
12 recognized texts stay unchanged. Region 4's Thai heading wording varies
between the two runs despite unchanged OCR, so translation equality across every
region is not claimed. Private evidence and the executed assertion script remain
under `.cache/replay/45-colored-recognition/`.

The final paper gate requires median pixelwise minimum-channel brightness of at
least 232 and channel-median spread at most 8. Independent channel medians alone
can mistake separate cyan, magenta and yellow areas for white paper; a CPU input
comparison verifies that this mixed-color control keeps the original luminance
input. The final public replay is retained in `green-joint/`, with the original
RED untouched. Neighboring colored headings, the dark-bubble control and all 54
old chart crops retain their previous recognition inputs.

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

### Display heading replay

`display-heading-treatment` exercises the public renderer with a synthetic red
band and yellow lettering: complete local Thai, source-ink coverage, sampled
fill, solid stroke/glow, explicit color/no-effect overrides, visual reuse,
changed-source invalidation, nearby artwork and Stop. The standard suite has
12 cases. `--performance` measures nine fresh frozen copies of each view; the
median must remain under 16.67 ms. This is renderer timing, not live navigation.

Private fixed-text cases may set `capturedView`, `autoStyle`, `localHeadings` and
`sourceInkPoints`. The last field contains independently annotated original
yellow-ink pixels checked on the cover layer before drawing translated text.
It is a sparse coverage check, not proof that the entire lettering mask is clean.
The three-heading reference remains local under `.cache/replay/reference-page/`.
