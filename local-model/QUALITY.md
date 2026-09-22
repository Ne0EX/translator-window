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
