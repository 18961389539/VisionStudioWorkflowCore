#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
echo "== VisionStudio V0.36 Soak Gate =="
cd "$ROOT/backend"
dotnet restore VisionStudio.slnx
dotnet test VisionStudio.slnx --configuration Release --no-restore --filter "Category=Soak"
echo "Soak Gate PASSED"
