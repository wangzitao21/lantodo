$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$Dotnet = Join-Path $ProjectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $Dotnet)) { $Dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_HOME = Join-Path $ProjectRoot '.tools\cli'
$env:NUGET_PACKAGES = Join-Path $ProjectRoot '.tools\nuget'
function Invoke-Dotnet {
    & $Dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "构建或测试失败，退出码：$LASTEXITCODE" }
}
