#!/usr/bin/env python3
"""Idempotently (re)apply the JLVision wiring that lives in shared, frequently-edited files.

Why this exists: another editing session rewrites `NodeCatalog.cs` (node descriptions) and can
silently drop the metrology catalog include and the match.template auto-tune parameters.
Run this script after such a rewrite, then regenerate the frontend catalog:

    python scripts/apply-jlvision-wiring.py
    dotnet run --project backend/src/VisionStudio.CatalogExporter -- frontend/src/generated/catalog.generated.json

Every edit is anchored and skipped when already present, so running it twice is harmless.
"""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def patch(relative: str, pairs: list[tuple[str, str]]) -> None:
    path = ROOT / relative
    text = path.read_text(encoding="utf-8")
    changed = 0
    for old, new in pairs:
        if new in text:
            continue
        if old not in text:
            print(f"  ! anchor missing in {relative}: {old.strip().splitlines()[0][:70]}")
            continue
        text = text.replace(old, new, 1)
        changed += 1
    if changed:
        path.write_text(text, encoding="utf-8")
    print(f"  {'patched' if changed else 'ok'} {relative} ({changed})")


def main() -> None:
    print("JLVision wiring:")
    patch(
        "backend/src/VisionStudio.Engine/NodeCatalog.cs",
        [
            (
                "        .. CaliperWidthCatalog.Items,\n        .. ContourThicknessCatalog.Items,",
                "        .. CaliperWidthCatalog.Items,\n        .. MetrologyCatalog.Items,\n        .. ContourThicknessCatalog.Items,",
            ),
            (
                '        .. MetrologyCatalog.Items,',
                '        .. MetrologyCatalog.Items,\n        .. JlVisionOperatorCatalog.Items,',
            ),
            (
                'new("pyramidLevels", "Pyramid Levels (0=auto)", "number", 0, 0, 8, 1, Group: "Search", Description: "0 derives a coarse-to-fine pyramid from the template size. 1 forces single-level search; models are cached by pixel content, levels, angle coverage and polarity."),',
                'new("pyramidLevels", "Pyramid Levels (0=auto)", "number", 0, 0, 8, 1, Group: "Search", Description: "0 derives a coarse-to-fine pyramid from the template size. 1 forces single-level search; models are cached by pixel content, levels, angle coverage and polarity."),\n'
                '                new("parameterSource", "Parameter Source", "select", "manual", Options:\n'
                '                [\n'
                '                    new("Manual (engineer-set values)", "manual"),\n'
                '                    new("Automatic (JLVision determine once per template)", "auto")\n'
                '                ], Group: "Auto Tune", Description: "manual keeps existing NCC recipes unchanged. auto calls determine_ncc_model_params once per template (cached by templatePath+mtime, or by a sampled pixel fingerprint for port templates) and overrides Pyramid Levels and Angle Step before CreateNccModel. The find path is untouched."),\n'
                '                new("autoTuneProfile", "Auto Tune Profile", "select", "balanced", Options:\n'
                '                [\n'
                '                    new("Balanced", "balanced"),\n'
                '                    new("Speed (one level less, coarser step)", "speed"),\n'
                '                    new("Accuracy (one level more, finer step)", "accuracy")\n'
                '                ], Group: "Auto Tune", VisibleWhen: new("parameterSource", ["auto"]), Description: "Post-adjusts the native recommendation inside the pyramid-level budget."),\n'
                '                new("autoTuneLockedParams", "Locked Parameters", "text", "", Group: "Auto Tune", VisibleWhen: new("parameterSource", ["auto"]), Description: "Comma-separated parameters the auto tune must not override: num_levels, angle_step. Locked values keep the node\'s current setting."),',
            ),
        ],
    )
    patch(
        "backend/src/VisionStudio.Api/Endpoints/EndpointMapping.cs",
        [("        app.MapCalibrationEndpoints();", "        app.MapCalibrationEndpoints();\n        app.MapMatchingEndpoints();")],
    )
    patch(
        "backend/src/VisionStudio.Api/Hosting/VisionStudioServiceRegistration.cs",
        [("        services.AddSingleton<CaliperArrayNode>();", "        services.AddSingleton<CaliperArrayNode>();\n        services.AddSingleton<MetrologyNode>();"),
         ("        services.AddSingleton<MetrologyNode>();", "        services.AddSingleton<MetrologyNode>();\n        services.AddSingleton<JLVisionImageOpNode>();")],
    )
    patch(
        "backend/src/VisionStudio.Api/Hosting/BootstrapHostedServices.cs",
        [("            services.GetRequiredService<CaliperArrayNode>(),", "            services.GetRequiredService<CaliperArrayNode>(),\n            services.GetRequiredService<MetrologyNode>(),\n            services.GetRequiredService<JLVisionImageOpNode>(),")],
    )
    patch(
        "backend/src/VisionStudio.Engine/Algorithms/VisionAlgorithmRuntime.cs",
        [('or "measure.contourThickness" or "measure.metrology") return "jlvision";', 'or "measure.contourThickness" or "measure.metrology" or "jlvision.imageOp") return "jlvision";')],
    )
    patch(
        "backend/src/VisionStudio.Engine/Batch5PrecisionCatalog.cs",
        [
            (
                'new("pyramidLevels", "Pyramid Levels", "number", 0, 0, 8, 1, Group: "Training", Description: "0 selects automatic pyramid depth; 1–8 fixes the coarse-to-fine levels used for training and search. More levels can speed up large-image search but may miss small features."),',
                'new("pyramidLevels", "Pyramid Levels", "number", 0, 0, 8, 1, Group: "Training", Description: "0 selects automatic pyramid depth; 1–8 fixes the coarse-to-fine levels used for training and search. More levels can speed up large-image search but may miss small features."),\n'
                '                new("parameterSource", "Parameter Source", "select", "manual", Options:\n'
                '                [\n'
                '                    new("Manual (engineer-set values)", "manual"),\n'
                '                    new("Automatic (JLVision determine at training)", "auto")\n'
                '                ], Group: "Auto Tune", Description: "manual keeps every existing recipe bit-identical. auto calls JLVision determine_shape_model_params on the training ROI and overrides Pyramid Levels, Angle Step and Minimum Feature Contrast at training time; the resolved values are reported in the training summary. The run-time search never calls determine."),\n'
                '                new("autoTuneProfile", "Auto Tune Profile", "select", "balanced", Options:\n'
                '                [\n'
                '                    new("Balanced", "balanced"),\n'
                '                    new("Speed (one level less, coarser step)", "speed"),\n'
                '                    new("Accuracy (one level more, finer step)", "accuracy")\n'
                '                ], Group: "Auto Tune", VisibleWhen: new("parameterSource", ["auto"]), Description: "Post-adjusts the native recommendation inside the pyramid-level budget. Balanced applies the recommendation unchanged."),\n'
                '                new("autoTuneLockedParams", "Locked Parameters", "text", "", Group: "Auto Tune", VisibleWhen: new("parameterSource", ["auto"]), Description: "Comma-separated parameter names the auto tune must not override: num_levels, angle_step, min_contrast. Locked values keep the node\'s current setting."),',
            ),
        ],
    )
    print("Done. Regenerate the frontend catalog afterwards.")


if __name__ == "__main__":
    main()
