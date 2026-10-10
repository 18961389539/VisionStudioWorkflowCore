#!/usr/bin/env bash
# Regression tests for the exact helper functions used by publish-runtime.sh.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
source "$ROOT/scripts/lib/publish-runtime.sh"

SANDBOX="$ROOT/artifacts/iteration-75/bash-sandbox"
EXPECTED_SANDBOX="$(realpath -m -- "$ROOT/artifacts/iteration-75/bash-sandbox")"
RESOLVED_SANDBOX="$(realpath -m -- "$SANDBOX")"
if [[ "$RESOLVED_SANDBOX" != "$EXPECTED_SANDBOX" || "$RESOLVED_SANDBOX" != "$ROOT"/artifacts/iteration-75/bash-sandbox ]]; then
  echo "ERROR: test sandbox path failed its absolute-path guard: '$RESOLVED_SANDBOX'." >&2
  exit 1
fi
mkdir -p -- "$SANDBOX"
RUN_ROOT="$SANDBOX/run-$(date -u +%Y%m%dT%H%M%SZ)-$$"
mkdir -- "$RUN_ROOT"
RUN_ROOT="$(realpath -e -- "$RUN_ROOT")"
case "$RUN_ROOT" in "$EXPECTED_SANDBOX"/*) ;; *) echo 'ERROR: run root escaped the sandbox.' >&2; exit 1 ;; esac

passed=0
failures=0
check() {
  local condition="$1" message="$2"
  if eval "$condition"; then
    printf 'PASS: %s\n' "$message"
    passed=$((passed + 1))
  else
    printf 'FAIL: %s\n' "$message" >&2
    failures=$((failures + 1))
  fi
}

mkdir -p -- "$RUN_ROOT/path-check"
paths="$(vs_normalize_publish_paths "$ROOT" "$RUN_ROOT/path-check/./nested/../out" "$RUN_ROOT/path-check/./data")"
normalized_out="${paths%%$'\n'*}"
normalized_data="${paths#*$'\n'}"
check '[[ "$normalized_out" == "$RUN_ROOT/path-check/out" && "$normalized_data" == "$RUN_ROOT/path-check/data" ]]' 'relative paths and dot segments normalize to absolute paths'
if vs_normalize_publish_paths "$ROOT" "$ROOT/artifacts/../outside" "$RUN_ROOT/path-check/data" >/dev/null 2>&1; then
  check false 'output traversal outside artifacts is rejected'
else
  check true 'output traversal outside artifacts is rejected'
fi
if vs_normalize_publish_paths "$ROOT" "$RUN_ROOT/path-check/out" "$RUN_ROOT/path-check/out/../out/data" >/dev/null 2>&1; then
  check false 'data root nested inside package is rejected after normalization'
else
  check true 'data root nested inside package is rejected after normalization'
fi
mkdir -p -- "$RUN_ROOT/path-check/symlink-target"
printf 'must-stay' > "$RUN_ROOT/path-check/symlink-target/sentinel"
if ln -s "$RUN_ROOT/path-check/symlink-target" "$RUN_ROOT/path-check/output-link" 2>/dev/null \
  && [[ -L "$RUN_ROOT/path-check/output-link" ]]; then
  if vs_normalize_publish_paths "$ROOT" "$RUN_ROOT/path-check/output-link" "$RUN_ROOT/path-check/data-link" >/dev/null 2>&1; then
    check false 'symlinked output root is rejected'
  else
    check true 'symlinked output root is rejected'
  fi
  if vs_remove_owned_tree "$RUN_ROOT/path-check/output-link" "$RUN_ROOT/path-check/output" 2>/dev/null; then
    check false 'cleanup refuses symlinked staging paths'
  else
    check true 'cleanup refuses symlinked staging paths'
  fi
  check '[[ -L "$RUN_ROOT/path-check/output-link" && "$(cat "$RUN_ROOT/path-check/symlink-target/sentinel")" == must-stay ]]' 'symlink cleanup guard leaves its target untouched'
else
  echo 'SKIP: Git Bash host did not create a real symlink for the symlink-safety test.'
fi

SRC="$RUN_ROOT/legacy"
DST="$RUN_ROOT/data"
mkdir -p -- "$SRC" "$DST"
printf 'recipe-a\n' > "$SRC/recipes.json"
printf 'image-bytes\n' > "$SRC/image.bin"
vs_migrate_legacy_data "$SRC" "$DST"
check '[[ -f "$DST/recipes.json" && -f "$DST/image.bin" && -f "$DST/.migration-complete" && ! -e "$DST/legacy" ]]' 'pre-created empty destination is atomically replaced without nesting'

SRC_EXISTING="$RUN_ROOT/legacy-existing"
DST_EXISTING="$RUN_ROOT/data-existing"
mkdir -p -- "$SRC_EXISTING" "$DST_EXISTING"
printf 'new\n' > "$SRC_EXISTING/recipe.json"
printf 'keep\n' > "$DST_EXISTING/recipe.json"
vs_migrate_legacy_data "$SRC_EXISTING" "$DST_EXISTING"
check '[[ "$(cat "$DST_EXISTING/recipe.json")" == keep && ! -e "$DST_EXISTING/.migration-complete" ]]' 'existing destination data is never overwritten'

SRC_CORRUPT="$RUN_ROOT/legacy-corrupt"
DST_CORRUPT="$RUN_ROOT/data-corrupt"
mkdir -p -- "$SRC_CORRUPT" "$DST_CORRUPT"
printf 'abc' > "$SRC_CORRUPT/value.txt"
corrupting_cp() {
  command cp "$@"
  local destination="${@: -1}"
  printf 'xyz' > "$destination/value.txt"
}
cp() { corrupting_cp "$@"; }
if vs_migrate_legacy_data "$SRC_CORRUPT" "$DST_CORRUPT"; then
  failures=$((failures + 1)); echo 'FAIL: same-size content corruption must fail manifest verification.' >&2
else
  passed=$((passed + 1)); echo 'PASS: same-size content corruption fails SHA-256 manifest verification.'
fi
unset -f cp corrupting_cp
check '[[ ! -e "$DST_CORRUPT/value.txt" && "$(cat "$SRC_CORRUPT/value.txt")" == abc ]]' 'manifest failure preserves source and leaves destination empty'

SRC_WAL="$RUN_ROOT/legacy-wal"
DST_WAL="$RUN_ROOT/data-wal"
mkdir -p -- "$SRC_WAL"
printf 'pending transaction' > "$SRC_WAL/camera.db-wal"
if vs_migrate_legacy_data "$SRC_WAL" "$DST_WAL"; then
  failures=$((failures + 1)); echo 'FAIL: non-empty WAL must block migration.' >&2
else
  passed=$((passed + 1)); echo 'PASS: non-empty WAL blocks migration.'
fi
check '[[ ! -e "$DST_WAL" && -f "$SRC_WAL/camera.db-wal" ]]' 'WAL refusal leaves source intact and does not create destination'

SRC_CHANGE="$RUN_ROOT/legacy-changing"
DST_CHANGE="$RUN_ROOT/data-changing"
mkdir -p -- "$SRC_CHANGE" "$DST_CHANGE"
printf 'before' > "$SRC_CHANGE/value.txt"
changing_cp() {
  command cp "$@"
  printf 'after!' > "$SRC_CHANGE/value.txt"
}
cp() { changing_cp "$@"; }
if vs_migrate_legacy_data "$SRC_CHANGE" "$DST_CHANGE"; then
  failures=$((failures + 1)); echo 'FAIL: a source change during copy must fail migration.' >&2
else
  passed=$((passed + 1)); echo 'PASS: source change during copy fails the before/after manifest check.'
fi
unset -f cp changing_cp
check '[[ ! -e "$DST_CHANGE/value.txt" ]]' 'source-change refusal does not publish a partial destination'

OUT_FAULT="$RUN_ROOT/out-fault"
STAGE_FAULT="$RUN_ROOT/stage-fault"
PREVIOUS_FAULT="$RUN_ROOT/previous-fault"
DATA_FAULT="$RUN_ROOT/data-fault"
mkdir -p -- "$OUT_FAULT/data" "$STAGE_FAULT" "$DATA_FAULT"
printf 'old-package' > "$OUT_FAULT/VisionStudio.Api.dll"
printf 'new-package' > "$STAGE_FAULT/VisionStudio.Api.dll"
printf 'active-journal' > "$OUT_FAULT/data/vision.db-journal"
if vs_migrate_legacy_data "$OUT_FAULT/data" "$DATA_FAULT"; then
  failures=$((failures + 1)); echo 'FAIL: migration fault must block package swap.' >&2
else
  passed=$((passed + 1)); echo 'PASS: migration fault is reported before package swap.'
fi
check '[[ "$(cat "$OUT_FAULT/VisionStudio.Api.dll")" == old-package && ! -e "$PREVIOUS_FAULT" ]]' 'pre-swap migration fault preserves the current package'

OUT_SWAP="$RUN_ROOT/out-swap"
STAGE_SWAP="$RUN_ROOT/stage-swap"
PREVIOUS_SWAP="$RUN_ROOT/previous-swap"
mkdir -p -- "$OUT_SWAP" "$STAGE_SWAP"
printf 'old-package' > "$OUT_SWAP/VisionStudio.Api.dll"
printf 'new-package' > "$STAGE_SWAP/VisionStudio.Api.dll"
SWAP_STAGING="$STAGE_SWAP"
fail_stage_move() {
  if [[ "${2:-}" == "$SWAP_STAGING" ]]; then return 1; fi
  command mv "$@"
}
if vs_swap_runtime_package "$OUT_SWAP" "$STAGE_SWAP" "$PREVIOUS_SWAP" fail_stage_move; then
  failures=$((failures + 1)); echo 'FAIL: injected new-package rename failure should fail the transaction.' >&2
else
  passed=$((passed + 1)); echo 'PASS: injected package move failure is reported.'
fi
check '[[ -f "$OUT_SWAP/VisionStudio.Api.dll" && "$(cat "$OUT_SWAP/VisionStudio.Api.dll")" == old-package && ! -e "$PREVIOUS_SWAP" ]]' 'failed package swap rolls the previous package back into place'

OUT_PREVIOUS="$RUN_ROOT/out-previous"
STAGE_PREVIOUS="$RUN_ROOT/stage-previous"
PREVIOUS="$RUN_ROOT/out-previous.previous"
mkdir -p -- "$OUT_PREVIOUS" "$STAGE_PREVIOUS" "$PREVIOUS/unique-data"
printf 'old-package' > "$OUT_PREVIOUS/VisionStudio.Api.dll"
printf 'new-package' > "$STAGE_PREVIOUS/VisionStudio.Api.dll"
printf 'only-copy' > "$PREVIOUS/unique-data/keep.bin"
vs_swap_runtime_package "$OUT_PREVIOUS" "$STAGE_PREVIOUS" "$PREVIOUS"
archives=("${PREVIOUS}.archive."*)
check '[[ "$(cat "$OUT_PREVIOUS/VisionStudio.Api.dll")" == new-package && "$(cat "${archives[0]}/unique-data/keep.bin")" == only-copy ]]' 'existing previous package is archived intact while publish succeeds'

STAGE_PREVIOUS_2="$RUN_ROOT/stage-previous-2"
mkdir -p -- "$STAGE_PREVIOUS_2"
printf 'third-package' > "$STAGE_PREVIOUS_2/VisionStudio.Api.dll"
vs_swap_runtime_package "$OUT_PREVIOUS" "$STAGE_PREVIOUS_2" "$PREVIOUS"
archives=("${PREVIOUS}.archive."*)
check '[[ "$(cat "$OUT_PREVIOUS/VisionStudio.Api.dll")" == third-package && "$(cat "$PREVIOUS/VisionStudio.Api.dll")" == new-package && ${#archives[@]} -eq 2 ]]' 'two consecutive package swaps succeed and retain both earlier previous directories'

OUT_PREV_FAIL="$RUN_ROOT/out-previous-fail"
STAGE_PREV_FAIL="$RUN_ROOT/stage-previous-fail"
PREV_FAIL="$RUN_ROOT/out-previous-fail.previous"
mkdir -p -- "$OUT_PREV_FAIL" "$STAGE_PREV_FAIL" "$PREV_FAIL/unique-data"
printf 'rollback-package' > "$OUT_PREV_FAIL/VisionStudio.Api.dll"
printf 'broken-package' > "$STAGE_PREV_FAIL/VisionStudio.Api.dll"
printf 'archive-me' > "$PREV_FAIL/unique-data/keep.bin"
SWAP_STAGING="$STAGE_PREV_FAIL"
if vs_swap_runtime_package "$OUT_PREV_FAIL" "$STAGE_PREV_FAIL" "$PREV_FAIL" fail_stage_move; then
  failures=$((failures + 1)); echo 'FAIL: injected swap failure with an existing previous package must fail.' >&2
else
  passed=$((passed + 1)); echo 'PASS: swap failure with an existing previous package is reported.'
fi
archived_failures=("${PREV_FAIL}.archive."*)
check '[[ "$(cat "${archived_failures[0]}/unique-data/keep.bin")" == archive-me && "$(cat "$OUT_PREV_FAIL/VisionStudio.Api.dll")" == rollback-package ]]' 'failed swap rolls current package back and preserves archived previous data'

echo "Test sandbox: $RUN_ROOT"
echo "Results: $passed passed, $failures failed"
if (( failures > 0 )); then exit 1; fi
