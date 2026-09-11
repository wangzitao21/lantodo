param([string]$Executable)
. "$PSScriptRoot\env.ps1"
[xml]$properties = Get-Content -LiteralPath (Join-Path $ProjectRoot 'Directory.Build.props') -Raw
if (-not $Executable) {
    $unpacked = Join-Path $ProjectRoot ('.tools/portable-smoke/' + [Guid]::NewGuid().ToString('N'))
    Expand-Archive -LiteralPath (Join-Path $ProjectRoot "release/LanTodo-v$($properties.Project.PropertyGroup.InformationalVersion)-Windows.zip") -DestinationPath $unpacked
    $top=Get-ChildItem -LiteralPath $unpacked
    if($top.Count -ne 1 -or -not $top[0].PSIsContainer){throw 'Windows ZIP 必须只有一个顶层文件夹。'}
    $Executable = Join-Path $top[0].FullName 'LanTodo.exe'
}
$appExe = (Resolve-Path -LiteralPath $(if ([IO.Path]::IsPathRooted($Executable)) { $Executable } else { Join-Path $ProjectRoot $Executable })).Path
$testProfile = Join-Path $ProjectRoot ('.tools/test-results/windows-' + [Guid]::NewGuid().ToString('N'))
$arguments = @('--data-dir', ('"' + $testProfile + '"'))
$first = Start-Process -FilePath $appExe -ArgumentList $arguments -WindowStyle Hidden -PassThru
try {
    Start-Sleep -Seconds 3
    if ($first.HasExited) { throw '首次启动异常退出。' }
    # The same directory with a trailing separator must also activate the existing instance.
    $second = Start-Process -FilePath $appExe -ArgumentList @('--data-dir', ('"' + $testProfile + '/"')) -WindowStyle Hidden -PassThru
    if (-not $second.WaitForExit(6000) -or $second.ExitCode -ne 0) { throw '重复启动未转交现有进程。' }
    if ($first.HasExited) { throw '原进程没有保持运行。' }
    $quit = Start-Process -FilePath $appExe -ArgumentList (@('--quit') + $arguments) -WindowStyle Hidden -PassThru
    if (-not $quit.WaitForExit(6000) -or -not $first.WaitForExit(6000)) { throw '退出没有完整结束进程。' }
    $lockFile = [IO.File]::Open((Join-Path $testProfile 'LanTodo.sqlite'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $lockFile.Dispose()
    $first = Start-Process -FilePath $appExe -ArgumentList $arguments -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 2
    if ($first.HasExited) { throw '退出后重开失败。' }
    $quit = Start-Process -FilePath $appExe -ArgumentList (@('--quit') + $arguments) -WindowStyle Hidden -PassThru
    if (-not $first.WaitForExit(6000)) { throw '重新打开后无法退出。' }
    Write-Output 'PASS: 重复启动转交原进程；完整退出释放数据锁；退出后可以重开。'
}
finally {
    # Only stop this test's explicitly captured process if a regression leaves it stuck.
    if (-not $first.HasExited) { Stop-Process -Id $first.Id }
}
