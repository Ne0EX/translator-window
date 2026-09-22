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

import cv2
import numpy as np

from worker import select_text_boxes


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


def normalize(text):
    return ''.join(c for c in unicodedata.normalize('NFKC', text)
                   if not c.isspace() and not unicodedata.category(c).startswith('P'))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--model', type=Path)
    parser.add_argument('--image', type=Path)
    args = parser.parse_args()
    check_selection()
    print('PASS: union recovery preserves separate bubbles, ignores isolated weak proposals and adjacent columns.', flush=True)
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
    result = subprocess.run([sys.executable, '-c', runner, str(Path(__file__).with_name('worker.py')),
                             '--model', str(args.model)], input='\n'.join(requests)+'\n',
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
