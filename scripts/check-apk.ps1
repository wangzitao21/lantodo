param([string]$Apk = 'release/android/LanTodo.apk')
. "$PSScriptRoot\env.ps1"
$localJdk = Join-Path $ProjectRoot '.tools\jdk'
if (Test-Path -LiteralPath $localJdk) { $env:JAVA_HOME = $localJdk }
$sdkPath = Join-Path $ProjectRoot '.tools\android-sdk'
if (-not (Test-Path -LiteralPath $sdkPath)) { $sdkPath = $env:ANDROID_HOME }
if (-not $sdkPath) { $sdkPath = $env:ANDROID_SDK_ROOT }
if (-not $sdkPath) { throw '请设置 ANDROID_HOME，或准备本机 .tools/android-sdk。' }
$buildTools = Get-ChildItem -LiteralPath (Join-Path $sdkPath 'build-tools') -Directory | Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $buildTools) { throw '请安装 Android SDK Build Tools。' }
$signer = Join-Path $buildTools.FullName 'apksigner.bat'
& $signer verify --verbose --print-certs (Join-Path $ProjectRoot $Apk)
if ($LASTEXITCODE -ne 0) { throw 'APK 签名校验失败。' }
