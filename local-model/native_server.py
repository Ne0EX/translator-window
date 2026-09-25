"""Authenticated loopback client for the bundled native llama-server."""

from collections import deque
import concurrent.futures
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request


class NativeServerError(RuntimeError):
    pass


class NativeServer:
    parallelism = 4

    def __init__(self, executable, model):
        self.executable = Path(executable).resolve()
        self.model = Path(model).resolve()
        self.api_key = secrets.token_urlsafe(32)
        self.port = self._free_port()
        self.process = None
        self.stderr_lines = deque(maxlen=100)
        self.stderr_thread = None
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    @staticmethod
    def _free_port():
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
            listener.bind(('127.0.0.1', 0))
            return listener.getsockname()[1]

    def start(self):
        if self.process is not None:
            return
        if not self.executable.is_file():
            raise FileNotFoundError(f'Native translation runtime is missing: {self.executable}')
        command = [
            str(self.executable), '--model', str(self.model), '--host', '127.0.0.1',
            '--port', str(self.port), '--threads', '4', '--n-gpu-layers', '99',
            # b11146 cannot unmap offloaded mmap fragments on Windows, so direct
            # reads avoid retaining the full GGUF in the server working set.
            '--load-mode', 'none',
            '--batch-size', '128', '--ubatch-size', '64', '--flash-attn', 'on',
            # Q8 KV reduces GPU allocation for the four 2048-token slots.
            '--parallel', '4', '--kv-unified-per-slot', '2048',
            '--cache-type-k', 'q8_0', '--cache-type-v', 'q8_0', '--cont-batching',
            # Requests do not reuse server prompts; avoid retaining completed slot states in RAM.
            '--no-cache-prompt', '--cache-ram', '0',
            '--no-webui', '--no-agent', '--no-slots', '--offline',
        ]
        environment = os.environ.copy()
        environment['LLAMA_API_KEY'] = self.api_key
        environment['PATH'] = str(self.executable.parent) + os.pathsep + environment.get('PATH', '')
        startup = None
        creation_flags = 0
        if sys.platform == 'win32':
            startup = subprocess.STARTUPINFO()
            startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
            creation_flags = subprocess.CREATE_NO_WINDOW
        self.process = subprocess.Popen(
            command, cwd=self.executable.parent, env=environment,
            stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE,
            startupinfo=startup, creationflags=creation_flags, text=True, encoding='utf-8',
            errors='replace',
        )
        self.stderr_thread = threading.Thread(
            target=self._drain_stderr, args=(self.process.stderr,), daemon=True)
        self.stderr_thread.start()
        try:
            self._wait_ready()
        except Exception:
            self.close()
            raise

    def _drain_stderr(self, stream):
        if stream is None:
            return
        for line in stream:
            self.stderr_lines.append(line.rstrip())

    def _request(self, path, body=None, timeout=180, authenticated=True):
        data = None if body is None else json.dumps(body, ensure_ascii=False).encode('utf-8')
        headers = {'Content-Type': 'application/json'}
        if authenticated:
            headers['Authorization'] = f'Bearer {self.api_key}'
        request = urllib.request.Request(
            f'http://127.0.0.1:{self.port}{path}', data=data, headers=headers)
        with self.opener.open(request, timeout=timeout) as response:
            return json.loads(response.read().decode('utf-8'))

    def _wait_ready(self):
        deadline = time.monotonic() + 120
        while time.monotonic() < deadline:
            if self.process.poll() is not None:
                detail = '\n'.join(self.stderr_lines)
                raise NativeServerError(f'Native translation server stopped during startup. {detail[-2000:]}')
            try:
                try:
                    health = self._request('/health', timeout=1, authenticated=False)
                except urllib.error.HTTPError as error:
                    if error.code != 401:
                        raise
                    health = self._request('/health', timeout=1, authenticated=True)
                if health.get('status') == 'ok':
                    props = self._request('/props', timeout=2, authenticated=True)
                    if not isinstance(props, dict):
                        raise NativeServerError('Native translation server returned invalid properties.')
                    reported = props.get('model_path')
                    if reported and Path(reported).resolve() != self.model:
                        raise NativeServerError('Another service answered on the native translation port.')
                    slots = props.get('total_slots')
                    if slots is not None and slots != self.parallelism:
                        raise NativeServerError('Native translation server activated an unexpected slot count.')
                    return
            except (OSError, urllib.error.URLError, json.JSONDecodeError):
                pass
            time.sleep(0.05)
        raise NativeServerError('Native translation server did not become ready.')

    def tokenize(self, text):
        try:
            response = self._request('/tokenize', {
                'content': text, 'add_special': True, 'parse_special': False,
            })
        except (OSError, urllib.error.URLError, json.JSONDecodeError) as error:
            raise NativeServerError(f'Native translation tokenization failed: {error}') from error
        tokens = response.get('tokens') if isinstance(response, dict) else None
        if not isinstance(tokens, list) or any(not isinstance(token, int) for token in tokens):
            raise NativeServerError('Native translation server returned invalid tokens.')
        return tokens

    def _complete(self, key, prompt, max_tokens):
        response = self._request('/v1/chat/completions', {
            'messages': [{'role': 'user', 'content': prompt}],
            'temperature': 0, 'top_p': 0.6, 'top_k': 20,
            'repeat_penalty': 1.05, 'max_tokens': max_tokens,
            'cache_prompt': False, 'stream': False,
        })
        try:
            choice = response['choices'][0]
            if choice.get('finish_reason') != 'stop':
                raise ValueError('Translation reached its output limit; select a smaller text region.')
            translated = choice['message']['content'].strip()
        except (KeyError, IndexError, TypeError, AttributeError) as error:
            raise NativeServerError('Native translation server returned an invalid response.') from error
        if not translated:
            raise ValueError('The local model returned an empty translation.')
        return key, translated

    def complete_many(self, jobs):
        executor = concurrent.futures.ThreadPoolExecutor(max_workers=self.parallelism)
        futures = [executor.submit(self._complete, *job) for job in jobs]
        failed = False
        try:
            for future in concurrent.futures.as_completed(futures):
                try:
                    yield future.result()
                except Exception as error:
                    failed = True
                    self.close()
                    for pending in futures:
                        pending.cancel()
                    raise NativeServerError(f'Native translation failed: {error}') from error
        finally:
            executor.shutdown(wait=True, cancel_futures=failed)

    def close(self):
        process, self.process = self.process, None
        if process is None:
            return
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=10)
        if self.stderr_thread is not None:
            self.stderr_thread.join(timeout=1)
            self.stderr_thread = None
        if process.stderr is not None:
            process.stderr.close()
