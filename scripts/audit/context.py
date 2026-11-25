from __future__ import annotations

import json
import pathlib
import xml.etree.ElementTree as ET


class AuditContext:
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
        if (self.root / rel).exists():
            return True
        if rel not in self._missing_docs:
            self._missing_docs.add(rel)
            self.ok(f"documentation absent (check skipped): {rel}")
        return False

    def doc_has(self, rel: str, token: str) -> bool:
        if not self.doc_present(rel):
            return True
        return token.lower() in (self.root / rel).read_text(encoding="utf-8").lower()


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
