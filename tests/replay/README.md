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
An overlapping, ready qualified heading must not discard the neighboring bubble.
While the heading is pending, its entire detected rectangle stays unchanged;
the synthetic reader includes a gray gutter for that conservative fallback.
Once ready, its qualified lettering mask protects the heading while allowing
the bubble's punctuation in the shared rectangle to be covered. The case checks
complete local text, independent artwork and outline pixels, fresh/progressive
pixel equality, repeated output, and a return from ready to pending.
A narrow question bubble must keep the Thai particle `เหรอ` on one line while
retaining its complete caption, local placement and font size of at least 20 DIP.
This regression protects a demonstrated dictionary boundary error; it does not
claim complete Thai word segmentation.
The same public case preserves middle dots and a Japanese-range punctuation
character while retaining the existing untranslated-Japanese failure status and
mixed-text filtering. Replay reports identify association badges by their actual
UID; text changed by the renderer must still be reported as a caption and checked
for completeness.
Pale gaps newly enclosed by a qualified source-lettering cover must also be
filled. A diagonal notch checks that an exterior-connected faint border remains
visible, while an off-detection lettering probe requires full-bubble coverage.
A gray cap above the detected text must become coherent white paper before
captions are drawn. Its public regression preserves every outline and antialias
pixel, a separate gray artwork pocket, and a neighboring passage awaiting
translation, while retaining complete local text at 28 DIP or larger.
A pale narration-box case requires each rendered line to share the box center,
retain the complete text at 43 DIP or larger, and preserve every outline and
exterior pixel. Joined bubbles retain their contour-fitting checks.
A distant closed boundary must still cover faint source-lettering fringes while
preserving a separate passage awaiting translation. An open corridor to the
capture edge and halftone dots outside the source region guard against treating
unbounded paper or textured artwork as an expanded bubble interior.
A disconnected thin diagram must retain every stroke while the source lettering
remains covered and its complete caption remains readable.
A panel-cropped bubble must keep its complete caption above the paper gutter,
preserving every gutter, border and exterior pixel. The synthetic regression
uses paired contour endpoints and an opposing straight panel rule; ordinary
open bubbles retain their existing source-cover and artwork checks.
The stylized fixture deliberately records one heading as
`unresolved-before-recognized-text`; it verifies that the evidence keeps the
missing passage separate from a successfully rendered body caption, rather than
claiming support or assigning an unevidenced detector fault.

## Actual live caption evidence

For the approved live-reading check, the normal app can export its current
accepted caption layer once. From the repository root, create the request before
starting a translation session or navigating to another captured view:

```powershell
New-Item -ItemType Directory -Force .cache/live-qa | Out-Null
New-Item -ItemType File -Force .cache/live-qa/request | Out-Null
```

Use the app's normal controls to start translation and leave the source still.
The next confirmed complete or restored caption state writes four files with a
shared timestamp and ID: `.source.png`, `.layer.png`, `.rendered.png`, and `.json`.
The app removes `request` only after successful output. All files remain ignored
and local. A failed or unsupported capture keeps the request for a later attempt.
Snapshots are limited to 16 million pixels for each capture and desktop layer.

This exports the actual accepted WPF canvas, including its clip, without running
a fresh renderer or changing screenshot exclusion. The transparent layer covers
the virtual desktop; the composite covers only `captureBounds`. Captions outside
those capture bounds are preserved in the layer, not in the composite. The JSON
records the running assembly version and module ID, accepted and source
generations, DPI and screen mapping, recognized text, translations, and caption
associations, geometry and fonts. A restored source generation can precede its
accepted generation.

Check one specific exported report with the existing Python environment:

```powershell
.\.venv\Scripts\python.exe tests/replay/check-live-evidence.py `
  .cache/live-qa/<snapshot-id>.json `
  --version "<expected-running-assembly-informational-version>" `
  --source-text "たとえどんなに攻略困難な相手でもね!"
```

Supply a source literal independently read from that captured view; repeat
`--source-text` for other required passages. Omit it only when checking artifact
integrity without making a source-coverage claim. `--version` checks an exact
build version; the module ID is also printed so builds from uncommitted changes
sharing one version can be distinguished.

The script verifies the accepted source PNG hash, decodes all three PNGs, checks
their recorded dimensions and layer transparency, and prints their SHA256 hashes.
It checks caption associations, complete displayed translations (allowing line
breaks and the associated margin number), recorded fonts and the 12 DIP caption
floor. Association badges are excluded from that font floor. Available
translations without captions are reported: browser controls may be skipped, so
they fail this check only when required by an explicit source literal. Required
passages must be recognized exactly and have complete translated captions.

Inspect the source, layer and composite visually as well. Passing establishes
consistency of this accepted WPF still and its recorded associations. It does not
establish correct translation meaning, complete source coverage, compositor
visibility, smooth navigation or absence of flicker. The one requested export
performs image encoding and file writes; exclude that interval from performance
measurements. Without a request, it does no PNG work.

The first live run on 2026-09-28 retains three distinct outcomes under
`.cache/live-qa/`. Snapshot `20260927-221650346-bb552234` has an empty layer while
the maximized app controls exclude the whole desktop; the checker rejects it as
visual evidence. Snapshot `20260927-221711708-932342df` passes with the independently
read colored-opponent literal above and a complete local 24-DIP caption. Its
upper-left bubble still exposes source ink and the right passage uses a margin,
so this is target-passage evidence, not whole-view acceptance. Snapshot
`20260927-221821178-07d44d45` records a later recognition error ending
`目Fでラロ!`; the raw translation retains `ラロ` while the displayed caption
filters it. The checker preserves both the exact-source and complete-caption
failures. That mismatch is not evidence of stale source association.

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
For a fixture with `progressiveRegion`, `--performance` also times completion
after that passage becomes ready, separately from fresh and repeated layout.

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

Set `progressiveRegion` to one region ID in a private fixed-result fixture to
check completion on the same renderer instance. That passage starts pending;
its source rectangle must remain untouched. Once it is ready, captions and
pixels must match a fresh all-ready render, and another identical render must
remain stable. This catches earlier captions retaining an obsolete layout when
a later region's source cover becomes available. It checks rendered stills,
not live compositor flicker.

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

`whiteSourceCoverPoints` similarly checks independently chosen source-lettering
pixels that should become white paper before captions are drawn. The pale-bubble
regression also checks every outline and exterior pixel remains unchanged. These
checks establish the annotated coverage and protection, not general segmentation.

The enclosed-bubble case also includes variable white paper: a gray strip touches
the outline and crosses source lettering. It must receive solid white coverage
while the complete local caption and every exterior pixel remain intact. Two
negative controls combine that shade with a faint outline and different exterior
paper colors, preventing a wider color range from painting through the border.

The distant-bubble regression also moves a source rectangle by one and two pixels
beside antialiased lettering. All three positions must retain complete local text
at least 24 DIP and white coverage of independently placed source ink, while preserving
pending text and every outline/exterior pixel. It catches source-fringe sampling
failures that otherwise send a readable bubble passage to the margin.

### Visible caption alignment

`check-caption-alignment.py` checks the public fixed-replay report and rendered
pixels. It requires the complete translation, a local black caption, the specified
minimum font size, and a maximum spread between the visible centers of its lines.
It measures changed dark pixels within each reported line row, excluding unchanged
source artwork. Use it for black captions over the supplied source frame, with
uniform line height and no overlapping captions; it does not inspect the fitter's
internal centers or masks.

Keep the private source PNG and a fixed-text manifest under `.cache/replay/`.
The manifest must retain the original captured view, DPI, region order, recognized
text and translations. Run the usual fixed replay first, then check its output:

```powershell
.\.venv\Scripts\python.exe -X utf8 tests/replay/check-caption-alignment.py `
  .cache/replay/latest-lower-alignment/shared-center/latest-lower-alignment.json `
  --region 0 --minimum-font 36 --maximum-center-spread 10
```

The private lower-left passage from live export `20260928-005002932-23775f41`
uses all 17 original regions at 144 DPI. Its baseline visible line centers span
23.5 pixels. A 10-pixel limit allows glyph side-bearing differences while rejecting
that visible stagger without reducing the demonstrated 36-DIP font. The checker
writes `.alignment.json` beside the report for both RED and GREEN. Publisher
images and private captured-view manifests remain outside the repository.

The later live source `20260928-043216909-09048ff5` has a separate regression:
its recovered lower passage must retain all text at 39 DIP while limiting visible
line-center spread to 20 captured pixels. Its original spread is 24.5 pixels.
With `.cache/replay/live-upper-cover/lower-fringe.fixture.json` rendered into
`.cache/replay/lower-fringe/best-anchor-live/`, run:

```powershell
.\.venv\Scripts\python.exe -X utf8 tests/replay/check-caption-alignment.py `
  .cache/replay/lower-fringe/best-anchor-live/lower-source-fringe.json `
  --region 2 --minimum-font 39 --maximum-center-spread 20
```

This check covers the local paragraph's visible alignment and completeness;
the independent source-letter probes remain in the private replay manifest.

### Complete passage with explicit punctuation allowances

`check-passage-caption.py` uses a complete-processing report's existing
`intendedPassages` source literal and bounds. It requires matching recognition,
the entire returned translation in one local caption, and a font of at least
12 DIP. Only punctuation characters explicitly supplied on the command line are
ignored in recognition. The original strict stage outcomes are printed unchanged;
a targeted pass does not turn a failing whole replay into a pass.

The private joined-right regression uses
`.cache/live-qa/joined-right-crops/processing.json` and the original
`.cache/live-qa/20260928-005002932-23775f41.source.png` at 144 DPI. Its required
source is `知力・体力・財力と時の運に第六感！使えるものはすべて使ってやるわ！`.
The processing reports retain every detected region. After the normal
`--processing` replay, run:

```powershell
.\.venv\Scripts\python.exe tests/replay/check-passage-caption.py `
  .cache/live-qa/joined-right-crops/green/joined-right-complete.json `
  --passage joined-right --ignore-punctuation '・！!“”'
```

RED omits the substantive clause `使えるものは`. GREEN retains it and presents the
complete returned Thai locally at 28 DIP. The strict full-width `！` versus ASCII
`!` mismatch still reports `recognition-error`; the targeted assertion records the
explicit allowance. Private `red-run.json` and `green-run.json` preserve the whole
harness exit status and worker provenance. Source images and manifests remain local.
