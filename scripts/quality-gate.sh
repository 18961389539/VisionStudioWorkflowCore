#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RESULTS="$ROOT/artifacts/test-results"
BACKEND_COVERAGE="$RESULTS/backend-coverage"
MIN_LINE_COVERAGE="${MIN_LINE_COVERAGE:-84}"
MIN_BRANCH_COVERAGE="${MIN_BRANCH_COVERAGE:-62}"

SCOPE="All"
SKIP_INSTALL=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --scope)
      SCOPE="${2:-}"
      shift 2
      ;;
    --scope=*)
      SCOPE="${1#*=}"
      shift
      ;;
    --skip-install)
      SKIP_INSTALL=1
      shift
      ;;
    All|Backend|Frontend)
      SCOPE="$1"
      shift
      ;;
    *)
      echo "Unknown option: $1 (expected --scope All|Backend|Frontend or --skip-install)" >&2
      exit 2
      ;;
  esac
done

case "$SCOPE" in
  All|Backend|Frontend) ;;
  *)
    echo "Invalid scope: $SCOPE (expected All, Backend, or Frontend)" >&2
    exit 2
    ;;
esac

RUN_BACKEND=0
RUN_FRONTEND=0
if [[ "$SCOPE" == "All" || "$SCOPE" == "Backend" ]]; then RUN_BACKEND=1; fi
if [[ "$SCOPE" == "All" || "$SCOPE" == "Frontend" ]]; then RUN_FRONTEND=1; fi

echo "== VisionStudio V0.63 Quality Gate (scope=$SCOPE) =="

if [[ "$RUN_BACKEND" -eq 1 ]]; then
  rm -rf "$BACKEND_COVERAGE"
  mkdir -p "$BACKEND_COVERAGE"

  python3 "$ROOT/scripts/static-audit.py"
  python3 "$ROOT/scripts/check-metric-floor.py"
  python3 "$ROOT/scripts/check-size-budget.py"

  if command -v pwsh >/dev/null 2>&1; then
    pwsh -NoProfile -File "$ROOT/scripts/test-publish-data-separation.ps1"
    pwsh -NoProfile -File "$ROOT/scripts/test-data-migration.ps1"
  else
    echo "Skipping PowerShell data migration tests: pwsh is unavailable (Windows CI runs both)." >&2
  fi

  # Q09: the Bash publish path shares the same safety contract (staging, data migration, atomic swap,
  # rollback) and has its own regression suite; it must run wherever the Bash gate runs.
  bash "$ROOT/scripts/test-publish-runtime.sh"

  (
    cd "$ROOT/backend"
    dotnet restore VisionStudio.slnx
    dotnet run --project src/VisionStudio.CatalogExporter/VisionStudio.CatalogExporter.csproj --no-restore -- --check "$ROOT/frontend/src/generated/catalog.generated.json"
    dotnet test VisionStudio.slnx \
      --configuration Release \
      --no-restore \
      --filter "Category!=Soak" \
      --settings coverage.runsettings \
      --collect:"XPlat Code Coverage" \
      --results-directory "$BACKEND_COVERAGE"
  )

  if command -v pwsh >/dev/null 2>&1; then
    pwsh -NoProfile -File "$ROOT/scripts/check-warning-baseline.ps1"
  else
    echo "Skipping PowerShell warning baseline: pwsh is unavailable." >&2
  fi

  python3 "$ROOT/scripts/check-coverage.py" "$BACKEND_COVERAGE" --min-line "$MIN_LINE_COVERAGE" --min-branch "$MIN_BRANCH_COVERAGE"

  if vuln_json="$(dotnet list "$ROOT/backend/VisionStudio.slnx" package --vulnerable --include-transitive --format json 2>/dev/null)"; then
    python3 - "$vuln_json" <<'PY'
import json, sys
raw = sys.argv[1]
start = raw.find("{")
if start < 0:
    print("NuGet vulnerability scan returned no JSON; skipping.")
    sys.exit(0)
data = json.loads(raw[start:])
hits = []
for proj in data.get("projects", []):
    for fw in proj.get("frameworks", []):
        for pkg in (fw.get("topLevelPackages") or []) + (fw.get("transitivePackages") or []):
            for v in pkg.get("vulnerabilities") or []:
                sev = str(v.get("severity", "")).lower()
                if sev in ("high", "critical"):
                    hits.append(f"{pkg.get('id')} {pkg.get('resolvedVersion')} [{sev}] {v.get('advisoryurl')}")
if hits:
    print("NuGet high/critical vulnerabilities found:", file=sys.stderr)
    for h in hits:
        print(f"  - {h}", file=sys.stderr)
    sys.exit(1)
print("NuGet: no high/critical known vulnerabilities.")
PY
  else
    echo "NuGet vulnerability scan unavailable (offline or feed unreachable); skipping." >&2
  fi
fi

if [[ "$RUN_FRONTEND" -eq 1 ]]; then
  (
    cd "$ROOT/frontend"
    if [[ "$SKIP_INSTALL" -eq 1 ]]; then
      if [[ ! -d "$ROOT/frontend/node_modules" ]]; then
        echo "--skip-install requires existing node_modules, which does not exist." >&2
        exit 1
      fi
      echo "[skip] npm ci (reusing existing node_modules; do not pass --skip-install in CI)"
    else
      npm ci --no-audit --no-fund
    fi

    npm audit --audit-level=high
    npm run lint
    npm run check:theme
    npm run test:coverage
    npm run build
    python3 "$ROOT/scripts/check-bundle-budget.py"
  )
fi

echo "Quality Gate PASSED (scope=$SCOPE)"
