# Reference-page typography correction

The reader supplied `Screenshot 2026-09-27 163339.png` and reaffirmed REF03
(`ChatGPT Image Sep 25, 2026, 08_55_43 PM.png`) as the visual target. The reference's
overly bold speech captions should not replace the current lighter dialogue.
Source and generated-reference images remain local.

## Target

The intended result combines source removal, composition, typeface/weight,
headline size and arrangement, fill colors, outline, glow, gradient/texture and
meaningful phrase emphasis. Dominant foreground-color extraction alone is not
an acceptable completion of tickets #9–#12. Margin fallback protects readability
but does not meet the local promotional-heading target.

## Actual-page evidence

On 2026-09-27 the supplied source screenshot was run through the current local
detector, recognizer, translator and public caption renderer. Diagnostics are in
`.cache/replay/reference-page/baseline/`. The underlying image is 3840 pixels wide;
the inline chat preview has different dimensions. DPI/zoom are replay assumptions,
not measurements of the reader's browser settings.

| Passage | Observed result | Work required |
| --- | --- | --- |
| Top heading | Recognized as `超バイスべなのに`; translated, then rendered in a normal black margin card | Recognition correction plus qualified local reconstruction and display typography |
| Middle heading | Recognized as `恋愛ボンゴツなお嬢様が`; translated, then rendered in a normal black margin card | Recognition correction plus qualified local reconstruction and display typography |
| Lower heading | No matching passage in the recognized-region list | Inspect detection proposals and recognition before attributing a stage-specific cause |
| Middle-left assertion | Recognized only as `この私に...`, missing the source's complete assertion | Recognition/grouping investigation; layout cannot restore missing words |
| Vertical expressive marks | Model returned Japanese marks unchanged; caption became the Thai failure label | Full-text comparison fails; this does not by itself prove that no visual was rendered |

The full-processing verification fails at region 6. Diagnostic output is now
written before verification so a failed assertion retains the evidence.

A separate private fixture supplies fixed Thai heading text and source bounds to
the public renderer with Auto enabled. This isolates typography from translation
quality. It fails the local-heading assertion because the top heading is placed in a margin. The fixed wording is taken as presentation-test input
from the user's reference and is not a translation-quality oracle.

## Acceptance and next implementation order

1. Qualify automatic lettering/effect masks and bounded reconstruction on these
   headings; independently annotate protected artwork and compare source pixels.
2. Render complete local headings with appropriate metrics, fill, real outline,
   glow and texture/gradient treatment. Keep ordinary dialogue at its current
   lighter weight. Current `Outline` is a blurred zero-offset shadow, not a
   distinct solid glyph stroke; the present type treatment needs further work.
3. Correct the demonstrated coverage/recognition losses independently; preserve
   inline color emphasis only with known source-to-translation correspondence.
4. Review a paired real-page render against REF03 and retain timing, cancellation,
   source preservation and live navigation checks.

The passing synthetic readability tests from CHG-020 remain valid for their
limited assertions. They do not establish this reference-page result. No runtime
style upgrade is claimed by this evidence correction, and no new release was cut.

## Heading renderer implementation — 2026-09-27

`DisplayLettering` now qualifies bright yellow/white lettering on continuous red
bands and supplies a bounded source cover, a sampled vertical fill gradient, a
solid glyph stroke and glow. Thai headings fit at the source location using the
selected typeface and actual glyph metrics. Ordinary dialogue retains its weight.
Explicit foreground and no-effect settings override the automatic treatment.
Unchanged source pixels reuse the cover and caption visuals; changed pixels and
style settings invalidate them. Stop clears both visuals and caches.

The private fixed-text replay now places all three headings locally, verifies six
annotated yellow-ink pixels on the cover before Thai is drawn, and checks the
public WPF stroke/fill/effect, visual reuse and settings behavior. These checks do
not establish that every source glyph/halo pixel is concealed. All 12 standard
replays pass. The 4K reference's repeated-view renderer median fell from 57.18 ms
to 9.18 ms after pixel-checked cover reuse (worst 12.10 ms in that run).

This is an implemented heading treatment, **not acceptance of the full reference
result**. Visual inspection still shows reconstruction seams/halo artifacts at
the bands. The qualifier does not reproduce arbitrary textured lettering or
infer the original font. It does not assign source colors to translated phrases
without correspondence. The lower heading in this replay is manually annotated;
the actual detector/recognizer losses above remain unresolved. No OCR model or
translation behavior changed in this implementation. Tickets #9–#12 remain open
against the full target. Bubble/text-box QA is deferred as requested.

Local candidate rebuilt with `scripts/build.ps1` and reopened. No release, remote
issue update or image publication is claimed.

## Live integration correction — 2026-09-27

The reader's follow-up photo showed unchanged Japanese headings and margin
captions. The saved profile was still Dialogue, which intentionally bypasses the
Auto treatment. Replaying the actual recognized headings with that setting
reproduced the margin failure; changing only Auto made those two headings local.
Auto was then selected through the running app and persisted.

The bottom heading had a real detector proposal at confidence 0.291, below the
existing general cutoff. The worker now recovers weak horizontal proposals only
when bright lettering and a continuous red band independently qualify them.
Band extent recovers the clipped trailing area. Strong proposals retain their
original bounds; ordinary weak detections keep the existing cutoff. OCR trials
with inverted/thresholded crops did not consistently improve recognition and
were not adopted.

The recovered heading then exposed a renderer bug: its transparent cover fringe
intersected a neighboring OCR rectangle, rejecting the whole treatment. Display
covers now test actual painted pixels. A genuine conflict still rejects the
cover, including its generic fallback, so a pending overlapping passage remains
visible. The public renderer regression covers both cases.

Verification:

- All 12 standard replays, color/style checks, OCR device/protocol/chart checks
  and scroll-reconciliation checks pass.
- A replay with all 12 actual processing regions and translations places the
  three headings locally. The expressive-mark failure uses its explicit displayed
  failure label in this fixed replay; this is **not** a full-translation pass.
- Full processing still reports the existing expressive-mark translation failure.
  Recognition wording remains imperfect; the lower heading is now recognized
  as `最愛の執事を攻略する`, with the final `話` still omitted.
- The rebuilt running app, capturing the open Senmanga Comet window in Auto,
  reports **12 translated blocks, 3 styled headings**. Its observed status was
  capture 64 ms, processing 7,683 ms, layout 18 ms. This single fresh session is
  not a performance comparison. The count comes from rendered heading visuals.
- The live caption layer is intentionally excluded from screen capture; desktop
  screenshots cannot establish its visual quality. The local composite remains
  `.cache/replay/reference-page/live-regions/live-regions-headings.rendered.png`.

The style path now reaches the actual live session. Cover seams, arbitrary
textures/font imitation, recognition quality and the reader's full visual target
are not declared accepted. Bubble/text-box QA and release remain deferred.

A subsequent renderer regression reproduced red glyph halos outside the detector
rectangle but inside the permitted cover. Removing the detector-only restriction
from red stroke masking makes that regression pass; all 12 replays still pass.
The real-region composite is now
`.cache/replay/reference-page/halo-fix/live-regions-headings.rendered.png`.
A broader continuous-band fill was rejected because it overlapped a neighboring
passage in the real-region replay. Remaining cover seams are visible in this
composite and are not marked fixed. The final bounded change was rebuilt locally.
