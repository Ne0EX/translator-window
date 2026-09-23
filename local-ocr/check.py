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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", type=Path)
    parser.add_argument("--image", type=Path)
    parser.add_argument("--language", default="", choices=("", "ja", "ko", "en", "th"))
    parser.add_argument("--device-self-check", action="store_true")
    args = parser.parse_args()
    if args.device_self_check:
        check_device_selection()
        return
    if not args.model or not args.image:
        parser.error("--model and --image are required unless --device-self-check is used")
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
