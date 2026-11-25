#!/usr/bin/env python3
"""一次性纯移动：把 ProductionRuntime.cs 按顶层类型 + 服务类关注面拆开。

校验方式不是"看编译过没过"，而是逐行精确比对：每个输出文件里被搬运的那一段，
必须与源文件对应行区间逐行完全相同；同时检查没有多余的尾部内容。
"""
from __future__ import annotations

# 一次性迁移脚本：行号针对拆分前的源文件（可用 `git show 38d4006:<path>` 取回）。
# 留在仓库里是为了让"纯移动"可被复核；在拆分后的源码上再跑会失败，这是预期的。

import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[2]
SOURCE = ROOT / "backend/src/VisionStudio.Api/ProductionRuntime.cs"
original = SOURCE.read_text(encoding="utf-8").splitlines()

# 1-based，闭区间
HEADER = (1, 7)
PRELUDE = (8, 243)
DECL_LINE = 244  # public sealed class ProductionRuntimeService
OPEN_BRACE = 245
CLOSE_BRACE = 1498
BODY = (246, 1497)

header = original[HEADER[0] - 1:HEADER[1]]
prelude = original[PRELUDE[0] - 1:PRELUDE[1]]
decl = original[DECL_LINE - 1]
assert decl.strip() == "public sealed class ProductionRuntimeService", decl
assert original[OPEN_BRACE - 1].strip() == "{"
assert original[CLOSE_BRACE - 1].strip() == "}"

partial_decl = "public sealed partial class ProductionRuntimeService"
folder = "backend/src/VisionStudio.Api/"
plans: list[tuple[str, tuple[int, int] | None, str | None]] = [
    ("ProductionRuntime.Models.cs", PRELUDE, None),
    ("ProductionRuntime.cs", (246, 645), partial_decl),
    ("ProductionRuntime.DeviceActions.cs", (646, 907), partial_decl),
    ("ProductionRuntime.Lifecycle.cs", (908, 1092), partial_decl),
    ("ProductionRuntime.Loop.cs", (1093, 1497), partial_decl),
]

# 分组必须恰好覆盖类体一次
covered: list[int] = []
for _, span, _decl in plans[1:]:
    assert span is not None
    covered.extend(range(span[0], span[1] + 1))
expected_cover = list(range(BODY[0], BODY[1] + 1))
assert covered == expected_cover, "groups must cover the class body exactly once, in order"

verifications: list[tuple[pathlib.Path, int, tuple[int, int] | None]] = []
for name, span, declaration in plans:
    lines = list(header)
    if span == PRELUDE:
        lines.extend(prelude)
        content_offset = len(header)
    else:
        assert declaration is not None and span is not None
        lines.append("")
        lines.append(declaration)
        lines.append("{")
        content_offset = len(lines)
        lines.extend(original[span[0] - 1:span[1]])
        lines.append("}")
    lines.append("")
    target = ROOT / (folder + name)
    target.write_text("\n".join(lines), encoding="utf-8")
    verifications.append((target, content_offset, span))
    print(f"wrote {name} ({len(lines)} lines)")

# 逐行精确校验：读回文件，被搬运区间必须与源文件一字不差
for target, offset, span in verifications:
    written = target.read_text(encoding="utf-8").splitlines()
    assert span is not None
    expected = original[span[0] - 1:span[1]]
    actual = written[offset:offset + len(expected)]
    if actual != expected:
        for i, (want, got) in enumerate(zip(expected, actual)):
            if want != got:
                raise SystemExit(f"MISMATCH {target.name} line {span[0] + i}:\n  want {want!r}\n  got  {got!r}")
        raise SystemExit(f"MISMATCH {target.name}: length differs")
    tail = [line for line in written[offset + len(expected):] if line.strip()]
    if span == PRELUDE:
        assert not tail, f"{target.name} has unexpected tail: {tail[:3]}"
    else:
        assert tail == ["}"], f"{target.name} has unexpected tail: {tail[:3]}"
    print(f"verified {target.name}: {len(expected)} source lines identical")

# 顶层类型也必须逐字保留（prelude 里）且源文件不再包含它们
main = (ROOT / (folder + "ProductionRuntime.cs")).read_text(encoding="utf-8")
# 顶层类型的**声明**必须只在 Models 文件里；服务类仍会引用这些类型名，那是正常的。
for token in ("public enum ProductionRuntimeState", "public sealed record ProductionRuntimeConfig(",
              "public sealed record ProductionRuntimeStatus(", "public sealed class ProductionRuntimeConfigStore"):
    assert token not in main, f"top-level declaration {token} left in main file"

print("ProductionRuntime split verified")
