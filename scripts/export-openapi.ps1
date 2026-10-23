# 导出后端 OpenAPI 快照到 frontend/src/generated/openapi.json。
#
# 契约测试（frontend/src/__tests__/apiContract.test.ts）以该快照为准：
# 后端 DTO / 路由变更后重新运行本脚本，并同步更新测试里的棘轮基线。
#
# 说明：核心域 DTO（RunResult / NodeCatalogItem / DeviceDescriptor 等）目前由
# 端点以匿名对象返回，尚未进入 spec；等相应端点补 Produces<T> 后棘轮会收敛。
# 如需生成 TS 类型供新代码使用：npx openapi-typescript frontend/src/generated/openapi.json -o frontend/src/generated/api-types.ts
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'backend/src/VisionStudio.Api/VisionStudio.Api.csproj'
$output = Join-Path $root 'frontend/src/generated/openapi.json'
$url = 'http://127.0.0.1:5399'

$previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$process = Start-Process dotnet -ArgumentList @('run', '--project', $project, '--no-build', '--urls', $url) -PassThru -WindowStyle Hidden
try {
    $deadline = (Get-Date).AddSeconds(120)
    $spec = $null
    while ((Get-Date) -lt $deadline -and -not $spec) {
        Start-Sleep -Milliseconds 500
        try { $spec = Invoke-RestMethod -Uri "$url/openapi/v1.json" -TimeoutSec 5 } catch { }
    }
    if (-not $spec) { throw 'OpenAPI endpoint did not respond within 120s; is the API build up to date?' }
    $spec | ConvertTo-Json -Depth 100 | Set-Content -Path $output -Encoding utf8
    Write-Output "OpenAPI snapshot written to $output"
}
finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment
}
