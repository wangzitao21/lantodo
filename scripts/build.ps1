param([switch]$Android, [string]$OutputRoot = 'release')
. "$PSScriptRoot\env.ps1"
Push-Location $ProjectRoot
try {
    $staging = Join-Path $ProjectRoot '.tools/build-output'
    Invoke-Dotnet publish src/LanTodo.Windows -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' -o (Join-Path $staging 'windows')
    New-Item -ItemType Directory -Force (Join-Path $OutputRoot 'windows') | Out-Null
    Copy-Item -LiteralPath (Join-Path $staging 'windows/LanTodo.exe') -Destination (Join-Path $OutputRoot 'windows/LanTodo.exe') -Force
    if ($Android) {
        $androidOptions = @()
        $sdkPath = Join-Path $ProjectRoot '.tools\android-sdk'
        $jdkPath = Join-Path $ProjectRoot '.tools\jdk'
        if (Test-Path -LiteralPath $sdkPath) { $androidOptions += "-p:AndroidSdkDirectory=$sdkPath" }
        if (Test-Path -LiteralPath $jdkPath) { $androidOptions += "-p:JavaSdkDirectory=$jdkPath" }
        # Reuse the local release key for upgrades; external builders can explicitly supply their own.
        $signing = @()
        $localKeystore = Join-Path $ProjectRoot '.tools\signing\lantodo.p12'
        $localPassword = Join-Path $ProjectRoot '.tools\signing\password.txt'
        if (-not $env:LANTODO_KEYSTORE -and (Test-Path -LiteralPath $localKeystore) -and (Test-Path -LiteralPath $localPassword)) {
            $env:LANTODO_KEYSTORE = $localKeystore
            $env:LANTODO_KEY_PASSWORD = [IO.File]::ReadAllText($localPassword)
        }
        if ($env:LANTODO_KEYSTORE) {
            if (-not (Test-Path -LiteralPath $env:LANTODO_KEYSTORE)) { throw '找不到 LANTODO_KEYSTORE 指定的密钥文件。' }
            if (-not $env:LANTODO_KEY_PASSWORD) { throw '请通过环境变量 LANTODO_KEY_PASSWORD 提供签名密码。' }
            $keyAlias = if ($env:LANTODO_KEY_ALIAS) { $env:LANTODO_KEY_ALIAS } else { 'lantodo' }
            $signing = @('-p:AndroidKeyStore=true', "-p:AndroidSigningKeyStore=$env:LANTODO_KEYSTORE", "-p:AndroidSigningKeyAlias=$keyAlias", '-p:AndroidSigningStorePass=env:LANTODO_KEY_PASSWORD', '-p:AndroidSigningKeyPass=env:LANTODO_KEY_PASSWORD')
        }
        if (-not $env:LANTODO_KEYSTORE) { throw '发布 Android 升级包需要原发布密钥，请设置 LANTODO_KEYSTORE 和 LANTODO_KEY_PASSWORD。' }
        Invoke-Dotnet publish src/LanTodo.Android -c Release @androidOptions @signing -o (Join-Path $staging 'android')
        New-Item -ItemType Directory -Force (Join-Path $OutputRoot 'android') | Out-Null
        Copy-Item -LiteralPath (Join-Path $staging 'android/app.lantodo.local-Signed.apk') -Destination (Join-Path $OutputRoot 'android/LanTodo.apk') -Force
    }
    Copy-Item -LiteralPath (Join-Path $ProjectRoot 'docs/usage.md') -Destination (Join-Path $OutputRoot '使用说明.md') -Force
    foreach ($name in @('LICENSE','THIRD_PARTY_NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $ProjectRoot $name) -Destination (Join-Path $OutputRoot $name) -Force
    }
    $licenseOutput = Join-Path $OutputRoot 'docs/licenses'
    New-Item -ItemType Directory -Force $licenseOutput | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $ProjectRoot 'docs/licenses') -File | Copy-Item -Destination $licenseOutput -Force
    $releaseFiles = @('windows/LanTodo.exe','android/LanTodo.apk','使用说明.md','LICENSE','THIRD_PARTY_NOTICES.md')
    $releaseFiles += Get-ChildItem -LiteralPath $licenseOutput -File | ForEach-Object { 'docs/licenses/' + $_.Name }
    $hashes = foreach ($relative in $releaseFiles) {
        $file = Join-Path $OutputRoot $relative
        if (Test-Path -LiteralPath $file) { "$((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash)  $relative" }
    }
    $hashes | Set-Content -LiteralPath (Join-Path $OutputRoot 'SHA256SUMS.txt') -Encoding utf8
}
finally { Pop-Location }
