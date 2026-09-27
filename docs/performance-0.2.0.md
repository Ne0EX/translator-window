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

## Bubble and three-reader stress slice — 2026-09-27

The reader accepted the narrow typography treatment and deferred broader styling.
Checkpoint `06c140b` was committed and pushed before this slice. Release and
seamless-reading acceptance remain pending.

Changes at the approved public renderer boundary:

- Sample twelve positions around detected text to avoid starting the closed-bubble
  fill on a glyph. Existing bounded flood, outline and pending-passage guards remain.
- Center eligible captions on the qualified plain container rather than an
  off-center detection. Light/dark bubble, exterior artwork and pending-text checks pass.
- Reuse layout/cover caches when separately captured frozen frames have identical
  pixels. Changed frames still invalidate the caches.
- Fill margin columns by measured height instead of splitting caption counts.
  Avoid page arrows with two extra bounded gutter insets, and account for existing
  captions that intersect a margin without being fully contained in it.

### Evidence

Synthetic public-renderer regressions first reproduced glyph-seed coverage and
off-center placement failures. The two newly captured dense reader views then
failed with `SubtitleLayoutException` at the margin fallback. Both now render a
complete scene without that exception. A redistribution-safe navigation-arrow
fixture joins the suite; all 13 fixtures pass deterministic pixels, text/geometry,
and the unchanged 16.67 ms repeated-view median gate. Latest slowest median:
1.14 ms; worst individual sample: 4.57 ms.

The previous Senmanga eight-region fixed replay measured 32.92 ms median / 38.59 ms
worst with the checkpoint DLL, then 1.64 / 4.91 ms with identical-frame reuse.
New fixed captures measured 3.46 / 6.91 ms for Tameshiyo and 2.98 / 6.24 ms for
Comic Days. These are nine repeated-view renderer samples, not cold layout,
translation latency, browser FPS or a guarantee against caption flicker.

Live Comet checks used the three supplied URLs, then saved 1524×810 source captures:

| Reader | Exercise and observed outcome |
| --- | --- |
| Senmanga | Two large upward scrolls from the color section into dialogue. Fresh status: 11 blocks, capture 19 ms, processing 1,010 ms, cached layout 0 ms. Separate captured-view full processing passes with 10/10 recognized regions rendered. |
| Comic Days | Two forward spread turns. Fresh status: 11 blocks, capture 17 ms, processing 313 ms, cached layout 0 ms. Captured replay exposed arrow-obstructed margins and partial caption/margin overlap; corrected. |
| Tameshiyo | Cover navigation, interior jump, forward/back reversal, ending on reader counter 6/27 (printed page 235). A space failure was observed; a later live result showed 23 blocks, capture 21 ms, processing 356 ms, layout 808 ms. Captured replay reproduced the column-split failure; corrected. |

The full-processing reruns for Comic Days and Tameshiyo still fail strict text
verification, rather than placement. Comic Days includes Japanese punctuation or
untranslated output that the existing Thai safety filter changes; Tameshiyo's first
flag is recognized browser bookmark text. Diagnostics retain the original model
output and rendered result. No failure was relabeled as a successful translation.
Whole-window capture also recognizes browser controls; Comic Days' browser
translation popup was present. These are not clean manga-only accuracy samples.

Private captures/manifests, fixed translations and composites are under
`.cache/replay/three-readers/`; checkpoint comparison is under
`.cache/benchmarks/bubble-baseline/`. Images remain local. The replay composites
were visually inspected: dense views still rely heavily on margin captions,
some source text remains, and small badges can clip. This is not the seamless
in-bubble target yet.

Live Stop returned `Translation stopped`. The layout/session lifecycle check
passes after adding its missing existing `DisplayLettering.cs` compile input.
Release publish succeeds with the existing NU1900 advisory-feed warning.
Native screenshots exclude the caption layer, and sparse UI snapshots cannot
establish animation smoothness; final live visual acceptance remains open.

### Tradeoffs and next acceptance work

Exact frame reuse adds one pooled pixel copy/comparison when a new frame arrives;
changed-view overhead has not been isolated. The gutter search has two extra
bounded insets when earlier candidates fail. Neither change adds model work or
VRAM allocation. Greedy column filling still cannot solve arbitrary obstacles.
The existing 12-DIP minimum was retained; dense-view margin text is still smaller
than desired. Prioritize more local bubble placement, complete source coverage,
badge readability and cold-layout cost before release. Preserve current translation
speed and obtain matched live scrolling/page-turn evidence for the final build.

## Prefer local bubble placement — 2026-09-27 follow-up

Reader feedback rejected excessive margin captions. Qualified plain containers
now supply fitting width/height and compact 2-pixel padding. For rounded bubbles,
each centered text line may fit the filled mask even when the empty corners of
the overall text rectangle do not. The fallback checks every line rectangle
against the qualified cover; it does not authorize painting text across artwork.
Short source utterances may now qualify closed bubbles up to 9,216 pixels in area
without failing the existing eight-times-source-area limit. Other flood, ink,
component and pending-passage checks remain in effect. Cached captions retain
their chosen padding. The 12-DIP floor and translation pipeline are unchanged.

The initial long-text bubble case failed with a space exception. After narrowing
the regression to a fitting example, its final long-text case still fails against
the saved checkpoint DLL and passes against the candidate. An isolated Comic Days
short utterance also failed annotated bubble containment before the small-bubble
qualification change and passes afterward. All 14 standard renderer fixtures and
the layout/session lifecycle suite pass. Existing exterior-pixel and pending-text
checks remain intact.

With the same fixed translations and source captures, matching local captions
increase from 1 to 6 on Comic Days (five manga captions recovered; the existing
local browser label is included in both counts), and from 0 to 1 on Tameshiyo.
Matching margin captions decrease from 16 to 11 and 18 to 17 respectively.
Senmanga's eight-region capture remains four local/four margin captions. Strict
full-text verification still flags the previously documented Japanese-output
sanitization mismatches; these counts exclude mismatching output and do not
establish translation accuracy. Private composites are in `.cache/replay/local-placement`.

Repeated-view medians: Comic Days 4.06 ms, Tameshiyo 5.34 ms, Senmanga 2.60 ms.
New first-layout instrumentation records 2,227 / 3,008 / 1,007 ms respectively,
including first-use renderer work. A checkpoint Comic Days run measured 2,691 ms
first layout and 8.41 ms repeated median, but these isolated runs are noisy and
do not establish a live latency improvement. The extra line-mask fitting runs
only during layout search; unchanged frames reuse visuals. First layout remains
too expensive to call optimized. No extra model calls or VRAM model allocations
were added. Small, long-word and complex/open containers still use margins;
the reader's request for predominantly local captions is not fully satisfied.

## Bubble boundary conflict correction — resumed QA

A public-renderer regression showed that a pending region in the empty corner of
an oval bubble's bounding rectangle incorrectly rejected the whole source cover.
Bubble conflicts now use the existing per-pixel mask check already used by display
headings. Actual overlapping pending text remains protected. The new regression
fails before the one-line correction and passes afterward; all 14 standard cases
pass, including exterior artwork preservation and pending-region protection.

The three existing captured views retain exactly the same rendered pixels as
before this correction. Repeated medians in this run: Comic Days 3.19 ms,
Tameshiyo 3.36 ms, Senmanga 1.86 ms. These are renderer replays, not browser FPS.
Comic Days and Tameshiyo retain their documented strict text-verification failures.

A fresh live Senmanga source capture (1524 by 810; browser already reported Restore)
was replayed through the local OCR/translation pipeline. Ordinary dialogue renders
locally; remaining margin captions include browser-toolbar detections, text over
artwork, and clipped passages at the viewport top. The run fails strict text
verification for region 10: the translator returned Chinese for the Japanese
utterance and the Thai safety filter shows its failure label. This is not a
caption-placement failure. Private source, diagnostics and inspected composite:
`.cache/replay/live-bubble-followup/result/`. Browser chrome must not be counted
as failed manga bubble placement. No new live animation smoothness claim or
release acceptance follows from this check.

## Fit translated text to the bubble area — 2026-09-27

Reader correction: use the available bubble area, rather than shrinking a uniform
text box or pushing text into side captions. Qualified Thai bubble layout now
measures the filled contour over each proposed line's height. It chooses word
breaks using each line's available width, allowing shorter edge lines and wider
middle lines. Complete text, Thai word boundaries, source covers and outline
protection remain required. The starting font size comes from the bubble height
(up to 72 DIP), replacing the fixed 24-DIP ceiling for qualified containers.
The existing 12-DIP floor remains. Cached visuals retain their line-height settings.

The roomy-bubble public-renderer test failed at the old ceiling and now renders
at 32 DIP, with all text inside the bubble and exterior pixels unchanged. All 14
standard cases pass, including the long-passage, off-center detection, dark-fill,
pending-text and replay stability checks. The layout/session lifecycle suite
passes. An early contour-only version lost two local captions on the Comic Days
capture, so it was rejected: the final code retains the existing local-placement
fallback before margin captions. Final Comic Days and Senmanga counts remain
6 local/11 margin and 4 local/4 margin, respectively; this change improves fitting
and sizing, not recognition completeness or translation accuracy.

Final repeated-view medians: Comic Days 2.32 ms, Senmanga 2.06 ms, Tameshiyo 2.51 ms.
First layouts: 1,545 / 810 / 2,117 ms, including first-use work. These are separate
replay runs, not a matched live FPS comparison. The existing Comic Days/Tameshiyo
strict text-verification failures persist. No translation model or VRAM changes.

Tradeoff: additional bounded contour scans and word-break search on a new layout;
unchanged frames reuse captions. Contour search is limited to 64 word segments and
16 lines, with centered lines; longer or asymmetric passages keep the established
local fitter and, when necessary, the margin fallback. Private inspected examples:
`.cache/replay/contour-standard/` and `.cache/replay/contour-real/`. General bubble
segmentation, asymmetric placement and final visual acceptance remain open.

## Senmanga reading acceptance follow-up — 2026-09-28

The current chapter was opened in Comet with Computer Use. The rebuilt app ran
in window capture mode, processed successive views after scrolling, and stopped
through its Stop control. One settled status reported 11 translated blocks,
19 ms capture, 383 ms processing and 0 ms layout. These status fields are not
first-caption latency or an animation/FPS measurement. Windows capture excludes
the caption layer, so source screenshots alone cannot establish live visual
acceptance. The source captures and inspected renderer composites remain private
under `.cache/replay/senmanga-acceptance/`.

The reading checks exposed several shared causes, now covered by renderer
regressions and private captured-view replays:

- A prior margin placement remained cached after the captured view changed,
  preventing a return to the bubble. Changed views now retry local fitting;
  identical views retain visual reuse. The regression renders bubble, artwork,
  then bubble on one overlay and requires the original complete caption, font
  and geometry to return without a margin badge.
- Rectangular detections of joined bubbles include exterior corners. Requiring
  every rectangle pixel inside the fill rejected usable containers. An interim
  95% threshold still rejected offset lobes and was removed. Covers paint only
  the closed contour; bounding, area, ink and neighboring-source checks remain.
  Equal-color seed candidates now prefer the point nearest the source center.
- White counters inside Japanese glyphs inflated the artwork detector's ink
  density. It now counts actual ink and reserves the compact-artwork rejection
  for near-solid components. A separate connected-artwork guard caught a real
  hair-region regression. Caption placement also stops treating an unqualified
  source rectangle as if an opaque cover already protected it.
- Tall, narrow detections can belong to wider bubbles. Horizontal search now
  includes the detection height, within the existing work and pixel budgets.
- A narrow connecting neck may remain empty while complete words occupy both
  lobes. Empty rows count toward the existing 16-row limit, rather than being
  restricted by the number of words. No font-floor reduction or text shortening
  was introduced.
- The recognizer omitted a lower passage when a tall joined crop was squeezed
  into one model input. One sufficiently wide white separator can now produce
  two recognition crops, concatenated under the original region geometry. Inner
  cuts have no overlapping padding. The saved two-passage case fails before
  this correction and passes after it; the adjacent joined view retains its
  recognized text. Existing protocol checks also pass.

The final isolated standard run passes all 14 cases, with repeated-view medians
of 0.21–0.93 ms and a 3.31 ms worst repeat. Positive tests still require complete
captions. Two artwork/pending-ink negative cases may reject placement when no
safe margin exists; rejection must leave an empty caption layer and preserve
the protected source pixels. A real hair-protection replay also passes.

Observed local results include a connected caption at 24 DIP, a spiked thought
bubble at 14 DIP, a wider warning bubble at 24 DIP, and a grade-reaction bubble
improving from 12 to 21 DIP. Offset joined captions now render locally, but
centered lines leave some upper lobes unused. Contextual ICU segmentation can
still split a colloquial Thai word poorly. One connected white region slightly
whitens five floor pixels outside the intended bubble (maximum 26 gray levels);
container-boundary quality is not yet a general guarantee.

Tradeoff: broader bounded searches and extra line arrangements add work on new
views; a qualified joined passage can require one additional recognition crop.
Unchanged views still reuse captions. No additional model or VRAM allocation
for a new model was introduced; the extra OCR latency has not been isolated.

The continuing chapter audit also finds source lettering over artwork, missed
stylized text, open-edge bubbles and translation-meaning errors. A processing
command's caption count only covers recognized regions. It does not establish
that every required source passage was found. Full chapter acceptance and the
v0.2.0 release gate remain open.

## Offset bubbles and punctuation — 2026-09-28

Three further public-renderer regressions failed before their corrections:

- Offset joined bubbles now position each line within its own safe horizontal
  strip. WPF text effects preserve normal text shaping and cached placement.
  The test requires visible translated glyphs in both lobes, complete text,
  and unchanged outline/exterior pixels.
- A narrow caption could report complete text while clipping the ellipsis
  glyph. Candidate widths now also have to contain measured wrapped glyphs;
  the regression checks the rendered dots.
- A small source ellipsis dot was classified as compact artwork, rejecting a
  normal closed bubble. Compact marks wholly inside the detected text and no
  larger than one sixth of its shorter dimension are now treated as possible
  punctuation. Larger compact components and marks outside the detection keep
  the artwork guard. This is a size heuristic, not general glyph segmentation.

| Private captured view | Observed change |
| --- | --- |
| 07, offset joined speech | 13 → 16 DIP; both lobes now used |
| 15, joined question | 14 → 17 DIP; leading punctuation still attaches to the following word |
| 19, ellipsis | Clipped 27-DIP glyph → complete three-dot glyph at 22 DIP |
| 24, narrow oval | Margin → two local lines at 20 DIP; all-region replay also keeps the other bubble local at 18 DIP |

All 14 standard cases pass. The final punctuation run's repeated-view medians
were 0.16–1.14 ms, with a 4.15 ms worst repeat. Dense fixed-result views 03, 04,
and 19 also passed without exhausting the existing work budget; their repeated
medians were 1.78 / 2.78 / 1.84 ms. First layouts in those separate runs took
0.938 / 1.272 / 0.873 seconds. These are renderer measurements, not live FPS or
translation latency. The additional contour scans and glyph measurements run
when fitting new captions; unchanged views retain reuse. No model was added.
The integrated Release publish and session lifecycle checks pass, including
navigation invalidation, progressive captions, recovery and Stop. Publish retains
the existing NU1900 advisory-feed and WFAC010 manifest-DPI warnings.

Full processing of saved views 23–26 on the published `92610a8` baseline passed
the recognized-region checks. The visual audit still finds a textured apology
bubble in the margin, an open-edge bubble, and translation-meaning errors.
An earlier joined greeting still has a small source-cover boundary leak; its
strict annotated-container check remains failing. Source lettering over artwork
and special-lettering detection gaps also remain. The private source-coverage
inventory distinguishes these from ordinary-bubble detection failures.

The rebuilt baseline ran through Computer Use and reported seven translated
blocks, 19 ms capture, 280 ms processing and 0 ms reused layout at one settled
view. Subsequent live capture was interrupted by unrelated windows in the
capture; automatic approval review rejected those screenshots. The reader
cleared Discord, but an overlapping terminal still prevented the next capture.
These events do not establish current-build navigation or full chapter acceptance.
Evidence remains local under `.cache/replay/senmanga-acceptance/`.

### Open-edge short reply follow-up

The saved view 20 reply had a qualified lettering cover but no closed container.
Its width candidates skipped usable space between the narrow detection and the
widest paragraph. The existing 65% and 80% width candidates now also apply when
no closed container qualifies. A synthetic open-bubble public replay failed
before this change and passes afterward, requiring a complete local caption,
concealed source lettering and preserved curved border/artwork.

The exact ten-region captured-view replay now places the short reply locally at
13 DIP in a 78×38-image-pixel rectangle; all 3,746 checked nonwhite exterior and
border pixels remain unchanged. Image metadata is 96 DPI with zoom 1; DIP font
size and image-pixel bounds are different units. All 14 standard cases pass,
with repeated medians of 0.17–1.06 ms and a 3.50 ms worst repeat. The actual-view
repeat median is 2.65 ms, worst 5.74 ms; first layout took 742 ms. Two additional
bounded width candidates per font are the new-layout cost. This does not qualify
arbitrary open/textured containers or resolve the longer open-edge apology.

### Roomy short utterances and edge sampling — 2026-09-28

Two further saved dialogue failures now stay local. Both have failing/passing
checks through the public renderer, with fixed translations.

- View 23: a 24×74-pixel text region was inside a 124×166-pixel closed bubble,
  exceeding the old area ratio. The bounded area check now also permits four
  times the square of the longer detected dimension. Existing closed-contour,
  ink, artwork and neighboring-passage checks still apply. The complete caption
  renders locally at 26 DIP. All seven regions are retained; 1,783 checked dark
  outline/exterior pixels and all 7,795 pixels outside the annotated bubble in
  its immediate neighborhood remain unchanged. Solid sampled RGB 249 replaces
  faint interior texture. An empty leading line still places the caption low.
- View 26: a tiny furigana fringe outside the detection distorted one row's
  background average, rejecting an otherwise plain open-edge bubble. Background
  sampling now uses the median of the existing two to six pixels per side.
  Sample-error, smoothness, ink and artwork thresholds are unchanged. The full
  caption renders locally at 14 DIP. All 4,024 pixels outside the detection but
  under its enlarged caption remain unchanged by the source cover. The fringe
  itself remains visible outside the detection.

All 14 standard renderer cases and the private hair-protection control pass
with both changes. Before the median change, view 23's repeated renderer median
was 2.00 ms, worst 4.66 ms; its first layout took 633 ms. No combined timing
claim is made from the subsequent correctness-only run. The median sorts at
most six bytes per color channel; no model or new dependency was added.

Computer Use source inspection has now reached the chapter-end controls,
including the color promotion. Saved views 28–46 add literal expected passages
for later complete-processing checks. These captures were made while Translumo
was idle and therefore do not establish live overlay acceptance. The rebuilt
app's live navigation check, complete source coverage and caption balance remain
open. The faint-border follow-up below addresses the saved boundary leak.

The observed Comet zoom was 80%; the new manifests were corrected from the
capture helper's default of 1 to 0.8. Existing view 29/30 reports retain their
original metadata and the correction is recorded in the private provenance.
Image metadata remains 96 DPI; replay font sizes are WPF DIP and geometry is
reported in captured-image pixels.

The missing white narration in view 15 is confirmed as detector filtering loss:
the full-view proposal scores 0.28065 and is discarded before NMS at the 0.3
prefilter. A manga-column diagnostic crop recovers one fragment, but the required
right passage scores 0.33727 and fails the later 0.4 threshold. Cropping alone
does not recover the passage. No production detection threshold was changed.
All image evidence and diagnostic outputs remain local under `.cache/replay/`.

### Faint bubble boundary follow-up

View 11's fill crossed a pale gray boundary into adjacent floor artwork. The
connected area still closed inside the search rectangle, so the existing closure
check accepted it. Tightening only the flood's per-channel color tolerance from
16 to 12 preserves that boundary. Seed consensus, ink classification and artwork
guards are unchanged. A public synthetic regression fails on the old tolerance
and passes with the correction, requiring full source-text coverage and an
unchanged neighboring floor and outline.

The strict seven-region replay now passes: its fill bounds narrow from
299×164 to 228×164 image pixels, the complete caption remains local at 15 DIP,
and all five previously whitened floor pixels are unchanged. The roomy textured
bubble, hair protection, long joined passage and all 14 standard cases pass.
The offset-bubble replay keeps a complete local 16-DIP caption; its unrelated
toolbar classification assertion still fails. No new timing claim is made from
these correctness runs. The tighter tolerance may reject other varying fills;
it does not establish general bubble segmentation.

### Lifecycle fixture correction

The session suite initially timed out waiting for five progressive captions.
An instrumented comparison reproduced the same failure on `125a594`: all five
translations completed, then layout refused the synthetic view. Marker 7's red
square extended beyond its declared text region into protected background.
Moving only that fixture marker inside the region restores the intended test.
Production artwork guards and assertions are unchanged. The corrected complete
suite passes progressive captions, navigation cancellation, OCR during scrolling,
caption reuse and Stop. The integrated Release publish also passes, retaining
the existing NU1900 advisory-feed and WFAC010 manifest-DPI warnings.

### Curved open bubbles and chapter coverage — 2026-09-28

View 32's past-concern caption was rejected because the empty corners of its
rectangle crossed a curved border. Its qualified plain source cover now permits
the existing background check to evaluate each complete text-line strip. All
strips must pass the same contrast and artwork checks; the caption background
is transparent. Closed-container fitting and the 12-DIP floor are unchanged.

The public curved-open-bubble replay failed before the change and passes after
it, including original-ink concealment and exact border/artwork preservation.
The eight-region captured-view replay keeps the complete passage local at
12 DIP in five lines. All 1,223 checked dark exterior pixels and both checked
side-border strips remain unchanged. An older open-bubble control now fits at
23 DIP; its annotation allows transparent padding while its 3,798 protected
pixels remain unchanged.

All 14 standard cases pass. Repeated-view medians are 0.25–1.01 ms, worst
3.14 ms. The actual view 32 repeat median is 3.20 ms, worst 5.53 ms; first
layout took 969 ms. These are renderer timings, with no matched baseline for
this slice. Additional per-line checks run only when the rectangle fails;
existing work limits and unchanged-view reuse remain in place.

Complete processing of 16 later chapter captures on the preceding area/median
build produced six strict passes and ten failures. Four required-passage
failures differ only in punctuation; another preserves the required words
across two regions. Remaining content defects include a missed white narration,
recognition errors in the appeal and color heading, omitted colored words,
a missing promotional title, and grouping of two right-side color passages.
Whole-capture counts include browser controls and cropped neighboring text;
they are not a count of manga bubbles or a measure of manga margin use.

A fixed-translation comparison against `b2aa679` found that its stricter fill
rule regressed one pale color bubble from a local caption to the margin.
The app refresh was held while correcting that regression. Passing the earlier
boundary controls did not establish general fill quality. Source captures and
all strict failures remain private under `.cache/replay/senmanga-acceptance/`.
Full live chapter acceptance and the release gate remain open.

### Pale fill and joined-container recovery

The pale color-bubble regression is corrected. Flood connectivity retains the
12-level darker limit, while allowing up to 16 levels brighter only for pale
fill. Dark fills keep the stricter limit in both directions, using the existing
145 luminance convention. A pale-highlight regression failed before this
change. A mirrored faint-light-outline control then caught an overly broad
version; both now pass with exact exterior checks.

Saved view 45 returns to a local 23-DIP caption, covering all 5,892 checked
dark/red source-lettering pixels. The full view retains its existing unrelated
toolbar assertion failure. View 11's entire composite is byte-identical to
the protected-boundary reference; all 10,660 checked floor pixels are preserved.
The color bubble still has visibly jagged transitions between sampled gray fill
and bright interior texture. Complete lettering coverage does not establish
seamless background restoration.

View 41's joined bubble was rejected because its first sampled point was on
matching white paper outside the contour. After an escaped flood, the renderer
now tries at most one other existing sample outside the visited component,
keeping the same fill and qualification rules. Each actual attempt reserves its
full search area from the existing four-million-pixel render budget; arrays are
reused. Rejected artwork/ink qualifications do not trigger another attempt.
The public regression fails before this change and passes afterward, including
an opened-outline control, complete text, and unchanged border/artwork pixels.
The saved passage is now local at 22 DIP across both lobes. Its original
recognition errors and toolbar assertion failure remain recorded.

The combined 16-view fixed-text comparison introduces no local-to-margin moves,
missing captions, or work-budget exceptions. Its original six strict passes and
ten failures remain unchanged. No text was shortened. The final current-source
run passes all 14 standard renderer cases with identical repeated renders:
repeat medians 0.19–1.51 ms, worst repeat 3.46 ms, first layouts 76.51–504.69 ms.
These are isolated renderer measurements, not live FPS or translation latency.
The extra flood and per-line checks affect new layouts; their individual costs
have not been isolated. The complete session lifecycle suite and Release publish
pass, retaining the existing NU1900 and WFAC010 warnings.

Further diagnosis of view 15 rejects a quick threshold-only detector fix. A tight
proposal rerun recovers only tiny fragments; the existing recognizer mixes
neighboring columns in both the broad and narrower proposals and misses the
required literal passage. No production detector threshold, OCR model, or
translation behavior changed. Full live chapter coverage, caption balance,
color-fill seams, and these source-content losses remain open.

Follow-up diagnosis explains the remaining color seam: view 45's bright lower
interior connects to the neighboring passage through an open white neck. A
four-connected 138-step path from `(550,450)` to `(530,568)` stays at or above
250 in every color channel, crosses no dark outline, and remains outside the
current cover. The sampled gray component is not a complete text-container
boundary. A wider color threshold alone is therefore not a justified fix;
handling the shared area must preserve both passages and neighboring artwork.

One additional private OCR probe inverted luminance on view 15's existing
narrow crop. It still mixed neighboring columns and omitted the required
literal passage. The initial `一` is visible inside the crop, so its loss is
recognition failure rather than clipping. These diagnoses changed no production
code or published app binary; the `81954c3` checkpoint remains unchanged.

### Neutral bubble paper and remaining fit limits — 2026-09-28

Qualified closed bubbles now reconstruct neutral near-white paper as white.
Every sampled RGB channel must be at least 232, with at most eight levels
between channels. Sampling, flood connectivity, the coverage mask and artwork
qualification still use the original color. Footprint and gradient covers are
unchanged. This intentionally flattens faint gray shading; genuinely tinted
and darker bubbles retain their sampled fill.

The public renderer regression fails on the preceding binary and passes with
the change. All 14 standard cases pass, including pale tint, dark fill, gradient,
pending text, border and artwork controls. View 45 retains its complete local
23-DIP caption and identical placement, with the large gray/white seams removed.
Some pale edge flecks remain. All 124 checked outline pixels are preserved;
view 11 is byte-identical to the protected-floor reference.

The direct 16-view comparison against the `81954c3` combined renderer changes
no caption bounds, font, text, lines or fallback decisions. Its six strict
passes and ten existing failures remain unchanged. The private direct report is
`neutral-paper-comparison/versus-81954c3-combined.json` under the later-chapter
processing evidence. The runner's separate comparison against the older median
baseline contains earlier improvements and must not be attributed to this fix.

View 19's remaining long margin caption has a valid source cover. Exhaustive
vertical placement with the existing complete-word boundaries and full-height
line strips still cannot fit at 12 DIP. It fits at 11 DIP, below the readability
floor, so the floor and full translation are retained. This establishes a limit
of the current layout model, not of every possible typographic arrangement.
No model inference or source-text rewriting was used in these layout checks.

### Preserve neighboring text without discarding the bubble

View 45's right passage occupies one connected two-lobed bubble. Its qualified
cover touched 524 pixels of a separate lower heading, causing the entire cover
to be rejected. Closed bubble covers now exclude conflicting detected regions
that lie outside their own text region. A neighbor overlapping the target text
still rejects the cover; display-heading covers retain their previous guard.
One copied mask governs both painting and caption fitting. Exclusion scans use
the existing work budget and cancellation checks; frozen-view reuse is retained.

The public Thai regression fails before this change and passes afterward,
checking a real source stroke plus exact pending-text, outline and artwork
preservation. View 45's complete right passage moves from the margin at 18 DIP
to the bubble at 19 DIP across 11 lines. Every pixel in the pending lower
heading's detected region is preserved, as are the checked colored halo pixels
outside the target text. The known unrelated toolbar sanitation failure remains.

The final 16-view comparison introduces no lost captions, local-to-margin moves
or new strict failures. View 46's two complete captions increase from 24 to
25/27 DIP; a nearby small caption decreases from 15 to 14 DIP to respect the
neighbor exclusion. That one-DIP reduction is an accepted placement tradeoff.
Other reported changes are line wrapping and the margin rows moving upward
after a caption returns to its bubble. No translation is shortened.

All 14 standard renderer cases repeat exactly. Current-source repeat medians
are 0.23–0.96 ms, worst repeat 3.33 ms; first layouts are 74.41–511.75 ms.
These are isolated renderer measurements, not live FPS or translation latency.
The complete session lifecycle suite passes, including progressive captions,
navigation recovery, stale-result rejection and Stop. Release publish succeeds;
the existing NU1900 advisory-feed and test-build WFAC010 warnings remain.
Private evidence is in `.cache/replay/bubble-neighbor-final-results/` and
`.cache/replay/senmanga-acceptance/neighbor-final-comparison/`. Full live chapter
acceptance and the previously documented source-content losses remain open.

### Recover colored words on neutral bubble paper — 2026-09-28

The missing red words in view 45 were a recognition error: the detector crop
contained the whole passage, but luminance grayscale weakened the colored ink.
The shared Japanese recognition path now uses the darkest RGB channel when its
median brightness is at least 232 and the crop's median channels differ by at
most eight levels. Checking brightness jointly prevents separate colored pixels
from falsely implying white paper. Other crops retain their previous input.
This is a crop-level paper estimate,
not bubble segmentation; crop geometry, grouping, model weights and decoding
remain unchanged for both supported recognition backends.

The complete-processing replay first failed the intended source literal
`たとえどんなに攻略困難な相手でもね!`, then recovered it after the change. Its
actual Thai translation is complete and local at 24 DIP in bounds
`[351,456,191,204]`; the rendered result was visually inspected. All 13 detector
regions retain their bounds. Only the target's recognized text changes. A
separate heading's translation varies despite unchanged recognized text, so
this comparison does not establish deterministic translation. The full-view
check still fails on the existing unrelated toolbar caption; the targeted
passage check passes without changing the full-view assertions.

The prior bounded probe also recovered the same passage in view 46. Five colored
heading crops and the dark-bubble control retain their original preprocessing
path. All 54 saved chart crops produce identical grayscale pixels. On view 45's
13 crops, 31 warm preparation measurements increase the median from 0.62 to
10.17 ms: about 9.54 ms extra per newly recognized view on this machine. This
excludes model inference and is not a live frame-rate measurement. Known-region
reuse and the renderer are unchanged. Private source, RED/GREEN reports, timing
samples and runnable checks remain in `.cache/replay/45-colored-recognition/`;
the reproduction recipe is in `tests/replay/README.md`.

Review caught a colored-background counterexample before commit: independent
channel medians could classify cyan/magenta/yellow pixels as white, then collapse
their contrast. The final joint-brightness guard rejects it and retains the
original input. The complete-processing target passed again with that guard;
the timing above measures the final guarded implementation.

Before this OCR change, the refreshed `f1094d6` app was exercised through Start,
Senmanga scrolling and Stop. A settled view reported 16 translated blocks and
one styled heading; Stop restored the idle controls and terminated model
processes. Computer Use now operates the capture dropdown using screenshot-only
popup observations. Its screenshots exclude the caption layer, so this live
check establishes session behavior, not visual overlay acceptance or smoothness.
The chapter-wide acceptance goal and the other documented detection/recognition
losses remain open.
