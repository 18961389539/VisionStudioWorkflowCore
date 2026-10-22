#!/usr/bin/env python3
"""Measure how much of the JLVision typed API the engine actually calls.

Metric definition (fixed, so the number cannot drift with the code being measured):

    denominator = every distinct public method name declared on the JLVision *vision* types
                  (JlImage, JlRegion, JlXLD*, JlShapeModel, JlNCCModel, JlMetrologyModel,
                   JlMeasure, JlHomMat2D, JlMatrix, JlPose, JlMisc)
    numerator   = names that appear as `.Name(` anywhere under
                  backend/src/VisionStudio.Engine (excluding obj/bin)

Deliberately excluded from the denominator:
  * JlTuple / JlTupleVector / JlTupleElements  - tuple bookkeeping, not vision operators
  * JlOperatorSet (1073)                       - untyped dispatcher over the same operators,
                                                 counting both would double count
  * JlObject/JlHandle/JlNativeApi/Jl*Exception - handle management, marshalling, errors

The inventory is generated once by reflection over the shipped assembly, so the denominator only
changes when the native package changes:

    dotnet run --project <probe>            # prints {"assembly":..,"types":[{type,methods[]}]}
    -> docs/jlvision-operator-inventory.json

Usage:
    python scripts/jlvision-coverage.py            # human summary
    python scripts/jlvision-coverage.py --json     # machine readable, exit 1 when below --target
    python scripts/jlvision-coverage.py --target 0.6 --json
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
INVENTORY = ROOT / "docs" / "jlvision-operator-inventory.json"
ENGINE = ROOT / "backend" / "src" / "VisionStudio.Engine"

VISION_TYPES = [
    "JlImage", "JlRegion",
    "JlXLD", "JlXLDCont", "JlXLDPoly", "JlXLDDistTrans",
    "JlXLDExtPara", "JlXLDModPara", "JlXLDPara",
    "JlShapeModel", "JlNCCModel", "JlMetrologyModel", "JlMeasure",
    "JlHomMat2D", "JlMatrix", "JlPose", "JlMisc",
]

CALL = re.compile(r"\.([A-Z][A-Za-z0-9_]*)\s*\(")


def load_inventory() -> dict[str, set[str]]:
    doc = json.loads(INVENTORY.read_text(encoding="utf-8"))
    return {entry["type"]: set(entry["methods"]) for entry in doc["types"]}


def engine_source() -> str:
    parts: list[str] = []
    for path in ENGINE.rglob("*.cs"):
        if any(seg in path.parts for seg in ("obj", "bin")):
            continue
        parts.append(path.read_text(encoding="utf-8", errors="ignore"))
    return "\n".join(parts)


def measure() -> dict:
    types = load_inventory()
    used = set(CALL.findall(engine_source()))

    per_type = []
    union: set[str] = set()
    union_hit: set[str] = set()
    for name in VISION_TYPES:
        methods = types.get(name, set())
        if not methods:
            continue
        hit = methods & used
        per_type.append({
            "type": name,
            "total": len(methods),
            "used": len(hit),
            "ratio": round(len(hit) / len(methods), 4),
            "missing": sorted(methods - used),
        })
        union |= methods
        union_hit |= hit

    return {
        "visionTypes": VISION_TYPES,
        # 口径：按"算子名"去重（同一个名字出现在多个类型上只算一个算子）
        "total": len(union),
        "used": len(union_hit),
        "ratio": round(len(union_hit) / len(union), 4),
        "perType": per_type,
        "missing": sorted(union - union_hit),
        "excluded": {
            "JlTuple": len(types.get("JlTuple", ())),
            "JlOperatorSet": len(types.get("JlOperatorSet", ())),
        },
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--json", action="store_true", help="emit JSON instead of a table")
    parser.add_argument("--target", type=float, default=None, help="exit 1 when ratio < target")
    args = parser.parse_args()

    result = measure()
    if args.json:
        print(json.dumps(result, ensure_ascii=False, indent=2))
    else:
        print(f"JLVision typed vision operators: {result['used']}/{result['total']} "
              f"= {result['ratio'] * 100:.1f}%")
        print(f"  (excluded from denominator: JlTuple {result['excluded']['JlTuple']}, "
              f"JlOperatorSet {result['excluded']['JlOperatorSet']})")
        print()
        print(f"  {'type':22s} {'total':>6s} {'used':>6s} {'ratio':>7s}")
        for row in result["perType"]:
            print(f"  {row['type']:22s} {row['total']:6d} {row['used']:6d} {row['ratio'] * 100:6.1f}%")

    if args.target is not None and result["ratio"] < args.target:
        print(f"\nBELOW TARGET: {result['ratio'] * 100:.1f}% < {args.target * 100:.1f}%",
              file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
