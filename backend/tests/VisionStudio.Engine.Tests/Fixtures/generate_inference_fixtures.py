"""Regenerate the tiny synthetic ONNX fixtures with Python and onnx.

Each graph accepts one NCHW image and returns known head tensors. These fixtures
exercise ONNX Runtime and node postprocessing without trained weights or downloads.
"""

from math import pi
from pathlib import Path

import onnx
from onnx import TensorProto, helper


def write_model(name, outputs):
    nodes = []
    output_infos = []
    for output_name, shape, values in outputs:
        tensor = helper.make_tensor(output_name, TensorProto.FLOAT, shape, values)
        nodes.append(helper.make_node("Constant", [], [output_name], value=tensor))
        output_infos.append(helper.make_tensor_value_info(output_name, TensorProto.FLOAT, shape))

    image = helper.make_tensor_value_info("image", TensorProto.FLOAT, [1, 3, 32, 32])
    graph = helper.make_graph(nodes, name, [image], output_infos)
    model = helper.make_model(graph, ir_version=8, opset_imports=[helper.make_opsetid("", 13)])
    onnx.checker.check_model(model)
    onnx.save(model, Path(__file__).with_name(f"{name}.onnx"))


def flatten(rows):
    return [cell for row in rows for cell in row]


def main():
    obb = [
        [16, 16, 10, 6, 0.94, 0.04, pi / 6],
        [16.25, 16, 10, 6, 0.80, 0.06, pi / 6],
        [16, 16, 10, 6, 0.04, 0.85, -pi / 6],
    ]
    write_model("inference-obb", [("boxes", [1, 3, 7], flatten(obb))])

    # COCO shoulders (5 and 6) form the only visible skeleton segment.
    keypoints = [[0, 0, 0] for _ in range(17)]
    keypoints[5] = [10, 14, 0.9]
    keypoints[6] = [22, 14, 0.8]
    keypoints[7] = [10, 20, 0.2]
    pose = [
        [16, 16, 24, 14, 0.95] + flatten(keypoints),
        [16, 16, 24, 14, 0.80] + [0] * 51,
    ]
    write_model("inference-pose", [("poses", [1, 2, 56], flatten(pose))])

    # The class-0 duplicate would also paint the left half if NMS missed it.
    instances = [
        [16, 16, 24, 8, 0.95, 0.01, 1],
        [16, 16, 24, 8, 0.90, 0.01, -1],
        [16, 16, 24, 8, 0.01, 0.85, -1],
    ]
    prototypes = [-8] * 4 + [8] * 4
    write_model("inference-instance-seg", [
        ("prototypes", [1, 1, 8, 8], prototypes * 8),
        ("instances", [1, 3, 7], flatten(instances)),
    ])


if __name__ == "__main__":
    main()
