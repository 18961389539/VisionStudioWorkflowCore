#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RESULTS="$ROOT/artifacts/test-results"
MIN_LINE_COVERAGE="${MIN_LINE_COVERAGE:-60}"
rm -rf "$RESULTS"
mkdir -p "$RESULTS"
echo "== VisionStudio V0.63 Quality Gate =="
python3 "$ROOT/scripts/static-audit.py"
# The data migration regression tests are PowerShell scripts. Run them where pwsh is
# available; Bash environments without PowerShell cannot execute these Windows CI checks.
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
    --results-directory "$RESULTS"
)
python3 "$ROOT/scripts/check-coverage.py" "$RESULTS" --min-line "$MIN_LINE_COVERAGE"
(
  cd "$ROOT/frontend"
  npm ci --no-audit --no-fund
  npm test
  npm run build
)
echo "Quality Gate PASSED"
