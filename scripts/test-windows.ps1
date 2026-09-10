. "$PSScriptRoot\env.ps1"
Push-Location $ProjectRoot
try { Invoke-Dotnet run --project tests/LanTodo.Windows.Layout -c Release -- (Join-Path $ProjectRoot '.tools/test-results/current') }
finally { Pop-Location }
