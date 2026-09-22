"""Offline JSON-lines translation worker. stdout is reserved for protocol replies."""

import argparse
from collections import OrderedDict
import json
import os
from pathlib import Path
import sys

os.environ['HF_HUB_OFFLINE'] = '1'
os.environ['TRANSFORMERS_OFFLINE'] = '1'
os.environ['HF_HUB_DISABLE_TELEMETRY'] = '1'

LANGUAGES = {'ja': 'Japanese', 'ko': 'Korean', 'en': 'English', 'th': 'Thai'}


class Translator:
    def __init__(self, model_path, device):
        self.model_path = Path(model_path).resolve()
        self.device = device
        self.engine = None
        self.using_cuda = False
        self.cache = OrderedDict()
        self.dll_directories = []

    def load(self):
        if self.engine is not None:
            return
        models = list(self.model_path.glob('*.gguf'))
        if len(models) != 1:
            raise ValueError('Local model is missing or ambiguous. Run local-model/setup.ps1; the model folder must contain exactly one GGUF file.')
        # Full CUDA setup also installs the OCR Torch wheel, which carries CUDA runtime DLLs.
        # Register that folder without importing Torch or allocating its CUDA context.
        torch_dlls = Path(sys.prefix) / 'Lib/site-packages/torch/lib'
        if sys.platform == 'win32' and torch_dlls.is_dir() and not self.dll_directories:
            self.dll_directories.append(os.add_dll_directory(str(torch_dlls)))
            os.environ['PATH'] = str(torch_dlls) + os.pathsep + os.environ.get('PATH', '')
        from llama_cpp import Llama, llama_supports_gpu_offload

        use_cuda = self.device != 'cpu' and llama_supports_gpu_offload()
        if self.device == 'cuda' and not use_cuda:
            raise RuntimeError('The installed local runtime has no CUDA support. Run setup with -Cuda.')
        try:
            self.engine = Llama(model_path=str(models[0]), n_ctx=2048, n_gpu_layers=-1 if use_cuda else 0,
                                n_threads=4, n_batch=512, flash_attn=use_cuda, verbose=False)
            self.using_cuda = use_cuda
        except (RuntimeError, ValueError, OSError):
            if self.device != 'auto' or not use_cuda:
                raise
            print('CUDA unavailable; using local CPU translation.', file=sys.stderr, flush=True)
            self.device = 'cpu'
            self.load()

    def complete(self, prompt):
        try:
            return self.engine.create_chat_completion(
                messages=[{'role': 'user', 'content': prompt}], temperature=0, top_p=0.6, top_k=20,
                repeat_penalty=1.05, max_tokens=512,
            )
        except (RuntimeError, ValueError, OSError):
            if self.device != 'auto' or not self.using_cuda:
                raise
            print('CUDA inference unavailable; using local CPU translation.', file=sys.stderr, flush=True)
            self.engine.close()
            self.device, self.engine, self.using_cuda = 'cpu', None, False
            self.load()
            return self.complete(prompt)

    def translate(self, request):
        if not isinstance(request, dict):
            raise ValueError('Request must be a JSON object.')
        texts, source, target = request.get('texts'), request.get('source'), request.get('target')
        if not isinstance(texts, list) or len(texts) > 64 or any(
            not isinstance(text, str) or len(text) > 4000 for text in texts
        ):
            raise ValueError('texts must contain at most 64 strings, each at most 4000 characters.')
        if not isinstance(source, str) or not isinstance(target, str):
            raise ValueError('source and target must be language codes.')
        comic = source.lower().endswith(('-comic', '-vert'))
        source, target = source.split('-', 1)[0].lower(), target.split('-', 1)[0].lower()
        if source not in LANGUAGES or target not in LANGUAGES:
            raise ValueError('Supported language codes: ja, ko, en, th.')
        self.load()
        if source == target:
            return texts
        missing = list(dict.fromkeys(text for text in texts if text.strip() and (source, target, comic, text) not in self.cache))
        for text in missing:
            if len(self.engine.tokenize(text.encode('utf-8'))) > 512:
                raise ValueError('A text region exceeds 512 model tokens; select a smaller text region.')
            kind = 'manga text' if comic else 'text'
            prompt = (f'Translate the following {LANGUAGES[source]} {kind} into natural {LANGUAGES[target]}. '
                      f'Preserve meaning, tone, names and numbers. Transliterate {LANGUAGES[source]} proper names '
                      f'into {LANGUAGES[target]}. Only output the translation:\n{text}')
            response = self.complete(prompt)
            choice = response['choices'][0]
            if choice.get('finish_reason') != 'stop':
                raise ValueError('Translation reached its output limit; select a smaller text region.')
            translated = choice['message']['content'].strip()
            if not translated:
                raise ValueError('The local model returned an empty translation.')
            self.cache[source, target, comic, text] = translated
        output = []
        for text in texts:
            if not text.strip():
                output.append(text)
                continue
            key = source, target, comic, text
            output.append(self.cache[key])
            self.cache.move_to_end(key)
        while len(self.cache) > 2048:
            self.cache.popitem(last=False)
        return output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--model', required=True)
    parser.add_argument('--device', choices=('auto', 'cpu', 'cuda'), default=os.environ.get('LOCAL_TRANSLATOR_DEVICE', 'auto'))
    args = parser.parse_args()
    sys.stdin.reconfigure(encoding='utf-8')
    sys.stdout.reconfigure(encoding='utf-8')
    translator = Translator(args.model, args.device)
    for line in sys.stdin:
        try:
            reply = {'translations': translator.translate(json.loads(line))}
        except Exception as error:
            reply = {'error': str(error)}
        print(json.dumps(reply, ensure_ascii=False), flush=True)


if __name__ == '__main__':
    main()
