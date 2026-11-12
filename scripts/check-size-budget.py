#!/usr/bin/env python3
"""文件规模预算守卫（棘轮）。

为什么需要这个：`static-audit.py` 只钉住了 `Program.cs <= 80`，其余文件可以无限长。
于是出现了"拆完一个 God file，半年后另一个文件长回 1700 行"的循环——拆分是一次性动作，
没有预算就没有东西阻止它回弹。

口径：
- 扫描 `backend/src/**/*.cs` 与 `frontend/src/**/*.{ts,tsx}`；
- 跳过生成物、测试、i18n 词表（词表长度随节点数线性增长，按行数设限只会制造噪音）；
- 未列入 budgets 的文件不得超过 `defaultMaxLines`；
- 已列入 budgets 的文件不得超过记录值（只许降）；
- 记录值高于实测值时打印提示，要求顺手下调——棘轮滞后等于没有棘轮；
- 记录的文件已不存在时报错，避免预算表变成历史遗迹。

修复方式：拆文件，或在本次提交里**下调** budgets；上调需要改 defaultMaxLines 并在提交说明里
写明为什么这个文件必须更大。
"""
from __future__ import annotations

import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
BASELINE = ROOT / "scripts" / "size-budget.json"

SKIP_PARTS = {"obj", "bin", "node_modules", "generated", "i18n", "__tests__", "e2e", "dist"}


def iter_sources() -> list[pathlib.Path]:
    files: list[pathlib.Path] = []
    for pattern in ("backend/src/**/*.cs", "frontend/src/**/*.ts", "frontend/src/**/*.tsx"):
        for path in ROOT.glob(pattern):
            if not path.is_file():
                continue
            if SKIP_PARTS.intersection(path.parts):
                continue
            if path.name.endswith((".test.ts", ".test.tsx", ".d.ts")):
                continue
            files.append(path)
    return files


def main() -> int:
    if not BASELINE.is_file():
        print(f"ERROR: size budget not found at {BASELINE}", file=sys.stderr)
        return 2

    config = json.loads(BASELINE.read_text(encoding="utf-8"))
    default_max = int(config.get("defaultMaxLines") or 1200)
    target_max = int(config.get("targetMaxLines") or 800)
    budgets: dict[str, int] = {str(k): int(v) for k, v in (config.get("budgets") or {}).items()}

    failures: list[str] = []
    hints: list[str] = []
    seen: set[str] = set()

    sources = iter_sources()
    for path in sources:
        relative = path.relative_to(ROOT).as_posix()
        lines = sum(1 for _ in path.open("rb"))
        if relative in budgets:
            seen.add(relative)
            limit = budgets[relative]
            if lines > limit:
                failures.append(f"{relative}: {lines} lines exceeds budget {limit}")
            elif lines < limit:
                hints.append(f"{relative}: {lines} lines, budget can be lowered from {limit} to {lines}")
        elif lines > default_max:
            failures.append(f"{relative}: {lines} lines exceeds default budget {default_max} (not in size-budget.json)")

    for relative in sorted(set(budgets) - seen):
        failures.append(f"{relative}: budget entry is stale (file no longer exists or is skipped)")

    print("== Size budget ==")
    print(f"scanned {len(sources)} source files; default {default_max} lines; target {target_max} lines")
    for hint in hints:
        print(f"hint: {hint}")

    if failures:
        print("", file=sys.stderr)
        for line in failures:
            print(f"SIZE BUDGET VIOLATED: {line}", file=sys.stderr)
        print(
            "Split the file, or lower the recorded budget in scripts/size-budget.json. "
            "Raising a budget requires a deliberate defaultMaxLines change documented in the commit message.",
            file=sys.stderr,
        )
        return 1

    print("Size budget PASSED")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
