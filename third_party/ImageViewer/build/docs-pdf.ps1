[CmdletBinding()]
param(
    [string] $HtmlPath,
    [string] $PdfPath
)

# 用本机 Edge（或 Chrome）的 headless 打印，把 docs/usage.html 输出成 docs/usage.pdf。
# 与 build/docs.py 同源：先跑 docs.py 生成 HTML，再跑本脚本生成 PDF。
# CI 校验随仓库提交的 PDF 和它的来源哈希。

$root = Split-Path -Parent $PSScriptRoot
if (-not $HtmlPath) { $HtmlPath = Join-Path $root 'docs\usage.html' }
if (-not $PdfPath) { $PdfPath = Join-Path $root 'docs\usage.pdf' }

$candidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'),
    (Join-Path $env:ProgramFiles 'Microsoft\Edge\Application\msedge.exe'),
    (Join-Path $env:ProgramFiles 'Google\Chrome\Application\chrome.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Google\Chrome\Application\chrome.exe')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }

if (-not $candidates) {
    throw 'Edge/Chrome not found. Install Microsoft Edge or Google Chrome to build the PDF.'
}

if (-not (Test-Path -LiteralPath $HtmlPath)) {
    throw "HTML not found: $HtmlPath. Run 'python build/docs.py' first."
}

$browser = @($candidates)[0]
$html = (Resolve-Path -LiteralPath $HtmlPath).Path
$pdf = [System.IO.Path]::GetFullPath($PdfPath)
$uri = ([System.Uri]$html).AbsoluteUri
$pdfDirectory = Split-Path -Parent $pdf
if (-not (Test-Path -LiteralPath $pdfDirectory -PathType Container) -or
    (Test-Path -LiteralPath $pdf -PathType Container)) {
    throw "Invalid PDF output path: $pdf"
}
$stagedPdf = Join-Path $pdfDirectory ('.imageviewer-' + [guid]::NewGuid().ToString('N') + '.pdf')
$standardHtml = Join-Path $root 'docs\usage.html'
$standardPdf = Join-Path $root 'docs\usage.pdf'
$stampPath = Join-Path $root 'docs\usage.pdf.source.json'
$stagedStamp = "$stagedPdf.source.json"
$stampManual = $html -eq $standardHtml -and $pdf -eq $standardPdf

function Get-ManualSourceHash {
    $hash = & python (Join-Path $PSScriptRoot 'docs.py') --pdf-source-hash
    if ($LASTEXITCODE -ne 0 -or $hash -cnotmatch '^[0-9a-f]{64}$') {
        throw 'Could not fingerprint the usage manual HTML and images.'
    }
    return $hash
}

Write-Host "Rendering $html"
try {
    if ($stampManual) {
        $sourceHash = Get-ManualSourceHash
    }
    # Edge/Chrome is a GUI executable on Windows. Wait for it before checking the staged output.
    $render = Start-Process -FilePath $browser -WindowStyle Hidden -Wait -PassThru -ArgumentList @(
        '--headless=new', '--disable-gpu', '--no-pdf-header-footer', "--print-to-pdf=`"$stagedPdf`"", $uri
    )
    $renderedFile = Get-Item -LiteralPath $stagedPdf -ErrorAction SilentlyContinue
    if ($render.ExitCode -ne 0 -or -not $renderedFile -or $renderedFile.Length -eq 0) {
        throw "PDF was not written: $pdf (browser exit code: $($render.ExitCode))"
    }

    if ($stampManual) {
        if ((Get-ManualSourceHash) -cne $sourceHash) {
            throw 'The usage manual changed during rendering. Run this script again.'
        }
        $stamp = [ordered]@{
            sourceSha256 = $sourceHash
            pdfSha256 = (Get-FileHash -LiteralPath $stagedPdf -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        [System.IO.File]::WriteAllText($stagedStamp, (($stamp | ConvertTo-Json) + [Environment]::NewLine))
    }

    Move-Item -LiteralPath $stagedPdf -Destination $pdf -Force -ErrorAction Stop
    if ($stampManual) {
        Move-Item -LiteralPath $stagedStamp -Destination $stampPath -Force -ErrorAction Stop
    }
} finally {
    foreach ($stagedFile in @($stagedPdf, $stagedStamp)) {
        if (Test-Path -LiteralPath $stagedFile -PathType Leaf) {
            Remove-Item -LiteralPath $stagedFile -ErrorAction SilentlyContinue
        }
    }
}

$info = Get-Item -LiteralPath $pdf
Write-Host ('PDF: {0} ({1:N0} KB)' -f $info.FullName, ($info.Length / 1KB))
