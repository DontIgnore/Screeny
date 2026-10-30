"""Weight quantization of the exported encoders (onnxruntime.quantization, the same 1.31.0 as the app).

    work/venv/Scripts/python -I tools/semantic-module/quantize.py --model siglip2-base-patch16-224

Writes next to vision.onnx / text.onnx:
  vision_int8.onnx  dynamic int8 (per-channel MatMul weights, activations quantized at run time)
  text_int8.onnx    dynamic int8, including the 256k x 768 token embedding (Gather)
  text_q4e.onnx     MatMul dynamic int8, token embedding 4-bit blocks (GatherBlockQuantized, block 32, symmetric)
  vision_w8.onnx / text_w8.onnx   weight-only 8-bit blocks (MatMulNBits bits=8, GatherBlockQuantized 8-bit)
  text_w8g4.onnx    weight-only 8-bit MatMuls, 4-bit token embedding
  vision_q4.onnx / text_q4.onnx   every MatMul and the embedding as 4-bit blocks (MatMulNBits / GatherBlockQuantized)
"""
import argparse
import os

import onnx
from onnxruntime.quantization import QuantType, quantize_dynamic
from onnxruntime.quantization.matmul_nbits_quantizer import MatMulNBitsQuantizer

HERE = os.path.dirname(os.path.abspath(__file__))
WORK = os.path.join(HERE, "work")


def nbits(src, dst, ops, block=32, bits=4):
    q = MatMulNBitsQuantizer(src, bits=bits, block_size=block, is_symmetric=True, op_types_to_quantize=ops,
                             quant_axes=(("MatMul", 0), ("Gather", 1)))
    q.process()
    q.model.save_model_to_file(dst, use_external_data_format=False)


def gather_nodes(path):
    m = onnx.load(path, load_external_data=False)
    return [n.name for n in m.graph.node if n.op_type == "Gather"]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", default="siglip2-base-patch16-224")
    args = ap.parse_args()
    d = os.path.join(WORK, "onnx", args.model)
    p = lambda n: os.path.join(d, n)

    quantize_dynamic(p("vision.onnx"), p("vision_int8.onnx"), weight_type=QuantType.QInt8, per_channel=True)
    quantize_dynamic(p("text.onnx"), p("text_int8.onnx"), weight_type=QuantType.QInt8, per_channel=True)

    # Embedding in 4 bits, the rest int8: quantize the Gather first (NBits), then int8 the MatMuls only.
    nbits(p("text.onnx"), p("_text_g4.onnx"), ("Gather",))
    quantize_dynamic(p("_text_g4.onnx"), p("text_q4e.onnx"), weight_type=QuantType.QInt8, per_channel=True,
                     op_types_to_quantize=["MatMul"], extra_options={"DefaultTensorType": onnx.TensorProto.FLOAT})
    os.remove(p("_text_g4.onnx"))

    # Weight-only 8-bit (fp32 activations): avoids the activation-quantization error of dynamic int8 on the ViT.
    nbits(p("vision.onnx"), p("vision_w8.onnx"), ("MatMul",), bits=8)
    nbits(p("text.onnx"), p("text_w8.onnx"), ("MatMul", "Gather"), bits=8)
    nbits(p("text.onnx"), p("_text_g4.onnx"), ("Gather",))
    nbits(p("_text_g4.onnx"), p("text_w8g4.onnx"), ("MatMul",), bits=8)
    os.remove(p("_text_g4.onnx"))

    nbits(p("vision.onnx"), p("vision_q4.onnx"), ("MatMul",))
    nbits(p("text.onnx"), p("text_q4.onnx"), ("MatMul", "Gather"))

    for fn in sorted(os.listdir(d)):
        if fn.endswith(".onnx"):
            m = onnx.load(p(fn), load_external_data=False)
            ops = sorted({n.op_type for n in m.graph.node if n.op_type in (
                "Gather", "GatherBlockQuantized", "MatMulNBits", "MatMulInteger", "DynamicQuantizeLinear", "DequantizeLinear")})
            big = max(m.graph.initializer, key=lambda t: len(t.raw_data))
            print(f"{fn:20} {os.path.getsize(p(fn)):>14,} bytes  largest init {big.name[:40]} dtype={big.data_type} "
                  f"dims={list(big.dims)}  ops={ops}")


if __name__ == "__main__":
    main()
