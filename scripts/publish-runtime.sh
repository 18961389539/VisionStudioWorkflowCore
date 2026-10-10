#!/usr/bin/env bash
# Q09：与 Windows 侧（publish-runtime.ps1）同步的安全发布路径。
# 旧实现在 restore/校验/构建**之前**就 rm -rf 输出目录，任一步失败都会毁掉唯一一份可回滚的
# 旧安装包（若包内仍有 data，还会连带删除数据）。现在的顺序是：
#   预检 → 前端构建 → 暂存发布 → 校验 → 旧包数据迁移（暂存+校验+原子改名）→ 原子切换（保留旧版）
# 任何失败都不会触碰当前输出目录与外部数据根。
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
source "$ROOT/scripts/lib/publish-runtime.sh"

OUT_ARG="${1:-$ROOT/artifacts/runtime-publish}"
DATA_ARG="${VISIONSTUDIO_DATA_ROOT:-$ROOT/artifacts/runtime-data}"
NORMALIZED_PATHS="$(vs_normalize_publish_paths "$ROOT" "$OUT_ARG" "$DATA_ARG")" || exit 1
OUT="${NORMALIZED_PATHS%%$'\n'*}"
VISIONSTUDIO_DATA_ROOT="${NORMALIZED_PATHS#*$'\n'}"
export VISIONSTUDIO_DATA_ROOT
STAGING="${OUT}.staging.$$"
PREVIOUS="${OUT}.previous"

echo "== VisionStudio Runtime Publish =="

# 旧 PREVIOUS 会由事务 helper 原子归档到唯一历史路径；脚本绝不删除它。
if [ -L "$PREVIOUS" ]; then
  echo "ERROR: previous package path '$PREVIOUS' is a symlink and cannot be safely archived." >&2
  exit 1
fi
if [ -e "$STAGING" ] || [ -L "$STAGING" ]; then
  echo "ERROR: staging path '$STAGING' already exists; it is preserved." >&2
  exit 1
fi

cleanup_staging() {
  if [ -d "$STAGING" ] && [ ! -L "$STAGING" ]; then
    vs_remove_owned_tree "$STAGING" "$OUT" || echo "WARNING: could not safely remove owned staging '$STAGING'." >&2
  fi
}
trap cleanup_staging EXIT

# 1) 预检：restore + 生成 catalog 的陈旧性检查。通过之前不删除/覆盖任何东西。
(
  cd "$ROOT/backend"
  dotnet restore VisionStudio.slnx
  dotnet run --project src/VisionStudio.CatalogExporter/VisionStudio.CatalogExporter.csproj --no-restore -- --check "$ROOT/frontend/src/generated/catalog.generated.json"
)

# 2) 前端构建，再把 dist 暂存到 wwwroot（发布输入缓存）。
(
  cd "$ROOT/frontend"
  npm ci --no-audit --no-fund
  npm run build
)
if [ ! -f "$ROOT/frontend/dist/index.html" ]; then
  echo "ERROR: frontend build output is missing index.html." >&2
  exit 1
fi
API_ROOT="$(realpath -e -- "$ROOT/backend/src/VisionStudio.Api")"
API_ROOT="$(realpath -m -- "$API_ROOT")"
WWWROOT_ARG="$API_ROOT/wwwroot"
WWWROOT="$(realpath -m -- "$WWWROOT_ARG")"
if ! vs_path_is_within "$API_ROOT" "$ROOT" || [[ "$API_ROOT" == "$ROOT" ]]; then
  echo "ERROR: API project root '$API_ROOT' escapes repository root '$ROOT'." >&2
  exit 1
fi
if ! vs_path_is_within "$WWWROOT" "$API_ROOT" || [[ "$WWWROOT" == "$API_ROOT" ]]; then
  echo "ERROR: web root '$WWWROOT' escapes API project root '$API_ROOT'." >&2
  exit 1
fi
if [ -L "$WWWROOT_ARG" ]; then
  echo "ERROR: refusing to replace symlinked web root '$WWWROOT'." >&2
  exit 1
fi
rm -rf "$WWWROOT"
mkdir -p "$ROOT/backend/src/VisionStudio.Api/wwwroot"
cp -a "$ROOT/frontend/dist/." "$ROOT/backend/src/VisionStudio.Api/wwwroot/"

# 3) 发布到暂存目录并校验：发布失败绝不触碰当前包；旧版本保留在 "$PREVIOUS" 供回滚。
(
  cd "$ROOT/backend"
  dotnet publish src/VisionStudio.Api/VisionStudio.Api.csproj -c Release --no-restore -o "$STAGING"
)
if [ ! -f "$STAGING/VisionStudio.Api.dll" ]; then
  echo "ERROR: staging publish is missing VisionStudio.Api.dll; the current package is untouched." >&2
  exit 1
fi

# 3.5) 旧包内数据迁移：共享 helper 执行 WAL 拒绝、完整 SHA-256 清单/源稳定性核验，
#      并在空目标上 rmdir 后原子改名；已有数据一律保留并跳过。
vs_migrate_legacy_data "$OUT/data" "$VISIONSTUDIO_DATA_ROOT"

# 4) 事务式切换：失败自动回滚旧目录，绝不留下"旧包已改名、新包未就位"的中间态。
vs_swap_runtime_package "$OUT" "$STAGING" "$PREVIOUS"

echo "Runtime published to: $OUT"
echo "Production data root (outside the package): $VISIONSTUDIO_DATA_ROOT"
echo "Previous version kept at: $PREVIOUS"
echo "Start the runtime with: export VISIONSTUDIO_DATA_ROOT='$VISIONSTUDIO_DATA_ROOT'; scripts/run-runtime.sh"
