"""Check detector protocol with sockets blocked and a user-supplied local test page."""
import argparse
import base64
import json
from pathlib import Path
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--image", type=Path, required=True)
    parser.add_argument("--language", default="", choices=("", "ja", "ko", "en", "th"))
    args = parser.parse_args()
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
