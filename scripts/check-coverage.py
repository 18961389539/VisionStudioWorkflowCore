#!/usr/bin/env python3
"""Merge Cobertura line/branch hits by filename+line and enforce minimum coverage.

为什么要合并：一个解决方案里多个测试项目会各自产出一份 Cobertura，覆盖同一份产品代码。
直接累加会把同一行算多次；这里按 (filename, line) 取各报告里的最大值，等价于"并集覆盖"。

分支覆盖率（2026-10-29 新增）：只看行覆盖率会漏掉"行执行了但分支只走了一边"的情况——
`if (x) return;` 只要走过一次就算整行覆盖，else 分支可能永远没测。Cobertura 在
`<line branch="True" condition-coverage="50% (1/2)">` 里给了每行的条件覆盖，按同一套
并集规则合并即可。
"""
from __future__ import annotations
import argparse
import pathlib
import re
import sys
import xml.etree.ElementTree as ET

CONDITION = re.compile(r"(\d+)%\s*\((\d+)/(\d+)\)")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=pathlib.Path, help="Directory containing coverage.cobertura.xml files")
    parser.add_argument("--min-line", type=float, default=20.0, help="Minimum merged line coverage percentage")
    parser.add_argument(
        "--min-branch",
        type=float,
        default=0.0,
        help="Minimum merged branch coverage percentage (0 disables the check)",
    )
    args = parser.parse_args()

    reports = sorted(args.root.rglob("coverage.cobertura.xml"))
    if not reports:
        print(f"ERROR: no coverage.cobertura.xml files found under {args.root}", file=sys.stderr)
        return 2

    lines: dict[tuple[str, int], int] = {}
    branches: dict[tuple[str, int], tuple[int, int]] = {}
    for report in reports:
        root = ET.parse(report).getroot()
        for cls in root.findall(".//class"):
            filename = cls.attrib.get("filename", "unknown").replace("\\", "/")
            for line in cls.findall("./lines/line"):
                try:
                    number = int(line.attrib["number"])
                    hits = int(line.attrib.get("hits", "0"))
                except (KeyError, ValueError):
                    continue
                key = (filename, number)
                lines[key] = max(lines.get(key, 0), hits)

                if line.attrib.get("branch", "").lower() != "true":
                    continue
                match = CONDITION.search(line.attrib.get("condition-coverage", ""))
                if not match:
                    continue
                covered, total = int(match.group(2)), int(match.group(3))
                # 同一行在所有报告里条件总数相同（同一份源码），取覆盖最多的那份。
                previous = branches.get(key)
                if previous is None or covered > previous[0]:
                    branches[key] = (covered, total)

    total = len(lines)
    if total == 0:
        print("ERROR: coverage reports contained no executable lines", file=sys.stderr)
        return 2
    covered = sum(1 for hits in lines.values() if hits > 0)
    percent = covered * 100.0 / total
    print(f"Merged line coverage: {covered}/{total} = {percent:.2f}% (minimum {args.min_line:.2f}%)")

    branch_total = sum(item[1] for item in branches.values())
    branch_covered = sum(item[0] for item in branches.values())
    branch_percent = branch_covered * 100.0 / branch_total if branch_total else 100.0
    if branch_total:
        print(
            f"Merged branch coverage: {branch_covered}/{branch_total} = {branch_percent:.2f}% "
            f"(minimum {args.min_branch:.2f}%)"
        )

    failed = False
    if percent + 1e-9 < args.min_line:
        print("LINE COVERAGE GATE FAILED", file=sys.stderr)
        failed = True
    if branch_total and args.min_branch > 0 and branch_percent + 1e-9 < args.min_branch:
        print("BRANCH COVERAGE GATE FAILED", file=sys.stderr)
        failed = True
    if failed:
        return 1
    print("Coverage gate PASSED")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
