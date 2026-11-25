#!/usr/bin/env python3
from __future__ import annotations

import pathlib
import sys

# Guarantee UTF-8 output: the default GBK console encoding on Windows raises UnicodeEncodeError
# and makes the audit fail for the wrong reason.
try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass

from audit.camera_and_hardware import audit_camera_and_hardware
from audit.catalog_contracts import audit_executor_and_catalog_contracts, audit_opencv_node_calls
from audit.code_hygiene import audit_changelog_version, audit_empty_catch_baseline
from audit.context import AuditContext, audit_json_and_xml
from audit.host_and_security import audit_host_and_api_contracts
from audit.storage_and_runtime import audit_storage_and_runtime
from audit.workflow_and_plugins import audit_workflow_and_plugins

ROOT = pathlib.Path(__file__).resolve().parents[1]


def main() -> int:
    ctx = AuditContext(ROOT)

    unknown_args = [arg for arg in sys.argv[1:] if arg != "--write-empty-catch-baseline"]
    if unknown_args:
        print(f"Unknown option(s): {', '.join(unknown_args)}", file=sys.stderr)
        return 2
    if "--write-empty-catch-baseline" in sys.argv:
        audit_empty_catch_baseline(ctx, write_baseline=True)
        return 0

    audit_json_and_xml(ctx)
    catalog_ctx = audit_executor_and_catalog_contracts(ctx)
    host_ctx = audit_host_and_api_contracts(ctx, catalog_ctx)
    audit_storage_and_runtime(ctx, catalog_ctx, host_ctx)
    vendor_appsettings = audit_camera_and_hardware(ctx, catalog_ctx, host_ctx)
    audit_workflow_and_plugins(ctx, catalog_ctx, host_ctx, vendor_appsettings)
    audit_opencv_node_calls(ctx)
    audit_changelog_version(ctx)
    audit_empty_catch_baseline(ctx, write_baseline=False)

    if ctx.errors:
        print("STATIC AUDIT FAILED")
        for err in ctx.errors:
            print(" -", err)
        return 1

    print("STATIC AUDIT PASSED")
    for note in ctx.notes:
        print(" -", note)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
