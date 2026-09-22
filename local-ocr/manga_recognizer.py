"""Offline Japanese manga crop recognition using the Apache-2.0 manga-ocr model."""
import os
from pathlib import Path

os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"
os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
os.environ["USE_TORCH"] = "1"
os.environ["USE_TF"] = "0"


class MangaRecognizer:
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
        # Four crops keep decoder attention memory bounded alongside detection and translation on 6 GB GPUs.
        for offset in range(0, len(images), 4):
            crops = [image.convert("L").convert("RGB") for image in images[offset:offset + 4]]
            try:
                pixels = self.processor(images=crops, return_tensors="pt").pixel_values.to(self.device)
                if self.device == "cuda":
                    pixels = pixels.half()
                with self.torch.inference_mode():
                    ids = self.model.generate(pixels, max_length=300)
            except RuntimeError:
                if not self.allow_cpu_fallback or self.device != "cuda":
                    raise
                self.device = "cpu"
                self.model.cpu().float()
                self.torch.cuda.empty_cache()
                return self.recognize(images)
            if any(self.model.config.eos_token_id not in row.tolist() for row in ids):
                raise ValueError("Japanese text crop exceeds the recognizer's 300-token limit; use a smaller crop.")
            output.extend("".join(text.split()) for text in self.tokenizer.batch_decode(ids, skip_special_tokens=True))
        return output
