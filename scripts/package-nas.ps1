param([string]$Output)
$ErrorActionPreference = 'Stop'
$projectPath = Split-Path -Parent $PSScriptRoot
[xml]$properties = Get-Content -LiteralPath (Join-Path $projectPath 'Directory.Build.props') -Raw
if (-not $Output) { $Output = "release/LanTodo-v$($properties.Project.PropertyGroup.InformationalVersion)-NAS.zip" }
$outputPath = if ([IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $projectPath $Output }
New-Item -ItemType Directory -Force (Split-Path -Parent $outputPath) | Out-Null
$files = @('Dockerfile', '.dockerignore', 'compose.yaml', 'global.json', 'Directory.Build.props', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'docs/nas.md')
foreach ($folder in @('src/LanTodo.Core', 'src/LanTodo.Nas')) {
    $files += Get-ChildItem -LiteralPath (Join-Path $projectPath $folder) -File |
        Where-Object { $_.Extension -in @('.cs', '.csproj') } |
        ForEach-Object { $folder + '/' + $_.Name }
}
$files += Get-ChildItem -LiteralPath (Join-Path $projectPath 'docs/licenses') -File | ForEach-Object { 'docs/licenses/' + $_.Name }
$files += Get-ChildItem -LiteralPath (Join-Path $projectPath 'src/LanTodo.Nas/Web') -File | ForEach-Object { 'src/LanTodo.Nas/Web/' + $_.Name }
$stream = [IO.File]::Open($outputPath, [IO.FileMode]::Create)
try {
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($relative in $files) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $projectPath $relative), $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $archive.Dispose() }
}
finally { $stream.Dispose() }
Write-Output "NAS deployment source: $outputPath"
