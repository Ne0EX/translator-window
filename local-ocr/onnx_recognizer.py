"""Torch-free ONNX Runtime backend for the pinned manga-ocr model."""

import ctypes
import json
import os
from pathlib import Path
import sys

import numpy as np
import onnxruntime as ort
from PIL import Image


ASSETS = ("encoder_model.onnx", "decoder_init.onnx", "decoder_with_past.onnx",
          "generation_config.json", "vocab.txt")
FP16_ASSETS = ("encoder_model_fp16.onnx", "decoder_init_fp16.onnx",
               "decoder_with_past_fp16.onnx")
EXPECTED_GENERATION = {
    "decoder_start_token_id": 2,
    "early_stopping": True,
    "eos_token_id": 3,
    "length_penalty": 2.0,
    "max_length": 300,
    "no_repeat_ngram_size": 3,
    "num_beams": 4,
    "pad_token_id": 0,
}
ORT_RUNTIME_ERRORS = (
    RuntimeError,
    ort.capi.onnxruntime_pybind11_state.RuntimeException,
    ort.capi.onnxruntime_pybind11_state.EPFail,
    ort.capi.onnxruntime_pybind11_state.EngineError,
    ort.capi.onnxruntime_pybind11_state.Fail,
)


class _BeamHypotheses:
    def __init__(self):
        self.items = []

    def add(self, tokens, score):
        normalized = score / (len(tokens) ** EXPECTED_GENERATION["length_penalty"])
        if len(self.items) < 4 or normalized > self.items[0][0]:
            self.items.append((normalized, tokens.copy()))
            self.items.sort(key=lambda item: item[0])
            if len(self.items) > 4:
                self.items.pop(0)

    @property
    def done(self):
        return len(self.items) >= 4


class OnnxMangaRecognizer:
    @staticmethod
    def available(model_path):
        model_path = Path(model_path)
        return all((model_path / name).is_file() for name in ASSETS)

    def __init__(self, model_path, device="auto"):
        if device not in ("auto", "cpu", "cuda"):
            raise ValueError("Manga recognition device must be auto, cpu, or cuda.")
        self.model_path = Path(model_path).resolve()
        if not self.available(self.model_path):
            raise ValueError("Local ONNX manga recognizer is incomplete. Run local-ocr/setup.ps1 first.")
        generation = json.loads((self.model_path / "generation_config.json").read_text(encoding="utf-8"))
        mismatches = {name: (generation.get(name), value) for name, value in EXPECTED_GENERATION.items()
                      if generation.get(name) != value}
        if mismatches:
            raise ValueError(f"Unsupported manga recognizer generation config: {mismatches}")
        self.vocab = (self.model_path / "vocab.txt").read_text(encoding="utf-8").splitlines()
        self.allow_cpu_fallback = device == "auto"
        self.dll_directories = []
        providers = self._providers(device)
        self._open_sessions(providers)

    def _providers(self, device):
        available = ort.get_available_providers()
        if device == "cpu":
            return ["CPUExecutionProvider"]
        if "CUDAExecutionProvider" not in available:
            if device == "cuda":
                raise RuntimeError("ONNX Runtime CUDAExecutionProvider is unavailable.")
            return ["CPUExecutionProvider"]
        torch_dlls = Path(sys.prefix) / "Lib/site-packages/torch/lib"
        try:
            if sys.platform == "win32" and torch_dlls.is_dir():
                self._preload_cuda_dlls(torch_dlls)
        except (OSError,) + ORT_RUNTIME_ERRORS:
            if device == "cuda":
                raise
            return ["CPUExecutionProvider"]
        return ["CUDAExecutionProvider", "CPUExecutionProvider"]

    def _preload_cuda_dlls(self, torch_dlls):
        dll_path = str(torch_dlls)
        current_path = os.environ.get("PATH", "")
        if dll_path.casefold() not in (item.casefold() for item in current_path.split(os.pathsep)):
            os.environ["PATH"] = dll_path + os.pathsep + current_path
        self.dll_directories.append(os.add_dll_directory(dll_path))
        if hasattr(ort, "preload_dlls"):
            ort.preload_dlls(cuda=True, cudnn=False, directory=dll_path)
        self.cudnn_dll = ctypes.WinDLL(str(torch_dlls / "cudnn64_9.dll"))

    def _open_sessions(self, providers):
        self._select_graphs(providers)
        options = ort.SessionOptions()
        options.intra_op_num_threads = 4
        options.inter_op_num_threads = 1
        options.log_severity_level = 3
        try:
            self.encoder = self._create_session(self.graph_names[0], options, providers, encoder=True)
            self.decoder_init = self._create_session(self.graph_names[1], options, providers)
            self.decoder_with_past = self._create_session(self.graph_names[2], options, providers)
            sessions = (self.encoder, self.decoder_init, self.decoder_with_past)
            cuda_requested = providers[0] == "CUDAExecutionProvider"
            if cuda_requested and not all(session.get_providers()[0] == "CUDAExecutionProvider"
                                          for session in sessions):
                raise RuntimeError("A manga recognition graph did not activate CUDAExecutionProvider.")
        except ORT_RUNTIME_ERRORS:
            if not self.allow_cpu_fallback or providers == ["CPUExecutionProvider"]:
                raise
            return self._open_sessions(["CPUExecutionProvider"])
        self.using_cuda = providers[0] == "CUDAExecutionProvider"
        self.present_names = [item.name for item in self.decoder_init.get_outputs()[1:]]
        self.past_names = [item.name for item in self.decoder_with_past.get_inputs()[3:]]

    def _create_session(self, filename, options, providers, encoder=False):
        arguments = {"providers": providers}
        if encoder and providers[0] == "CUDAExecutionProvider":
            arguments["provider_options"] = [{"cudnn_conv_algo_search": "HEURISTIC"}, {}]
        return ort.InferenceSession(str(self.model_path / filename), options, **arguments)

    def _select_graphs(self, providers):
        use_fp16 = (providers[0] == "CUDAExecutionProvider"
                    and all((self.model_path / name).is_file() for name in FP16_ASSETS))
        self.graph_names = FP16_ASSETS if use_fp16 else ASSETS[:3]
        self.input_dtype = np.float16 if use_fp16 else np.float32

    def _pixels(self, images):
        arrays = []
        for image in images:
            image = image.convert("L").convert("RGB").resize((224, 224), Image.Resampling.BILINEAR)
            array = np.asarray(image, dtype=np.float32) / 255.0
            arrays.append(((array - 0.5) / 0.5).transpose(2, 0, 1))
        return np.ascontiguousarray(arrays, dtype=self.input_dtype)

    @staticmethod
    def _log_softmax(values):
        values = values.astype(np.float32, copy=False)
        maximum = values.max(axis=-1, keepdims=True)
        shifted = values - maximum
        return shifted - np.log(np.exp(shifted).sum(axis=-1, keepdims=True))

    @staticmethod
    def _ban_repeated_trigrams(scores, sequences):
        if sequences.shape[1] < 3:
            return
        for row, sequence in enumerate(sequences.tolist()):
            prefix = tuple(sequence[-2:])
            for index in range(len(sequence) - 2):
                if tuple(sequence[index:index + 2]) == prefix:
                    scores[row, sequence[index + 2]] = -np.inf

    def _advance(self, logits, sequences, beam_scores, finished):
        batch, beams = beam_scores.shape
        scores = self._log_softmax(logits)
        self._ban_repeated_trigrams(scores, sequences)
        scores = scores.reshape(batch, beams, -1) + beam_scores[:, :, None]
        flat = scores.reshape(batch, -1)
        candidate_ids = np.argpartition(flat, -2 * beams, axis=1)[:, -2 * beams:]
        candidate_scores = np.take_along_axis(flat, candidate_ids, axis=1)
        order = np.argsort(candidate_scores, axis=1)[:, ::-1]
        candidate_ids = np.take_along_axis(candidate_ids, order, axis=1)
        candidate_scores = np.take_along_axis(candidate_scores, order, axis=1)
        next_sequences, next_scores, sources = [], [], []
        vocabulary = logits.shape[-1]
        for image_index in range(batch):
            if finished[image_index].done:
                source = image_index * beams
                active = [(np.append(sequences[source], 0), 0.0, source)] * beams
            else:
                active = []
                for rank, (candidate, score) in enumerate(zip(candidate_ids[image_index],
                                                               candidate_scores[image_index])):
                    source_beam, token = divmod(int(candidate), vocabulary)
                    source = image_index * beams + source_beam
                    if token == EXPECTED_GENERATION["eos_token_id"]:
                        if rank < beams:
                            finished[image_index].add(sequences[source], float(score))
                    else:
                        active.append((np.append(sequences[source], token), float(score), source))
                    if len(active) == beams:
                        break
                if not active:
                    source = image_index * beams
                    active = [(np.append(sequences[source], 0), -1e9, source)] * beams
                while len(active) < beams:
                    active.append(active[-1])
            next_sequences.extend(item[0] for item in active)
            next_scores.extend(item[1] for item in active)
            sources.extend(item[2] for item in active)
        return (np.stack(next_sequences), np.asarray(next_scores, dtype=np.float32).reshape(batch, beams),
                np.asarray(sources, dtype=np.int64))

    def _run_bound(self, session, cpu_inputs, device_inputs):
        binding = session.io_binding()
        for name, value in cpu_inputs.items():
            binding.bind_cpu_input(name, value)
        for name, value in device_inputs.items():
            binding.bind_ortvalue_input(name, value)
        binding.bind_output("logits", "cpu")
        for name in self.present_names:
            binding.bind_output(name, "cuda")
        session.run_with_iobinding(binding)
        values = binding.get_outputs()
        return values[0].numpy()[:, -1, :], list(values[1:])

    @staticmethod
    def _finalize(finished, sequences, beam_scores, reached_limit):
        outputs = []
        beams = beam_scores.shape[1]
        for image_index, hypotheses in enumerate(finished):
            best = max(hypotheses.items, default=None, key=lambda item: item[0])
            if reached_limit and not hypotheses.done:
                start = image_index * beams
                active_score = max(
                    float(beam_scores[image_index, beam]) /
                    max(1, len(sequences[start + beam]) - 1) ** EXPECTED_GENERATION["length_penalty"]
                    for beam in range(beams)
                )
                if best is None or active_score >= best[0]:
                    outputs.append(None)
                    continue
            outputs.append(None if best is None else best[1])
        return outputs

    def _generate_cuda(self, hidden):
        batch = hidden.shape()[0]
        sequences = np.full((batch * 4, 1), EXPECTED_GENERATION["decoder_start_token_id"], dtype=np.int64)
        beam_scores = np.full((batch, 4), -1e9, dtype=np.float32)
        beam_scores[:, 0] = 0
        finished = [_BeamHypotheses() for _ in range(batch)]
        past, sources = None, np.arange(batch * 4, dtype=np.int64)
        for _ in range(EXPECTED_GENERATION["max_length"] - 1):
            if past is None:
                logits, present = self._run_bound(
                    self.decoder_init, {"input_ids": sequences}, {"encoder_hidden_states": hidden},
                )
            else:
                device_inputs = {"encoder_hidden_states": hidden}
                device_inputs.update(zip(self.past_names, past))
                logits, present = self._run_bound(
                    self.decoder_with_past,
                    {"input_ids": sequences[:, -1:], "beam_indices": sources}, device_inputs,
                )
            sequences, beam_scores, sources = self._advance(logits, sequences, beam_scores, finished)
            if all(item.done for item in finished):
                return self._finalize(finished, sequences, beam_scores, False)
            past = present
        return self._finalize(finished, sequences, beam_scores, True)

    def _generate_cpu(self, hidden):
        batch = hidden.shape[0]
        sequences = np.full((batch * 4, 1), EXPECTED_GENERATION["decoder_start_token_id"], dtype=np.int64)
        beam_scores = np.full((batch, 4), -1e9, dtype=np.float32)
        beam_scores[:, 0] = 0
        finished = [_BeamHypotheses() for _ in range(batch)]
        past, sources = None, np.arange(batch * 4, dtype=np.int64)
        for _ in range(EXPECTED_GENERATION["max_length"] - 1):
            if past is None:
                result = self.decoder_init.run(None, {
                    "input_ids": sequences, "encoder_hidden_states": hidden,
                })
            else:
                inputs = {"input_ids": sequences[:, -1:], "encoder_hidden_states": hidden,
                          "beam_indices": sources}
                inputs.update(zip(self.past_names, past))
                result = self.decoder_with_past.run(None, inputs)
            logits, present = result[0][:, -1, :], result[1:]
            sequences, beam_scores, sources = self._advance(logits, sequences, beam_scores, finished)
            if all(item.done for item in finished):
                return self._finalize(finished, sequences, beam_scores, False)
            past = present
        return self._finalize(finished, sequences, beam_scores, True)

    def _decode(self, tokens):
        if tokens is None:
            return ""
        text = "".join(self.vocab[token] for token in tokens if 4 < token < len(self.vocab))
        return "".join(text.split())

    def _recognize(self, images):
        output = []
        batch_size = 8 if self.using_cuda else 4
        for offset in range(0, len(images), batch_size):
            pixels = self._pixels(images[offset:offset + batch_size])
            if self.using_cuda:
                binding = self.encoder.io_binding()
                binding.bind_cpu_input("pixel_values", pixels)
                binding.bind_output(self.encoder.get_outputs()[0].name, "cuda")
                self.encoder.run_with_iobinding(binding)
                finished = self._generate_cuda(binding.get_outputs()[0])
            else:
                hidden = self.encoder.run(None, {"pixel_values": pixels})[0]
                finished = self._generate_cpu(hidden)
            output.extend(self._decode(item) for item in finished)
        return output

    def recognize(self, images):
        if not images:
            return []
        if len(images) > 64:
            raise ValueError("At most 64 manga text crops are accepted per batch.")
        try:
            return self._recognize(images)
        except ORT_RUNTIME_ERRORS:
            if not self.allow_cpu_fallback or not self.using_cuda:
                raise
            self._open_sessions(["CPUExecutionProvider"])
            return self._recognize(images)
