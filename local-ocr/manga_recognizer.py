"""Offline Japanese manga crop recognition using the Apache-2.0 manga-ocr model."""
import os
from pathlib import Path

os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"
os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
os.environ["USE_TORCH"] = "1"
os.environ["USE_TF"] = "0"


class TorchMangaRecognizer:
    def __init__(self, model_path, device="auto"):
        model_path = Path(model_path).resolve()
        if not (model_path / "pytorch_model.bin").is_file():
            raise ValueError("Local Japanese manga recognizer is missing. Run local-ocr/setup.ps1 first.")
        import torch
        from transformers import AutoTokenizer, ViTImageProcessor, VisionEncoderDecoderModel

        self.torch = torch
        self.allow_cpu_fallback = device == "auto"
        self.device = "cuda" if device == "auto" and torch.cuda.is_available() else "cpu" if device == "auto" else device
        torch.set_num_threads(4)
        self.processor = ViTImageProcessor.from_pretrained(str(model_path), local_files_only=True)
        # OCR only decodes character IDs; a Japanese input segmentation dictionary is unnecessary.
        self.tokenizer = AutoTokenizer.from_pretrained(
            str(model_path), local_files_only=True, do_word_tokenize=False,
        )
        self.model = VisionEncoderDecoderModel.from_pretrained(str(model_path), local_files_only=True).eval()
        try:
            self.model.to(self.device)
            if self.device == "cuda":
                self.model.half()
        except RuntimeError:
            if not self.allow_cpu_fallback or self.device != "cuda":
                raise
            self.device = "cpu"
            self.model.cpu().float()

    def recognize(self, images):
        if not images:
            return []
        if len(images) > 64:
            raise ValueError("At most 64 manga text crops are accepted per batch.")
        output = []
        # Eight crops are faster on the tested 6 GB GPU; retry four if memory is tight.
        batch_size = 8 if self.device == "cuda" else 4
        offset = 0
        while offset < len(images):
            crops = [image.convert("L").convert("RGB") for image in images[offset:offset + batch_size]]
            pixels = None
            try:
                pixels = self.processor(images=crops, return_tensors="pt").pixel_values.to(self.device)
                if self.device == "cuda":
                    pixels = pixels.half()
                with self.torch.inference_mode():
                    ids = self.model.generate(pixels, max_length=300)
            except RuntimeError:
                if self.device == "cuda" and batch_size > 4:
                    pixels = None
                    self.torch.cuda.empty_cache()
                    batch_size = 4
                    continue
                if not self.allow_cpu_fallback or self.device != "cuda":
                    raise
                self.device = "cpu"
                self.model.cpu().float()
                self.torch.cuda.empty_cache()
                return self.recognize(images)
            decoded = self.tokenizer.batch_decode(ids, skip_special_tokens=True)
            output.extend("".join(text.split()) if self.model.config.eos_token_id in row.tolist() else ""
                          for row, text in zip(ids, decoded))
            offset += len(crops)
        return output


class MangaRecognizer:
    """Choose the Torch-free cached ONNX backend when its exported assets exist."""

    def __new__(cls, model_path, device="auto"):
        from onnx_recognizer import OnnxMangaRecognizer

        if OnnxMangaRecognizer.available(model_path):
            return OnnxMangaRecognizer(model_path, device)
        return TorchMangaRecognizer(model_path, device)
