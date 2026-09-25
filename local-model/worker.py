"""Offline JSON-lines translation worker. stdout is reserved for protocol replies."""

import argparse
from collections import OrderedDict
import json
import os
from pathlib import Path
import sys

from native_server import NativeServer, NativeServerError


os.environ['HF_HUB_OFFLINE'] = '1'
os.environ['TRANSFORMERS_OFFLINE'] = '1'
os.environ['HF_HUB_DISABLE_TELEMETRY'] = '1'

LANGUAGES = {'ja': 'Japanese', 'ko': 'Korean', 'en': 'English', 'th': 'Thai'}


class Translator:
    def __init__(self, model_path, device, native_path=None):
        self.model_path = Path(model_path).resolve()
        self.device = device
        self.native_path = (Path(native_path).resolve() if native_path is not None else
                            Path(__file__).resolve().parent.parent / '.tools/llama-server/llama-server.exe')
        self.engine = None
        self.using_cuda = False
        self.using_native = False
        self.native_failed = False
        self.cache = OrderedDict()
        self.dll_directories = []

    @property
    def parallelism(self):
        return 4 if self.using_native else 1

    def _model_file(self):
        models = list(self.model_path.glob('*.gguf'))
        if len(models) != 1:
            raise ValueError('Local model is missing or ambiguous. Run local-model/setup.ps1; the model folder must contain exactly one GGUF file.')
        return models[0]

    def load(self):
        if self.engine is not None:
            return
        model = self._model_file()
        if self.device != 'cpu' and not self.native_failed and self.native_path.is_file():
            native = NativeServer(self.native_path, model)
            try:
                native.start()
                self.engine = native
                self.using_cuda = True
                self.using_native = True
                return
            except Exception:
                native.close()
                self.native_failed = True
                if self.device != 'auto':
                    raise
                print('Native CUDA translation unavailable; using the local Python runtime.',
                      file=sys.stderr, flush=True)
        self._load_legacy(model)

    def _load_legacy(self, model):
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
            self.engine = Llama(model_path=str(model), n_ctx=2048,
                                n_gpu_layers=-1 if use_cuda else 0, n_threads=4,
                                n_batch=128, n_ubatch=64, flash_attn=use_cuda,
                                verbose=False)
            self.using_cuda = use_cuda
            self.using_native = False
        except (RuntimeError, ValueError, OSError):
            if self.device != 'auto' or not use_cuda:
                raise
            print('CUDA unavailable; using local CPU translation.', file=sys.stderr, flush=True)
            self.device = 'cpu'
            self.engine = None
            self._load_legacy(model)

    def close(self):
        engine, self.engine = self.engine, None
        self.using_cuda = False
        self.using_native = False
        if engine is not None:
            engine.close()

    def complete(self, prompt, max_tokens=512):
        try:
            return self.engine.create_chat_completion(
                messages=[{'role': 'user', 'content': prompt}], temperature=0, top_p=0.6,
                top_k=20, repeat_penalty=1.05, max_tokens=max_tokens,
            )
        except (RuntimeError, ValueError, OSError):
            if self.device != 'auto' or not self.using_cuda:
                raise
            print('CUDA inference unavailable; using local CPU translation.', file=sys.stderr, flush=True)
            self.engine.close()
            self.device, self.engine, self.using_cuda = 'cpu', None, False
            self.load()
            return self.complete(prompt, max_tokens)

    @staticmethod
    def _prompt(source, target, comic, text):
        kind = 'manga text' if comic else 'text'
        return (f'Translate the following {LANGUAGES[source]} {kind} into natural {LANGUAGES[target]}. '
                f'Preserve meaning, tone, names and numbers. Transliterate {LANGUAGES[source]} proper names '
                f'into {LANGUAGES[target]}. Only output the translation:\n{text}')

    def _tokens(self, text):
        if self.using_native:
            return self.engine.tokenize(text)
        return self.engine.tokenize(text.encode('utf-8'))

    @staticmethod
    def _legacy_result(response):
        try:
            choice = response['choices'][0]
            if choice.get('finish_reason') != 'stop':
                raise ValueError('Translation reached its output limit; select a smaller text region.')
            translated = choice['message']['content'].strip()
        except (KeyError, IndexError, TypeError, AttributeError) as error:
            raise ValueError('The local model returned an invalid translation.') from error
        if not translated:
            raise ValueError('The local model returned an empty translation.')
        return translated

    def _fallback_from_native(self):
        self.close()
        self.native_failed = True
        self.load()

    def translate(self, request, progress_callback=None):
        if not isinstance(request, dict):
            raise ValueError('Request must be a JSON object.')
        texts, source, target = request.get('texts'), request.get('source'), request.get('target')
        progress = request.get('progress', False)
        if not isinstance(progress, bool):
            raise ValueError('progress must be true or false.')
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

        keys = [(source, target, comic, text) for text in texts]
        missing = list(dict.fromkeys(
            text for key, text in zip(keys, texts) if text.strip() and source != target and key not in self.cache))
        def prepare_jobs():
            prepared = []
            for text in missing:
                input_tokens = len(self._tokens(text))
                if input_tokens > 512:
                    raise ValueError('A text region exceeds 512 model tokens; select a smaller text region.')
                max_tokens = min(512, max(128, input_tokens * 3 + 32))
                prepared.append(((source, target, comic, text),
                                 self._prompt(source, target, comic, text), max_tokens))
            return prepared

        try:
            jobs = prepare_jobs()
        except NativeServerError:
            if self.device != 'auto' or not self.using_native:
                raise
            print('Native CUDA tokenization unavailable; using the local Python runtime.',
                  file=sys.stderr, flush=True)
            self._fallback_from_native()
            jobs = prepare_jobs()

        output = [None] * len(texts)
        emitted = set()

        def publish_key(key, translated):
            for index, item_key in enumerate(keys):
                if item_key == key:
                    output[index] = translated
                    if progress and progress_callback is not None and index not in emitted:
                        progress_callback(index, translated)
                        emitted.add(index)

        for key, text in zip(keys, texts):
            if not text.strip() or source == target:
                publish_key(key, text)
            elif key in self.cache:
                publish_key(key, self.cache[key])

        completed = set()
        if self.using_native and jobs:
            try:
                for key, translated in self.engine.complete_many(jobs):
                    self.cache[key] = translated
                    completed.add(key)
                    publish_key(key, translated)
            except NativeServerError:
                if self.device != 'auto':
                    raise
                print('Native CUDA inference unavailable; using the local Python runtime.',
                      file=sys.stderr, flush=True)
                self._fallback_from_native()

        for key, prompt, max_tokens in jobs:
            if key in completed:
                continue
            response = self.complete(prompt, max_tokens)
            translated = self._legacy_result(response)
            self.cache[key] = translated
            publish_key(key, translated)

        for key in keys:
            if key in self.cache:
                self.cache.move_to_end(key)
        while len(self.cache) > 2048:
            self.cache.popitem(last=False)
        if any(value is None for value in output):
            raise RuntimeError('Translation did not produce every requested result.')
        return output


def serve(translator, input_stream, output_stream):
    try:
        for line in input_stream:
            try:
                request = json.loads(line)

                def progress(index, translation):
                    print(json.dumps({'index': index, 'translation': translation}, ensure_ascii=False),
                          file=output_stream, flush=True)

                translations = translator.translate(request, progress)
                reply = {'translations': translations, 'parallelism': translator.parallelism}
            except Exception as error:
                reply = {'error': str(error)}
            print(json.dumps(reply, ensure_ascii=False), file=output_stream, flush=True)
    finally:
        translator.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--model', required=True)
    parser.add_argument('--device', choices=('auto', 'cpu', 'cuda'),
                        default=os.environ.get('LOCAL_TRANSLATOR_DEVICE', 'auto'))
    args = parser.parse_args()
    sys.stdin.reconfigure(encoding='utf-8')
    sys.stdout.reconfigure(encoding='utf-8')
    serve(Translator(args.model, args.device), sys.stdin, sys.stdout)


if __name__ == '__main__':
    main()
