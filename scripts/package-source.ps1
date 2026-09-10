param([string]$Output)
$ErrorActionPreference = 'Stop'
$projectPath = Split-Path -Parent $PSScriptRoot
[xml]$properties = Get-Content -LiteralPath (Join-Path $projectPath 'Directory.Build.props') -Raw
$version = $properties.Project.PropertyGroup.InformationalVersion
if (-not $Output) { $Output = "release/v$version/LanTodo-v$version-source.zip" }
$outputPath = if ([IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $projectPath $Output }
$files = @(& git -c core.quotepath=false -C $projectPath ls-files --cached --others --exclude-standard) | Sort-Object -Unique
if ($LASTEXITCODE -ne 0 -or $files.Count -eq 0) { throw '请从有效的 Git 源码仓库运行此脚本。' }
$excluded = '(^|/)(\.git|\.tools|release|bin|obj|backups|revisions|TestResults)/|\.(sqlite[^/]*|db|pfx|p12|pem|key|jks|keystore|apk|aab|exe|zip|log|tmp|binlog)$|(^|/)(\.env(\..*)?|password\.txt|LanTodo\.location)$'
foreach ($file in $files) {
    if ($file -match $excluded -and $file -notmatch '(^|/)\.env\.example$') { throw "源码列表包含非发布文件，请先从 Git 移除：$file" }
}
New-Item -ItemType Directory -Force (Split-Path -Parent $outputPath) | Out-Null
$temporaryPath = $outputPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
try {
    $stream = [IO.File]::Open($temporaryPath, [IO.FileMode]::CreateNew)
    try {
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($relative in $files) {
                $source = Join-Path $projectPath $relative
                if (Test-Path -LiteralPath $source -PathType Leaf) {
                    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $source, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
                }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }
    Move-Item -LiteralPath $temporaryPath -Destination $outputPath -Force
}
finally { if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath } }
Write-Output "Complete source package: $outputPath"
