$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$game = 'F:\SteamLibrary\steamapps\common\Iron Nest Heavy Turret Simulator'
$stage = Join-Path $repo 'artifacts\p1-fix'

Push-Location $repo
try {
    # Build to the repository first; a failed build must not replace installed Logic.
    & dotnet build IronNestFCS.sln --no-restore "-p:OutputPath=$stage\"
    if ($LASTEXITCODE -ne 0) { throw 'Build failed. Nothing installed.' }
    & dotnet run --project tests/IronNestFCS.RegressionChecks --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Regression checks failed. Nothing installed.' }

    $destinations = @{
        'IronNestFCS' = 'Mods'
        'IronNestFCS.Abstractions' = 'UserLibs'
        'IronNestFCS.Logic' = 'UserData\IronNestFCS'
    }
    foreach ($name in $destinations.Keys) {
        $destination = Join-Path $game $destinations[$name]
        foreach ($extension in @('.dll', '.pdb')) {
            $source = Join-Path $stage ($name + $extension)
            if (!(Test-Path $source)) { throw "Missing build output: $source" }
        }
        if (!(Test-Path $destination)) { throw "Missing game directory: $destination" }
    }
    foreach ($name in $destinations.Keys) {
        foreach ($extension in @('.dll', '.pdb')) {
            $source = Join-Path $stage ($name + $extension)
            $destination = Join-Path (Join-Path $game $destinations[$name]) ($name + $extension)
            Copy-Item -LiteralPath $source -Destination $destination -Force
            if ((Get-FileHash $source).Hash -ne (Get-FileHash $destination).Hash) {
                throw "Installed hash mismatch: $destination"
            }
            Write-Host "Installed: $destination"
        }
    }
    Write-Host 'Installed without backups. Restart the game to load the updated host.'
}
finally { Pop-Location }
