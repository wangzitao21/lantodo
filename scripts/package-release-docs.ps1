param([string]$OutputRoot = 'release')
$ErrorActionPreference = 'Stop'
$projectPath = Split-Path -Parent $PSScriptRoot
$outputPath = if ([IO.Path]::IsPathRooted($OutputRoot)) { $OutputRoot } else { Join-Path $projectPath $OutputRoot }
New-Item -ItemType Directory -Force $outputPath | Out-Null
$manual = foreach ($relative in @('docs/usage.md','docs/nas.md','docs/sync-review-v1.0.1.md')) {
    Get-Content -LiteralPath (Join-Path $projectPath $relative) -Raw
}
$manualText = $manual -join "`n`n---`n`n"
$manualText = $manualText.Replace('](nas.md)','](#nas-同步成员)').Replace('](nas.md#数据与备份)','](#数据与备份)')
$manualText | Set-Content -LiteralPath (Join-Path $outputPath '使用说明.md') -Encoding utf8
$licenseFiles = @((Join-Path $projectPath 'LICENSE'),(Join-Path $projectPath 'THIRD_PARTY_NOTICES.md'))
$licenseFiles += Get-ChildItem -LiteralPath (Join-Path $projectPath 'docs/licenses') -File | Sort-Object Name | Select-Object -ExpandProperty FullName
$licenseText = foreach ($file in $licenseFiles) {
    "===== $([IO.Path]::GetRelativePath($projectPath,$file)) ====="
    Get-Content -LiteralPath $file -Raw
}
$licenseText -join "`n`n" | Set-Content -LiteralPath (Join-Path $outputPath '许可证与第三方声明.txt') -Encoding utf8
