"""Check complete, readable, locally aligned dark caption lines in a fixed replay."""

import argparse
import json
from pathlib import Path

from PIL import Image


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", type=Path)
    parser.add_argument("--region", required=True)
    parser.add_argument("--minimum-font", type=float, required=True)
    parser.add_argument("--maximum-center-spread", type=float, required=True,
                        help="Allowed spread of visible line centers, in captured pixels")
    args = parser.parse_args()
    report = json.loads(args.report.read_text(encoding="utf-8-sig"))
    region = next(item for item in report["regions"] if item["id"] == args.region)
    caption = next(item for item in report["captions"]
                   if item["kind"] == "caption" and item["regionId"] == args.region)
    assert caption["text"].replace("\r", "").replace("\n", "") == region["translation"], \
        "The complete translation must remain in the caption."
    assert caption["fontSize"] >= args.minimum_font, \
        "Alignment must retain the required readable font size."
    assert caption["fallbackReason"] is None, "The passage must remain local."
    assert caption["foreground"].upper() == "#FF000000", \
        "This pixel check requires a black caption."

    with Image.open(args.report.with_suffix(".captured-view.png")) as original, \
            Image.open(args.report.with_suffix(".rendered.png")) as result:
        source, rendered = original.convert("RGB"), result.convert("RGB")
        assert source.size == rendered.size
        source_pixels, rendered_pixels = source.load(), rendered.load()
        x, y, width, height = caption["bounds"]
        assert 0 <= x < x + width <= source.width and 0 <= y < y + height <= source.height
        lines = caption["lines"]
        line_height = height / len(lines)
        rows = []
        for index, text in enumerate(lines):
            if not text.strip():
                continue
            top = y + round(index * line_height)
            bottom = y + round((index + 1) * line_height)
            # Changed dark pixels exclude unchanged source outlines/artwork.
            ink = [(px, py) for py in range(top, bottom) for px in range(x, x + width)
                   if max(rendered_pixels[px, py]) < 128
                   and rendered_pixels[px, py] != source_pixels[px, py]]
            assert ink, f"Line {index} must have visible glyphs."
            xs, ys = zip(*ink)
            rows.append({"line": index, "text": text,
                         "inkBounds": [min(xs), min(ys), max(xs) - min(xs) + 1, max(ys) - min(ys) + 1],
                         "center": (min(xs) + max(xs)) / 2})

    assert len(rows) >= 2, "An alignment check requires multiple occupied lines."
    spread = max(row["center"] for row in rows) - min(row["center"] for row in rows)
    proof = {"publicReport": str(args.report), "regionId": args.region,
             "captionFontSize": caption["fontSize"], "captionBounds": caption["bounds"],
             "rows": rows, "centerSpreadPixels": spread,
             "maximumCenterSpreadPixels": args.maximum_center_spread}
    args.report.with_suffix(".alignment.json").write_text(
        json.dumps(proof, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(proof, indent=2))
    assert spread <= args.maximum_center_spread, \
        f"Caption line centers must remain aligned: {spread}px spread exceeds {args.maximum_center_spread}px."
    print("PASS: complete local caption retains size and visibly aligned rows.")


if __name__ == "__main__":
    main()
