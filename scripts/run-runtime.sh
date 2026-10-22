#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
DIR="$ROOT/artifacts/runtime-publish"
export ASPNETCORE_ENVIRONMENT=Production
# R08: default to loopback only; a non-loopback plaintext http:// binding is refused at startup
# unless Security:AllowInsecureRemoteTransport=true (isolated test networks only).
export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://127.0.0.1:5080}"
# Production data must live outside the versioned publish directory, so upgrading to a new publish
# folder never migrates or recreates the database (publish-runtime migrates existing data first):
#   export VISIONSTUDIO_DATA_ROOT=/srv/visionstudio-data
if [ -z "${VISIONSTUDIO_DATA_ROOT:-}" ]; then
  export VISIONSTUDIO_DATA_ROOT="$ROOT/artifacts/runtime-data"
fi
mkdir -p "$VISIONSTUDIO_DATA_ROOT"
# Pin the content root so config/static/data always resolve to the publish directory,
# regardless of the caller's working directory.
exec dotnet "$DIR/VisionStudio.Api.dll" --contentRoot "$DIR"
