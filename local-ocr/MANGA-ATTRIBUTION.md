# Japanese manga recognition attribution

Model: kha-white/manga-ocr-base, Apache-2.0.
Pinned model revision: aa6573bd10b0d446cbf622e29c3e084914df9741.
Official card: https://huggingface.co/kha-white/manga-ocr-base/tree/aa6573bd10b0d446cbf622e29c3e084914df9741

The local adapter follows the published grayscale/RGB preprocessing and model
configuration from https://github.com/kha-white/manga-ocr . It adds batched crop
processing, local-files-only loading, bounded input, and automatic CPU fallback.
It decodes character IDs directly, so no Japanese segmentation dictionary is needed.

LICENSE.manga-ocr is an unchanged copy of the upstream Apache-2.0 license at:
https://raw.githubusercontent.com/kha-white/manga-ocr/c333b5d36e88d539d6b040b4c4cf90ad5ecd4f69/LICENSE

Provisioning retains this license, this attribution, and the pinned official model
card beside the weights. Runtime recognition performs no downloads or network calls.
