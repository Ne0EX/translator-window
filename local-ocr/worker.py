"""Offline comic text detection. JSON-lines stdin/stdout, no model downloads.

Preprocessing and output interpretation follow dmMaze/comic-text-detector,
GPL-3.0; see LICENSE.comic-text-detector and README.md.
"""
import argparse
import base64
import hashlib
import json
import mmap
import os
from pathlib import Path
import re
import sys

import cv2
import numpy as np
import onnxruntime as ort
ort.disable_telemetry_events()

BLOCKS_SHA256 = "d92958fc0e73fbde5d26e4f8512b2298a7308cb70d2ba1303c4eec67d58aff03"
MAPPING_NAME = re.compile(r"Local\\TranslumoOcr-[0-9a-f]{32}\Z")


def resolve_detector_model(model):
    model = Path(model)
    if not model.is_file():
        raise ValueError("Local comic detector is missing. Run local-ocr/setup.ps1 first.")
    blocks = model.with_name(model.stem + "-blocks" + model.suffix)
    if blocks.is_file():
        with blocks.open("rb") as stream:
            if hashlib.file_digest(stream, "sha256").hexdigest() == BLOCKS_SHA256:
                return blocks
    return model


class Detector:
    def __init__(self, model):
        model = resolve_detector_model(model)
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


def _chart_rules(ink, scale):
    horizontal = cv2.morphologyEx(
        ink, cv2.MORPH_OPEN,
        cv2.getStructuringElement(cv2.MORPH_RECT, (max(18, round(24 * scale)), 1)),
    )
    vertical = cv2.morphologyEx(
        ink, cv2.MORPH_OPEN,
        cv2.getStructuringElement(cv2.MORPH_RECT, (1, max(10, round(12 * scale)))),
    )
    return horizontal, vertical


def _chart_mask_boxes(mask, left=0, top=0):
    _, _, stats, _ = cv2.connectedComponentsWithStats(mask)
    return [(left + int(x), top + int(y), int(width), int(height), int(pixels))
            for x, y, width, height, pixels in stats[1:]]


def _chart_text_boxes(ink, rules, scale):
    text = ink & ~cv2.dilate(rules, cv2.getStructuringElement(cv2.MORPH_RECT, (3, 3)))
    joined = cv2.dilate(text, cv2.getStructuringElement(
        cv2.MORPH_RECT, (max(3, round(7 * scale)), max(5, round(11 * scale)))))
    _, _, stats, _ = cv2.connectedComponentsWithStats(joined)
    output = []
    for x, y, width, height, _ in stats[1:]:
        pixels = cv2.countNonZero(text[y:y + height, x:x + width])
        if (pixels >= max(60, round(300 * scale * scale)) and pixels >= .07 * width * height
                and max(12, round(25 * scale)) <= width <= max(50, round(110 * scale))
                and max(25, round(50 * scale)) <= height <= max(150, round(320 * scale))
                and height >= 1.15 * width):
            output.append((int(x), int(y), int(width), int(height), int(pixels)))
    return output


def _chart_connector_bars(box, verticals, horizontals, scale):
    x, y, width, _, _ = box
    near = max(5, round(12 * scale))
    output = []
    for vx, vy, vwidth, vheight, _ in verticals:
        bottom = vy + vheight
        if (vheight < max(10, round(14 * scale)) or vheight > max(150, round(300 * scale))
                or vheight < 2 * vwidth or vx + vwidth < x - near or vx > x + width + near
                or not y - max(8, round(16 * scale)) <= bottom <= y + max(2, round(4 * scale))):
            continue
        for index, (hx, hy, hwidth, hheight, _) in enumerate(horizontals):
            if (hwidth >= max(30, round(40 * scale)) and hwidth >= 5 * hheight
                    and hx - near <= vx + vwidth / 2 <= hx + hwidth + near
                    and hy + hheight <= y - max(2, round(4 * scale))
                    and abs(hy + hheight / 2 - vy) <= near):
                output.append(index)
    return output


def _chart_has_stem(box, verticals, scale):
    x, y, width, _, _ = box
    near = max(5, round(12 * scale))
    return any(
        max(10, round(14 * scale)) <= height <= max(150, round(300 * scale))
        and height >= 2 * vwidth and vx + vwidth >= x - near and vx <= x + width + near
        and y - max(8, round(16 * scale)) <= vy + height <= y + max(2, round(4 * scale))
        for vx, vy, vwidth, height, _ in verticals
    )


def _expand_chart_label(box, ink, verticals, horizontals, scale):
    """Recover glyph strokes removed by rule detection without swallowing the connector."""
    x, y, width, height, pixels = box
    original_top, original_bottom = y, y + height
    pad = max(2, round(4 * scale))
    horizontal_reach = max(12, round(20 * scale))
    window_left = max(0, x - horizontal_reach)
    window_right = min(ink.shape[1], x + width + horizontal_reach)
    window_top = max(0, y - 2 * width)
    window_bottom = min(ink.shape[0], y + height + max(width, round(40 * scale)))
    evidence = ink[window_top:window_bottom, window_left:window_right].copy()
    # Remove only connector segments ending at this label. Keeping all other ink
    # retains long Japanese strokes that the morphology rule masks also contain.
    for vx, vy, vwidth, vheight, _ in verticals:
        bottom = vy + vheight
        connector = (vheight >= max(30, round(40 * scale)) and vwidth <= max(3, round(6 * scale)))
        parent_stem = y - max(8, round(16 * scale)) <= bottom <= y + max(2, round(4 * scale))
        if (vheight >= 2 * vwidth and (connector or parent_stem)
                and vx + vwidth >= x - pad and vx <= x + width + pad):
            left = max(0, vx - window_left - 1)
            right = min(evidence.shape[1], vx + vwidth - window_left + 1)
            top = max(0, vy - window_top - 1)
            end = min(evidence.shape[0], bottom - window_top + 1)
            evidence[top:end, left:right] = 0
    for hx, hy, hwidth, hheight, _ in horizontals:
        if (hx + hwidth >= x - pad and hx <= x + width + pad
                and window_top <= hy and hy + hheight <= y):
            left = max(0, hx - window_left - 1)
            right = min(evidence.shape[1], hx + hwidth - window_left + 1)
            top = max(0, hy - window_top - 1)
            end = min(evidence.shape[0], hy + hheight - window_top + 1)
            evidence[top:end, left:right] = 0
    columns = np.flatnonzero(np.count_nonzero(evidence, axis=0) >= 2)
    label_left, label_right = x, x + width
    if len(columns):
        starts = np.r_[0, np.flatnonzero(np.diff(columns) > max(6, round(10 * scale))) + 1]
        ends = np.r_[starts[1:] - 1, len(columns) - 1]
        groups = [(window_left + int(columns[start]), window_left + int(columns[end]) + 1)
                  for start, end in zip(starts, ends)]
        gap = max(4, round(6 * scale))
        selected = [(left, right) for left, right in groups
                    if right >= x + width - gap and left <= x + width + gap]
        if selected:
            label_right = min(window_right, max(x + width, max(right for _, right in selected)))
    ordinal_left = max(x, x + width - max(12, round(20 * scale)))
    ordinal = evidence[:, ordinal_left - window_left:label_right - window_left]
    if ordinal.size:
        extra = ordinal
        rows = np.flatnonzero(np.count_nonzero(extra, axis=1) >= max(3, round(4 * scale)))
        if len(rows):
            starts = np.r_[0, np.flatnonzero(np.diff(rows) > max(6, round(10 * scale))) + 1]
            ends = np.r_[starts[1:] - 1, len(rows) - 1]
            groups = [(window_top + int(rows[start]), window_top + int(rows[end]) + 1)
                      for start, end in zip(starts, ends)]
            selected = [(top, bottom) for top, bottom in groups
                        if bottom >= original_top and top <= original_bottom]
            if selected:
                y = min(original_top, max(original_top - width, min(top for top, _ in selected)))
                height = max(original_bottom, min(original_bottom + width,
                                                   max(bottom for _, bottom in selected))) - y
    left, right = max(0, label_left - pad), min(ink.shape[1], label_right + pad)
    return (left, max(0, y - pad), right - left,
            min(ink.shape[0], y + height + pad) - max(0, y - pad), pixels)


def split_chart_regions(image, regions):
    """Replace a detector union only when its text hangs from a shared chart bar."""
    # ponytail: this deliberately recognizes only thin ruled, vertical-text family
    # charts. General diagrams belong in a detector model, not more page heuristics.
    if not regions:
        return regions
    gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    _, ink = cv2.threshold(gray, 0, 255, cv2.THRESH_BINARY_INV | cv2.THRESH_OTSU)
    frame_scale = min(image.shape[1] / 3840, image.shape[0] / 2088)
    seeds = []
    for index, region in enumerate(regions):
        x, y, width, height = (region[key] for key in ("x", "y", "width", "height"))
        local = ink[y:y + height, x:x + width]
        best = None
        try_larger_scale = False
        # The desktop frame can stay fixed while the reader enlarges page content.
        # A single bounded larger scale covers that case without fragmenting normal
        # text into extra labels at progressively smaller morphology kernels.
        for scale_index, scale in enumerate((frame_scale, frame_scale * 1.25)):
            if scale_index and not try_larger_scale:
                break
            local_h, local_v = _chart_rules(local, scale)
            boxes = _chart_text_boxes(local, local_h | local_v, scale)
            if len(boxes) < 3:
                continue
            pad_x, pad_y = max(width, round(250 * scale)), max(height * 2, round(350 * scale))
            left, top = max(0, x - pad_x), max(0, y - pad_y)
            right = min(image.shape[1], x + width + pad_x)
            bottom = min(image.shape[0], y + height + round(100 * scale))
            context = ink[top:bottom, left:right]
            context_h, context_v = _chart_rules(context, scale)
            horizontals = _chart_mask_boxes(context_h, left, top)
            verticals = _chart_mask_boxes(context_v, left, top)
            global_boxes = [(x + bx, y + by, bw, bh, count) for bx, by, bw, bh, count in boxes]
            connected = [(box, _chart_connector_bars(box, verticals, horizontals, scale))
                         for box in global_boxes]
            if scale_index == 0:
                try_larger_scale = any(bars for _, bars in connected)
            common = {bar for _, bars in connected for bar in bars
                      if sum(bar in other for _, other in connected) >= 2}
            attached = [box for box, bars in connected if common.intersection(bars)]
            if len(attached) >= 2 and (best is None or len(attached) > len(best[0])):
                context_boxes = [(left + bx, top + by, bw, bh, count)
                                 for bx, by, bw, bh, count in _chart_text_boxes(
                                     context, context_h | context_v, scale)]
                best = (attached, context_boxes, verticals, horizontals, scale)
        if best is not None:
            seeds.append((index, region, *best))
    if not seeds:
        return regions

    seed_indexes = {seed[0] for seed in seeds}
    replacements = {}
    for index, region, attached, context_boxes, verticals, horizontals, scale in seeds:
        labels = list(attached)
        if len(attached) <= 3:
            labels.extend(box for box in context_boxes if _chart_has_stem(box, verticals, scale))
        labels.sort(key=lambda item: (item[1], item[0]))
        unique = []
        for box in labels:
            if not any(abs(box[0] - other[0]) <= 3 and abs(box[1] - other[1]) <= 3 for other in unique):
                unique.append(box)
        merged = []
        for box in unique:
            bx, by, bw, bh, count = box
            for offset, other in enumerate(merged):
                ox, oy, ow, oh, old_count = other
                horizontal_overlap = max(0, min(bx + bw, ox + ow) - max(bx, ox))
                vertical_gap = max(by - (oy + oh), oy - (by + bh), 0)
                if horizontal_overlap >= .5 * min(bw, ow) and vertical_gap <= max(3, round(8 * scale)):
                    left, top = min(bx, ox), min(by, oy)
                    right, bottom = max(bx + bw, ox + ow), max(by + bh, oy + oh)
                    merged[offset] = (left, top, right - left, bottom - top, count + old_count)
                    break
            else:
                merged.append(box)
        accepted = []
        for box in merged:
            area = box[2] * box[3]
            hits = [other_index for other_index, other in enumerate(regions)
                    if overlap(box, (other["x"], other["y"], other["width"], other["height"])) > .2 * area]
            if not hits or all(hit in seed_indexes for hit in hits):
                accepted.append(_expand_chart_label(box, ink, verticals, horizontals, scale))
        recovered = [
            {"x": bx, "y": by, "width": bw, "height": bh, "vertical": True}
            for bx, by, bw, bh, _ in sorted(accepted, key=lambda item: (item[1], item[0]))
        ]
        if len(recovered) >= 2:
            replacements[index] = recovered

    output = []
    for index, region in enumerate(regions):
        output.extend(replacements.get(index, [region]))
    return output


def decode_request_image(request):
    """Accept the current named BGRA mapping and legacy PNG protocol."""
    if "mapping" not in request:
        if not isinstance(request.get("image"), str):
            raise ValueError("Expected an object containing a mapped BGRA frame or base64 PNG image.")
        image = cv2.imdecode(np.frombuffer(base64.b64decode(request["image"], validate=True), dtype=np.uint8),
                             cv2.IMREAD_COLOR)
        if image is None:
            raise ValueError("Could not decode image.")
        return image

    if "image" in request or not isinstance(request["mapping"], dict):
        raise ValueError("Expected exactly one mapped BGRA frame or base64 PNG image.")
    frame = request["mapping"]
    name = frame.get("name")
    width, height, stride, length = (frame.get(key) for key in ("width", "height", "stride", "length"))
    if (not isinstance(name, str) or MAPPING_NAME.fullmatch(name) is None
            or frame.get("pixelFormat") != "bgra32"
            or any(type(value) is not int for value in (width, height, stride, length))
            or width < 4 or height < 4 or width > 20000 or height > 20000
            or width * height > 100_000_000 or stride != width * 4 or length != stride * height):
        raise ValueError("Invalid mapped BGRA frame.")
    with mmap.mmap(-1, length, tagname=name, access=mmap.ACCESS_READ) as mapped:
        bgra = np.ndarray((height, width, 4), dtype=np.uint8, buffer=mapped, strides=(stride, 4, 1))
        return cv2.cvtColor(bgra, cv2.COLOR_BGRA2BGR)


def reconcile_known_regions(regions, known, width, height):
    """Reuse only an unambiguous detector match inside its pixel-validated OCR footprint."""
    if known is None:
        known = []
    if not isinstance(known, list) or len(known) > 4096:
        raise ValueError("Known OCR regions must be an array with at most 4096 items.")
    validated = []
    for item in known:
        if not isinstance(item, dict) or any(type(item.get(key)) is not int for key in ("x", "y", "width", "height")):
            raise ValueError("Known OCR region coordinates must be integers.")
        x, y, box_width, box_height = (item[key] for key in ("x", "y", "width", "height"))
        text = item.get("text")
        if (box_width <= 0 or box_height <= 0 or x < 0 or y < 0
                or x + box_width > width or y + box_height > height):
            raise ValueError("Known OCR region is outside the image.")
        if not isinstance(text, str) or not text.strip() or len(text) > 4096:
            raise ValueError("Known OCR region text must be a nonempty string of at most 4096 characters.")
        core = (x, y, box_width, box_height)
        guard = (max(0, x - 8), max(0, y - 8),
                 min(width, x + box_width + 8) - max(0, x - 8),
                 min(height, y + box_height + 8) - max(0, y - 8))
        validated.append((core, guard, item))

    boxes = [(region["x"], region["y"], region["width"], region["height"]) for region in regions]
    known_hits = [[index for index, box in enumerate(boxes) if overlap(core, box) > 0]
                  for core, _, _ in validated]
    detector_hits = [[index for index, (core, _, _) in enumerate(validated) if overlap(core, box) > 0]
                     for box in boxes]
    claimed = {}
    missed = []
    for (_, guard, item), hits in zip(validated, known_hits):
        if not hits:
            missed.append(item)
            continue
        if len(hits) != 1:
            continue
        detector_index = hits[0]
        box = boxes[detector_index]
        if len(detector_hits[detector_index]) == 1 and (guard[0] <= box[0] and guard[1] <= box[1]
                and box[0] + box[2] <= guard[0] + guard[2]
                and box[1] + box[3] <= guard[1] + guard[3]):
            claimed[detector_index] = item

    output = []
    for index, region in enumerate(regions):
        if index in claimed:
            item = claimed[index]
            output.append({"x": item["x"], "y": item["y"], "width": item["width"],
                           "height": item["height"], "text": item["text"]})
        else:
            output.append(region)
    output.extend({"x": item["x"], "y": item["y"], "width": item["width"],
                   "height": item["height"], "text": item["text"]} for item in missed)
    return output, len(claimed) + len(missed)


def request_progress(request):
    progress = request.get("progress", False)
    if type(progress) is not bool:
        raise ValueError("OCR progress must be a boolean.")
    return progress


def process_image(image, language, known, detector, get_recognizer, progress=False, emit=None):
    regions = detector.detect(image)
    if language == "ja":
        regions = split_chart_regions(image, regions)
    height, width = image.shape[:2]
    regions, reused = reconcile_known_regions(regions, known, width, height)
    if progress:
        detected = [{**region, "vertical": region.get(
            "vertical", bool(region["height"] > region["width"] * 0.8),
        )} for region in regions]
        emit({"detected": detected})
    if language == "ja" and regions:
        pending = [(index, region) for index, region in enumerate(regions) if "text" not in region]
        if not pending:
            return {"regions": regions, "reused": reused}
        from PIL import Image
        recognizer = get_recognizer()
        crops = [Image.fromarray(cv2.cvtColor(
            image[max(0, region["y"] - 8):min(height, region["y"] + region["height"] + 8),
                  max(0, region["x"] - 8):min(width, region["x"] + region["width"] + 8)],
            cv2.COLOR_BGR2RGB,
        )) for _, region in pending]
        batch_size = 8 if progress else 64
        for offset in range(0, len(crops), batch_size):
            batch = pending[offset:offset + batch_size]
            texts = recognizer.recognize(crops[offset:offset + batch_size])
            if len(texts) != len(batch) or any(not isinstance(text, str) for text in texts):
                raise ValueError("Local manga recognizer returned invalid text.")
            recognized = []
            for (index, region), text in zip(batch, texts):
                if text.strip():
                    region["text"] = text
                    recognized.append({"index": index, "text": text})
            if progress and recognized:
                emit({"recognized": recognized})
    return {"regions": regions, "reused": reused}


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

    def get_recognizer():
        nonlocal recognizer
        if recognizer is None:
            from manga_recognizer import MangaRecognizer
            recognizer = MangaRecognizer(
                args.recognizer_model or Path(args.model).resolve().parent.parent / "manga-ocr",
                device=os.environ.get("LOCAL_OCR_DEVICE", "auto"),
            )
        return recognizer

    def emit(reply):
        print(json.dumps(reply, ensure_ascii=False), flush=True)

    if args.image:
        image = cv2.imread(args.image)
        if image is None:
            raise ValueError("Could not read diagnostic image.")
        print(json.dumps(process_image(image, args.language, None, detector, get_recognizer), ensure_ascii=False))
        return
    sys.stdin.reconfigure(encoding="utf-8")
    for line in sys.stdin:
        try:
            if len(line) > 150_000_000:
                raise ValueError("Encoded frame exceeds 150 MB.")
            request = json.loads(line)
            if not isinstance(request, dict):
                raise ValueError("Expected an object containing a mapped BGRA frame or base64 PNG image.")
            image = decode_request_image(request)
            language = request.get("language", "")
            if language not in ("", "ja", "ko", "en", "th"):
                raise ValueError("Unsupported comic language.")
            progress = request_progress(request)
            reply = process_image(image, language, request.get("known"), detector, get_recognizer,
                                  progress, emit)
        except Exception as error:
            reply = {"error": str(error)}
        print(json.dumps(reply), flush=True)


if __name__ == "__main__":
    main()
