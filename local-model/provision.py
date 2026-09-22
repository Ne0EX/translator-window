"""Provision the pinned, Apache-2.0 local translation model. Runtime never calls this."""
import hashlib
import json
from pathlib import Path
import shutil
from huggingface_hub import snapshot_download

MODEL = 'tencent/Hy-MT2-1.8B-GGUF'
REVISION = 'a0c709d9fac510f2c807aa3af52872340dc37a4a'
FILENAME = 'Hy-MT2-1.8B-Q8_0.gguf'
SHA256 = '5c3fe0b1408a5ceb0143184ef247b11b579c525f4b02b060e6c851bb76fef1a4'
ROOT = Path(__file__).resolve().parent.parent
DESTINATION = ROOT / 'models' / 'hy-mt2'


def main():
    model = DESTINATION / FILENAME
    if not model.is_file():
        snapshot_download(MODEL, revision=REVISION, local_dir=DESTINATION, cache_dir=ROOT / '.cache/huggingface',
                          token=False, allow_patterns=[FILENAME, 'README.md', 'LICENSE.txt'])
    with model.open('rb') as stream:
        if hashlib.file_digest(stream, 'sha256').hexdigest() != SHA256:
            raise RuntimeError(f'Model SHA256 mismatch: {model}. Remove this damaged file and rerun setup.')
    for source, target in [('MODEL_CARD.md', 'MODEL_CARD.md'), ('LICENSE-APACHE-2.0.txt', 'LICENSE.txt'), ('ATTRIBUTION.md', 'ATTRIBUTION.md')]:
        shutil.copyfile(ROOT / 'local-model/attribution' / source, DESTINATION / target)
    (DESTINATION / 'provisioned.json').write_text(json.dumps({
        'model': MODEL, 'revision': REVISION, 'file': FILENAME, 'sha256': SHA256,
        'license': 'Apache-2.0', 'quantization': 'Q8_0', 'runtime': 'llama-cpp-python 0.3.35',
    }, indent=2), encoding='utf-8')
    print(f'Verified local translation model: {DESTINATION}')


if __name__ == '__main__':
    main()
