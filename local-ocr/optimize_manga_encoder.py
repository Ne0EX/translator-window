"""Replace the pinned FP16 MangaOCR patch Conv with its qualified Gemm form."""

import argparse
import hashlib
from pathlib import Path

import numpy as np
import onnx
from onnx import helper, numpy_helper


SOURCE_SHA256 = "415fabd26d2438ed0d855252ecf068710d5a452f3c288fd5e37c7bf257dab49f"
OPTIMIZED_SHA256 = "4ac9456c950f57d4081f309a03e9db3bfe2cb626866f2b4ee39f4684d9082264"


def checksum(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def optimize(path):
    path = Path(path).resolve()
    actual = checksum(path)
    if actual == OPTIMIZED_SHA256:
        return False
    if actual != SOURCE_SHA256:
        raise ValueError("FP16 MangaOCR encoder failed SHA256 verification.")

    model = onnx.load(str(path), load_external_data=True)
    graph = model.graph
    nodes = list(graph.node)
    conv = next(node for node in nodes
                if node.name == "/encoder/embeddings/patch_embeddings/projection/Conv")
    initializers = {item.name: item for item in graph.initializer}
    weight = initializers[conv.input[1]]
    conv_at = nodes.index(conv)
    tail = nodes[conv_at + 1:conv_at + 10]
    if [node.op_type for node in tail] != [
            "Shape", "Constant", "Constant", "Constant", "Slice",
            "Constant", "Concat", "Reshape", "Transpose"]:
        raise ValueError("MangaOCR patch projection tail changed.")
    transpose = tail[-1]

    del weight.dims[:]
    weight.dims.extend([768, 768])
    prefix = "manga_patch_gemm"
    shape1 = f"{prefix}_shape_n_c_gh_kh_gw_kw"
    shape2 = f"{prefix}_shape_rows_features"
    shape3 = f"{prefix}_shape_n_patches_hidden"
    graph.initializer.extend([
        numpy_helper.from_array(np.asarray([-1, 3, 14, 16, 14, 16], dtype=np.int64), shape1),
        numpy_helper.from_array(np.asarray([-1, 768], dtype=np.int64), shape2),
        numpy_helper.from_array(np.asarray([-1, 196, 768], dtype=np.int64), shape3),
    ])
    replacement = [
        helper.make_node("Reshape", [conv.input[0], shape1], [f"{prefix}_blocked"],
                         name=f"{prefix}_reshape_blocked"),
        helper.make_node("Transpose", [f"{prefix}_blocked"], [f"{prefix}_patches"],
                         perm=[0, 2, 4, 1, 3, 5], name=f"{prefix}_transpose_patches"),
        helper.make_node("Reshape", [f"{prefix}_patches", shape2], [f"{prefix}_rows"],
                         name=f"{prefix}_reshape_rows"),
        helper.make_node("Gemm", [f"{prefix}_rows", conv.input[1], conv.input[2]],
                         [f"{prefix}_projected"], transB=1, alpha=1.0, beta=1.0,
                         name=f"{prefix}_projection"),
        helper.make_node("Reshape", [f"{prefix}_projected", shape3], [transpose.output[0]],
                         name=f"{prefix}_reshape_output"),
    ]
    remove = {id(node) for node in [conv, *tail]}
    kept = [node for node in nodes if id(node) not in remove]
    kept[conv_at:conv_at] = replacement
    del graph.node[:]
    graph.node.extend(kept)

    partial = path.with_name(path.name + ".optimize.partial")
    try:
        onnx.checker.check_model(model)
        onnx.save(model, str(partial))
        if checksum(partial) != OPTIMIZED_SHA256:
            raise ValueError("Optimized FP16 MangaOCR encoder failed SHA256 verification.")
        partial.replace(path)
    finally:
        partial.unlink(missing_ok=True)
    return True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("encoder", type=Path)
    args = parser.parse_args()
    optimize(args.encoder)


if __name__ == "__main__":
    main()
