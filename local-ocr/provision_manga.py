"""Provision the pinned manga-ocr model. Runtime loaders never download files."""
import json
import shutil
import os
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
os.environ["HF_HOME"] = str(ROOT / ".cache" / "huggingface")
os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
os.environ["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1"
from huggingface_hub import snapshot_download

MODEL = "kha-white/manga-ocr-base"
REVISION = "aa6573bd10b0d446cbf622e29c3e084914df9741"

if __name__ == "__main__":
    destination = ROOT / "models" / "manga-ocr"
    snapshot_download(MODEL, revision=REVISION, local_dir=destination,
                      allow_patterns=["*.json", "*.txt", "pytorch_model.bin", "README.md"])
    shutil.copyfile(ROOT / "local-ocr" / "LICENSE.manga-ocr", destination / "LICENSE")
    shutil.copyfile(ROOT / "local-ocr" / "MANGA-ATTRIBUTION.md", destination / "ATTRIBUTION.md")
    (destination / "provisioned.json").write_text(
        json.dumps({"model": MODEL, "revision": REVISION, "license": "Apache-2.0"}, indent=2), encoding="utf-8",
    )
    print(f"Japanese manga recognizer ready: {destination}")
