#!/usr/bin/env python3
"""把一个 C# 类按**行范围纯移动**拆成多个 partial 文件（行为不变）。

为什么需要这个：巨型文件拆分最容易出的事故是"搬的过程中手滑改了一行"——编译还能过，
但语义悄悄变了，而 diff 里全是移动噪音，评审看不出来。所以这里只做机械搬运，并且强制三条约束：

1. **切点必须落在成员边界上**。脚本在剥掉字符串/注释后统计花括号深度，切点前一行的深度
   必须回到类体基线（1），否则拒绝执行——不允许把方法切成两半。
2. **分组必须恰好覆盖类体一次**。漏了会丢代码；重复会把 using / namespace / 花括号复制进
   类体——编译期能过，但文件里会多出一段谁也没写过的头部。
3. **多重集相等**。搬完之后把"源文件 + 所有输出文件"里除脚手架（using / namespace /
   类声明 / 收尾花括号）之外的所有非空行做多重集比较，必须与拆分前的类体完全一致。
   少一行、多一行、改一行都会报出来。这是纯移动的机器证明，比人眼看 diff 可靠。

类后面如果还有其它顶层类型（本仓库的 VisionWorkflowCompiler.cs 末尾就有一个
`CompileWarning` 记录），那些行原样留在主文件末尾，不参与分组。

用法：
    python scripts/split-cs-members.py scripts/splits/<name>.json
"""
from __future__ import annotations

import collections
import json
import pathlib
import sys


def strip_code(line: str, state: dict[str, str]) -> str:
    """把字符串/字符字面量与注释替换成空格，只留下用于数花括号的骨架。

    `state['mode']` 在行间保留跨行状态（块注释、逐字字符串、原始字符串）。
    """
    out: list[str] = []
    i = 0
    n = len(line)
    while i < n:
        mode = state.get("mode", "code")
        two = line[i:i + 2]

        if mode == "code":
            if two == "//":
                break
            if two == "/*":
                state["mode"] = "block"
                i += 2
                continue
            if two == '@"':
                state["mode"] = "verbatim"
                i += 2
                continue
            if line[i:i + 3] == '"""':
                state["mode"] = "raw"
                state["quotes"] = 3
                i += 3
                continue
            if line[i] == '"':
                state["mode"] = "string"
                i += 1
                continue
            if line[i] == "'":
                state["mode"] = "char"
                i += 1
                continue
            out.append(line[i])
            i += 1
            continue

        if mode == "block":
            if two == "*/":
                state["mode"] = "code"
                i += 2
                continue
            i += 1
            continue

        if mode == "verbatim":
            if two == '""':
                i += 2
                continue
            if line[i] == '"':
                state["mode"] = "code"
            i += 1
            continue

        if mode == "raw":
            quotes = state.get("quotes", 3)
            if line[i:i + quotes] == '"' * quotes:
                state["mode"] = "code"
                i += quotes
                continue
            i += 1
            continue

        if mode == "string":
            if line[i] == "\\":
                i += 2
                continue
            if line[i] == '"':
                state["mode"] = "code"
            i += 1
            continue

        if mode == "char":
            if line[i] == "\\":
                i += 2
                continue
            if line[i] == "'":
                state["mode"] = "code"
            i += 1
            continue

        i += 1

    if state.get("mode") in ("string", "char"):
        # 单行字面量不可能跨行
        state["mode"] = "code"
    return "".join(out)


def depths(lines: list[str]) -> list[int]:
    """返回每行**结束时**的花括号深度。"""
    state: dict[str, str] = {}
    result: list[int] = []
    depth = 0
    for line in lines:
        skeleton = strip_code(line, state)
        depth += skeleton.count("{") - skeleton.count("}")
        result.append(depth)
    return result


def type_body(lines: list[str], declaration: str) -> tuple[int, int] | None:
    """给定一个 partial 文件，返回 (类体起始下标, 收尾花括号下标)。起始下标指向右花括号后第一行。"""
    decl_index = next((i for i, line in enumerate(lines) if line.strip() == declaration.strip()), None)
    if decl_index is None:
        return None
    line_depths = depths(lines)
    brace_index = next(
        (i for i in range(decl_index, len(lines)) if "{" in strip_code(lines[i], {})),
        None,
    )
    if brace_index is None:
        return None
    close_index = next((i for i in range(brace_index, len(lines)) if line_depths[i] == 0), None)
    if close_index is None:
        return None
    return brace_index + 1, close_index


def body_multiset(lines: list[str], declaration: str) -> collections.Counter[str]:
    span = type_body(lines, declaration)
    if span is None:
        raise SystemExit(f"ERROR: cannot locate the type body for: {declaration}")
    start, close = span
    return collections.Counter(
        line.strip() for line in lines[start:close] if line.strip()
    )


def main() -> int:
    if len(sys.argv) < 2:
        print("usage: split-cs-members.py <spec.json>", file=sys.stderr)
        return 2
    spec_path = pathlib.Path(sys.argv[1])
    spec = json.loads(spec_path.read_text(encoding="utf-8"))
    root = pathlib.Path(__file__).resolve().parent.parent
    source_path = root / spec["source"]
    original = source_path.read_text(encoding="utf-8").splitlines()

    decl_index = next((i for i, line in enumerate(original) if spec["declaration"] in line), None)
    if decl_index is None:
        print(f"ERROR: declaration not found: {spec['declaration']}", file=sys.stderr)
        return 2

    line_depths = depths(original)
    brace_index = next(i for i in range(decl_index, len(original)) if "{" in strip_code(original[i], {}))
    close_index = next(i for i in range(brace_index, len(original)) if line_depths[i] == 0)

    header = original[:decl_index]
    body_start = brace_index + 1  # 类体第一行（0-based）
    tail = original[close_index + 1:]  # 类之后的其它顶层类型，原样保留

    # 切点必须是成员边界：该行之前的深度是 1（类体基线），且该行是成员声明开头。
    bad: list[str] = []
    for cut in spec["cutPoints"]:
        index = int(cut) - 1
        if line_depths[index - 1] != 1 or not original[index].startswith("    "):
            bad.append(f"line {cut}: depth-before={line_depths[index - 1]}, text={original[index].strip()[:80]}")
    if bad:
        print("ERROR: cut points are not member boundaries:", file=sys.stderr)
        for entry in bad:
            print(f"  {entry}", file=sys.stderr)
        return 1

    # 按组切分（1-based，闭区间）
    groups: dict[str, list[list[str]]] = {}
    covered: list[int] = []
    for name, ranges in spec["groups"].items():
        chunks: list[list[str]] = []
        for start, end in ranges:
            covered.extend(range(int(start), int(end) + 1))
            chunks.append(original[int(start) - 1:int(end)])
        groups[name] = chunks

    # 不变量：分组必须恰好覆盖类体一次（类收尾花括号是脚手架，不属于任何分组）。
    expected_span = list(range(body_start + 1, close_index + 1))
    if sorted(covered) != expected_span:
        counts = collections.Counter(covered)
        duplicated = sorted(line for line, count in counts.items() if count > 1)
        missed = sorted(set(expected_span) - set(covered))
        extra = sorted(set(covered) - set(expected_span))
        print(
            f"ERROR: groups must cover lines {body_start + 1}..{close_index} exactly once; "
            f"missing={missed[:10]} extra={extra[:10]} duplicated={duplicated[:10]}",
            file=sys.stderr,
        )
        return 1

    # 其它 partial 文件不要把类的 XML 文档注释一起复制过去：partial 类型允许每部分各有文档，
    # 但同一段注释在多个文件里各来一遍既难看也会让 IDE 提示重复。
    shared_header = list(header)
    while shared_header and (shared_header[-1].strip().startswith("///") or shared_header[-1].strip() == ""):
        shared_header.pop()
    shared_header.append("")

    def render(
        header_lines: list[str],
        declaration: str,
        chunks: list[list[str]],
        extra_tail: list[str] | None = None,
    ) -> list[str]:
        lines = list(header_lines)
        lines.append(declaration)
        lines.append("{")
        for chunk in chunks:
            lines.extend(chunk)
            if chunk and chunk[-1].strip() != "":
                lines.append("")
        while lines and lines[-1].strip() == "":
            lines.pop()
        lines.append("}")
        if extra_tail:
            lines.extend(extra_tail)
        while lines and lines[-1].strip() == "":
            lines.pop()
        lines.append("")
        return lines

    for name, target in spec["outputs"].items():
        lines = render(shared_header, spec["partialDeclarationBare"], groups[name])
        (root / target).write_text("\n".join(lines), encoding="utf-8")
        print(f"wrote {target} ({len(lines)} lines)")

    main_lines = render(header, spec["partialDeclaration"], groups[spec["mainGroup"]], tail)
    source_path.write_text("\n".join(main_lines), encoding="utf-8")
    print(f"wrote {spec['source']} ({len(main_lines)} lines)")

    # 多重集校验：源文件类体 vs 每个输出文件的类体
    expected = body_multiset(original, spec["declaration"])
    actual: collections.Counter[str] = collections.Counter()
    for target in [*spec["outputs"].values(), spec["source"]]:
        lines = (root / target).read_text(encoding="utf-8").splitlines()
        # 主文件带主构造函数（参数列表），其余 partial 只能写无参声明（CS8863）。
        declaration = next(
            candidate
            for candidate in (spec["partialDeclaration"], spec["partialDeclarationBare"])
            if any(line.strip() == candidate.strip() for line in lines)
        )
        actual.update(body_multiset(lines, declaration))

    missing = expected - actual
    extra = actual - expected
    if missing or extra:
        print(f"ERROR: multiset mismatch (-{sum(missing.values())} / +{sum(extra.values())})", file=sys.stderr)
        for line, count in list(missing.items())[:10]:
            print(f"  -{count}: {line[:120]}", file=sys.stderr)
        for line, count in list(extra.items())[:10]:
            print(f"  +{count}: {line[:120]}", file=sys.stderr)
        return 1

    print(f"multiset OK: {sum(expected.values())} non-empty lines identical")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
