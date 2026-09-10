param([Parameter(Mandatory=$true)][string]$Serial)
. "$PSScriptRoot\env.ps1"
$adbPath = Join-Path $ProjectRoot '.tools\android-sdk\platform-tools\adb.exe'
if (-not (Test-Path -LiteralPath $adbPath)) {
    $sdkPath = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { $env:ANDROID_SDK_ROOT }
    $adbPath = if ($sdkPath) { Join-Path $sdkPath 'platform-tools/adb.exe' } else { (Get-Command adb -ErrorAction Stop).Source }
}
function Invoke-Adb {
    $output = & $adbPath -s $Serial @args
    if ($LASTEXITCODE -ne 0) { throw "ADB 操作失败：$args" }
    return $output
}
# Run only against an explicitly selected test device. Does not uninstall or clear app data.
Invoke-Adb shell am force-stop app.lantodo.local | Out-Null
Invoke-Adb shell monkey -p app.lantodo.local -c android.intent.category.LAUNCHER 1 | Out-Null
$started = $false
for ($attempt = 0; $attempt -lt 10; $attempt++) {
    Start-Sleep -Seconds 1
    $appPid = & $adbPath -s $Serial shell pidof app.lantodo.local
    if ($appPid) { $started = $true; break }
}
if (-not $started) { throw '应用没有保持运行，请检查该应用的崩溃日志。' }
$ready = $false
for ($attempt = 0; $attempt -lt 10; $attempt++) {
    $dump = Invoke-Adb shell uiautomator dump /sdcard/lantodo-smoke.xml
    if (($dump -join "`n") -match 'dumped to:') {
        [xml]$ui = (Invoke-Adb shell cat /sdcard/lantodo-smoke.xml) -join "`n"
        if ($ui.SelectSingleNode('//node[@package="app.lantodo.local" and @content-desc="添加待办"]')) { $ready = $true; break }
    }
    Start-Sleep -Seconds 1
}
if (-not $ready) { throw '没有到达可操作的待办首页。' }
Write-Output "PASS: $Serial 启动后到达待办首页。"
