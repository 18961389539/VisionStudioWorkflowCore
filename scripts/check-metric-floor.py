#!/usr/bin/env python3
"""度量下限守卫（棘轮）：节点目录、后端测试方法数、前端测试文件数只许升不许降。

为什么需要这个：覆盖率门槛能挡住"代码变多但没人测"，却挡不住"测试被删掉"——
删掉 50 个测试往往会让覆盖率**上升**（删掉的是难覆盖的分支），门禁反而更绿。
同样，节点目录从 242 掉到 240 不会有任何测试失败，但前端工具箱会少两个入口，
已保存的流程加载时才报"未知节点类型"。

口径：
- catalogNodes：frontend/src/generated/catalog.generated.json 的 items 长度
- *TestMethods：测试目录下 [Fact] / [Theory] 特性数量（方法数，不含 Theory 的每个 InlineData）
- frontendTestFiles：frontend/src/__tests__/*.test.* 文件数

升级基线：实测值 >= 基线即可通过。新增测试后应顺手把 baseline 抬到实测值，
否则"删掉新增的那些"不会被发现。
"""
from __future__ import annotations

import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
BASELINE = ROOT / "scripts" / "metric-floor.json"

TEST_ATTRIBUTE = re.compile(r"^\s*\[(?:Fact|Theory)\b", re.MULTILINE)
SKIP_DIRS = {"obj", "bin"}


def count_backend_tests(relative: str) -> int:
    directory = ROOT / relative
    if not directory.is_dir():
        return -1
    total = 0
    for path in directory.rglob("*.cs"):
        if SKIP_DIRS.intersection(path.parts):
            continue
        total += len(TEST_ATTRIBUTE.findall(path.read_text(encoding="utf-8", errors="replace")))
    return total


def count_catalog_nodes() -> int:
    catalog = ROOT / "frontend" / "src" / "generated" / "catalog.generated.json"
    if not catalog.is_file():
        return -1
    return len(json.loads(catalog.read_text(encoding="utf-8")).get("items", []))


def count_frontend_test_files() -> int:
    # 用递归匹配而不是只看 __tests__ 顶层：vitest 的 include 是 src/**/*.test.*，
    # 顶层统计会漏掉放进子目录的用例，让下限偏松（实测曾漏 3 个）。
    directory = ROOT / "frontend" / "src"
    if not directory.is_dir():
        return -1
    return sum(1 for path in directory.rglob("*.test.*") if path.is_file())


MEASURES = {
    "catalogNodes": count_catalog_nodes,
    "backendEngineTestMethods": lambda: count_backend_tests("backend/tests/VisionStudio.Engine.Tests"),
    "backendApiTestMethods": lambda: count_backend_tests("backend/tests/VisionStudio.Api.Tests"),
    "frontendTestFiles": count_frontend_test_files,
}


def main() -> int:
    if not BASELINE.is_file():
        print(f"ERROR: metric baseline not found at {BASELINE}", file=sys.stderr)
        return 2

    baseline = json.loads(BASELINE.read_text(encoding="utf-8"))
    failures: list[str] = []
    print("== Metric floor ==")

    for name, measure in MEASURES.items():
        expected = baseline.get(name)
        if not isinstance(expected, int):
            print(f"ERROR: baseline '{name}' is missing or not an integer", file=sys.stderr)
            return 2

        actual = measure()
        if actual < 0:
            print(f"ERROR: could not measure '{name}' (path missing)", file=sys.stderr)
            return 2

        status = "ok" if actual >= expected else "REGRESSED"
        print(f"{name:26} {actual:>6} (floor {expected:>6})  {status}")
        if actual < expected:
            failures.append(f"{name} dropped to {actual}, floor is {expected}")

    if failures:
        print("", file=sys.stderr)
        for line in failures:
            print(f"METRIC FLOOR VIOLATED: {line}", file=sys.stderr)
        print(
            "Removing tests or nodes is only allowed together with a deliberate baseline change "
            "in scripts/metric-floor.json, documented in the commit message.",
            file=sys.stderr,
        )
        return 1

    print("Metric floor PASSED")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
