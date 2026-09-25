"""Check protocol guards; --model adds real inference with loopback-only networking."""

import argparse
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import types
from unittest import mock
import urllib.parse

import native_server
from native_server import NativeServer, NativeServerError
from worker import Translator, serve


def command(model):
    code = '''import runpy, socket, sys
original_connect = socket.socket.connect
original_create_connection = socket.create_connection
def checked_address(address):
    if not isinstance(address, tuple) or not address or address[0] != "127.0.0.1":
        raise AssertionError("Non-loopback network access attempted during offline translation")
def loopback_connect(self, address):
    checked_address(address)
    return original_connect(self, address)
def loopback_create_connection(address, *args, **kwargs):
    checked_address(address)
    return original_create_connection(address, *args, **kwargs)
socket.socket.connect = loopback_connect
socket.create_connection = loopback_create_connection
sys.argv = sys.argv[1:]
sys.path.insert(0, str(__import__("pathlib").Path(sys.argv[0]).parent))
runpy.run_path(sys.argv[0], run_name="__main__")
'''
    return [sys.executable, '-c', code, str(Path(__file__).with_name('worker.py')),
            '--model', str(model)]


def run(model, lines):
    process = subprocess.run(command(model), input='\n'.join(lines) + '\n', capture_output=True,
                             text=True, encoding='utf-8', timeout=180)
    assert process.returncode == 0, process.stderr
    replies = [json.loads(line) for line in process.stdout.splitlines()]
    assert len(replies) == len(lines), process.stdout
    return replies


class Engine:
    def __init__(self, output='translated'):
        self.output = output
        self.completions = []
        self.closed = False

    def tokenize(self, text):
        return [1, 2]

    def create_chat_completion(self, **kwargs):
        self.completions.append(kwargs)
        return {'choices': [{'finish_reason': 'stop',
                             'message': {'content': self.output}}]}

    def close(self):
        self.closed = True


def check_native_client():
    class Response:
        def __init__(self, value):
            self.value = value

        def __enter__(self):
            return self

        def __exit__(self, *args):
            return False

        def read(self):
            return json.dumps(self.value).encode('utf-8')

    class Opener:
        def __init__(self, model):
            self.model = model
            self.requests = []

        def open(self, request, timeout):
            path = urllib.parse.urlsplit(request.full_url).path
            body = json.loads(request.data) if request.data else None
            self.requests.append((path, dict(request.headers), body, timeout))
            if path == '/health':
                return Response({'status': 'ok'})
            assert request.get_header('Authorization', '').startswith('Bearer ')
            if path == '/props':
                return Response({'model_path': str(self.model), 'total_slots': 4})
            if path == '/tokenize':
                return Response({'tokens': [1, 7, 9]})
            raise AssertionError(path)

    class Process:
        def __init__(self):
            self.stderr = io.StringIO('')
            self.returncode = None
            self.terminated = False

        def poll(self):
            return self.returncode

        def terminate(self):
            self.terminated = True
            self.returncode = 0

        def wait(self, timeout=None):
            return self.returncode

        def kill(self):
            self.returncode = -9

    with tempfile.TemporaryDirectory() as directory:
        directory = Path(directory)
        executable, model = directory / 'llama-server.exe', directory / 'model.gguf'
        executable.touch()
        model.touch()
        process = Process()
        with mock.patch.object(NativeServer, '_free_port', return_value=43127), \
             mock.patch.object(native_server.subprocess, 'Popen', return_value=process) as popen:
            server = NativeServer(executable, model)
            server.opener = Opener(model)
            server.start()
            assert server.tokenize('父→子🙂') == [1, 7, 9]
            command_line = popen.call_args.args[0]
            for flag in ('--offline', '--no-webui', '--no-agent', '--no-slots',
                         '--cont-batching', '--no-cache-prompt'):
                assert flag in command_line, command_line
            assert command_line[command_line.index('--parallel') + 1] == '4'
            assert command_line[command_line.index('--kv-unified-per-slot') + 1] == '2048'
            assert command_line[command_line.index('--load-mode') + 1] == 'none'
            assert command_line[command_line.index('--cache-ram') + 1] == '0'
            assert command_line[command_line.index('--batch-size') + 1] == '128'
            assert command_line[command_line.index('--ubatch-size') + 1] == '64'
            environment = popen.call_args.kwargs['env']
            assert environment['LLAMA_API_KEY'] == server.api_key
            assert all(urllib.parse.urlsplit(
                f'http://127.0.0.1:43127{path}').hostname == '127.0.0.1'
                       for path, _, _, _ in server.opener.requests)
            tokenize = next(item for item in server.opener.requests if item[0] == '/tokenize')
            assert tokenize[2] == {
                'content': '父→子🙂', 'add_special': True, 'parse_special': False,
            }
            server.close()
            server.close()
            assert process.terminated


def check_translator_protocol():
    class FakeNative:
        def __init__(self):
            self.calls = []
            self.closed = False

        def tokenize(self, text):
            return [1, 2]

        def complete_many(self, jobs):
            self.calls.append(list(jobs))
            jobs = list(jobs)
            for key, _, _ in reversed(jobs):
                yield key, f'translated-{key[-1]}'

        def close(self):
            self.closed = True

    native = FakeNative()
    probe = Translator('unused', 'auto')
    probe.engine, probe.using_native, probe.using_cuda = native, True, True
    events = []
    request = {'texts': ['one', 'two', 'one', ''], 'source': 'en',
               'target': 'th', 'progress': True}
    output = probe.translate(request, lambda index, text: events.append((index, text)))
    assert output == ['translated-one', 'translated-two', 'translated-one', '']
    assert sorted(index for index, _ in events) == [0, 1, 2, 3], events
    assert events.index((1, 'translated-two')) < events.index((0, 'translated-one')), events
    assert len(native.calls) == 1 and len(native.calls[0]) == 2
    events.clear()
    assert probe.translate({'texts': ['one', 'one'], 'source': 'en',
                            'target': 'th', 'progress': True},
                           lambda index, text: events.append((index, text))) == [
                               'translated-one', 'translated-one']
    assert events == [(0, 'translated-one'), (1, 'translated-one')], events
    assert len(native.calls) == 1, 'Cache hits must not run native inference.'

    class FailingNative(FakeNative):
        def complete_many(self, jobs):
            jobs = list(jobs)
            yield jobs[0][0], 'native-first'
            self.close()
            raise NativeServerError('native request failed')

    with tempfile.TemporaryDirectory() as model_dir:
        (Path(model_dir) / 'model.gguf').touch()
        failed = FailingNative()
        fallback = Engine('legacy-rest')
        probe = Translator(model_dir, 'auto')
        probe.engine, probe.using_native, probe.using_cuda = failed, True, True

        def load_legacy(model):
            probe.engine = fallback
            probe.using_native = False
            probe.using_cuda = False

        probe._load_legacy = load_legacy
        events = []
        output = probe.translate({'texts': ['first', 'second'], 'source': 'en',
                                  'target': 'th', 'progress': True},
                                 lambda index, text: events.append((index, text)))
        assert output == ['native-first', 'legacy-rest'], output
        assert events == [(0, 'native-first'), (1, 'legacy-rest')], events
        assert failed.closed and probe.native_failed
        assert len(fallback.completions) == 1, 'Successful native results must survive fallback.'

    class TokenFailingNative(FakeNative):
        def tokenize(self, text):
            raise NativeServerError('native tokenizer stopped')

    with tempfile.TemporaryDirectory() as model_dir:
        (Path(model_dir) / 'model.gguf').touch()
        failed = TokenFailingNative()
        fallback = Engine('legacy-after-tokenize')
        probe = Translator(model_dir, 'auto')
        probe.engine, probe.using_native, probe.using_cuda = failed, True, True

        def load_after_token_failure(model):
            probe.engine = fallback
            probe.using_native = False
            probe.using_cuda = False

        probe._load_legacy = load_after_token_failure
        events = []
        output = probe.translate({'texts': ['text'], 'source': 'en', 'target': 'th',
                                  'progress': True},
                                 lambda index, text: events.append((index, text)))
        assert output == ['legacy-after-tokenize']
        assert events == [(0, 'legacy-after-tokenize')]
        assert failed.closed and probe.native_failed

    class TruncatedNative(FakeNative):
        def complete_many(self, jobs):
            self.close()
            raise NativeServerError('Translation reached its output limit')
            yield

    probe = Translator('unused', 'cuda')
    truncated = TruncatedNative()
    probe.engine, probe.using_native, probe.using_cuda = truncated, True, True
    try:
        probe.translate({'texts': ['test'], 'source': 'en', 'target': 'th'})
        raise AssertionError('A partial native translation was accepted')
    except NativeServerError as error:
        assert 'output limit' in str(error)
    assert not probe.cache and truncated.closed

    for invalid in ('yes', 1, None):
        try:
            probe.translate({'texts': [], 'source': 'en', 'target': 'th',
                             'progress': invalid})
            raise AssertionError('Invalid progress was accepted')
        except ValueError as error:
            assert 'progress' in str(error)

    class ClosingTranslator:
        parallelism = 1

        def __init__(self):
            self.closed = False

        def close(self):
            self.closed = True

    closing = ClosingTranslator()
    serve(closing, io.StringIO(''), io.StringIO())
    assert closing.closed, 'EOF must close the translation engine.'

    class ServingTranslator(ClosingTranslator):
        parallelism = 4

        def translate(self, request, callback):
            assert request['progress'] is True
            callback(1, 'second')
            callback(0, 'first')
            return ['first', 'second']

    serving = ServingTranslator()
    output = io.StringIO()
    serve(serving, io.StringIO(json.dumps({
        'texts': ['one', 'two'], 'source': 'en', 'target': 'th', 'progress': True,
    }) + '\n'), output)
    lines = [json.loads(line) for line in output.getvalue().splitlines()]
    assert lines == [
        {'index': 1, 'translation': 'second'},
        {'index': 0, 'translation': 'first'},
        {'translations': ['first', 'second'], 'parallelism': 4},
    ], lines
    assert serving.closed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--model', type=Path)
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding='utf-8')
    with tempfile.TemporaryDirectory() as empty:
        replies = run(empty, [
            'invalid json',
            '[]',
            json.dumps({'texts': [42], 'source': 'ko', 'target': 'en'}),
            json.dumps({'texts': ['hello'], 'source': 'en', 'target': 'th'}),
        ])
        assert all('error' in reply for reply in replies), replies
        assert 'Local model is missing' in replies[-1]['error'], replies[-1]

    configured = {}

    class ConfiguredEngine:
        def __init__(self, **kwargs):
            configured.update(kwargs)

        def close(self):
            pass

    with tempfile.TemporaryDirectory() as model_dir:
        (Path(model_dir) / 'model.gguf').touch()
        missing_native = Path(model_dir) / 'missing-server.exe'
        llama_stub = types.SimpleNamespace(Llama=ConfiguredEngine,
                                           llama_supports_gpu_offload=lambda: True)
        with mock.patch.dict(sys.modules, {'llama_cpp': llama_stub}):
            Translator(model_dir, 'cuda', native_path=missing_native).load()
    assert configured['n_batch'] == 128 and configured['n_ubatch'] == 64, configured
    configured.clear()
    with tempfile.TemporaryDirectory() as model_dir:
        model_dir = Path(model_dir)
        (model_dir / 'model.gguf').touch()
        available_native = model_dir / 'llama-server.exe'
        available_native.touch()
        llama_stub = types.SimpleNamespace(Llama=ConfiguredEngine,
                                           llama_supports_gpu_offload=lambda: True)
        with mock.patch.dict(sys.modules, {'llama_cpp': llama_stub}), \
             mock.patch('worker.NativeServer') as native_constructor:
            cpu = Translator(model_dir, 'cpu', native_path=available_native)
            cpu.load()
            native_constructor.assert_not_called()
    assert configured['n_gpu_layers'] == 0, configured

    completions = []

    class RecordingEngine(Engine):
        def create_chat_completion(self, **kwargs):
            completions.append(kwargs)
            return super().create_chat_completion(**kwargs)

    class MissingCuda(Engine):
        def create_chat_completion(self, **kwargs):
            raise RuntimeError('CUDA allocation failed')

    class Truncated(Engine):
        def create_chat_completion(self, **kwargs):
            return {'choices': [{'finish_reason': 'length',
                                 'message': {'content': 'partial'}}]}

    probe = Translator('unused', 'auto')
    probe.engine, probe.using_cuda = MissingCuda(), True
    probe.load = lambda: setattr(probe, 'engine', RecordingEngine())
    assert probe.complete('test', 137)['choices'][0]['message']['content'] == 'translated'
    assert probe.device == 'cpu'
    assert completions[-1]['max_tokens'] == 137, completions[-1]
    probe.engine, probe.load = Truncated(), lambda: None
    try:
        probe.translate({'texts': ['test'], 'source': 'en', 'target': 'th'})
        raise AssertionError('A partial translation was accepted')
    except ValueError as error:
        assert 'output limit' in str(error)
    assert not probe.cache, 'Partial translations must not enter the cache.'

    check_native_client()
    check_translator_protocol()
    print('PASS: invalid input stays in sync; native runtime is authenticated and loopback-only; '
          'bounded batches, indexed progress, duplicate/cache mapping, fallback, truncation, close and EOF pass.')
    if not args.model:
        return

    for source, target in [('MODEL_CARD.md', 'MODEL_CARD.md'),
                           ('LICENSE-APACHE-2.0.txt', 'LICENSE.txt'),
                           ('ATTRIBUTION.md', 'ATTRIBUTION.md')]:
        assert (args.model / target).read_bytes() == (
            Path(__file__).parent / 'attribution' / source).read_bytes(), target
    process = subprocess.Popen(command(args.model), stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                               stderr=subprocess.PIPE, text=True, encoding='utf-8')

    def request(source, target, texts):
        started = __import__('time').perf_counter()
        process.stdin.write(json.dumps({'texts': texts, 'source': source, 'target': target}) + '\n')
        process.stdin.flush()
        line = process.stdout.readline()
        assert line, process.stderr.read()
        reply = json.loads(line)
        assert 'error' not in reply, reply
        output = reply['translations']
        assert len(output) == len(texts) and all(output), reply
        print(json.dumps({
            'pair': source + '->' + target,
            'seconds': round(__import__('time').perf_counter() - started, 3),
            'parallelism': reply['parallelism'],
            'translations': output,
        }, ensure_ascii=False))
        return reply

    try:
        warmup = request('ja-comic', 'th', [])
        installed_native = (Path(__file__).resolve().parent.parent /
                            '.tools/llama-server/llama-server.exe').is_file()
        expected_parallelism = 4 if installed_native else 1
        assert warmup['parallelism'] == expected_parallelism, (
            'Native runtime was installed but the worker silently used its fallback.')
        print(f'BACKEND: {"native llama-server" if warmup["parallelism"] == 4 else "legacy llama_cpp"}')
        samples = {'en': 'The train leaves at 9.', 'ja': '電車は9時に出発します。',
                   'ko': '기차는 9시에 출발합니다.', 'th': 'รถไฟออกตอน 9 โมง'}
        for source, text in samples.items():
            for target in samples:
                if source == target:
                    continue
                output = request(source, target, [text])['translations'][0]
                assert output != text
                if target == 'th':
                    assert any('\u0e00' <= character <= '\u0e7f' for character in output)
                if target == 'ko':
                    assert any('\uac00' <= character <= '\ud7a3' for character in output)
                if target == 'ja':
                    assert any('\u3040' <= character <= '\u30ff' for character in output)
        first = request('ja-vert', 'th-TH',
                        ['本日は君に当院の当直のアルバイトをお願いする'])['translations']
        assert 'พาร์ทไทม์' in first[0] and 'โรงพยาบาล' in first[0], (
            'The work and hospital meanings must survive.')
        assert request('ja-comic', 'th',
                       ['本日は君に当院の当直のアルバイトをお願いする'])['translations'] == first
        assert request('en-US', 'en', ['Unchanged.'])['translations'] == ['Unchanged.']
        print('PASS: all twelve directions; region tags; cache; semantic work/hospital check; '
              'numeric loopback is the only permitted network destination.')
    finally:
        process.stdin.close()
        try:
            process.wait(timeout=30)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()

    if installed_native:
        models = list(args.model.glob('*.gguf'))
        assert len(models) == 1
        token_server = NativeServer(
            Path(__file__).resolve().parent.parent / '.tools/llama-server/llama-server.exe',
            models[0])
        try:
            token_server.start()
            source9 = 'これまでは「父から子へ」が基本とされていた皇位継承がーー'
            assert len(token_server.tokenize(source9)) == 22
            boundary = '父🙂' * 128
            assert len(token_server.tokenize(boundary)) == 512
            assert len(token_server.tokenize(boundary + '父')) == 513
        finally:
            token_server.close()
        print('PASS: native raw Unicode token counts match the Python contract at source9 and '
              'the 512/513 input boundary.')


if __name__ == '__main__':
    main()
