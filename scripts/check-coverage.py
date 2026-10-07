#!/usr/bin/env python3
"""Merge Cobertura line hits by filename+line and enforce a minimum line coverage percentage."""
from __future__ import annotations
import argparse
import pathlib
import sys
import xml.etree.ElementTree as ET


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=pathlib.Path, help="Directory containing coverage.cobertura.xml files")
    parser.add_argument("--min-line", type=float, default=20.0, help="Minimum merged line coverage percentage")
    args = parser.parse_args()

    reports = sorted(args.root.rglob("coverage.cobertura.xml"))
    if not reports:
        print(f"ERROR: no coverage.cobertura.xml files found under {args.root}", file=sys.stderr)
        return 2

    lines: dict[tuple[str, int], int] = {}
    for report in reports:
        root = ET.parse(report).getroot()
        for cls in root.findall(".//class"):
            filename = cls.attrib.get("filename", "unknown")
            for line in cls.findall("./lines/line"):
                try:
                    number = int(line.attrib["number"])
                    hits = int(line.attrib.get("hits", "0"))
                except (KeyError, ValueError):
                    continue
                key = (filename.replace("\\", "/"), number)
                lines[key] = max(lines.get(key, 0), hits)

    total = len(lines)
    covered = sum(1 for hits in lines.values() if hits > 0)
    if total == 0:
        print("ERROR: coverage reports contained no executable lines", file=sys.stderr)
        return 2
    percent = covered * 100.0 / total
    print(f"Merged line coverage: {covered}/{total} = {percent:.2f}% (minimum {args.min_line:.2f}%)")
    if percent + 1e-9 < args.min_line:
        print("COVERAGE GATE FAILED", file=sys.stderr)
        return 1
    print("Coverage gate PASSED")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
