$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $repo 'tmp\emergency-stop'
Push-Location $repo
try {
    # Repository outputs only; no installation or game file changes.
    & dotnet build IronNestFCS.sln --nologo "-p:OutputPath=$stage\"
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & dotnet run --project tests/IronNestFCS.RegressionChecks
    if ($LASTEXITCODE -ne 0) { throw 'Regression checks failed.' }
    Write-Host 'Build and offline checks passed. In-game acceptance is still required.'
}
finally { Pop-Location }
