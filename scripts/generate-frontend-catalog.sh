#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet run --project "$ROOT/backend/src/VisionStudio.CatalogExporter/VisionStudio.CatalogExporter.csproj" -- "$ROOT/frontend/src/generated/catalog.generated.json"
