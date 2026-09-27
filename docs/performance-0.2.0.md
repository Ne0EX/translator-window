# v0.2.0 reading performance investigation

Date: 2026-09-27. Baseline: `bcd37ee`. First renderer TDD cycle complete;
live Comet verification is in progress; caption appearance requires renderer
evidence or reader observation because the caption layer is excluded from capture.

The reader approved two boundaries: Comet with Translumo during navigation and
Stop; and public caption rendering with fixed translations.

## Prepared live examples

- [Tameshiyo](https://nc.tameshiyo.me/9784094066371?page=1)
- [Comic DAYS](https://comic-days.com/episode/12207421983488249751)
- [Senmanga](https://raw.senmanga.com/%E3%82%B6%E3%83%A9%E3%83%A1%E3%81%AA%E6%81%8B%E3%81%AF%E7%94%98%E3%81%8F%E3%81%AA%E3%81%84%E3%80%80%E8%AA%AD%E5%88%87/1.3)

No live before/after measurement on these readers has completed in this pass.
The Computer Use helper initially exited during initialization. The measurements below
use committed synthetic fixtures, not the three live reader sessions.

Resetting the JavaScript kernel succeeded, but the next initialization reported
`orchestrator_helper_launch_failed`: `codex-windows-sandbox-setup.exe` was not
found. Restarting the host app and retrying desktop access is the next recovery
step; Comet itself has not been identified as the cause.

### Desktop retry and candidate reopening

On 2026-09-27, the helper recovered enough to list windows and read application
controls. Rebuilt and reopened `0.2.0+f11c481` on the interactive desktop before
starting another TDD cycle. The returned window is `Translumo Local 0.2.0`, idle
with Start available and Stop disabled. Build succeeded with NU1900 (unavailable
NuGet advisory feed) and the existing WFAC010 DPI warning.

The helper's screenshot showed underlying windows while accessibility showed
Translumo. Initial diagnosis as a desktop failure was incorrect: `LocalWindow`
and `SubtitleOverlay` deliberately use capture exclusion to prevent OCR feedback.
Accessibility input works when the state request also includes screenshot geometry.
The two approved test boundaries remain unchanged.

Started a Comet window-capture session on Senmanga. The app reported insufficient
layout space; the reader independently observed completed captions flickering and
then disappearing with that message. Stop returned to the idle state with
"Translation stopped." This is evidence of a live failure, not a measurement of
animation frame time. Browser resizing and navigation during that first pass mean
it is not a controlled before/after performance comparison.

## TDD cycle 1: repeated dense margin layout

Run `tests/replay/ReplayCheck.csproj` in Release with `--performance`.
Nine fresh frozen copies of an unchanged captured view go through public Render
and UpdateLayout. Input copying, capture, OCR, translation and compositor
presentation are excluded. The opt-in development target is median <=16.67 ms,
one 60 Hz frame; it is not a portable hosted CI timing gate.

| Dense margin fixture | Median | Worst |
| --- | ---: | ---: |
| Red run 1 | 18.69 ms | 20.91 ms |
| Red run 2 | 18.42 ms | 25.33 ms |
| Green run | 6.77 ms | 8.93 ms |

The margin path repeated Thai segmentation and balanced line measurement.
It now reuses the existing wrapping cache, keyed by text, size, width, typeface
and weight. Each new capture still receives background safety checks. Existing
layout/style invalidation remains in place.

All five cases passed the timing target and retained the same output hashes,
complete caption text and geometry as fresh renders. Dense margin preview and
session/layout lifecycle checks passed. This establishes a fixture rendering
improvement, not a live flicker or scrolling fix. Cold layout, model warmup and
VRAM were not optimized in this cycle.

## TDD cycle 2: margin ending at an incomplete scan row

Changed the existing dense-margin public renderer fixture from 700 to 701 pixels
high without changing its three passages, their translations, or protected art.
The red run threw `SubtitleLayoutException` from the margin fallback. The margin
scan advanced four pixels at a time and never finalized a clear run reaching the
bottom when the captured height was not divisible by four.

The scan now visits the exact bottom boundary once, including after a short final
row. All five public renderer fixtures pass, including complete text, containment,
no collisions, protected artwork, association badges, and deterministic pixels.
The 701-pixel dense case measured 8.78 ms median / 10.63 ms worst for repeated
views. Rebuilt and reopened the candidate for live verification. This fixes a
proven cause of false insufficient-space errors; it does not yet establish that
every live layout rejection or flicker has the same cause.

## TDD cycle 3: clear gutters inside window borders

The live window-capture check still reported insufficient space after cycle 2.
A controlled 500-pixel scroll succeeded, but activation was needed to refresh the
background app's accessibility status; repeated background "Recognizing" readings
are not evidence of an OCR stall. Stop again returned to idle.

Added a public replay case with an 8-pixel dark left border and 16-pixel dark right
border around the existing dense-margin scene. It failed with the same margin
exception. The search previously considered only rectangles touching capture
edges. It now tries bounded 16- and 32-pixel insets after the existing edge search.
The full background and artwork checks still apply. Wider application chrome is
outside this bounded correction.

All six cases pass with complete captions, no collisions and protected artwork
intact. The border case's repeated-view median was 9.22 ms, worst 10.33 ms; the
other five retained their output hashes. This models a window-capture failure;
it does not alone prove that every live failure has been resolved.

## TDD cycles 4–5: validate candidates during the search

The second live attempt still rejected layout. Saved the Computer Use source
capture privately under `.cache/replay/live-senmanga/` and ran the existing
complete-processing replay. It reproduced a caption-placement exception after
eight recognized regions had translations. The capture is a 1524×810 tool image;
96 DPI and zoom 1 in its manifest describe replay assumptions, not measured browser
zoom or desktop DPI. The tool image is JPEG-encoded, so this is a reproduced
captured-view failure, not a pixel-identical copy of the app's original capture.

Temporary diagnostics identified two sparsely sampled margin candidates, both
rejected by full background validation. The search stopped without trying narrower
safe rectangles. A new synthetic case with thin vertical protected rules reproduced
this. Full background acceptance now runs during the search; rejected candidates
do not prevent narrower candidates from being considered. Removed diagnostic prints.

The real replay still failed because the scan tested only the middle row of each
four-pixel band. A thin horizontal border elsewhere in the band contaminated every
candidate. A second synthetic case reproduced this; the scan now samples every
row in each band. Final full-pixel background validation remains mandatory.

All eight renderer cases pass. The slowest repeated-view median is 11.65 ms
(worst 14.33 ms). The private Senmanga complete-processing replay now passes:
eight recognized regions become eight complete captions. Visual inspection confirms
local speech captions and numbered margin fallback; whole-window capture also
recognizes browser bookmark text. This does not assess translation accuracy or
prove flicker-free animation. No source images or private diagnostics are committed.

## Reader feedback and readability fixes — 2026-09-27

The reader accepted speed and performance in the running candidate and supplied
nine additional photos. Review found source lettering left above/below captions,
pale residual lettering, poor dark-fill contrast, and small low-contrast margin
captions. Photo evidence identifies presentation defects; it does not establish
that every leftover passage was detected or translated correctly.

Public-renderer TDD cycles, each observed failing before its implementation:

- Margin readability: the existing dense fixtures failed a 16-DIP minimum plus
  opaque black-on-white check. Margin cards now try 18 then 16 DIP, use left
  alignment, and preserve full translations. For exceptionally crowded views,
  14/12 DIP remain last-resort sizes; a scrollable caption panel is not implemented.
- Bubble coverage: dark and pale synthetic source strokes outside a detection
  survived the old cover. A bounded flood now qualifies a flat closed interior,
  fills enclosed lettering holes with the sampled color, and preserves the
  outline. Pixel assertions check both complete coverage and unchanged exterior.
- Progressive safety: the first bubble implementation covered a second passage
  that was awaiting translation. A regression exposed this; full-bubble covers
  now reject overlap with other detected text regions. Caption fitting uses the
  actual cover mask rather than treating its rectangular bounds as filled.
- Dark-fill contrast: default black captions failed on a dark qualified fill.
  They now use white with a dark effect. Explicit nonblack color choices remain.
- Source styling: Auto failed to retain a known red source ink. On qualified
  fills it now samples a dominant ink color and uses a contrasting effect.
  Existing font/weight choices remain; exact font imitation, multi-ink effects,
  textured/open balloons, and arbitrary artwork reconstruction are not solved.

Bubble search is bounded to one million pixels per region and four million per
render; rejected or over-budget cases retain the earlier footprint fallback.
No model or dependency was added. All 11 fixed-text fixtures pass with identical
repeated pixels and geometry. Slowest repeated-view median: 15.33 ms (worst in
that case: 16.92 ms), against the opt-in 16.67 ms median development target.
This remains renderer timing, excluding capture, OCR, translation and compositor.

The private Senmanga complete-processing replay also passes: eight recognized
regions reach eight captions. Its rendered output was visually inspected for
margin readability and speech covers. Browser bookmark recognition remains
visible in whole-window capture. The nine photos and replay images remain local;
this is not a claim that every pictured defect is now solved or that the new
candidate has received reader acceptance.

## Remaining live comparison

Record viewport, DPI/zoom, capture mode, page
and navigation input for each example. Compare native reading, baseline 0.2.0
and the candidate on the same route. Observe progressive arrival, stable reading,
long scrolling, page turns and Stop. Record caption delay, flicker/misalignment
and source stutter separately; reproduce the next defect before changing code.

## 2026-09-27 display-heading implementation

At the approved public renderer seam, a frozen copy of the private 3840×2088
reference with three fixed Thai headings initially took 57.18 ms median and
62.82 ms worst across nine repeated views. Pixel-checked cover/visual reuse
reduced this to 9.18 ms median, 12.10 ms worst. This measures repeated unchanged
source content, not cold reconstruction or actual browser animation.

All 12 standard replays pass their functional checks. A full performance run
passed with a slowest median of 16.03 ms; a subsequent run reached 16.67 ms on
the existing dense-margin-window-border fixture and narrowly failed the
16.6667 ms gate. The display-heading case in that later run was 2.49 ms median.
This timing variability remains recorded; the frame budget has not been raised.
No new live scrolling comparison was performed in this typography slice.

Build succeeds with the existing high-DPI warning and NU1900 (the NuGet
vulnerability service is unavailable). Private source images remain local.
See [visual limits](feedback/2026-09-27-style-target.md) before interpreting
passing text/coverage samples as reference-quality reconstruction.
