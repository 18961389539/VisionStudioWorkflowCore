#!/usr/bin/env bash
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../artifacts/runtime-publish" && pwd)"
export ASPNETCORE_ENVIRONMENT=Production
export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://0.0.0.0:5080}"
exec dotnet "$DIR/VisionStudio.Api.dll"
