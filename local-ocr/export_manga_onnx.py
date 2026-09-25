"""Export the pinned local manga-ocr weights to cached ONNX beam-search graphs."""

import argparse
import json
import os
from pathlib import Path

os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"
os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
os.environ["USE_TORCH"] = "1"
os.environ["USE_TF"] = "0"

import onnx
import torch
from transformers import VisionEncoderDecoderModel
from transformers.cache_utils import EncoderDecoderCache

from optimize_manga_encoder import optimize as optimize_manga_encoder


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


class Encoder(torch.nn.Module):
    def __init__(self, encoder):
        super().__init__()
        self.encoder = encoder

    def forward(self, pixel_values):
        return self.encoder(pixel_values=pixel_values, return_dict=True).last_hidden_state


class DecoderInit(torch.nn.Module):
    def __init__(self, decoder):
        super().__init__()
        self.decoder = decoder

    def forward(self, input_ids, encoder_hidden_states):
        output = self.decoder(
            input_ids=input_ids,
            encoder_hidden_states=encoder_hidden_states.repeat_interleave(4, dim=0),
            use_cache=True,
            return_dict=True,
        )
        return (output.logits,) + tuple(
            value for layer in output.past_key_values.to_legacy_cache() for value in layer
        )


class DecoderWithPast(torch.nn.Module):
    def __init__(self, decoder):
        super().__init__()
        self.decoder = decoder

    def forward(self, input_ids, encoder_hidden_states, beam_indices, *flat_past):
        reordered = tuple(value.index_select(0, beam_indices) for value in flat_past)
        legacy = tuple(tuple(reordered[layer * 4 + item] for item in range(4)) for layer in range(2))
        output = self.decoder(
            input_ids=input_ids,
            encoder_hidden_states=encoder_hidden_states.repeat_interleave(4, dim=0),
            past_key_values=EncoderDecoderCache.from_legacy_cache(legacy),
            use_cache=True,
            return_dict=True,
        )
        return (output.logits,) + tuple(
            value for layer in output.past_key_values.to_legacy_cache() for value in layer
        )


def cache_names(prefix):
    return [
        f"{prefix}.{layer}.{kind}"
        for layer in range(2)
        for kind in ("self.key", "self.value", "cross.key", "cross.value")
    ]


def export_graph(module, arguments, output, input_names, output_names, dynamic_axes):
    temporary = output.with_suffix(output.suffix + ".partial")
    torch.onnx.export(
        module,
        arguments,
        temporary,
        input_names=input_names,
        output_names=output_names,
        dynamic_axes=dynamic_axes,
        opset_version=17,
        dynamo=False,
    )
    onnx.checker.check_model(str(temporary))
    return temporary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--force", action="store_true")
    parser.add_argument("--fp16", action="store_true",
                        help="Export separate CUDA FP16 graphs alongside the FP32 CPU graphs.")
    args = parser.parse_args()
    model_path = args.model.resolve()
    output = args.output.resolve()
    if not (model_path / "pytorch_model.bin").is_file():
        raise ValueError("Pinned local manga-ocr PyTorch weights are missing.")
    output.mkdir(parents=True, exist_ok=True)
    suffix = "_fp16" if args.fp16 else ""
    targets = [output / f"{name}{suffix}.onnx"
               for name in ("encoder_model", "decoder_init", "decoder_with_past")]
    generation_path = output / "generation_config.json"
    if not args.force and all(path.is_file() for path in targets) and generation_path.is_file():
        generation = json.loads(generation_path.read_text(encoding="utf-8"))
        if generation == EXPECTED_GENERATION:
            if args.fp16:
                optimize_manga_encoder(targets[0])
            print("Cached manga-ocr ONNX graphs are ready.")
            return

    model = VisionEncoderDecoderModel.from_pretrained(str(model_path), local_files_only=True).eval()
    if args.fp16:
        model.half()
    actual = {name: getattr(model.generation_config, name) for name in EXPECTED_GENERATION}
    if actual != EXPECTED_GENERATION:
        raise ValueError(f"Pinned manga-ocr generation config changed: {actual}")

    batch = 2
    dtype = torch.float16 if args.fp16 else torch.float32
    pixels = torch.randn(batch, 3, 224, 224, dtype=dtype)
    hidden = torch.randn(batch, 197, 768, dtype=dtype)
    ids = torch.full((batch * 4, 1), EXPECTED_GENERATION["decoder_start_token_id"], dtype=torch.long)
    present_names = cache_names("present")
    past_names = cache_names("past")

    temporary = [export_graph(
        Encoder(model.encoder), pixels, targets[0], ["pixel_values"], ["last_hidden_state"],
        {
            "pixel_values": {0: "batch"},
            "last_hidden_state": {0: "batch", 1: "encoder_sequence"},
        },
    )]
    init_dynamic = {
        "input_ids": {0: "beam_batch", 1: "sequence"},
        "encoder_hidden_states": {0: "batch", 1: "encoder_sequence"},
        "logits": {0: "beam_batch", 1: "sequence"},
    }
    for name in present_names:
        init_dynamic[name] = {
            0: "beam_batch",
            2: "encoder_sequence" if ".cross." in name else "sequence",
        }
    temporary.append(export_graph(
        DecoderInit(model.decoder), (ids, hidden), targets[1],
        ["input_ids", "encoder_hidden_states"], ["logits"] + present_names, init_dynamic,
    ))

    initial = model.decoder(
        input_ids=ids[:batch], encoder_hidden_states=hidden, use_cache=True, return_dict=True,
    ).past_key_values.to_legacy_cache()
    flat_past = tuple(value.repeat_interleave(4, dim=0) for layer in initial for value in layer)
    past_dynamic = {
        "input_ids": {0: "beam_batch", 1: "sequence"},
        "encoder_hidden_states": {0: "batch", 1: "encoder_sequence"},
        "beam_indices": {0: "beam_batch"},
        "logits": {0: "beam_batch", 1: "sequence"},
    }
    for name in past_names:
        past_dynamic[name] = {
            0: "beam_batch",
            2: "encoder_sequence" if ".cross." in name else "past_sequence",
        }
    for name in present_names:
        past_dynamic[name] = {
            0: "beam_batch",
            2: "encoder_sequence" if ".cross." in name else "total_sequence",
        }
    temporary.append(export_graph(
        DecoderWithPast(model.decoder),
        (ids, hidden, torch.arange(batch * 4, dtype=torch.long), *flat_past),
        targets[2],
        ["input_ids", "encoder_hidden_states", "beam_indices"] + past_names,
        ["logits"] + present_names,
        past_dynamic,
    ))
    if args.fp16:
        optimize_manga_encoder(temporary[0])
    for partial, target in zip(temporary, targets):
        partial.replace(target)
    generation_path.write_text(json.dumps(EXPECTED_GENERATION, indent=2) + "\n", encoding="utf-8")
    precision = "FP16 CUDA" if args.fp16 else "FP32 CPU"
    print(f"Exported cached manga-ocr {precision} graphs to {output}")


if __name__ == "__main__":
    main()
