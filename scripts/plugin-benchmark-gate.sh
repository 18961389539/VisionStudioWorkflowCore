#!/usr/bin/env bash
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SPEC="${1:-}"
SERVER="${VISIONSTUDIO_BENCH_SERVER:-http://127.0.0.1:5080}"
OUTPUT="${VISIONSTUDIO_BENCH_OUTPUT:-artifacts/plugin-benchmark-result.json}"
JUNIT="${VISIONSTUDIO_BENCH_JUNIT:-artifacts/plugin-benchmark-junit.xml}"
TIMEOUT="${VISIONSTUDIO_BENCH_TIMEOUT_SECONDS:-3600}"
if [[ -z "$SPEC" ]]; then
  echo "usage: $0 <ci-spec.json>" >&2
  exit 12
fi

dotnet run --project "$ROOT/backend/src/VisionStudio.Benchmark.Cli/VisionStudio.Benchmark.Cli.csproj" --configuration Release -- \
  --server "$SERVER" \
  --spec "$SPEC" \
  --output "$OUTPUT" \
  --junit "$JUNIT" \
  --timeout-seconds "$TIMEOUT"
exit $?
