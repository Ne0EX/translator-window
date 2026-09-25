"""Check detector protocol with sockets blocked and a user-supplied local test page."""
import argparse
import base64
import json
from pathlib import Path
import subprocess
import sys
import tempfile


def check_device_selection():
    import types
    from unittest.mock import patch
    import worker

    class Session:
        def __init__(self, providers): self.providers = providers

    class Runtime:
        def __init__(self, fail_cuda=False): self.calls, self.fail_cuda = [], fail_cuda
        def get_available_providers(self): return ["CUDAExecutionProvider", "CPUExecutionProvider"]
        def SessionOptions(self): return types.SimpleNamespace()
        def InferenceSession(self, model, options, providers):
            self.calls.append(providers)
            if self.fail_cuda and providers[0] != "CPUExecutionProvider": raise RuntimeError("simulated CUDA OOM")
            return Session(providers)

    cpu = Runtime()
    Session.get_inputs = lambda self: [types.SimpleNamespace(name="image")]
    with tempfile.NamedTemporaryFile() as model, patch.object(worker, "ort", cpu), patch.dict(worker.os.environ, {}, clear=True):
        worker.Detector(model.name)
    assert cpu.calls == [["CPUExecutionProvider"]], cpu.calls

    cuda = Runtime(fail_cuda=True)
    with patch.dict(sys.modules, {"torch": types.SimpleNamespace(cuda=types.SimpleNamespace(is_available=lambda: True))}):
        session = worker.create_session(cuda, "model", None, "cuda")
    assert len(cuda.calls) == 2 and cuda.calls[1] == ["CPUExecutionProvider"], cuda.calls
    assert session.providers == ["CPUExecutionProvider"]
    print("PASS: detector defaults to CPU and retries CPU after CUDA session creation fails.")


def check_mapping_validation():
    import base64
    import cv2
    import numpy as np
    import worker

    valid = {"name": "Local\\TranslumoOcr-" + "a" * 32, "width": 4, "height": 4,
             "stride": 16, "length": 64, "pixelFormat": "bgra32"}
    invalid = [
        {**valid, "width": True},
        {**valid, "width": 3},
        {**valid, "width": 20001},
        {**valid, "stride": 15},
        {**valid, "length": 63},
        {**valid, "pixelFormat": "rgba32"},
        {**valid, "name": "Global\\untrusted"},
    ]
    for frame in invalid:
        try:
            worker.decode_request_image({"mapping": frame})
        except ValueError:
            continue
        raise AssertionError(f"Invalid mapped frame was accepted: {frame}")
    try:
        worker.decode_request_image({"mapping": valid, "image": "also-present"})
    except ValueError:
        pass
    else:
        raise AssertionError("Ambiguous mapped and PNG request was accepted")
    legacy = np.arange(4 * 4 * 3, dtype=np.uint8).reshape(4, 4, 3)
    encoded, png = cv2.imencode(".png", legacy)
    assert encoded and np.array_equal(worker.decode_request_image(
        {"image": base64.b64encode(png).decode("ascii")}), legacy)
    print("PASS: mapped BGRA dimensions, stride, byte count, format and name are validated before open; legacy PNG remains exact.")


def check_progress_protocol():
    import numpy as np
    import worker

    for request, expected in (({}, False), ({"progress": False}, False), ({"progress": True}, True)):
        assert worker.request_progress(request) is expected
    for value in (None, 0, 1, "true", [], {}):
        try:
            worker.request_progress({"progress": value})
        except ValueError:
            continue
        raise AssertionError(f"Invalid progress value was accepted: {value!r}")

    detected = [{"x": 1 + index * 10, "y": 4, "width": 4, "height": 8,
                 "vertical": True} for index in range(18)]
    image = np.zeros((32, 192, 3), dtype=np.uint8)
    known = [{"x": 1, "y": 4, "width": 4, "height": 8, "text": "cached"}]

    class FakeDetector:
        def detect(self, _image):
            return [region.copy() for region in detected]

    class FakeRecognizer:
        def __init__(self):
            self.calls = []
            self.next = 1

        def recognize(self, crops):
            self.calls.append(len(crops))
            texts = [f"text-{index}" for index in range(self.next, self.next + len(crops))]
            self.next += len(crops)
            return texts

    progressive_recognizer = FakeRecognizer()
    events = []
    final = worker.process_image(
        image, "ja", known, FakeDetector(), lambda: progressive_recognizer, True,
        lambda event: events.append(json.loads(json.dumps(event))),
    )
    assert progressive_recognizer.calls == [8, 8, 1], progressive_recognizer.calls
    assert list(events[0]) == ["detected"] and len(events[0]["detected"]) == 18, events
    assert events[0]["detected"][0]["text"] == "cached"
    assert all(type(item["vertical"]) is bool for item in events[0]["detected"])
    assert all("text" not in item for item in events[0]["detected"][1:])
    assert [len(event["recognized"]) for event in events[1:]] == [8, 8, 1], events
    recognized = [item for event in events[1:] for item in event["recognized"]]
    assert [item["index"] for item in recognized] == list(range(1, 18)), recognized
    assert final["reused"] == 1 and [item.get("text") for item in final["regions"]] == [
        "cached", *(f"text-{index}" for index in range(1, 18)),
    ]
    assert [(item["x"], item["y"], item["width"], item["height"])
            for item in final["regions"]] == [(item["x"], item["y"], item["width"], item["height"])
                                         for item in events[0]["detected"]]

    final_recognizer = FakeRecognizer()
    legacy_events = []
    legacy = worker.process_image(
        image, "ja", known, FakeDetector(), lambda: final_recognizer, False, legacy_events.append,
    )
    assert final_recognizer.calls == [17] and not legacy_events
    assert legacy == final, (legacy, final)

    passthrough_events = []
    passthrough = worker.process_image(
        image, "en", None, FakeDetector(), lambda: (_ for _ in ()).throw(
            AssertionError("Non-Japanese request loaded the manga recognizer")), True,
        lambda event: passthrough_events.append(json.loads(json.dumps(event))),
    )
    assert len(passthrough_events) == 1 and "detected" in passthrough_events[0]
    assert passthrough == {"regions": detected, "reused": 0}
    print("PASS: progressive OCR emits ordered geometry then 8-crop indexed chunks; final-only and non-Japanese paths remain stable.")


def check_chart_split():
    import cv2
    import numpy as np
    import worker

    image = np.full((400, 600, 3), 255, np.uint8)
    cv2.line(image, (100, 100), (400, 100), (0, 0, 0), 2)
    for center in (150, 250, 350):
        cv2.line(image, (center, 100), (center, 122), (0, 0, 0), 2)
        for index, top in enumerate(range(130, 230, 8)):
            left = center - 8 if index % 2 == 0 else center
            cv2.rectangle(image, (left, top), (left + 8, top + 6), (0, 0, 0), -1)
    before = image.copy()
    chart = {"x": 125, "y": 125, "width": 250, "height": 120, "vertical": False}
    dialogue = {"x": 460, "y": 100, "width": 50, "height": 100, "vertical": True}
    output = worker.split_chart_regions(image, [chart, dialogue])
    assert len(output) == 4 and output[-1] == dialogue, output
    assert all(item["vertical"] and 20 <= item["width"] <= 25 and item["height"] >= 100
               for item in output[:3]), output
    assert [item["x"] for item in output[:3]] == sorted(item["x"] for item in output[:3])
    assert np.array_equal(image, before), "Chart refinement modified source pixels"
    assert worker.split_chart_regions(image, [dialogue]) == [dialogue]
    print("PASS: a shared ruled connector splits three vertical labels without changing pixels or ordinary text.")


def check_block_graph(source, image_path):
    import cv2
    import numpy as np
    import onnx
    import onnxruntime as ort
    import worker

    blocks = source.with_name(source.stem + "-blocks" + source.suffix)
    assert worker.resolve_detector_model(source) == blocks, "Worker did not select the verified block-only graph"
    source_graph = onnx.load(source, load_external_data=False).graph
    block_graph = onnx.load(blocks, load_external_data=False).graph
    assert [output.name for output in block_graph.output] == ["blk"]
    assert len(block_graph.node) < len(source_graph.node), (len(source_graph.node), len(block_graph.node))

    image = cv2.imread(str(image_path))
    assert image is not None, image_path
    height, width = image.shape[:2]
    scale = 1024 / max(height, width)
    scaled_width, scaled_height = max(1, round(width * scale)), max(1, round(height * scale))
    canvas = np.zeros((1024, 1024, 3), dtype=np.uint8)
    canvas[:scaled_height, :scaled_width] = cv2.cvtColor(
        cv2.resize(image, (scaled_width, scaled_height), interpolation=cv2.INTER_LINEAR), cv2.COLOR_BGR2RGB,
    )
    blob = np.ascontiguousarray(canvas.transpose(2, 0, 1)[None], dtype=np.float32) / 255
    options = ort.SessionOptions()
    options.intra_op_num_threads = 4
    options.inter_op_num_threads = 1
    original = ort.InferenceSession(str(source), options, providers=["CPUExecutionProvider"])
    optimized = ort.InferenceSession(str(blocks), options, providers=["CPUExecutionProvider"])
    expected = original.run(["blk"], {original.get_inputs()[0].name: blob})[0]
    actual = optimized.run(["blk"], {optimized.get_inputs()[0].name: blob})[0]
    assert np.array_equal(actual, expected), np.max(np.abs(actual - expected))
    print(f"PASS: block-only detector is bit-exact and prunes {len(source_graph.node) - len(block_graph.node)} graph nodes.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", type=Path)
    parser.add_argument("--image", type=Path)
    parser.add_argument("--language", default="", choices=("", "ja", "ko", "en", "th"))
    parser.add_argument("--device-self-check", action="store_true")
    args = parser.parse_args()
    if args.device_self_check:
        check_device_selection()
        check_mapping_validation()
        check_progress_protocol()
        check_chart_split()
        return
    if not args.model or not args.image:
        parser.error("--model and --image are required unless --device-self-check is used")
    check_block_graph(args.model, args.image)
    runner = """import socket, runpy, sys, pathlib
def deny_network(*a, **kw):
    raise AssertionError('Network attempted during local OCR')
socket.socket.connect = deny_network
socket.create_connection = deny_network
sys.argv = sys.argv[1:]
sys.path.insert(0, str(pathlib.Path(sys.argv[0]).resolve().parent))
runpy.run_path(sys.argv[0], run_name='__main__')
"""
    frame = base64.b64encode(args.image.read_bytes()).decode("ascii")
    requests = ["invalid json", "[]", '{"image":"not-base64"}', json.dumps({"image": frame, "language": args.language}), json.dumps({"image": frame, "language": args.language})]
    result = subprocess.run([sys.executable, "-c", runner, str(Path(__file__).with_name("worker.py")), "--model", str(args.model)],
                            input="\n".join(requests)+"\n", capture_output=True, text=True, encoding="utf-8", timeout=120)
    assert result.returncode == 0, result.stderr
    replies = [json.loads(line) for line in result.stdout.splitlines()]
    assert len(replies) == len(requests), result.stdout
    assert all("error" in reply for reply in replies[:3]), replies
    import cv2
    image = cv2.imread(str(args.image))
    height, width = image.shape[:2]
    for reply in replies[3:]:
        assert reply.get("regions"), reply
        for region in reply["regions"]:
            if args.language == "ja":
                assert region.get("text"), region
            assert 0 <= region["x"] < region["x"] + region["width"] <= width, region
            assert 0 <= region["y"] < region["y"] + region["height"] <= height, region
    assert replies[3] == replies[4], "Warmed detector changed regions for an identical frame"
    print(f"PASS: invalid requests recover, {len(replies[3]['regions'])} valid regions, repeat frame stable, sockets blocked.")


if __name__ == "__main__":
    main()
