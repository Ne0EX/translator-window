"""Extract the verified block-only graph from the pinned comic detector."""
import argparse
import hashlib
from pathlib import Path

import onnx


SOURCE_SHA256 = "1a86ace74961413cbd650002e7bb4dcec4980ffa21b2f19b86933372071d718f"
BLOCKS_SHA256 = "d92958fc0e73fbde5d26e4f8512b2298a7308cb70d2ba1303c4eec67d58aff03"


def checksum(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    if checksum(args.source) != SOURCE_SHA256:
        raise ValueError("Comic detector source failed SHA256 verification.")
    if args.output.is_file() and checksum(args.output) == BLOCKS_SHA256:
        return
    partial = args.output.with_name(args.output.stem + ".partial.onnx")
    try:
        onnx.utils.extract_model(args.source, partial, ["images"], ["blk"])
        if checksum(partial) != BLOCKS_SHA256:
            raise ValueError("Generated block detector failed SHA256 verification.")
        partial.replace(args.output)
    finally:
        partial.unlink(missing_ok=True)


if __name__ == "__main__":
    main()
