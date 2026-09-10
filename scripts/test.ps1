. "$PSScriptRoot\env.ps1"
Push-Location $ProjectRoot
try { Invoke-Dotnet run --project tests/LanTodo.Tests -c Release }
finally { Pop-Location }
