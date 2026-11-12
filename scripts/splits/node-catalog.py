#!/usr/bin/env python3
"""一次性纯移动：把 BuiltInNodeCatalog 的 1546 行 RawItems 字面量按连续域拆到 5 个 partial 文件。

关键点（为什么不是简单切文件）：
- 顺序即语义：目录里节点的先后顺序会出现在 UI 列表里，所以按**连续行区间**分组，
  再用 `[.. A, .. B, ...]` 按原顺序拼回去，不按前缀重排。
- 静态字段初始化顺序跨 partial 文件是未指定的。若把每段写成 `static readonly` 字段，
  RawItems 可能先于其它段初始化而读到 null。因此每段写成**表达式体属性**（本质是方法），
  没有初始化顺序问题；ExecIn/Next 仍在主文件里、且声明在 RawItems 之前，顺序确定。
- 段内的 `.. BatchXxxCatalog.Items` 展开项照旧放在它原本的位置（ValueFlow 段），
  集合表达式里 spread 的顺序就是最终顺序。

校验：每个输出文件里被搬运的原文行必须与源文件逐行完全相同；再交给 CatalogExporter
--check 与 NodeCatalogContractTests 证明导出目录逐字节未变。
"""
from __future__ import annotations

# 一次性迁移脚本：行号针对拆分前的源文件（可用 `git show 38d4006:<path>` 取回）。
# 留在仓库里是为了让"纯移动"可被复核；在拆分后的源码上再跑会失败，这是预期的。

import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[2]
SOURCE = ROOT / "backend/src/VisionStudio.Engine/NodeCatalog.cs"
original = SOURCE.read_text(encoding="utf-8").splitlines()
assert len(original) == 1622, f"unexpected source length: {len(original)}"

HEADER = (1, 4)          # namespace + 空行 + 类声明 + {
PORT_FIELDS = (5, 7)     # ExecIn / Next / 空行
LITERAL_DECL = (8, 8)    # private static readonly ... RawItems =
LITERAL_OPEN = 9         # [
LITERAL_CLOSE = 1546     # ];
TAIL = (1547, 1622)      # ]; 之后的全部成员

header = original[HEADER[0] - 1:HEADER[1]]
ports = original[PORT_FIELDS[0] - 1:PORT_FIELDS[1]]
tail = original[TAIL[0] - 1:TAIL[1]]
assert header[2].strip() == "public static class BuiltInNodeCatalog"
assert original[LITERAL_OPEN - 1].strip() == "["
assert original[LITERAL_CLOSE - 1].strip() == "];"

# 连续域：区间覆盖字面量内部全部行（10..1545），顺序不变
groups: list[tuple[str, str, tuple[int, int]]] = [
    ("AcquisitionItems", "NodeCatalog.Acquisition.cs", (10, 191)),
    ("ImageProcessingItems", "NodeCatalog.ImageProcessing.cs", (192, 654)),
    ("CoordinateItems", "NodeCatalog.Coordinate.cs", (655, 858)),
    ("DeviceItems", "NodeCatalog.Device.cs", (859, 1265)),
    ("ValueFlowItems", "NodeCatalog.ValueFlow.cs", (1266, 1545)),
]
covered: list[int] = []
for _, _, (start, end) in groups:
    covered.extend(range(start, end + 1))
assert covered == list(range(LITERAL_OPEN + 1, LITERAL_CLOSE)), "groups must cover the literal exactly once, in order"

folder = "backend/src/VisionStudio.Engine/"
partial_header = [
    "namespace VisionStudio.Engine;",
    "",
    "public static partial class BuiltInNodeCatalog",
    "{",
]

verifications: list[tuple[pathlib.Path, int, tuple[int, int]]] = []
for property_name, filename, (start, end) in groups:
    lines = list(partial_header)
    lines.append(f"    private static NodeCatalogItem[] {property_name} =>")
    lines.append("    [")
    offset = len(lines)
    lines.extend(original[start - 1:end])
    lines.append("    ];")
    lines.append("}")
    lines.append("")
    target = ROOT / (folder + filename)
    target.write_text("\n".join(lines), encoding="utf-8")
    verifications.append((target, offset, (start, end)))
    print(f"wrote {filename} ({len(lines)} lines)")

# 主文件：类声明改 partial，RawItems 改成按原顺序拼接
main = list(header)
main[2] = "public static partial class BuiltInNodeCatalog"
main.extend(ports)
main.append("    private static readonly IReadOnlyList<NodeCatalogItem> RawItems =")
main.append("    [")
for property_name, _, _ in groups:
    main.append(f"        .. {property_name},")
main.append("    ];")
main.extend(tail)
main.append("")
(SOURCE).write_text("\n".join(main), encoding="utf-8")
print(f"wrote NodeCatalog.cs ({len(main)} lines)")

# 逐行精确校验
for target, offset, (start, end) in verifications:
    written = target.read_text(encoding="utf-8").splitlines()
    expected = original[start - 1:end]
    actual = written[offset:offset + len(expected)]
    if actual != expected:
        for i, (want, got) in enumerate(zip(expected, actual)):
            if want != got:
                raise SystemExit(f"MISMATCH {target.name} line {start + i}:\n  want {want!r}\n  got  {got!r}")
        raise SystemExit(f"MISMATCH {target.name}: length differs")
    print(f"verified {target.name}: {len(expected)} source lines identical")

main_written = SOURCE.read_text(encoding="utf-8").splitlines()
# splitlines() 不会为末尾那个空字符串多出一行，所以尾部偏移量就是"总行数 - 尾部行数"。
tail_offset = len(main_written) - len(tail)
assert main_written[tail_offset:tail_offset + len(tail)] == tail, "main tail changed"
print("NodeCatalog split verified (tail preserved verbatim)")
