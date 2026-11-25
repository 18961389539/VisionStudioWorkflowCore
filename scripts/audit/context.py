from __future__ import annotations

import json
import pathlib
import xml.etree.ElementTree as ET


class AuditContext:
    # 后端源码根（2026-11-25 起 Engine 不再是唯一边界）。
    # 契约/图像/算法从引擎拆出后，"按文件名找源码"必须跨根搜索，
    # 否则拆分当天门禁就会静默漏检（历史上 static-audit 已踩过两次）。
    SOURCE_ROOTS = (
        "backend/src/VisionStudio.Engine",
        "backend/src/VisionStudio.Algorithms",
        "backend/src/VisionStudio.Imaging",
        "backend/src/VisionStudio.Hardware.Abstractions",
        "backend/src/VisionStudio.Abstractions",
        "backend/src/VisionStudio.Scripting",
    )

    def __init__(self, root: pathlib.Path) -> None:
        self.root = root
        self.errors: list[str] = []
        self.notes: list[str] = []
        self._missing_files: set[str] = set()
        self._missing_docs: set[str] = set()
        self._engine_root = root / "backend/src/VisionStudio.Engine"

    def fail(self, msg: str) -> None:
        self.errors.append(msg)

    def ok(self, msg: str) -> None:
        self.notes.append(msg)

    def read_safe(self, rel: str) -> str:
        path = self.root / rel
        if not path.exists():
            if rel not in self._missing_files:
                self._missing_files.add(rel)
                self.fail(f"Required file missing: {rel}")
            return ""
        return path.read_text(encoding="utf-8")

    def read_family(self, rel_glob: str) -> str:
        parts = sorted(self.root.glob(rel_glob))
        if not parts:
            self.fail(f"Required source family missing: {rel_glob}")
            return ""
        return "\n".join(p.read_text(encoding="utf-8") for p in parts)

    def source_files(self, pattern: str) -> list[pathlib.Path]:
        """按文件名/通配符在后端全部源码根下查找，返回去重后的文件列表。"""
        found: list[pathlib.Path] = []
        seen: set[pathlib.Path] = set()
        for rel_root in self.SOURCE_ROOTS:
            root = self.root / rel_root
            if not root.is_dir():
                continue
            for path in sorted(root.rglob(pattern)):
                if path.is_file() and path not in seen and not any(
                    part in ("bin", "obj") for part in path.parts
                ):
                    seen.add(path)
                    found.append(path)
        return found

    def read_source(self, pattern: str) -> str:
        """读取匹配的源码内容；一个都没找到即失败（防止"拆到别处"变成静默通过）。"""
        parts = self.source_files(pattern)
        if not parts:
            self.fail(f"Required backend source missing: {pattern}")
            return ""
        return "\n".join(p.read_text(encoding="utf-8") for p in parts)

    def read_engine_source(self, name: str) -> str:
        direct = self._engine_root / f"{name}.cs"
        if direct.is_file():
            return direct.read_text(encoding="utf-8")
        matches = sorted(self._engine_root.rglob(f"{name}.cs"))
        if not matches:
            self.fail(f"Required Engine source missing: {name}.cs")
            return ""
        return "\n".join(p.read_text(encoding="utf-8") for p in matches)

    def doc_present(self, rel: str) -> bool:
        if (self.root / rel).is_file():
            return True
        if rel not in self._missing_docs:
            self._missing_docs.add(rel)
            self.fail(f"Required documentation missing: {rel}")
        return False

    def doc_has(self, rel: str, token: str) -> bool:
        if not self.doc_present(rel):
            return False
        if token.lower() not in (self.root / rel).read_text(encoding="utf-8").lower():
            self.fail(f"Required documentation token missing in {rel}: {token}")
            return False
        return True


def audit_json_and_xml(ctx: AuditContext) -> None:
    json_files = [
        p
        for p in ctx.root.rglob("*.json")
        if "node_modules" not in p.parts
        and not any(part in ("bin", "obj", ".artifacts", "artifacts") for part in p.parts)
    ]
    for p in json_files:
        try:
            json.loads(p.read_text(encoding="utf-8"))
        except Exception as e:
            ctx.fail(f"JSON {p.relative_to(ctx.root)}: {e}")
    ctx.ok(f"JSON parsed: {len(json_files)}")

    xml_files = list(ctx.root.rglob("*.csproj")) + list(ctx.root.rglob("*.slnx")) + list(ctx.root.rglob("*.runsettings"))
    for p in xml_files:
        try:
            ET.parse(p)
        except Exception as e:
            ctx.fail(f"XML {p.relative_to(ctx.root)}: {e}")
    ctx.ok(f"XML parsed: {len(xml_files)}")
