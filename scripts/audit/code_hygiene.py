from __future__ import annotations

import json
import pathlib
import re
from .context import AuditContext

EMPTY_CATCH_BASELINE = "scripts/empty-catch-baseline.json"


def audit_changelog_version(ctx: AuditContext) -> None:
    api_project = ctx.read_safe("backend/src/VisionStudio.Api/VisionStudio.Api.csproj")
    version_match = re.search(r"<Version>\s*([^<\s]+)\s*</Version>", api_project)
    if not version_match:
        ctx.fail("VisionStudio.Api.csproj is missing <Version>")
        return
    api_version = version_match.group(1)
    changelog = ctx.read_safe("CHANGELOG.md")
    if f"## {api_version}" not in changelog:
        ctx.fail(
            f"CHANGELOG.md has no section for version {api_version} "
            f'(expected a "## {api_version} - YYYY-MM-DD" heading)'
        )
    else:
        ctx.ok(f"CHANGELOG covers the shipped version {api_version}")


def scan_empty_catch_violations(root: pathlib.Path) -> dict[str, list[int]]:
    violations: dict[str, list[int]] = {}
    source_root = root / "backend/src"
    if not source_root.exists():
        return violations
    for path in sorted(source_root.rglob("*.cs")):
        rel = path.relative_to(root).as_posix()
        if "/obj/" in rel or "/bin/" in rel:
            continue
        lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
        for i, line in enumerate(lines):
            stripped = line.strip()
            if stripped.startswith("//") or not re.search(r"\bcatch\b", stripped):
                continue
            inline = re.search(r"\bcatch\b\s*(\([^)]*\))?\s*\{\s*\}", stripped)
            if inline:
                after = stripped[inline.end():].strip()
                if not (after.startswith("//") or after.startswith("/*")):
                    violations.setdefault(rel, []).append(i + 1)
                continue
            tail = re.sub(r"^.*?\bcatch\b", "catch", stripped)
            if "{" in tail:
                continue
            j = i + 1
            while j < len(lines) and not lines[j].strip():
                j += 1
            if j >= len(lines) or lines[j].strip() != "{":
                continue
            k = j + 1
            while k < len(lines) and not lines[k].strip():
                k += 1
            if k >= len(lines) or lines[k].strip() != "}":
                continue
            explained = any("//" in lines[m] or "/*" in lines[m] for m in range(i, k + 1))
            if not explained:
                violations.setdefault(rel, []).append(i + 1)
    return violations


def audit_empty_catch_baseline(ctx: AuditContext, write_baseline: bool) -> bool:
    empty_catch_hits = scan_empty_catch_violations(ctx.root)
    if write_baseline:
        payload = {
            "_comment": (
                '空 catch 的存量基线（棘轮）：值是该文件中"未写忽略原因"的空 catch 数量。'
                "只许降不许升——新增空 catch 必须写成 `catch { /* 或 // 原因 */ }` 或补注释，"
                "否则 static-audit 直接失败。行号会漂移，故按文件计数而不是锁行号。"
            ),
            "_updated": "2026-11-12",
            "unexplainedByFile": {rel: len(items) for rel, items in sorted(empty_catch_hits.items())},
        }
        (ctx.root / EMPTY_CATCH_BASELINE).write_text(
            json.dumps(payload, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        print(
            f"Empty-catch baseline written: {sum(len(v) for v in empty_catch_hits.values())} "
            f"unexplained clause(s) across {len(empty_catch_hits)} file(s) -> {EMPTY_CATCH_BASELINE}"
        )
        print("Other audit results were ignored because --write-empty-catch-baseline only refreshes the baseline.")
        return True

    baseline_file = ctx.root / EMPTY_CATCH_BASELINE
    if not baseline_file.exists():
        ctx.fail(
            f"Missing empty-catch baseline: {EMPTY_CATCH_BASELINE} "
            "(run static-audit.py --write-empty-catch-baseline once)"
        )
        return False

    allowed_by_file = json.loads(baseline_file.read_text(encoding="utf-8")).get("unexplainedByFile", {})
    for rel in sorted(set(allowed_by_file) | set(empty_catch_hits)):
        allowed = allowed_by_file.get(rel, 0)
        actual_lines = empty_catch_hits.get(rel, [])
        if len(actual_lines) > allowed:
            ctx.fail(
                f"Unexplained empty catch added in {rel}: {len(actual_lines)} > {allowed} "
                f'(lines {actual_lines}). Add a "// reason" comment or handle the error.'
            )
    baseline_total = sum(allowed_by_file.values())
    actual_total = sum(len(v) for v in empty_catch_hits.values())
    if actual_total > baseline_total:
        ctx.fail(f"Unexplained empty catch total grew: {actual_total} > {baseline_total}")
    elif actual_total < baseline_total:
        ctx.ok(
            f"Empty catch: {baseline_total - actual_total} baseline entr(y/ies) now explained — "
            "downshift scripts/empty-catch-baseline.json"
        )
    ctx.ok(f"Empty catch: {actual_total} unexplained clause(s) locked by baseline ({EMPTY_CATCH_BASELINE})")
    return False
