#!/usr/bin/env bash
# Shared, production-used path and transaction helpers for publish-runtime.sh.

vs_path_is_within() {
  local path="$1" base="$2"
  [[ "$path" == "$base" || "$path" == "$base"/* ]]
}

vs_normalize_publish_paths() {
  local root_arg="$1" out_arg="$2" data_arg="$3"
  local root allowed out data
  root="$(realpath -e -- "$root_arg")" || return 1
  allowed="$(realpath -m -- "$root/artifacts")" || return 1
  out="$(realpath -m -- "$out_arg")" || return 1
  data="$(realpath -m -- "$data_arg")" || return 1

  local out_leaf="${out_arg%/}" data_leaf="${data_arg%/}"
  if [[ -L "$out_leaf" || -L "$data_leaf" ]]; then
    echo 'ERROR: output and data roots cannot be symlinks.' >&2
    return 1
  fi
  if ! vs_path_is_within "$out" "$allowed" || [[ "$out" == "$allowed" ]]; then
    echo "ERROR: output must be a directory inside '$allowed' (got '$out_arg')." >&2
    return 1
  fi
  if [[ "$data" == / || "$data" == "$root" || "$data" == "$out" ]] \
    || vs_path_is_within "$data" "$out" || vs_path_is_within "$out" "$data"; then
    echo "ERROR: output '$out' and data root '$data' must be separate, non-overlapping directories." >&2
    return 1
  fi

  printf '%s\n%s\n' "$out" "$data"
}

vs_manifest_tree() {
  local root="$1"
  (
    cd "$root"
    find . -type f -print0 | LC_ALL=C sort -z | xargs -0 -r sha256sum
  )
}

vs_tree_has_symlink() {
  local root="$1"
  find "$root" -type l -print -quit | grep -q .
}

vs_tree_has_nonempty_sqlite_journal() {
  local root="$1"
  find "$root" -type f \( -name '*-wal' -o -name '*-journal' \) -size +0c -print -quit | grep -q .
}

vs_remove_owned_tree() {
  local path="$1" expected_parent="$2"
  local normalized parent
  [[ "$path" == /* && "$expected_parent" == /* ]] || return 1
  normalized="$(realpath -m -- "$path")" || return 1
  parent="$(realpath -m -- "$expected_parent")" || return 1
  [[ "$normalized" == "$path" && "$normalized" == "$parent".* ]] || return 1
  [[ ! -L "$path" && -d "$path" ]] || return 1
  rm -rf -- "$path"
}

vs_migrate_legacy_data() {
  local src="$1" dst="$2"
  [[ -d "$src" ]] || return 0
  [[ ! -L "$src" && "$src" == /* && "$dst" == /* ]] || {
    echo 'ERROR: migration source and destination must be absolute, non-symlink paths.' >&2
    return 1
  }
  if vs_tree_has_symlink "$src"; then
    echo "ERROR: legacy data '$src' contains symlinks; migration is refused to keep the source boundary intact." >&2
    return 1
  fi
  if vs_tree_has_nonempty_sqlite_journal "$src"; then
    echo "ERROR: legacy data '$src' contains a non-empty SQLite WAL/journal; stop the writer and retry migration." >&2
    return 1
  fi
  if [[ -L "$dst" ]]; then
    echo "ERROR: data root '$dst' cannot be a symlink." >&2
    return 1
  fi
  if [[ -e "$dst" && ! -d "$dst" ]]; then
    echo "ERROR: data root '$dst' exists and is not a directory." >&2
    return 1
  fi
  if [[ -d "$dst" ]] && find "$dst" -mindepth 1 -print -quit | grep -q .; then
    echo "Data root '$dst' already contains data; legacy migration was not marked complete and source '$src' remains unchanged." >&2
    return 0
  fi

  local staging src_before src_after copy_manifest
  mkdir -p -- "$(dirname "$dst")"
  staging="$(mktemp -d "${dst}.migration.XXXXXX")"
  src_before="$(vs_manifest_tree "$src" | sha256sum)" || {
    vs_remove_owned_tree "$staging" "$dst" || true
    return 1
  }
  if ! cp -a -- "$src/." "$staging/"; then
    echo "ERROR: data migration copy failed; '$src' is preserved." >&2
    vs_remove_owned_tree "$staging" "$dst" || true
    return 1
  fi
  if vs_tree_has_symlink "$src" || vs_tree_has_symlink "$staging"; then
    echo "ERROR: a symlink appeared in the legacy data tree during copy; '$src' is preserved." >&2
    vs_remove_owned_tree "$staging" "$dst" || true
    return 1
  fi
  if vs_tree_has_nonempty_sqlite_journal "$src" || vs_tree_has_nonempty_sqlite_journal "$staging"; then
    echo "ERROR: a non-empty SQLite WAL/journal appeared during migration; '$src' is preserved." >&2
    vs_remove_owned_tree "$staging" "$dst" || true
    return 1
  fi
  copy_manifest="$(vs_manifest_tree "$staging" | sha256sum)" || {
    echo "ERROR: copied data manifest could not be read; '$src' is preserved." >&2
    vs_remove_owned_tree "$staging" "$dst" || true
    return 1
  }
  if [[ "$src_before" != "$copy_manifest" ]]; then
    echo "ERROR: data migration hash manifest mismatch; '$src' is preserved." >&2
    vs_remove_owned_tree "$staging" "$dst" || true
    return 1
  fi
  src_after="$(vs_manifest_tree "$src" | sha256sum)" || {
    vs_remove_owned_tree "$staging" "$dst" || true
    return 1
  }
  if [[ "$src_before" != "$src_after" ]]; then
    echo "ERROR: source data changed during migration; '$src' is preserved." >&2
    vs_remove_owned_tree "$staging" "$dst" || true
    return 1
  fi

  : > "$staging/.migration-complete"
  if [[ -d "$dst" ]]; then
    if ! rmdir -- "$dst"; then
      echo "ERROR: data root '$dst' became non-empty during migration; source and destination are preserved." >&2
      vs_remove_owned_tree "$staging" "$dst" || true
      return 1
    fi
  fi
  if ! mv -- "$staging" "$dst"; then
    echo "ERROR: atomic data directory rename failed; '$src' is preserved." >&2
    vs_remove_owned_tree "$staging" "$dst" || true
    return 1
  fi
  echo "Legacy in-package data migrated into '$dst' (staged, SHA-256 verified, atomically renamed)." >&2
}

vs_swap_runtime_package() {
  local out="$1" staging="$2" previous="$3" move_command="${4:-mv}"
  [[ "$out" == /* && "$staging" == /* && "$previous" == /* ]] || return 1
  [[ ! -L "$out" && ! -L "$staging" && ! -L "$previous" ]] || {
    echo 'ERROR: package transaction paths cannot be symlinks.' >&2
    return 1
  }
  [[ -d "$staging" ]] || { echo "ERROR: staging package '$staging' is missing." >&2; return 1; }
  local archived_previous="" swapped=0
  if [[ -e "$previous" ]]; then
    local archive_stamp archive_candidate
    archive_stamp="$(date -u +%Y%m%dT%H%M%SZ)"
    archive_candidate="${previous}.archive.${archive_stamp}.$$"
    local suffix=0
    while [[ -e "$archive_candidate" || -L "$archive_candidate" ]]; do
      suffix=$((suffix + 1))
      archive_candidate="${previous}.archive.${archive_stamp}.$$.${suffix}"
    done
    if ! "$move_command" -- "$previous" "$archive_candidate"; then
      echo "ERROR: could not preserve prior archive '$previous'; package output is untouched." >&2
      return 1
    fi
    archived_previous="$archive_candidate"
  fi
  if [[ -e "$out" ]]; then
    [[ -d "$out" ]] || { echo "ERROR: output '$out' is not a directory." >&2; return 1; }
    if ! "$move_command" -- "$out" "$previous"; then
      if [[ -n "$archived_previous" && ! -e "$previous" ]]; then
        mv -- "$archived_previous" "$previous" || echo "WARNING: preserved earlier previous package at '$archived_previous'." >&2
      fi
      return 1
    fi
    swapped=1
  fi
  if ! "$move_command" -- "$staging" "$out"; then
    echo 'ERROR: package swap failed; rolling back to the previous directory.' >&2
    if [[ "$swapped" -eq 1 && -d "$previous" && ! -e "$out" ]]; then
      if ! mv -- "$previous" "$out"; then
        echo "ERROR: rollback failed; old package remains at '$previous'." >&2
      fi
    fi
    if [[ -n "$archived_previous" ]]; then
      echo "Earlier previous package was preserved at '$archived_previous'." >&2
    fi
    return 1
  fi
  if [[ -n "$archived_previous" ]]; then
    echo "Earlier previous package archived at: $archived_previous" >&2
  fi
}
