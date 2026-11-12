<#
.SYNOPSIS
    本地开发：以 Development 环境启动后端 API（前端另开一个终端跑 npm run dev）。

.DESCRIPTION
    为什么要有这个脚本：Production 环境下 Security:AutoLoginAdmin 被硬性拒绝（免密码管理员
    不能随配置继承进入现场），而 appsettings.json 默认 AutoLoginAdmin=true。直接用
    `dotnet run` 而不设环境变量时，宿主按 Production 处理并拒绝启动——这条报错信息里提到的
    run-dev.ps1 就是本文件。

    端口固定 5180：frontend/vite.config.ts 的 /api 与 /hubs 代理指向 localhost:5180。
    改端口时两边必须一起改，否则前端拿到的全是 404。
#>
param(
    [int]$Port = 5180
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = "http://127.0.0.1:$Port"
# 开发数据根与发布数据根分开：调试产生的配方/图片不应该污染 artifacts/runtime-data。
if (-not $env:VISIONSTUDIO_DATA_ROOT) {
    $env:VISIONSTUDIO_DATA_ROOT = Join-Path $root 'artifacts/dev-data'
}
New-Item $env:VISIONSTUDIO_DATA_ROOT -ItemType Directory -Force | Out-Null

Write-Host "VisionStudio dev API: $env:ASPNETCORE_URLS (data: $env:VISIONSTUDIO_DATA_ROOT)" -ForegroundColor Cyan
Write-Host 'Frontend: cd frontend; npm install; npm run dev  (http://localhost:5173)' -ForegroundColor Cyan
dotnet run --project (Join-Path $root 'backend/src/VisionStudio.Api/VisionStudio.Api.csproj') --urls $env:ASPNETCORE_URLS
