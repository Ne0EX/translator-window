# v0.2.0 reading performance investigation

Date: 2026-09-27. Baseline: `bcd37ee`. First renderer TDD cycle complete;
live Comet verification is blocked by desktop connection failure.

The reader approved two boundaries: Comet with Translumo during navigation and
Stop; and public caption rendering with fixed translations.

## Prepared live examples

- [Tameshiyo](https://nc.tameshiyo.me/9784094066371?page=1)
- [Comic DAYS](https://comic-days.com/episode/12207421983488249751)
- [Senmanga](https://raw.senmanga.com/%E3%82%B6%E3%83%A9%E3%83%A1%E3%81%AA%E6%81%8B%E3%81%AF%E7%94%98%E3%81%8F%E3%81%AA%E3%81%84%E3%80%80%E8%AA%AD%E5%88%87/1.3)

No live before/after measurement on these readers has completed in this pass.
The Computer Use helper exits during initialization. The measurements below
use committed synthetic fixtures, not the three live reader sessions.

Resetting the JavaScript kernel succeeded, but the next initialization reported
`orchestrator_helper_launch_failed`: `codex-windows-sandbox-setup.exe` was not
found. Restarting the host app and retrying desktop access is the next recovery
step; Comet itself has not been identified as the cause.

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

## Remaining live comparison

After desktop access is restored, record viewport, DPI/zoom, capture mode, page
and navigation input for each example. Compare native reading, baseline 0.2.0
and the candidate on the same route. Observe progressive arrival, stable reading,
long scrolling, page turns and Stop. Record caption delay, flicker/misalignment
and source stutter separately; reproduce the next defect before changing code.
