#!/usr/bin/env python3
"""生成金样回归夹具（固定输入图）。

为什么不用测试里现场画图：`new Mat(...)` 合成图会跟着被测代码一起变——改了预处理、
换了插值方式，图也跟着变，"基准"就失去意义。夹具必须是**提交进仓库、永不随代码变化**
的二进制资产，才能捕捉 OpenCV/JLVision 升级与默认参数漂移。

三张图覆盖三类算法路径：
- scratch-640x480.png：拉丝背景 + 三条划痕 → 阈值 + 斑点（缺陷检出）；
- edge-640x480.png：亚像素级灰度台阶 → 卡尺线（边缘定位）；
- ring-640x480.png：亚像素级圆环 → 卡尺圆（圆拟合）。

重新生成会改变像素值，必须同时更新测试里的期望值——这是刻意的：夹具不是"随便画一张"，
而是回归基线。用法：python scripts/generate-golden-fixtures.py
"""
from __future__ import annotations

import math
import pathlib
import struct
import zlib

W, H = 640, 480
OUT = pathlib.Path(__file__).resolve().parent.parent / "backend" / "tests" / "VisionStudio.Engine.Tests" / "Fixtures"


class Lcg:
    """固定种子的线性同余噪声：不依赖 Python random 的实现细节。"""

    def __init__(self, seed: int) -> None:
        self.state = seed & 0xFFFFFFFF

    def next(self) -> float:
        self.state = (1103515245 * self.state + 12345) & 0x7FFFFFFF
        return self.state / 0x7FFFFFFF


def write_png(path: pathlib.Path, rows: list[list[int]]) -> None:
    raw = b"".join(b"\x00" + bytes(row) for row in rows)

    def chunk(tag: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", W, H, 8, 0, 0, 0, 0)
    path.write_bytes(
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", header)
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b"")
    )


def clamp(value: float) -> int:
    return max(0, min(255, int(round(value))))


def segment_distance(x: float, y: float, x1: float, y1: float, x2: float, y2: float) -> float:
    dx, dy = x2 - x1, y2 - y1
    length2 = dx * dx + dy * dy
    t = 0.0 if length2 == 0 else max(0.0, min(1.0, ((x - x1) * dx + (y - y1) * dy) / length2))
    px, py = x1 + t * dx, y1 + t * dy
    return math.hypot(x - px, y - py)


def scratch_scene() -> list[list[int]]:
    noise = Lcg(20261112)
    scratches = [
        (120, 140, 360, 260, 1.2),
        (180, 330, 300, 300, 0.9),
        (420, 120, 470, 360, 1.4),
    ]
    rows: list[list[int]] = []
    for y in range(H):
        row: list[int] = []
        for x in range(W):
            # 拉丝纹理：低频方向性起伏，模拟磨削表面
            base = 96 + 14 * math.sin(x / 9.0) + 8 * math.sin(y / 7.0) + 5 * math.sin((x + y) / 23.0)
            value = base + (noise.next() - 0.5) * 6
            for x1, y1, x2, y2, width in scratches:
                distance = segment_distance(x, y, x1, y1, x2, y2)
                value -= 82 * math.exp(-((distance / width) ** 2))
            row.append(clamp(value))
        rows.append(row)
    return rows


def edge_scene() -> list[list[int]]:
    noise = Lcg(7122026)
    rows: list[list[int]] = []
    for y in range(H):
        row: list[int] = []
        for x in range(W):
            # 0.7 px 过渡宽度：抛物线插值能稳定落在 0.3 px 以内
            value = 40 + 180 / (1 + math.exp(-(x - 312.4) / 0.7))
            row.append(clamp(value + (noise.next() - 0.5) * 4))
        rows.append(row)
    return rows


def ring_scene() -> list[list[int]]:
    noise = Lcg(11223344)
    radius = 120.5
    rows: list[list[int]] = []
    for y in range(H):
        row: list[int] = []
        for x in range(W):
            distance = math.hypot(x - 319.6, y - 240.3)
            value = 40 + 185 * math.exp(-(((distance - radius) / 1.15) ** 2))
            row.append(clamp(value + (noise.next() - 0.5) * 4))
        rows.append(row)
    return rows


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    for name, rows in (
        ("scratch-640x480.png", scratch_scene()),
        ("edge-640x480.png", edge_scene()),
        ("ring-640x480.png", ring_scene()),
    ):
        path = OUT / name
        write_png(path, rows)
        print(f"{path}  {path.stat().st_size} bytes")


if __name__ == "__main__":
    main()
