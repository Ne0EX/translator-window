# Local Japanese-to-Thai comparison

Measured on the same eight real manga text regions on a Ryzen 7 5800H,
RTX 3060 laptop (6 GiB VRAM), and approximately 14 GiB system RAM. This is a
small manual comparison, not a standardized quality score. All inference was
local; downloaded candidates were pinned and separate from the working model.

| Model | Eight-region translation time | Observed result |
| --- | --- | --- |
| M2M100 418M int8 | 0.16–0.32 s | Lost hospital-duty meaning; mistranslated names and graduation. Beam search and English pivot did not fix it. |
| MADLAD400 3B CT2 int8 | 0.50–0.63 s warm | Improved some wording, but omitted duty and invented university names. English pivot also hallucinated content. |
| Existing Qwen3 4B Instruct Q4 | 2.34 s warm | Lost duty meaning and left Japanese inside a Thai name. |
| Hy-MT2 1.8B Q8 | About 1.7 s | Preserved more of the part-time, evening, hospital, and graduation meanings. Chosen for the working app. |
| Hy-MT2 7B Q4, 20 GPU layers | 34 s | Better duty and university wording, but still added an ambiguous subject and mixed scripts in names. Too slow on this laptop. |

The selected prompt explicitly names the source and target languages and requests
natural manga translation. It contains no fixture-specific terms or translations.
Adding every page segment as context caused one response to translate the entire
context instead of its requested segment, so that approach was rejected. Generating
all eight segments as one JSON array took 10–12 seconds and was also rejected.

The selected model can still misread proper names, interpret omitted subjects
incorrectly, or lose details in short fragments. For example, the sample's
hospital-duty phrase became part-time evening work; its exact on-call nuance was
not fully preserved. A sentence with no explicit Japanese subject gained a first-
person pronoun. These are translation limitations, separate from OCR accuracy or
the native pixel checks proving the original page stays covered during scrolling.

Model sources: [Hy-MT2 official card](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF),
[MADLAD official card](https://huggingface.co/google/madlad400-3b-mt),
[Qwen official card](https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507).
Detailed local outputs remain in `artifacts/manga-test/*quality-probe.json` and
`*probe.json`. The rejected large trial weights are not part of the application.

## Q4 speed trial, 2026-09-25

The official Hy-MT2 1.8B Q4_K_M candidate used the same pinned revision, prompts,
CUDA settings, and one-region requests as Q8_0. With translation caches cleared,
the eight-region fixture took 1.22 seconds warm versus 1.65 seconds for Q8; the
29-region reader spread took 5.81 seconds versus 7.97 seconds. Model loading took
2.48 versus 4.32 seconds. These are translation-only measurements.

Q4 was rejected despite the speed gain. It lost the hospital-duty meaning,
invented a nursing role, and changed "it is not our fault that we were born from
the same mother" into "not all of us were born from the same mother." Q8 retains
the limitations above, but these additional meaning errors make Q4 a regression.
The candidate and both output sets remain in `.cache/verification/performance/`;
the application continues using Q8_0.

## Ultra-low-bit deployment review, 2026-09-25

The official 1.25-bit GGUF is not a candidate for this Windows RTX system. Its
required [STQ1_0 llama.cpp change](https://github.com/ggml-org/llama.cpp/pull/22836)
is still an open CPU change with an ARM NEON kernel; its documented inference and
benchmarks use `-ngl 0`. It provides no CUDA or Ryzen x86 kernel for the current
runtime. The 2-bit SEQ format is separate from STQ1_0. The official
[AngelSlim deployment instructions](https://huggingface.co/AngelSlim/HY-1.8B-2Bit-GGUF/blob/refs%2Fpr%2F2/README_convert_gguf.md)
require an experimental llama.cpp branch and an SME2-capable ARM device such as an
Apple M4 or vivo x300, so it also has no applicable Windows CUDA path.

The [Hy-MT2 report](https://arxiv.org/pdf/2605.22064) measures the advertised
1.25-bit speedup against a 4-bit Hy-MT1.5 model on Apple A15, not Q8 on NVIDIA.
Its quantization table also reports a larger quality decline for Hy-MT2 2-bit than
for Q4_K_M; no comparable 1.25-bit quality row is reported. Since Q4_K_M already
failed the local meaning checks, neither unsupported ultra-low-bit format warrants
a model trial on this machine.

## Q6 memory trial, 2026-09-25

Measured RAM pressure justified testing the previously deferred official Q6_K
candidate. The 1,474,785,120-byte file at the same pinned revision was verified
against SHA256
`d98fe604dec1f28f58f80d7d560f7177e584d3b8e5835862687660e5ff97cb40`.
Its token embedding is also Q6_K, reducing that host buffer from 250.72 to
193.57 MiB; the CUDA model buffer fell from 1,815.26 to 1,401.61 MiB.

A Q8/Q6 comparison used the current native runtime, four-text waves, identical
prompts, and real OCR resident. After inference, Q6 saved about 49 MiB of native
working set and 394 MiB of whole-device GPU use. Its roughly 466 MiB lower private
commit is not a physical RAM saving. Dense-page translation took 4.75/4.92 seconds
and the warm repeat 5.29/4.93 seconds. Different starting memory pressure prevents
a reliable cold-start speed claim from this pair.

Q6 failed the meaning gate before reverse-order timing runs:

- Hospital duty became generic patient treatment, losing the duty/shift meaning.
- The shared-mother sentence lost its premise or corrupted the relationship
  between birth and fault in both dense-page passes.
- Eldest brother became younger royal brother in both passes.

Names, transliteration, and prose style were not rejection criteria. Q6 corrected
one Q8 father-to-student error to father-to-child, while another maternity error
was shared by both models; those were not counted as Q6 regressions. The protected
meaning losses still rule out promotion. Production remains Q8_0. Verified weights,
full outputs, memory samples, and `semantic-review.json` remain in
`.cache/verification/native-q6/`.

## Q8 KV-cache trial, 2026-09-25

The selected Q8_0 model weights and four 2,048-token slots were unchanged. This
trial quantized only the generated key and value activation caches from F16 to
Q8_0. The pinned CUDA runtime kept Flash Attention enabled and reported the KV
allocation falling from 512 to 272 MiB. Two real-motion runs per mode measured a
repeatable 224–226 MiB reduction in the native process's dedicated GPU memory.

The isolated eight-region and 29-region comparison preserved the hospital-duty,
shared-mother and fault, and eldest-brother meanings. Differences in names and
wording were accepted. Two apparent errors in one Q8-cache pass were already
present in earlier F16 runs, while father-to-child and crown-prince wording
improved in some Q8-cache outputs; the trial therefore found no new material
meaning regression.

Latency was mixed. The isolated dense pass was neutral, while the short and warm
passes varied by several hundred milliseconds. In the less-confounded real-motion
pair, Q8 cache reduced hide and settle delays but added about 0.47 seconds to the
first cold caption. Large source stalls still occurred under severe host-memory
pressure, so this is a verified GPU-memory saving rather than proof of smooth
animation. Full evidence remains in
`.cache/verification/native-q8-kv/results-f16-q8kv.json` and
`.cache/verification/real-model-motion/kv-motion-summary.json`.
