"""Check a complete local passage while retaining the replay's strict stage outcomes."""

import argparse
import json
from pathlib import Path
import unicodedata


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", type=Path)
    parser.add_argument("--passage", required=True)
    parser.add_argument("--ignore-punctuation", default="",
                        help="Explicit punctuation characters to ignore in source recognition only")
    args = parser.parse_args()
    assert all(unicodedata.category(char).startswith("P") for char in args.ignore_punctuation), \
        "Only punctuation may be ignored; substantive source characters must remain exact."
    report = json.loads(args.report.read_text(encoding="utf-8-sig"))
    passage = next(item for item in report["intendedPassages"] if item["id"] == args.passage)
    print(json.dumps({"report": str(args.report), "passage": passage,
                      "ignoredPunctuation": list(args.ignore_punctuation),
                      "strictStageOutcomes": report["stageOutcomes"]}, indent=2))
    assert report["mode"] == "complete-processing", "An actual complete-processing report is required."
    assert passage["expectedStage"] == "caption-complete", "This passage must require a complete caption."
    assert passage["bounds"] is not None, "Independently annotated source bounds are required."
    x, y, width, height = passage["bounds"]
    nearby = [region for region in report["regions"]
              if (passage["regionId"] is None or region["id"] == passage["regionId"])
              and region["bounds"][0] < x + width and region["bounds"][1] < y + height
              and region["bounds"][0] + region["bounds"][2] > x
              and region["bounds"][1] + region["bounds"][3] > y]
    print(json.dumps({"nearbyRecognizedRegions": nearby}, indent=2))
    ignored = str.maketrans("", "", args.ignore_punctuation)
    matches = [region for region in nearby if region["recognizedText"].translate(ignored)
               == passage["sourceText"].translate(ignored)]
    assert len(matches) == 1, "The complete intended source must match exactly apart from the listed punctuation."
    region = matches[0]
    captions = [item for item in report["captions"]
                if item["kind"] == "caption" and item["regionId"] == region["id"]]
    assert len(captions) == 1, "The recognized passage needs one associated caption."
    caption = captions[0]
    assert region["translation"], "The passage must have a translation."
    assert caption["text"].replace("\r", "").replace("\n", "") == region["translation"], \
        "The caption must contain the complete returned translation."
    assert caption["fallbackReason"] is None, "The caption must remain local."
    assert caption["fontSize"] >= 12, "The caption must retain the renderer's readable font floor."
    print(json.dumps({"targetPassed": True, "caption": caption}, indent=2))
    print("PASS targeted passage; strict stage outcomes above remain unchanged.")


if __name__ == "__main__":
    main()
