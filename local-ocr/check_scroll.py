"""Regression for separate manga bubbles after scrolling the author-provided page 12.

See artifacts/manga-test/source.md for fixture attribution. This check uses local
model files, blocks socket connections, and never changes the desktop.
"""
import argparse
import base64
import json
from pathlib import Path
import subprocess
import sys
import unicodedata
from contextlib import nullcontext
from types import SimpleNamespace

import cv2
import numpy as np

from worker import reconcile_known_regions, select_text_boxes
from manga_recognizer import MangaRecognizer, TorchMangaRecognizer


def check_selection():
    def predictions(boxes):
        return np.array([[x + w / 2, y + h / 2, w, h, score, 1, 0]
                         for x, y, w, h, score in boxes], dtype=np.float32)

    union = (100, 100, 240, 280, 0.91)
    left = (100, 160, 62, 220, 0.69)
    right = (260, 100, 76, 204, 0.376)
    weak_noise = (600, 600, 60, 200, 0.376)
    boxes = select_text_boxes(predictions([union, left, right, weak_noise]))
    assert len(boxes) == 2 and all(box[2] < 100 for box in boxes), boxes
    assert len(select_text_boxes(predictions([union, left, weak_noise]))) == 1
    # Neighboring text columns within one bubble have no bubble-sized gap.
    columns = [(100, 100, 160, 280, 0.91), (100, 100, 72, 280, 0.7),
               (180, 100, 72, 280, 0.6)]
    assert len(select_text_boxes(predictions(columns))) == 1
    assert select_text_boxes(predictions([weak_noise])) == []
    assert select_text_boxes(np.empty((0, 7), dtype=np.float32)) == []


def check_known_reconciliation():
    def region(x, y, width, height, text=None):
        result = {'x': x, 'y': y, 'width': width, 'height': height, 'vertical': False}
        if text is not None:
            result['text'] = text
        return result

    known = [region(100, 100, 80, 60, 'known')]
    reused, count = reconcile_known_regions([region(97, 98, 86, 65)], known, 500, 400)
    assert count == 1 and reused[0].get('text') == 'known' \
        and tuple(reused[0][key] for key in ('x', 'y', 'width', 'height')) == (100, 100, 80, 60), reused

    split, count = reconcile_known_regions([region(100, 100, 35, 60), region(145, 100, 35, 60)], known, 500, 400)
    assert count == 0 and all('text' not in item for item in split), split

    second = region(200, 100, 80, 60, 'second')
    merged, count = reconcile_known_regions([region(97, 98, 186, 65)], known + [second], 500, 400)
    assert count == 0 and len(merged) == 1 and 'text' not in merged[0], merged

    nearby, count = reconcile_known_regions([region(100, 100, 80, 60), region(185, 110, 6, 20)], known, 500, 400)
    assert count == 1 and nearby[0].get('text') == 'known' and 'text' not in nearby[1], nearby

    missed, count = reconcile_known_regions([], known, 500, 400)
    assert count == 1 and missed[0].get('text') == 'known' \
        and tuple(missed[0][key] for key in ('x', 'y', 'width', 'height')) == (100, 100, 80, 60), missed

    for invalid in ([region(-1, 0, 10, 10, 'bad')], [region(0, 0, 10, 10, '')], 'bad'):
        try:
            reconcile_known_regions([], invalid, 500, 400)
            raise AssertionError(invalid)
        except ValueError:
            pass


def check_overlong_crop():
    from PIL import Image

    class Tensor:
        def to(self, *_): return self
        def half(self): return self

    class Row:
        def __init__(self, values): self.values = values
        def tolist(self): return self.values

    recognizer = TorchMangaRecognizer.__new__(TorchMangaRecognizer)
    recognizer.device = 'cpu'
    recognizer.torch = SimpleNamespace(inference_mode=nullcontext)
    recognizer.processor = lambda **_: SimpleNamespace(pixel_values=Tensor())
    recognizer.model = SimpleNamespace(config=SimpleNamespace(eos_token_id=2),
                                       generate=lambda *_args, **_kwargs: [Row([1, 2]), Row([1] * 300)])
    recognizer.tokenizer = SimpleNamespace(batch_decode=lambda *_args, **_kwargs: ['readable', 'runaway'])
    image = Image.new('RGB', (8, 8), 'white')
    assert recognizer.recognize([image, image]) == ['readable', '']


def normalize(text):
    return ''.join(c for c in unicodedata.normalize('NFKC', text)
                   if not c.isspace() and not unicodedata.category(c).startswith('P'))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--model', type=Path)
    parser.add_argument('--recognizer-model', type=Path)
    parser.add_argument('--image', type=Path)
    args = parser.parse_args()
    check_selection()
    check_known_reconciliation()
    check_overlong_crop()
    print('PASS: union recovery and strict known-region reconciliation preserve split, merge and nearby detections.', flush=True)
    if not args.model or not args.image:
        return
    image = cv2.imread(str(args.image))
    assert image is not None, args.image
    height, width = image.shape[:2]
    offsets = (0, 60, 100, 120)
    requests = []
    for dy in offsets:
        shifted = np.full_like(image, 255)
        shifted[:height-dy] = image[dy:]
        ok, png = cv2.imencode('.png', shifted)
        assert ok
        requests.append(json.dumps({'image': base64.b64encode(png).decode('ascii'), 'language': 'ja'}))
    runner = """import socket, runpy, sys, pathlib

def deny_network(*a, **kw):
    raise AssertionError('Network attempted during local OCR')
socket.socket.connect = deny_network
socket.create_connection = deny_network
sys.argv = sys.argv[1:]
sys.path.insert(0, str(pathlib.Path(sys.argv[0]).resolve().parent))
runpy.run_path(sys.argv[0], run_name='__main__')
"""
    command = [sys.executable, '-c', runner, str(Path(__file__).with_name('worker.py')),
               '--model', str(args.model)]
    if args.recognizer_model:
        command.extend(['--recognizer-model', str(args.recognizer_model)])
    result = subprocess.run(command, input='\n'.join(requests)+'\n',
                            capture_output=True, text=True, encoding='utf-8', timeout=120)
    assert result.returncode == 0, result.stderr
    replies = [json.loads(line) for line in result.stdout.splitlines()]
    assert len(replies) == len(offsets), result.stdout
    expected = sorted(map(normalize, ['本日は君に当院の当直のアルバイトをお願いする', 'それでは斉藤くん',
                                     '斉藤英二郎25歳', 'エリートって奴だな', '永大卒か',
                                     'この病院の当直は初めてと', '永禄大学医学部卒', '誠同病院']))
    for dy, reply in zip(offsets, replies):
        regions = reply.get('regions', [])
        actual = sorted(normalize(region.get('text', '')) for region in regions)
        assert actual == expected, (dy, reply)
        for region in regions:
            assert 0 <= region['x'] < region['x'] + region['width'] <= width
            assert 0 <= region['y'] < region['y'] + region['height'] <= height
        print(f'PASS: scroll {dy} px, all eight exact Japanese transcripts and valid source bounds, sockets blocked.', flush=True)


if __name__ == '__main__':
    main()
