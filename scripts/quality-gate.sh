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
    All|Backend|Frontend|E2E)
      SCOPE="$1"
      shift
      ;;
    *)
      echo "Unknown option: $1 (expected --scope All|Backend|Frontend|E2E or --skip-install)" >&2
      exit 2
      ;;
  esac
done

case "$SCOPE" in
  All|Backend|Frontend|E2E) ;;
  *)
    echo "Invalid scope: $SCOPE (expected All, Backend, Frontend, or E2E)" >&2
    exit 2
    ;;
esac

RUN_BACKEND=0
RUN_FRONTEND=0
RUN_E2E=0
if [[ "$SCOPE" == "All" || "$SCOPE" == "Backend" ]]; then RUN_BACKEND=1; fi
if [[ "$SCOPE" == "All" || "$SCOPE" == "Frontend" ]]; then RUN_FRONTEND=1; fi
if [[ "$SCOPE" == "All" || "$SCOPE" == "E2E" ]]; then RUN_E2E=1; fi

echo "== VisionStudio V0.63 Quality Gate (scope=$SCOPE) =="

if [[ "$RUN_BACKEND" -eq 1 ]]; then
  rm -rf "$BACKEND_COVERAGE"
  mkdir -p "$BACKEND_COVERAGE"

  python3 "$ROOT/scripts/static-audit.py"
  (cd "$ROOT" && python3 -m unittest discover -s scripts/tests)
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

if [[ "$RUN_E2E" -eq 1 ]]; then
  if [[ ! -f "$ROOT/frontend/node_modules/@playwright/test/package.json" ]]; then
    echo "E2E: frontend dependencies missing; running npm ci from the lockfile."
    (cd "$ROOT/frontend" && npm ci --no-audit --no-fund)
  fi

  E2E_API="http://127.0.0.1:5199"
  E2E_DATA="$ROOT/artifacts/e2e-data/$(python3 -c 'import uuid; print(uuid.uuid4().hex)')"
  E2E_LOG_DIR="$RESULTS/e2e-api"
  mkdir -p "$E2E_DATA" "$E2E_LOG_DIR"

  (cd "$ROOT/backend" && dotnet build src/VisionStudio.Api/VisionStudio.Api.csproj -c Debug --nologo -v q)
  API_DLL="$(find "$ROOT/backend/src/VisionStudio.Api/bin/Debug" -name 'VisionStudio.Api.dll' -print | head -n 1)"
  if [[ -z "$API_DLL" ]]; then
    echo "E2E smoke: cannot locate VisionStudio.Api.dll after build." >&2
    exit 1
  fi
  API_DIR="$(dirname "$API_DLL")"

  API_PID=""
  cleanup_e2e() {
    if [[ -n "$API_PID" ]] && kill -0 "$API_PID" 2>/dev/null; then
      kill "$API_PID" 2>/dev/null || true
      wait "$API_PID" 2>/dev/null || true
    fi
  }
  trap cleanup_e2e EXIT

  (
    cd "$API_DIR"
    ASPNETCORE_ENVIRONMENT=Development \
    ASPNETCORE_URLS="$E2E_API" \
    VISIONSTUDIO_DATA_ROOT="$E2E_DATA" \
    Security__AutoLoginAdmin=true \
    dotnet "$API_DLL" --urls "$E2E_API" \
      >"$E2E_LOG_DIR/host.out.log" 2>"$E2E_LOG_DIR/host.err.log"
  ) &
  API_PID=$!

  HEALTHY=0
  for _ in $(seq 1 240); do
    if ! kill -0 "$API_PID" 2>/dev/null; then break; fi
    if curl -sf "$E2E_API/healthz" >/dev/null 2>&1; then
      HEALTHY=1
      break
    fi
    sleep 0.5
  done
  if [[ "$HEALTHY" -ne 1 ]]; then
    echo "E2E smoke: backend did not become healthy; host log tail follows." >&2
    tail -n 40 "$E2E_LOG_DIR/host.err.log" 2>/dev/null || true
    tail -n 40 "$E2E_LOG_DIR/host.out.log" 2>/dev/null || true
    exit 1
  fi

  echo "E2E smoke: auth/status = $(curl -sf "$E2E_API/api/auth/status" || true)"

  (
    cd "$ROOT/frontend"
    npx playwright install chromium --no-shell
    export VISIONSTUDIO_E2E_API="$E2E_API"
    export VISIONSTUDIO_E2E_OUTPUT_DIR="${TMPDIR:-/tmp}/visionstudio-e2e-output-$(python3 -c 'import uuid; print(uuid.uuid4().hex)')"
    npm run test:e2e
  )
  cleanup_e2e
  trap - EXIT
fi

echo "Quality Gate PASSED (scope=$SCOPE)"
