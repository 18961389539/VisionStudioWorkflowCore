from __future__ import annotations

import json
import re
from .context import AuditContext

PORT_TYPE_BY_GENERIC = {
    "VisionImage": "Image",
    "IVisionImage": "Image",
    "VisionRegion": "Region",
    "VisionLine2D": "Line2D",
    "VisionCircle": "Circle",
    "VisionPoint2D": "Point2D",
    "VisionPose2D": "Pose2D",
    "VisionTransform2D": "Transform2D",
    "VisionTransform3D": "Transform3D",
    "VisionFrameTree2D": "FrameTree2D",
    "IVisionFrameSet": "FrameSet",
    "VisionCoordinatePose2D": "CoordinatePose2D",
    "VisionCoordinatePoint2D": "CoordinatePoint2D",
    "VisionCoordinatePoint3D": "CoordinatePoint3D",
    "VisionRobotTarget2D": "RobotTarget2D",
    "bool": "Boolean",
    "string": "String",
    "double": "Double",
    "int": "Integer",
    "long": "Integer",
}

NODE_TYPE_RE = re.compile(r'public string Type => "([a-zA-Z0-9.]+)"')
NUMERIC_FALLBACK_RE = re.compile(
    r'node\.Get(?:Int|Double|Long)\(\s*"([A-Za-z0-9_]+)"\s*,\s*(-?\d[\d_]*(?:\.\d+)?(?:[eE][+-]?\d+)?)\s*\)'
)
STRING_FALLBACK_RE = re.compile(r'node\.GetString\(\s*"([A-Za-z0-9_]+)"\s*,\s*"((?:[^"\\]|\\.)*)"\s*\)')
BOOL_FALLBACK_RE = re.compile(r'node\.GetBool\(\s*"([A-Za-z0-9_]+)"\s*,\s*(true|false)\s*\)')
REQUIRE_GENERIC_RE = re.compile(r'context\.Require<\s*([A-Za-z0-9_.]+)\s*>\(\s*"([^"]+)"')
REQUIRE_IMAGE_RE = re.compile(r'context\.RequireImage\(\s*"([^"]+)"')

DEFAULT_DRIFT_EXEMPT = {
    ("measure.caliperCircle", "samplingMode"),
    ("recognition.barcodeQuality", "decodePolicy"),
    ("image.fuse", "alignmentPolicy"),
}

MATCHING_READ_FLOOR = 82

ALLOWED_CV_NODE_CALLS = {
    "ImRead", "ImDecode", "ImEncode", "ImWrite",
    "Circle", "Rectangle", "Line", "Polylines",
}

ALLOWED_CV_NODE_EXCEPTIONS = {
    ("HandEye3DCalibrationNode.cs", "HandEye3DCalibrationNode", "CalibrateHandEye"),
    ("DualCameraExtrinsicsNode.cs", "DualCameraExtrinsicsNode", "StereoCalibrate"),
    ("DualCameraExtrinsicsNode.cs", "DualCameraExtrinsicsNode", "SolvePnP"),
    ("DualCameraExtrinsicsNode.cs", "DualCameraExtrinsicsNode", "Rodrigues"),
    ("ManagedShapeMatch.cs", "ManagedShapeMatch", "Sobel"),
    ("PreprocessNodes.cs", "GaussianBlurNode", "CopyMakeBorder"),
    ("PreprocessNodes.cs", "MedianBlurNode", "CopyMakeBorder"),
    ("JLVisionNodeSupport.cs", "JLVisionNodeSupport", "Normalize"),
    ("JLVisionNodeSupport.cs", "JLVisionNodeSupport", "CvtColor"),
    ("InferenceNodes.cs", "OnnxOrientedDetectionNode", "PutText"),
    ("ColorClassifyNode.cs", "ColorClassifyNode", "CvtColor"),
    ("ColorClassifyNode.cs", "ColorClassifyNode", "Kmeans"),
    ("ImageDenoiseNode.cs", "ImageDenoiseNode", "FastNlMeansDenoising"),
    ("ImageDenoiseNode.cs", "ImageDenoiseNode", "FastNlMeansDenoisingColored"),
    ("ArucoMarkerNode.cs", "ArucoMarkerNode", "Aruco"),
    ("ArucoMarkerNode.cs", "ArucoMarkerNode", "SolvePnP"),
    ("ArucoMarkerNode.cs", "ArucoMarkerNode", "Rodrigues"),
    ("CharucoBoardNode.cs", "CharucoBoardNode", "Aruco"),
    ("MaskToVisionNodes.cs", "MaskThreshold", "Threshold"),
    ("MaskToVisionNodes.cs", "MaskThreshold", "BitwiseNot"),
    ("MaskToVisionNodes.cs", "MaskThreshold", "InRange"),
    ("MaskToVisionNodes.cs", "RegionFromMaskNode", "ConnectedComponents"),
    ("MaskToVisionNodes.cs", "ContourFromMaskNode", "FindContours"),
    ("MaskToVisionNodes.cs", "ContourFromMaskNode", "ContourArea"),
}


def audit_executor_and_catalog_contracts(ctx: AuditContext) -> dict:
    node_sources = "\n".join(
        p.read_text(encoding="utf-8")
        for p in (ctx.root / "backend/src/VisionStudio.Engine/Nodes").glob("*.cs")
    )
    executor_type_names = set(re.findall(r'public string Type\s*=>\s*"([^"]+)";', node_sources))
    for const_name, const_value in re.findall(r'const string (\w+)\s*=\s*"([^"]+)"', node_sources):
        if re.search(rf"public string Type\s*=>\s*{const_name};", node_sources):
            executor_type_names.add(const_value)
    executor_types = sorted(executor_type_names)

    executor_registry = ctx.read_safe("backend/src/VisionStudio.Api/Hosting/BuiltInNodeExecutors.cs")
    host_registration = ctx.read_safe("backend/src/VisionStudio.Api/Hosting/VisionStudioServiceRegistration.cs")
    if "AddBuiltInNodeExecutors()" not in host_registration:
        ctx.fail("Built-in executors are never registered: services.AddBuiltInNodeExecutors() is missing")
    if "typeof(IVisionNodeExecutor).IsAssignableFrom" not in executor_registry:
        ctx.fail("BuiltInNodeExecutors.Discover() no longer discovers IVisionNodeExecutor implementations by reflection")
    if "TryAddEnumerable" not in executor_registry:
        ctx.fail("BuiltInNodeExecutors no longer registers executors into the IVisionNodeExecutor enumerable")

    catalog = ctx.read_family("backend/src/VisionStudio.Engine/NodeCatalog*.cs")
    for helper in sorted(set(re.findall(r"\b(\w+Catalog)\.Items\b", catalog))):
        catalog += "\n" + ctx.read_engine_source(helper)

    frontend_catalog_source = ctx.read_safe("frontend/src/catalog.ts")
    generated_catalog_doc = json.loads(ctx.read_safe("frontend/src/generated/catalog.generated.json"))
    generated_catalog_items = generated_catalog_doc.get("items", [])
    generated_catalog_types = {x.get("type") for x in generated_catalog_items}

    migration_source = ctx.read_safe("backend/src/VisionStudio.Engine/NodeTypeMigration.cs")
    migrated_types = set(re.findall(r'\["([a-z][\w.]+)"\]\s*=\s*new\(', migration_source))
    for typ in executor_types:
        if typ in migrated_types:
            continue
        if f'"{typ}"' not in catalog:
            ctx.fail(f"Missing backend catalog type: {typ}")
        if typ not in generated_catalog_types:
            ctx.fail(f"Missing generated frontend fallback catalog type: {typ}")
    ctx.ok(f"Built-in executors closed: {len(executor_types)} types ({len(migrated_types)} legacy aliases excluded)")

    declared_parameters = {
        item.get("type"): {p.get("name") for p in item.get("parameters", [])}
        for item in generated_catalog_items
    }
    param_drift: list[str] = []
    for source_path in sorted((ctx.root / "backend/src/VisionStudio.Engine/Nodes").glob("*.cs")):
        blocks = re.split(
            r"(?:^|\n)(?=(?:public|internal)\s+(?:sealed\s+|static\s+|abstract\s+)*class\s)",
            source_path.read_text(encoding="utf-8"),
        )
        for block in blocks:
            node_type = re.search(r'public string Type => "([a-z][\w.]+)"', block)
            if not node_type:
                continue
            type_name = node_type.group(1)
            if type_name in migrated_types:
                continue
            reads = set(re.findall(r'node\.Get\w+\(\s*"([^"]+)"', block))
            undeclared = sorted(reads - declared_parameters.get(type_name, set()))
            if undeclared:
                param_drift.append(f'{type_name} reads undeclared parameter(s): {", ".join(undeclared)}')
    for problem in param_drift:
        ctx.fail(f"Catalog parameter drift: {problem}")
    ctx.ok(f"Catalog parameter contract: {len(declared_parameters)} nodes, no undeclared reads")

    matching_dir = ctx.root / "backend/src/VisionStudio.Engine/Algorithms"
    matching_files = sorted(matching_dir.glob("JLVisionMatching*.cs"))
    if not matching_files:
        ctx.fail("JLVisionMatching*.cs not found; the algorithm-layer parameter contract cannot be checked.")
    algo_matching = "\n".join(path.read_text(encoding="utf-8") for path in matching_files)
    matching_reads = set(re.findall(r'node\.Get\w+\(\s*"([^"]+)"', algo_matching))
    matching_declared = declared_parameters.get("match.shape", set()) | declared_parameters.get("match.template", set())
    matching_drift = sorted(matching_reads - matching_declared)
    if matching_drift:
        ctx.fail(f'JLVisionMatching reads undeclared parameter(s): {", ".join(matching_drift)}')
    elif len(matching_reads) < MATCHING_READ_FLOOR:
        ctx.fail(
            f"JLVisionMatching parameter reads dropped to {len(matching_reads)} (floor {MATCHING_READ_FLOOR}). "
            "继续拆分时漏读了某个 partial，或参数被删；确认后下调本值并在提交说明里写明原因。"
        )
    else:
        ctx.ok(
            f"Algorithm-layer parameter contract: {len(matching_reads)} reads across "
            f"{len(matching_files)} partial file(s) covered by match.shape/match.template"
        )

    algo_reads: set[str] = set()
    for algo_path in sorted((ctx.root / "backend/src/VisionStudio.Engine/Algorithms").glob("*.cs")):
        algo_reads |= set(re.findall(r'node\.Get\w+\(\s*"([^"]+)"', algo_path.read_text(encoding="utf-8")))
    all_declared = set().union(*declared_parameters.values()) if declared_parameters else set()
    orphan_reads = sorted(algo_reads - all_declared)
    if orphan_reads:
        ctx.fail(f'Algorithm layer reads parameter(s) declared by no node: {", ".join(orphan_reads)}')
    else:
        ctx.ok(f"Algorithm layer: {len(algo_reads)} distinct parameter reads all declared in the catalog")

    catalog_by_type = {item.get("type"): item for item in generated_catalog_items}
    default_drift: list[str] = []
    port_type_drift: list[str] = []
    compared_defaults = 0
    compared_text_defaults = 0
    compared_ports = 0

    for source_path in sorted((ctx.root / "backend/src/VisionStudio.Engine/Nodes").glob("*.cs")):
        blocks = re.split(
            r"(?:^|\n)(?=(?:public|internal)\s+(?:sealed\s+|static\s+|abstract\s+)*class\s)",
            source_path.read_text(encoding="utf-8"),
        )
        for block in blocks:
            node_type = NODE_TYPE_RE.search(block)
            if not node_type:
                continue
            type_name = node_type.group(1)
            item = catalog_by_type.get(type_name)
            if item is None:
                continue
            parameter_map = {p.get("name"): p for p in item.get("parameters", [])}
            input_types = {p.get("name"): p.get("dataType") for p in item.get("inputs", [])}

            for match in NUMERIC_FALLBACK_RE.finditer(block):
                name, literal = match.group(1), match.group(2).replace("_", "")
                parameter = parameter_map.get(name)
                if parameter is None:
                    continue
                expected = parameter.get("defaultValue")
                if isinstance(expected, bool) or not isinstance(expected, (int, float, str)):
                    continue
                try:
                    expected_value = float(expected)
                except (TypeError, ValueError):
                    continue
                compared_defaults += 1
                actual_value = float(literal)
                if abs(expected_value - actual_value) > 1e-9:
                    default_drift.append(f"{type_name}.{name}: catalog={expected_value:g} executor={actual_value:g}")

            for match in STRING_FALLBACK_RE.finditer(block):
                name, literal = match.group(1), match.group(2)
                if (type_name, name) in DEFAULT_DRIFT_EXEMPT:
                    continue
                parameter = parameter_map.get(name)
                if parameter is None:
                    continue
                expected = parameter.get("defaultValue")
                if not isinstance(expected, str):
                    continue
                actual = literal.replace('\\"', '"').replace("\\\\", "\\")
                compared_text_defaults += 1
                if expected != actual:
                    default_drift.append(f"{type_name}.{name}: catalog={expected!r} executor={actual!r}")

            for match in BOOL_FALLBACK_RE.finditer(block):
                name = match.group(1)
                if (type_name, name) in DEFAULT_DRIFT_EXEMPT:
                    continue
                parameter = parameter_map.get(name)
                if parameter is None:
                    continue
                expected = parameter.get("defaultValue")
                if not isinstance(expected, bool):
                    continue
                actual = match.group(2) == "true"
                compared_text_defaults += 1
                if expected is not actual:
                    default_drift.append(f"{type_name}.{name}: catalog={expected} executor={actual}")

            for match in REQUIRE_GENERIC_RE.finditer(block):
                expected_type = PORT_TYPE_BY_GENERIC.get(match.group(1).split(".")[-1])
                if expected_type is None:
                    continue
                port = match.group(2)
                compared_ports += 1
                declared = input_types.get(port)
                if declared is None:
                    port_type_drift.append(
                        f"{type_name}.{port}: executor reads it as {expected_type} but the catalog declares no such input"
                    )
                elif declared not in ("Any", expected_type):
                    port_type_drift.append(f"{type_name}.{port}: catalog={declared} executor={expected_type}")

            for match in REQUIRE_IMAGE_RE.finditer(block):
                port = match.group(1)
                compared_ports += 1
                declared = input_types.get(port)
                if declared is None:
                    port_type_drift.append(
                        f"{type_name}.{port}: executor requires an Image but the catalog declares no such input"
                    )
                elif declared not in ("Any", "Image"):
                    port_type_drift.append(f"{type_name}.{port}: catalog={declared} executor=Image")

    for problem in default_drift:
        ctx.fail(f"Catalog/executor default drift: {problem}")
    if not default_drift:
        ctx.ok(f"Parameter defaults: {compared_defaults} numeric and {compared_text_defaults} text/boolean fallbacks match the catalog")
    for problem in port_type_drift:
        ctx.fail(f"Input port type drift: {problem}")
    if not port_type_drift:
        ctx.ok(f"Input port types: {compared_ports} reads match the catalog")

    overlay_ports = [
        item.get("type")
        for item in generated_catalog_items
        if any(port.get("name") == "overlay" for port in item.get("outputs", []))
    ]
    overlay_without_capability = [
        node_type
        for node_type in overlay_ports
        if not (catalog_by_type.get(node_type, {}).get("capabilities") or {}).get("emitsOverlay")
    ]
    for node_type in overlay_without_capability:
        ctx.fail(f"{node_type} publishes an overlay image port but EmitsOverlay is false")
    if not overlay_without_capability:
        ctx.ok(f"Overlay capability: {len(overlay_ports)} overlay image ports all declare EmitsOverlay")

    retired_parameters = {
        "strictMode", "bufferPolicy", "strictId", "defaultPolicy",
        "missingAPolicy", "strictMissingB", "directionPolicy", "reportMode",
        "requireFiniteScores", "outOfRangePolicy",
    }
    diagnostics_level_allowed = {
        "image.dynamicThreshold", "image.difference", "measure.regionIntensity", "image.flatFieldCorrect",
        "measure.caliper", "output.saveImage", "output.exportResult", "output.httpRequest",
        "match.template", "match.shape",
    }
    retry_policy_allowed = {"output.saveImage", "output.exportResult", "output.httpRequest"}

    for item in generated_catalog_items:
        item_type = item.get("type")
        names = {p.get("name") for p in item.get("parameters", [])}
        for name in sorted(names & retired_parameters):
            ctx.fail(f"Retired control parameter: {item_type}.{name}")
        if "diagnosticsLevel" in names and item_type not in diagnostics_level_allowed:
            ctx.fail(f"diagnosticsLevel is not implemented by {item_type}")
        if "retryPolicy" in names and item_type not in retry_policy_allowed:
            ctx.fail(f"retryPolicy is not implemented by {item_type}")
        if "failOnWarning" in names and item_type != "flow.try":
            ctx.fail(f"failOnWarning is runtime-backed only on flow.try, found on {item_type}")
        for parameter in item.get("parameters", []):
            description = (parameter.get("description") or "").strip()
            if not parameter.get("internal") and not description:
                ctx.fail(f'Undocumented user parameter: {item_type}.{parameter.get("name")}')
            if not parameter.get("internal") and re.search(
                r"\bparameter for [a-z0-9_.]+\.", description, re.IGNORECASE
            ):
                ctx.fail(f'Generated placeholder help: {item_type}.{parameter.get("name")}')
        user_parameters = [p for p in item.get("parameters", []) if not p.get("internal")]
        groups = {p.get("group") for p in user_parameters if p.get("group")}
        if len(user_parameters) >= 8 and len(groups) < 2:
            ctx.fail(f"Parameter-heavy node needs at least two groups: {item_type} ({len(user_parameters)} parameters)")
    ctx.ok("Catalog controls: no retired controls, no unbacked diagnostics/retry options, all user parameters documented and grouped")

    filler_markers = (
        "r90-gate", "r85-gate", "AnchorSharedValidators", "RequireTimeoutBudget",
        "completion floor", "__r90", "__r85",
    )
    for source_path in sorted((ctx.root / "backend/src/VisionStudio.Engine").rglob("*.cs")):
        text = source_path.read_text(encoding="utf-8")
        for marker in filler_markers:
            if marker in text:
                ctx.fail(f"Scoring filler in {source_path.relative_to(ctx.root)}: {marker}")
        if '_ = "' in text:
            ctx.fail(f"Discarded string literal (coverage padding) in {source_path.relative_to(ctx.root)}")
    for test_path in sorted((ctx.root / "backend/tests").rglob("*.cs")):
        text = test_path.read_text(encoding="utf-8")
        lowered = text.lower()
        if (
            "_ = nameof(" in text
            or "raise score" in lowered
            or "type-string density" in lowered
            or "audit d (testhits)" in lowered
        ):
            ctx.fail(f"Scoring-oriented test assertion in {test_path.relative_to(ctx.root)}")
        if '_ = "' in text:
            ctx.fail(f"Discarded string literal (coverage padding) in {test_path.relative_to(ctx.root)}")
    ctx.ok("Executor/test sources: no completion-score filler")

    sample_files = sorted((ctx.root / "samples").glob("*-workflow.json"))
    known_types = set(executor_types) | {"math.offset"}
    for p in sample_files:
        data = json.loads(p.read_text(encoding="utf-8"))
        nodes = data.get("nodes", [])
        ids = {n["id"] for n in nodes}
        if len(ids) != len(nodes):
            ctx.fail(f"{p.name}: duplicate node ids")
        for n in nodes:
            if n.get("type") not in known_types:
                ctx.fail(f'{p.name}: unknown node type {n.get("type")}')
        for e in data.get("edges", []):
            if e.get("sourceNodeId") not in ids or e.get("targetNodeId") not in ids:
                ctx.fail(f'{p.name}: edge {e.get("id")} references missing node')
    ctx.ok(f"Workflow sample references: {len(sample_files)}")

    return {
        "executor_types": executor_types,
        "migrated_types": migrated_types,
        "frontend_catalog_source": frontend_catalog_source,
        "generated_catalog_doc": generated_catalog_doc,
        "generated_catalog_items": generated_catalog_items,
        "generated_catalog_types": generated_catalog_types,
        "host_registration": host_registration,
    }


def audit_opencv_baseline(ctx: AuditContext, write_baseline: bool) -> bool:
    opencv_baseline_path = ctx.root / "scripts" / "opencv-call-baseline.json"
    opencv_baseline: set[tuple[str, str, str]] = set()
    if opencv_baseline_path.exists():
        opencv_baseline = {tuple(entry) for entry in json.loads(opencv_baseline_path.read_text(encoding="utf-8"))}
    opencv_baseline_hits: set[tuple[str, str, str]] = set()

    for source in (ctx.root / "backend/src/VisionStudio.Engine/Nodes").glob("*.cs"):
        body = source.read_text(encoding="utf-8-sig")
        scan_body = re.sub(r"/\*.*?\*/", "", body, flags=re.S)
        scan_body = re.sub(r"//[^\n]*", "", scan_body)
        for match in re.finditer(r"\bCv2\.(\w+)", scan_body):
            call = match.group(1)
            enclosing_classes = re.findall(r"\bclass\s+(\w+)", scan_body[:match.start()])
            synthetic_polygon_drawing = (
                source.name == "OpenCvNodes.cs"
                and call == "FillConvexPoly"
                and enclosing_classes
                and enclosing_classes[-1] == "SyntheticImageNode"
            )
            exception = (source.name, enclosing_classes[-1] if enclosing_classes else "", call)
            if (
                call not in ALLOWED_CV_NODE_CALLS
                and not synthetic_polygon_drawing
                and exception not in ALLOWED_CV_NODE_EXCEPTIONS
            ):
                if write_baseline or exception in opencv_baseline:
                    opencv_baseline_hits.add(exception)
                else:
                    ctx.fail(
                        f"OpenCV algorithm call remains in node: {source.name}: Cv2.{call} "
                        "(not registered in scripts/opencv-call-baseline.json)"
                    )
        if "OpenCvSharp.Dnn" in body:
            ctx.fail(f"OpenCV DNN dependency remains in node: {source.name}")

    if write_baseline:
        opencv_baseline_path.write_text(
            json.dumps([list(entry) for entry in sorted(opencv_baseline_hits)], ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        print(f"OpenCV baseline written: {len(opencv_baseline_hits)} call site(s) -> {opencv_baseline_path}")
        print("Other audit results were ignored because --write-opencv-baseline only refreshes the baseline.")
        return True

    stale_baseline = opencv_baseline - opencv_baseline_hits
    if stale_baseline:
        ctx.ok(
            f"OpenCV baseline: {len(stale_baseline)} stale entr(y/ies) no longer present — "
            "remove them from scripts/opencv-call-baseline.json"
        )
    ctx.ok(f"OpenCV baseline: {len(opencv_baseline_hits)} registered call site(s) still awaiting JLVision migration")
    algorithm_project = ctx.read_safe("backend/src/VisionStudio.Engine/VisionStudio.Engine.csproj")
    for package in ["JLVisionLib", "Microsoft.ML.OnnxRuntime"]:
        if f'Include="{package}"' not in algorithm_project:
            ctx.fail(f"Missing algorithm backend dependency: {package}")
    ctx.ok("JLVision traditional algorithms + ONNX Runtime; explicit 3D hand-eye/preview exceptions, no OpenCV DNN fallback")
    return False
