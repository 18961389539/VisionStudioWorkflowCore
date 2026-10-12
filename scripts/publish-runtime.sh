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

# 3.05) R08/F07：候选包烟雾验证——与 Windows 侧同一契约（候选 contentRoot + Production 环境 +
#       隔离数据根 + 批准的测试配置 + 资产清单 + health + 打包前端 + 插件加载干净）。
#       无法启动或资产不完整的候选包绝不替换当前版本。
SMOKE_ASSETS=("VisionStudio.Api.dll" "appsettings.json" "wwwroot/index.html" "VisionStudio.Plugin.Worker.dll")
for asset in "${SMOKE_ASSETS[@]}"; do
  if [ ! -e "$STAGING/$asset" ]; then
    echo "ERROR: candidate package is missing required asset '$asset'; the current package is untouched." >&2
    exit 1
  fi
done
SMOKE_DATA="$(mktemp -d "${TMPDIR:-/tmp}/visionstudio-smoke-XXXXXX")"
SMOKE_PORT=$(( (RANDOM % 20000) + 20000 ))
SMOKE_PID=""
smoke_stop() {
  if [ -n "$SMOKE_PID" ] && kill -0 "$SMOKE_PID" 2>/dev/null; then
    kill "$SMOKE_PID" 2>/dev/null || true
    wait "$SMOKE_PID" 2>/dev/null || true
  fi
}
trap 'smoke_stop; rm -rf "$SMOKE_DATA"; cleanup_staging' EXIT
(
  cd "$STAGING" || exit 1
  VISIONSTUDIO_DATA_ROOT="$SMOKE_DATA" \
  ASPNETCORE_ENVIRONMENT=Production \
  Security__Enabled=false \
  RobotTcpSimulator__Enabled=false \
  exec dotnet "$STAGING/VisionStudio.Api.dll" --urls "http://127.0.0.1:$SMOKE_PORT" >"$SMOKE_DATA/host.out.log" 2>&1
) &
SMOKE_PID=$!
SMOKE_HEALTHY=0
SMOKE_DEADLINE=$(( SECONDS + 90 ))
while [ "$SECONDS" -lt "$SMOKE_DEADLINE" ]; do
  if ! kill -0 "$SMOKE_PID" 2>/dev/null; then break; fi
  if curl -fsS --max-time 3 "http://127.0.0.1:$SMOKE_PORT/api/health" >/dev/null 2>&1; then SMOKE_HEALTHY=1; break; fi
  sleep 0.5
done
SMOKE_INDEX_OK=0
if [ "$SMOKE_HEALTHY" -eq 1 ]; then
  if curl -fsS --max-time 5 "http://127.0.0.1:$SMOKE_PORT/" 2>/dev/null | grep -q 'id="root"'; then SMOKE_INDEX_OK=1; fi
fi
SMOKE_PLUGIN_FAILURE=0
if [ -f "$SMOKE_DATA/host.out.log" ] && grep -q 'Failed to load VisionStudio plugin package' "$SMOKE_DATA/host.out.log"; then
  SMOKE_PLUGIN_FAILURE=1
fi
smoke_stop
if [ "$SMOKE_HEALTHY" -ne 1 ] || [ "$SMOKE_INDEX_OK" -ne 1 ] || [ "$SMOKE_PLUGIN_FAILURE" -ne 0 ]; then
  echo "ERROR: candidate smoke test FAILED (health=$SMOKE_HEALTHY, frontend=$SMOKE_INDEX_OK, pluginLoadFailure=$SMOKE_PLUGIN_FAILURE). Keeping the current package." >&2
  tail -n 20 "$SMOKE_DATA/host.out.log" 2>/dev/null || true
  exit 1
fi
echo "Candidate package smoke test passed: health + packaged frontend + clean plugin load."
rm -rf "$SMOKE_DATA"

# 3.4) R08/F06：升级前数据库检查点——SQLite Online Backup API 一致性快照 + integrity/schema
#      清单（与 Windows 侧同一工具与契约）；活动写者下也安全。已有数据库时才执行。
LIVE_DB="$VISIONSTUDIO_DATA_ROOT/visionstudio.db"
if [ -f "$LIVE_DB" ]; then
  CHECKPOINT="$VISIONSTUDIO_DATA_ROOT/upgrade-checkpoints/$(date +%Y%m%d%H%M%S)"
  mkdir -p "$CHECKPOINT"
  dotnet run --project "$ROOT/backend/src/VisionStudio.SqliteSnapshot/VisionStudio.SqliteSnapshot.csproj" -c Release -- snapshot "$LIVE_DB" "$CHECKPOINT"
  echo "Pre-upgrade database checkpoint saved to: $CHECKPOINT"
else
  echo "No live database at '$LIVE_DB'; nothing to checkpoint before the upgrade."
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
echo "Rollback (R08/F06): stop the host; restore the previous package ('$PREVIOUS' -> '$OUT'); then"
echo "restore the pre-upgrade database with the maintenance tool (verifies the checkpoint, cleans"
echo "WAL/SHM sidecars and replaces the database as one unit):"
echo "  dotnet run --project backend/src/VisionStudio.SqliteSnapshot -c Release -- restore <checkpointDir> '$VISIONSTUDIO_DATA_ROOT/visionstudio.db'"
echo "Re-verify physical device state afterwards; a data rollback never replaces device reconciliation."
