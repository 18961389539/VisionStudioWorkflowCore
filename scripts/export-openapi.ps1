# 导出后端 OpenAPI 快照到 frontend/src/generated/openapi.json。
#
# 契约测试（frontend/src/__tests__/apiContract.test.ts）以该快照为准：
# 后端 DTO / 路由变更后重新运行本脚本，并同步更新测试里的棘轮基线。
#
# 前提：先 `dotnet build`（本脚本用 --no-build 启动，避免导出过程触发重编译）。
# 如需生成 TS 类型供新代码使用：
#   npx openapi-typescript frontend/src/generated/openapi.json -o frontend/src/generated/api-types.ts
#
# 2026-10-23 三项修正（此前脚本在本机与 CI 上均会失败或产出不可用快照）：
#  1. 必须绕过系统代理：环境里存在 HTTP_PROXY 时，Invoke-RestMethod 会把 127.0.0.1 的请求
#     也交给代理，返回 502 并表现为"OpenAPI 端点 120s 未响应"。改用 UseProxy=false 的 HttpClient。
#  2. 直接写入后端返回的原始 JSON，不再 ConvertTo-Json -Depth 100 重新序列化——保真且不受深度截断影响。
#  3. 写无 BOM 的 UTF-8：PowerShell 5.1 的 `Set-Content -Encoding utf8` 会写 BOM，
#     使 openapi-typescript / jq / Python json 在首字符解析失败。
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'backend/src/VisionStudio.Api/VisionStudio.Api.csproj'
$output = Join-Path $root 'frontend/src/generated/openapi.json'
$url = 'http://127.0.0.1:5399'

$previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$process = Start-Process dotnet -ArgumentList @('run', '--project', $project, '--no-build', '--urls', $url) -PassThru -WindowStyle Hidden
try {
    # 必须用 WebRequest 并显式 Proxy=$null：Windows PowerShell 5.1 的 Invoke-RestMethod 走
    # IE/WinINET 代理设置，把 127.0.0.1 的请求也交给代理而返回 502
    # （表现为"OpenAPI 端点 120s 未响应"）。curl.exe 在 PS 5.1 下的参数传递不稳定，故不用它。
    $deadline = (Get-Date).AddSeconds(120)
    $json = $null
    $lastError = 'no attempt made'
    while ((Get-Date) -lt $deadline -and -not $json) {
        Start-Sleep -Milliseconds 500
        if ($process.HasExited) {
            throw "API process exited with code $($process.ExitCode) before serving OpenAPI; run 'dotnet build' first."
        }
        try {
            $request = [System.Net.WebRequest]::Create("$url/openapi/v1.json")
            $request.Proxy = $null
            $request.Timeout = 10000
            $response = $request.GetResponse()
            $reader = New-Object System.IO.StreamReader($response.GetResponseStream(), [System.Text.Encoding]::UTF8)
            $candidate = $reader.ReadToEnd()
            $reader.Close()
            $response.Close()
            if ($candidate -and $candidate.Length -gt 10000) { $json = $candidate }
            else { $lastError = "response too short: $($candidate.Length) chars" }
        }
        catch { $lastError = $_.Exception.Message }
    }
    if (-not $json) { throw "OpenAPI endpoint did not respond within 120s (last error: $lastError); is the API build up to date?" }
    # 无 BOM UTF-8：PS 5.1 的 `Set-Content -Encoding utf8` 会写 BOM，
    # 使 openapi-typescript / jq / Python json 在首字符解析失败。
    [System.IO.File]::WriteAllText($output, $json, [System.Text.UTF8Encoding]::new($false))
    Write-Output "OpenAPI snapshot written to $output"
}
finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment
}
