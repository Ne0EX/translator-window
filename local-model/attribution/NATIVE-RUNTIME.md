# Native translation runtime provenance

Runtime: [llama.cpp](https://github.com/ggml-org/llama.cpp), MIT license.

- Release: `b11146`
- Commit: `7fe450e19305b828c199d602c23a8337aaa1f03b`
- Reported version: `0.5.0-dev (build 11146, commit 7fe450e19)`
- Platform: Windows x64, CUDA 12.4

Setup downloads the following official release assets and accepts them only after
their byte size and SHA256 match:

| Asset | Bytes | SHA256 |
| --- | ---: | --- |
| [`llama-b11146-bin-win-cuda-12.4-x64.zip`](https://github.com/ggml-org/llama.cpp/releases/download/b11146/llama-b11146-bin-win-cuda-12.4-x64.zip) | 253,869,799 | `3c806a6ceccc3dae1c743ceb1a1fb2cce5b76f40bfbd4c6b7b8afb6ef45a5807` |
| [`cudart-llama-bin-win-cuda-12.4-x64.zip`](https://github.com/ggml-org/llama.cpp/releases/download/b11146/cudart-llama-bin-win-cuda-12.4-x64.zip) | 391,443,627 | `8c79a9b226de4b3cacfd1f83d24f962d0773be79f1e7b75c6af4ded7e32ae1d6` |

The release archive's `LICENSE-LLVM-OpenMP` is retained with the installed
runtime. `LICENSE-LLAMA.CPP-MIT.txt` is the exact llama.cpp license at the pinned
commit. The separately packaged NVIDIA CUDA runtime libraries remain subject to
the [NVIDIA CUDA Toolkit EULA](https://docs.nvidia.com/cuda/eula/index.html).

The files are installed only inside `.tools/llama-server`; setup does not modify
the system `PATH` or install a service. The application starts `llama-server.exe`
as a child process bound to local loopback and stops that process with the local
translation session.
