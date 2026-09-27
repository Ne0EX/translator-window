"""Validate one opt-in live caption export without rerendering or loading models."""

import argparse
import hashlib
import json
import math
from pathlib import Path
import sys
import uuid

from PIL import Image


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", type=Path)
    parser.add_argument("--version", help="Exact expected running assembly informational version")
    parser.add_argument("--source-text", action="append", default=[],
                        help="Independently read literal requiring a complete associated caption; repeatable")
    args = parser.parse_args()
    report = json.loads(args.report.read_text(encoding="utf-8-sig"))
    failures = []

    def require(condition, message):
        if not condition:
            failures.append(message)

    require(report["schemaVersion"] == 1, "Unsupported live evidence schema")
    version = report["assemblyInformationalVersion"]
    require(bool(version), "Running assembly version is absent")
    if args.version:
        require(version == args.version, "Running assembly version differs from the expected build")
    uuid.UUID(report["assemblyModuleVersionId"])
    require(report["overlayVisible"] and report["overlayOpacity"] > 0, "Caption layer was not visible")
    require(all(math.isfinite(value) and value > 0 for value in report["dpi"]), "Invalid display DPI")
    require(report["phase"] in ("complete", "restored"), "Snapshot is not an accepted caption state")
    require(report["acceptedGeneration"] >= report["sourceGeneration"], "Source generation is newer than acceptance")

    images = {}
    for kind, dimensions in (("source", report["captureBounds"][2:]),
                             ("layer", report["virtualScreen"][2:]),
                             ("rendered", report["captureBounds"][2:])):
        path = args.report.parent / report[kind]
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        with Image.open(path) as image:
            image.load()
            require(image.format == "PNG", f"{kind} is not a PNG")
            require(list(image.size) == dimensions, f"{kind} dimensions differ from the recorded capture mapping")
            if kind == "layer":
                require("A" in image.getbands(), "Caption layer has no alpha channel")
                if "A" in image.getbands():
                    alpha = image.getchannel("A")
                    require(alpha.getbbox() is not None, "Caption layer is empty")
                    require(alpha.getextrema()[0] == 0, "Caption layer has no transparent area")
            images[kind] = {"size": list(image.size), "sha256": digest}
        if kind == "source":
            require(digest == report["sourceSha256"].lower(), "Accepted source PNG hash differs from its report")

    regions = {region["id"]: region for region in report["regions"]}
    require(len(regions) == len(report["regions"]), "Text region IDs are not unique")
    captions = [item for item in report["captions"] if item["kind"] == "caption"]
    badges = {item["regionId"]: item["text"] for item in report["captions"]
              if item["kind"] == "association-badge"}
    require(bool(captions), "No translated captions were exported")
    associated = {}
    for caption in captions:
        region_id = caption["regionId"]
        require(region_id in regions, f"Caption has no associated text region: {region_id}")
        require(region_id not in associated, f"More than one caption is associated with region {region_id}")
        associated[region_id] = caption
        require(bool(caption["typeface"]) and bool(caption["fontWeight"]), f"Region {region_id} has no font metadata")
        size = caption["fontSize"]
        require(isinstance(size, (int, float)) and math.isfinite(size) and size >= 12,
                f"Region {region_id} caption is below the 12 DIP reading floor")
        require(all(math.isfinite(value) for value in caption["bounds"])
                and all(value > 0 for value in caption["bounds"][2:]), f"Region {region_id} has invalid caption bounds")
        if region_id in regions and regions[region_id]["translation"]:
            text = caption["text"].replace("\r", "").replace("\n", "")
            prefix = f"{badges.get(region_id)}. "
            if region_id in badges and text.startswith(prefix):
                text = text[len(prefix):]
            expected = regions[region_id]["translation"].replace("\r", "").replace("\n", "")
            require(text == expected, f"Region {region_id} displayed caption differs from its complete translation")

    for literal in args.source_text:
        matches = [region for region in regions.values() if region["recognizedText"] == literal]
        require(bool(matches), f"Required source passage was not recognized exactly: {literal}")
        for region in matches:
            require(bool(region["translation"]) and region["id"] in associated,
                    f"Required source passage has no translated caption: {literal}")

    print(json.dumps({
        "report": str(args.report), "version": version,
        "assemblyModuleVersionId": report["assemblyModuleVersionId"],
        "phase": report["phase"], "images": images,
        "captions": [{key: caption[key] for key in
                      ("regionId", "typeface", "fontSize", "fontWeight", "fallbackReason")} for caption in captions],
        "translationsWithoutCaption": [region_id for region_id, region in regions.items()
                                       if region["translation"] and region_id not in associated],
        "requiredPassages": len(args.source_text), "failures": failures,
        "scope": "Accepted WPF still and recorded caption associations; not compositor timing or complete source coverage"
    }, ensure_ascii=False, indent=2))
    return 1 if failures else 0


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    try:
        sys.exit(main())
    except (OSError, ValueError, KeyError, TypeError) as error:
        print(f"FAIL: incomplete or invalid live evidence: {error}", file=sys.stderr)
        sys.exit(1)
