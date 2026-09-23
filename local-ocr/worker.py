"""Offline comic text detection. JSON-lines stdin/stdout, no model downloads.

Preprocessing and output interpretation follow dmMaze/comic-text-detector,
GPL-3.0; see LICENSE.comic-text-detector and README.md.
"""
import argparse
import base64
import json
import os
from pathlib import Path
import sys

import cv2
import numpy as np
import onnxruntime as ort
ort.disable_telemetry_events()


class Detector:
    def __init__(self, model):
        if not Path(model).is_file():
            raise ValueError("Local comic detector is missing. Run local-ocr/setup.ps1 first.")
        options = ort.SessionOptions()
        options.intra_op_num_threads = 4
        options.inter_op_num_threads = 1
        self.model_path, self.options = str(model), options
        self.session = create_session(ort, self.model_path, options,
                                      os.environ.get("LOCAL_OCR_DETECTOR_DEVICE", "cpu"))
        self.input_name = self.session.get_inputs()[0].name

    def detect(self, image):
        height, width = image.shape[:2]
        if min(height, width) < 4 or max(height, width) > 20000 or height * width > 100_000_000:
            raise ValueError("Image dimensions must be 4..20000 pixels and at most 100 megapixels.")
        scale = 1024 / max(height, width)
        scaled_width, scaled_height = max(1, round(width * scale)), max(1, round(height * scale))
        resized = cv2.resize(image, (scaled_width, scaled_height), interpolation=cv2.INTER_LINEAR)
        canvas = np.zeros((1024, 1024, 3), dtype=np.uint8)
        canvas[:scaled_height, :scaled_width] = cv2.cvtColor(resized, cv2.COLOR_BGR2RGB)
        blob = np.ascontiguousarray(canvas.transpose(2, 0, 1)[None], dtype=np.float32) / 255
        try:
            predictions = self.session.run(["blk"], {self.input_name: blob})[0][0]
        except Exception:
            if "CUDAExecutionProvider" not in self.session.get_providers():
                raise
            print("CUDA detector unavailable; continuing with local CPU inference.", file=sys.stderr, flush=True)
            self.session = ort.InferenceSession(self.model_path, self.options, providers=["CPUExecutionProvider"])
            predictions = self.session.run(["blk"], {self.input_name: blob})[0][0]
        accepted = select_text_boxes(predictions)
        regions = []
        for x, y, box_width, box_height in accepted:
            left = max(0, int(np.floor(x * width / scaled_width)))
            top = max(0, int(np.floor(y * height / scaled_height)))
            right = min(width, int(np.ceil((x + box_width) * width / scaled_width)))
            bottom = min(height, int(np.ceil((y + box_height) * height / scaled_height)))
            if right > left and bottom > top:
                regions.append({"x": left, "y": top, "width": right - left, "height": bottom - top,
                                "vertical": bool(box_height > box_width * 0.8)})
        return regions


def create_session(runtime, model, options, device):
    providers = ["CPUExecutionProvider"]
    if device == "cuda" and "CUDAExecutionProvider" in runtime.get_available_providers():
        import torch  # Load the CUDA/cuDNN DLLs bundled with the installed torch wheel.
        if torch.cuda.is_available():
            providers.insert(0, ("CUDAExecutionProvider", {"cudnn_conv_algo_search": "HEURISTIC",
                              "gpu_mem_limit": 2147483648, "arena_extend_strategy": "kSameAsRequested"}))
    try:
        return runtime.InferenceSession(model, options, providers=providers)
    except Exception:
        if providers == ["CPUExecutionProvider"]:
            raise
        print("CUDA detector unavailable; continuing with local CPU inference.", file=sys.stderr, flush=True)
        return runtime.InferenceSession(model, options, providers=["CPUExecutionProvider"])


def select_text_boxes(predictions):
    # Keep weak proposals only as evidence for splitting a confident union. A small
    # scroll can change a real bubble's score without changing its visible text.
    confidence = predictions[:, 4] * predictions[:, 5:].max(axis=1)
    candidates, confidence = predictions[confidence > 0.3], confidence[confidence > 0.3]
    boxes = candidates[:, :4].copy()
    boxes[:, :2] -= boxes[:, 2:] / 2
    selected = np.asarray(cv2.dnn.NMSBoxes(boxes.tolist(), confidence.tolist(), 0.3, 0.35)).flatten()
    recovered, unions = set(), set()
    for parent in selected:
        if confidence[parent] <= 0.4:
            continue
        parent_area = boxes[parent, 2] * boxes[parent, 3]
        children = [i for i in selected if i != parent
                    and 0.15 * parent_area <= boxes[i, 2] * boxes[i, 3] <= 0.65 * parent_area
                    and overlap(boxes[parent], boxes[i]) >= 0.8 * boxes[i, 2] * boxes[i, 3]]
        for offset, first in enumerate(children):
            for second in children[offset + 1:]:
                a, b = boxes[first], boxes[second]
                gap_x = max(a[0] - b[0] - b[2], b[0] - a[0] - a[2], 0)
                gap_y = max(a[1] - b[1] - b[3], b[1] - a[1] - a[3], 0)
                # Separate bubbles have clear space between their text blocks;
                # adjacent columns within one bubble must remain together.
                if ((gap_x >= 0.5 * min(a[2], b[2]) or gap_y >= 0.5 * min(a[3], b[3]))
                        and a[2] * a[3] + b[2] * b[3] >= 0.35 * parent_area):
                    unions.add(parent)
                    recovered.update((first, second))
    accepted = []
    for index in selected:
        if index in unions or (confidence[index] <= 0.4 and index not in recovered):
            continue
        box = boxes[index]
        area = box[2] * box[3]
        if any(overlap(box, other) / min(area, other[2] * other[3]) > 0.8 for other in accepted):
            continue
        accepted.append(box)
    return accepted


def overlap(a, b):
    return max(0, min(a[0]+a[2], b[0]+b[2])-max(a[0], b[0])) * max(0, min(a[1]+a[3], b[1]+b[3])-max(a[1], b[1]))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", required=True)
    parser.add_argument("--image", help="Optional diagnostic image instead of JSON-lines protocol")
    parser.add_argument("--language", default="", help="Diagnostic OCR language; ja enables local manga recognition")
    parser.add_argument("--recognizer-model", type=Path, help="Local manga-ocr model folder; defaults to sibling models/manga-ocr")
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    detector = Detector(args.model)
    recognizer = None

    def process_image(image, language):
        nonlocal recognizer
        regions = detector.detect(image)
        if language == "ja" and regions:
            from PIL import Image
            from manga_recognizer import MangaRecognizer
            if recognizer is None:
                recognizer = MangaRecognizer(args.recognizer_model or Path(args.model).resolve().parent.parent / "manga-ocr", device=os.environ.get("LOCAL_OCR_DEVICE", "auto"))
            height, width = image.shape[:2]
            crops = [Image.fromarray(cv2.cvtColor(image[max(0, r["y"]-8):min(height, r["y"]+r["height"]+8),
                                                       max(0, r["x"]-8):min(width, r["x"]+r["width"]+8)], cv2.COLOR_BGR2RGB)) for r in regions]
            texts = []
            for offset in range(0, len(crops), 64):
                texts.extend(recognizer.recognize(crops[offset:offset + 64]))
            if len(texts) != len(regions) or any(not isinstance(text, str) or not text.strip() for text in texts):
                raise ValueError("Local manga recognizer returned invalid or empty text.")
            for region, text in zip(regions, texts):
                region["text"] = text
        return {"regions": regions}
    if args.image:
        image = cv2.imread(args.image)
        if image is None:
            raise ValueError("Could not read diagnostic image.")
        print(json.dumps(process_image(image, args.language), ensure_ascii=False))
        return
    sys.stdin.reconfigure(encoding="utf-8")
    for line in sys.stdin:
        try:
            if len(line) > 150_000_000:
                raise ValueError("Encoded frame exceeds 150 MB.")
            request = json.loads(line)
            if not isinstance(request, dict) or not isinstance(request.get("image"), str):
                raise ValueError("Expected an object containing a base64 PNG image.")
            image = cv2.imdecode(np.frombuffer(base64.b64decode(request["image"], validate=True), dtype=np.uint8), cv2.IMREAD_COLOR)
            if image is None:
                raise ValueError("Could not decode image.")
            language = request.get("language", "")
            if language not in ("", "ja", "ko", "en", "th"):
                raise ValueError("Unsupported comic language.")
            reply = process_image(image, language)
        except Exception as error:
            reply = {"error": str(error)}
        print(json.dumps(reply), flush=True)


if __name__ == "__main__":
    main()
