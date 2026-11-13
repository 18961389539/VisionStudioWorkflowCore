#!/usr/bin/env python3
"""前端构建产物的 gzip 体积预算守卫（棘轮）。

为什么需要这个：覆盖率与单测数量都挡不住"某个依赖悄悄回到首屏"——中文词表曾以 445 kB
裸体积进入 index，编辑器依赖也随时可能被静态 import 拉回来。构建产物是唯一能提前看到
"现场要下载多少字节"的地方，没有预算就只能在用户抱怨加载慢时才发现。

口径：
- 扫描 `frontend/dist/assets/**/*.{js,css}`，按内容哈希前的名字归组（`index-3yTIFfEb.js` → `index.js`）；
- gzip -9 后的体积与 `scripts/bundle-budget.json` 比较；
- 未列入 budgets 的 chunk 不得超过 `defaultMaxGzipKB`（新增大块必须先显式登记）；
- 已列入 budgets 的 chunk 不得超过记录值；只许降，降下来后应同步下调记录值；
- 所有产物 gzip 之和不得超过 `totalMaxGzipKB`，防止"很多小块各自合规、合起来变大"；
- budgets 里记录但产物中不存在的 chunk 直接失败，避免预算表变成历史遗迹。

修复方式：拆分 / 改懒加载，或在本提交里显式上调 budgets 并在提交说明里写明原因。
"""
from __future__ import annotations

import gzip
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
DIST = ROOT / "frontend" / "dist" / "assets"
BASELINE = ROOT / "scripts" / "bundle-budget.json"

# Vite/rolldown 的内容哈希：8 位 [A-Za-z0-9_-]。
HASH_SUFFIX = re.compile(r"-[A-Za-z0-9_-]{8}\.(js|css)$")


def normalize(name: str) -> str:
    return HASH_SUFFIX.sub(r".\1", name)


def gzip_kb(path: pathlib.Path) -> int:
    return (len(gzip.compress(path.read_bytes(), compresslevel=9)) + 1023) // 1024


def main() -> int:
    if not BASELINE.is_file():
        print(f"ERROR: bundle budget not found at {BASELINE}", file=sys.stderr)
        return 2
    if not DIST.is_dir():
        print(f"ERROR: build output not found at {DIST}; run `npm run build` first.", file=sys.stderr)
        return 2

    config = json.loads(BASELINE.read_text(encoding="utf-8"))
    default_max = int(config.get("defaultMaxGzipKB") or 20)
    total_max = int(config.get("totalMaxGzipKB") or 0)
    budgets: dict[str, int] = {str(k): int(v) for k, v in (config.get("budgets") or {}).items()}

    files = sorted(p for p in DIST.rglob("*") if p.suffix in (".js", ".css") and p.is_file())
    totals: dict[str, int] = {}
    total_kb = 0
    for path in files:
        key = normalize(path.name)
        kb = gzip_kb(path)
        totals[key] = max(totals.get(key, 0), kb)
        total_kb += kb

    failures: list[str] = []
    hints: list[str] = []
    seen: set[str] = set()

    for key, kb in sorted(totals.items(), key=lambda item: (-item[1], item[0])):
        if key in budgets:
            seen.add(key)
            limit = budgets[key]
            if kb > limit:
                failures.append(f"{key}: {kb} kB gzip exceeds budget {limit} kB")
            elif kb <= int(limit * 0.9) and limit - kb >= 2:
                hints.append(f"{key}: {kb} kB gzip, budget can be lowered from {limit} kB to {kb} kB")
        elif kb > default_max:
            failures.append(
                f"{key}: {kb} kB gzip exceeds default budget {default_max} kB "
                "(register it in scripts/bundle-budget.json)"
            )

    for key in sorted(set(budgets) - seen):
        failures.append(f"{key}: budget entry is stale (chunk no longer emitted)")

    if total_max and total_kb > total_max:
        failures.append(f"total: {total_kb} kB gzip exceeds total budget {total_max} kB (chunks: {len(files)})")

    print("== Bundle budget ==")
    print(f"scanned {len(files)} assets ({len(totals)} chunks); default {default_max} kB gzip; total {total_kb} kB")
    for hint in hints:
        print(f"hint: {hint}")

    if failures:
        print("", file=sys.stderr)
        for line in failures:
            print(f"BUNDLE BUDGET VIOLATED: {line}", file=sys.stderr)
        print(
            "Split or lazy-load the chunk, or raise the recorded budget in scripts/bundle-budget.json "
            "with the reason in the commit message.",
            file=sys.stderr,
        )
        return 1

    print("Bundle budget PASSED")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
