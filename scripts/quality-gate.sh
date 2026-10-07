#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RESULTS="$ROOT/artifacts/test-results"
MIN_LINE_COVERAGE="${MIN_LINE_COVERAGE:-20}"
rm -rf "$RESULTS"
mkdir -p "$RESULTS"
echo "== VisionStudio V0.63 Quality Gate =="
python3 "$ROOT/scripts/static-audit.py"
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
    --results-directory "$RESULTS"
)
python3 "$ROOT/scripts/check-coverage.py" "$RESULTS" --min-line "$MIN_LINE_COVERAGE"
(
  cd "$ROOT/frontend"
  npm install --no-audit --no-fund
  npm run build
)
echo "Quality Gate PASSED"
