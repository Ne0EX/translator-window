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
- Reject results from obsolete frames. Manga overwrite holds the previous artwork
  and translations together during scrolling, then swaps in the completed new view.
- Keep controls accessible; subtitles neither intercept input nor appear in OCR captures.
- Stop/cancellation clears presentation, terminates active workers, and releases models.

The implementation deliberately uses whole-frame stability and rectangular masks.
Animated pages need per-region tracking; artwork restoration would need inpainting.
Neither is claimed by the current implementation.

## Recorded checks on 2026-09-22

Test machine: Windows, Ryzen 7 5800H, RTX 3060 Laptop, one 4K display at 150% scaling.

| Check | Evidence |
| --- | --- |
| Local Windows build and self-contained publish | Workspace .NET SDK 8.0.425; only the known WPF/WinForms DPI manifest analyzer warning |
| Four source-language OCR paths | `tests/OcrSmoke`: Windows/Tesseract, comic crops, Japanese vertical text, oversized images, physical capture |
| Local translation protocol | `local-model/check.py`: blocked sockets, all twelve directions among four languages, comic/plain prompts, input/output limits, CUDA fallback, cache, missing assets |
| Cancellation and restart | `local-model/check/BridgeCheck.csproj`: canceled reply cannot contaminate a later request |
| Subtitle rendering | `tests/overlay`: geometry, actual source-pixel masking, capture exclusion, click-through, held frame and moving control cutout |
| All six capture/style combinations | `tests/integration`: real Japanese OCR → Thai translation, content changes, moved/resized window, minimize/restore, stop |
| Real manga OCR | Eight exact Japanese transcripts, ignoring punctuation, including hospital sign; source and 60/100/120 px scroll offsets |
| Offline manga scroll regression | `local-ocr/check_scroll.py`: same eight transcripts with worker socket connections blocked |

The real fixture is a privately downloaded author-provided *Give My Regards to Black
Jack* page by SHUHO SATO. Attribution and acquisition terms remain with the ignored
fixture in `artifacts/manga-test/source.md`. No manga imagery is included in the repo.

## Real manga acceptance

`tests/manga/MangaCheck.csproj` passed using the final Hy-MT2 1.8B Q8 model:
cold startup remained covered; all eight blocks received Thai captions; the native
visible image was pixel-identical during the first 120 ms after scrolling; the
completed new view replaced it; a blank/no-detection view retained the previous
image; minimize/restore and stopping cleared the presentation correctly.

Measured end-to-end times were 14.5 seconds for cold startup, 1.19 seconds for the
scrolled view, and 752 ms for the restored cached page. Model-only translation of
eight uncached dialogue regions was about 1.7 seconds. These are local measurements
on one page, not a throughput guarantee. Evidence remains in the ignored
`artifacts/manga-test/live-manga-check.json` and `live-*.png` files.

Thai captions preserve Unicode grapheme clusters and use Windows dictionary boundaries,
balanced wrapping, and fitting
checks. Unreadable detected blocks retain their masks. A layout that cannot fit
keeps the last translated view and continues watching for another frame; the
focused `tests/unreadable` and `tests/layout-hold` regressions cover those paths.

Translation fidelity is separate from rendering. The selected small model improves
on the initial M2M100 baseline but can still mistranslate names, omitted subjects,
and dialogue fragments. The [measured comparison](../local-model/QUALITY.md) records
those limitations and why the much slower 7B candidate was rejected. Rendering
checks establish source coverage on the tested page, not human-quality translation
or perfect OCR on arbitrary comics.

Physical mixed-DPI multi-monitor testing remains unavailable on this single-display
machine; negative-coordinate and scale geometry checks do not substitute for it.
