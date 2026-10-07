#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${1:-$ROOT/artifacts/runtime-publish}"
echo "== VisionStudio V0.36 Runtime Publish =="
rm -rf "$ROOT/backend/src/VisionStudio.Api/wwwroot" "$OUT"
(
  cd "$ROOT/backend"
  dotnet restore VisionStudio.slnx
  dotnet run --project src/VisionStudio.CatalogExporter/VisionStudio.CatalogExporter.csproj --no-restore -- --check "$ROOT/frontend/src/generated/catalog.generated.json"
)
(
  cd "$ROOT/frontend"
  npm install --no-audit --no-fund
  npm run build
)
mkdir -p "$ROOT/backend/src/VisionStudio.Api/wwwroot"
cp -a "$ROOT/frontend/dist/." "$ROOT/backend/src/VisionStudio.Api/wwwroot/"
(
  cd "$ROOT/backend"
  dotnet publish src/VisionStudio.Api/VisionStudio.Api.csproj -c Release --no-restore -o "$OUT"
)
echo "Runtime published to: $OUT"
