"""Focused offline checks for cached ONNX manga recognition."""

import argparse
import json
import os
from pathlib import Path
import socket
import sys
import tempfile

import numpy as np
import onnxruntime as ort
from PIL import Image

import onnx_recognizer
from manga_recognizer import MangaRecognizer
from onnx_recognizer import (ASSETS, EXPECTED_GENERATION, FP16_ASSETS, OnnxMangaRecognizer,
                             _BeamHypotheses)


def deny_network(*_args, **_kwargs):
    raise AssertionError("Network attempted during local OCR")


def check_beam_rules():
    logits = np.asarray([[1000.0, 999.0, 998.0]], dtype=np.float16)
    log_probabilities = OnnxMangaRecognizer._log_softmax(logits)
    shifted = logits.astype(np.float32) - logits.astype(np.float32).max(axis=-1, keepdims=True)
    expected = shifted - np.log(np.exp(shifted).sum(axis=-1, keepdims=True))
    assert log_probabilities.dtype == np.float32
    assert np.allclose(log_probabilities, expected, rtol=0, atol=1e-6)

    scores = np.zeros((1, 20), dtype=np.float32)
    OnnxMangaRecognizer._ban_repeated_trigrams(scores, np.asarray([[2, 10, 11, 12, 10, 11]]))
    assert np.isneginf(scores[0, 12])
    assert np.count_nonzero(np.isneginf(scores)) == 1

    hypotheses = _BeamHypotheses()
    for score in (-10.0, -9.0, -8.0, -7.0, -100.0):
        hypotheses.add(np.asarray([2, 5, 6]), score)
    assert hypotheses.done and len(hypotheses.items) == 4

    finished = _BeamHypotheses()
    finished.add(np.asarray([2, 5]), -8.0)
    sequences = np.asarray([[2, 6, 7], [2, 6, 8], [2, 6, 9], [2, 6, 10]])
    # The unfinished beam wins HF finalization and therefore must become the same empty result
    # as the Torch wrapper's missing-EOS guard.
    assert OnnxMangaRecognizer._finalize(
        [finished], sequences, np.asarray([[-1.0, -9.0, -10.0, -11.0]]), True,
    ) == [None]
    assert np.array_equal(OnnxMangaRecognizer._finalize(
        [finished], sequences, np.asarray([[-100.0, -101.0, -102.0, -103.0]]), True,
    )[0], np.asarray([2, 5]))

    completed = _BeamHypotheses()
    for score in (-8.0, -9.0, -10.0, -11.0):
        completed.add(np.asarray([2, 5]), score)
    mixed = OnnxMangaRecognizer._finalize(
        [completed, _BeamHypotheses()], np.vstack((sequences, sequences)),
        np.asarray([[0.0, 0.0, 0.0, 0.0], [-1.0, -9.0, -10.0, -11.0]]), True,
    )
    assert np.array_equal(mixed[0], np.asarray([2, 5])) and mixed[1] is None


def check_limits_and_fallback():
    recognizer = OnnxMangaRecognizer.__new__(OnnxMangaRecognizer)
    try:
        recognizer.recognize([object()] * 65)
    except ValueError as error:
        assert "64" in str(error)
    else:
        raise AssertionError("Oversized recognition batch was accepted")

    recognizer.allow_cpu_fallback = True
    recognizer.using_cuda = True
    attempts = []

    def recognize(_images):
        attempts.append(recognizer.using_cuda)
        if recognizer.using_cuda:
            raise RuntimeError("simulated CUDA failure")
        return ["ok"]

    def open_sessions(providers):
        assert providers == ["CPUExecutionProvider"]
        recognizer.using_cuda = False

    recognizer._recognize = recognize
    recognizer._open_sessions = open_sessions
    assert recognizer.recognize([object()]) == ["ok"]
    assert attempts == [True, False]

    recognizer.using_cuda = True
    def recognize_ort_failure(_images):
        if recognizer.using_cuda:
            raise ort.capi.onnxruntime_pybind11_state.EPFail("simulated provider failure")
        return ["ok"]
    recognizer._recognize = recognize_ort_failure
    assert recognizer.recognize([object()]) == ["ok"]

    recognizer.using_cuda = True
    recognizer._recognize = lambda _images: (_ for _ in ()).throw(
        ort.capi.onnxruntime_pybind11_state.InvalidArgument("invalid input")
    )
    try:
        recognizer.recognize([object()])
    except ort.capi.onnxruntime_pybind11_state.InvalidArgument:
        pass
    else:
        raise AssertionError("Invalid ONNX input was hidden by CPU fallback")


def check_config_validation():
    with tempfile.TemporaryDirectory() as temporary:
        model = Path(temporary)
        for name in ASSETS:
            (model / name).write_bytes(b"")
        generation = dict(EXPECTED_GENERATION)
        generation["num_beams"] = 1
        (model / "generation_config.json").write_text(json.dumps(generation), encoding="utf-8")
        try:
            OnnxMangaRecognizer(model, "cpu")
        except ValueError as error:
            assert "num_beams" in str(error)
        else:
            raise AssertionError("Unsupported generation config was accepted")


def check_precision_selection():
    with tempfile.TemporaryDirectory() as temporary:
        recognizer = OnnxMangaRecognizer.__new__(OnnxMangaRecognizer)
        recognizer.model_path = Path(temporary)
        for name in FP16_ASSETS:
            (recognizer.model_path / name).write_bytes(b"")
        recognizer._select_graphs(["CUDAExecutionProvider", "CPUExecutionProvider"])
        assert recognizer.graph_names == FP16_ASSETS and recognizer.input_dtype == np.float16
        recognizer._select_graphs(["CPUExecutionProvider"])
        assert recognizer.graph_names == ASSETS[:3] and recognizer.input_dtype == np.float32
        recognizer._select_graphs(["CUDAExecutionProvider", "CPUExecutionProvider"])
        recognizer.allow_cpu_fallback = True
        recognizer.using_cuda = True
        def fail_then_check(_images):
            if recognizer.using_cuda:
                raise ort.capi.onnxruntime_pybind11_state.EPFail("simulated FP16 CUDA failure")
            assert recognizer.graph_names == ASSETS[:3] and recognizer.input_dtype == np.float32
            return ["ok"]
        def reopen(providers):
            recognizer._select_graphs(providers)
            recognizer.using_cuda = False
        recognizer._recognize = fail_then_check
        recognizer._open_sessions = reopen
        assert recognizer.recognize([object()]) == ["ok"]


def check_encoder_provider_options():
    recognizer = OnnxMangaRecognizer.__new__(OnnxMangaRecognizer)
    recognizer.model_path = Path("unused")
    calls = []
    original = ort.InferenceSession
    ort.InferenceSession = lambda path, options, **arguments: calls.append((path, arguments))
    try:
        cuda = ["CUDAExecutionProvider", "CPUExecutionProvider"]
        recognizer._create_session("encoder.onnx", None, cuda, encoder=True)
        recognizer._create_session("decoder.onnx", None, cuda)
        recognizer._create_session("encoder.onnx", None, ["CPUExecutionProvider"], encoder=True)
    finally:
        ort.InferenceSession = original
    assert calls[0][1]["provider_options"] == [{"cudnn_conv_algo_search": "HEURISTIC"}, {}]
    assert "provider_options" not in calls[1][1] and "provider_options" not in calls[2][1]


def check_cuda_dll_loading():
    recognizer = OnnxMangaRecognizer.__new__(OnnxMangaRecognizer)
    recognizer.dll_directories = []
    calls = []
    original_path = os.environ.get("PATH")
    original_add = onnx_recognizer.os.add_dll_directory
    original_preload = ort.preload_dlls
    original_windll = onnx_recognizer.ctypes.WinDLL
    try:
        onnx_recognizer.os.add_dll_directory = lambda path: calls.append(("add", path)) or object()
        ort.preload_dlls = lambda **kwargs: calls.append(("preload", kwargs))
        onnx_recognizer.ctypes.WinDLL = lambda path: calls.append(("cudnn", path)) or object()
        dlls = Path("trusted-torch-lib").resolve()
        recognizer._preload_cuda_dlls(dlls)
        assert [item[0] for item in calls] == ["add", "preload", "cudnn"], calls
        assert calls[1][1] == {"cuda": True, "cudnn": False, "directory": str(dlls)}
        assert calls[2][1] == str(dlls / "cudnn64_9.dll")
        assert recognizer.cudnn_dll is not None and recognizer.dll_directories
        assert os.environ["PATH"].split(os.pathsep)[0] == str(dlls)
    finally:
        onnx_recognizer.os.add_dll_directory = original_add
        ort.preload_dlls = original_preload
        onnx_recognizer.ctypes.WinDLL = original_windll
        if original_path is None:
            os.environ.pop("PATH", None)
        else:
            os.environ["PATH"] = original_path

    original_available = ort.get_available_providers
    original_method = OnnxMangaRecognizer._preload_cuda_dlls
    try:
        ort.get_available_providers = lambda: ["CUDAExecutionProvider", "CPUExecutionProvider"]
        OnnxMangaRecognizer._preload_cuda_dlls = lambda self, path: (_ for _ in ()).throw(
            OSError("simulated CUDA DLL failure"))
        assert recognizer._providers("auto") == ["CPUExecutionProvider"]
        try:
            recognizer._providers("cuda")
        except OSError:
            pass
        else:
            raise AssertionError("Explicit CUDA hid a DLL preload failure")
    finally:
        ort.get_available_providers = original_available
        OnnxMangaRecognizer._preload_cuda_dlls = original_method


def check_optimized_encoder(model_path):
    from optimize_manga_encoder import OPTIMIZED_SHA256, SOURCE_SHA256, checksum, optimize

    encoder = model_path / FP16_ASSETS[0]
    if not encoder.is_file():
        return
    before = checksum(encoder)
    assert before in (SOURCE_SHA256, OPTIMIZED_SHA256), before
    if before == OPTIMIZED_SHA256:
        assert optimize(encoder) is False
        assert checksum(encoder) == before


def load_crops(image_path, regions_path):
    image = Image.open(image_path).convert("RGB")
    regions = json.loads(regions_path.read_text(encoding="utf-8"))
    crops, expected = [], []
    for region in regions:
        x, y, width, height = region["box"]
        crops.append(image.crop((max(0, x - 8), max(0, y - 8),
                                 min(image.width, x + width + 8), min(image.height, y + height + 8))))
        expected.append(region["text"])
    return crops, expected


def check_real_model(model_path, image_path, regions_path):
    socket.socket.connect = deny_network
    socket.create_connection = deny_network
    recognizer = MangaRecognizer(model_path, "auto")
    assert type(recognizer) is OnnxMangaRecognizer
    assert "torch" not in sys.modules and "transformers" not in sys.modules
    crops, expected = load_crops(image_path, regions_path)
    actual = recognizer.recognize(crops)
    assert actual == expected, [(index, want, got) for index, (want, got) in
                                enumerate(zip(expected, actual)) if want != got]
    assert recognizer.recognize(crops) == expected
    assert "torch" not in sys.modules and "transformers" not in sys.modules
    print(f"PASS: {len(crops)} exact cached-ONNX transcripts twice; sockets and Torch imports blocked.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", type=Path)
    parser.add_argument("--image", type=Path)
    parser.add_argument("--regions", type=Path)
    args = parser.parse_args()
    check_beam_rules()
    check_limits_and_fallback()
    check_config_validation()
    check_precision_selection()
    check_encoder_provider_options()
    check_cuda_dll_loading()
    print("PASS: beam rules, config and precision selection, 64-crop limit, and CUDA fallback.")
    provided = (args.model, args.image, args.regions)
    if any(provided):
        if not all(provided):
            parser.error("--model, --image, and --regions must be supplied together")
        check_optimized_encoder(args.model)
        check_real_model(*provided)


if __name__ == "__main__":
    main()
