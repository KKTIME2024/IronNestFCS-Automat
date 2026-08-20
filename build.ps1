[CmdletBinding()]
param(
    [string]$GameDirectory = 'F:\SteamLibrary\steamapps\common\Iron Nest Heavy Turret Simulator',
    [string]$OutputDirectory
)

# 上游 svr2kos2 735d42e8 移植（去掉了本仓库已删除的 CustomRecords 部分）。
# 用法：.\build.ps1 [-GameDirectory <游戏根目录>] [-OutputDirectory <输出目录，默认仓库根\Release>]

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot 'Release'
}

$repositoryRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$gameRoot = [IO.Path]::GetFullPath($GameDirectory).TrimEnd('\')
$releaseRoot = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
$releaseParent = Split-Path -Parent $releaseRoot
$stagingRoot = "$releaseRoot.tmp.$([Guid]::NewGuid().ToString('N'))"

function Assert-DirectoryExists {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Description does not exist: $Path"
    }
}

function Assert-SafeReleasePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $pathRoot = [IO.Path]::GetPathRoot($Path).TrimEnd('\')
    if ($Path -eq $pathRoot -or $Path -eq $repositoryRoot -or $Path -eq $gameRoot) {
        throw "Refusing to use an unsafe release directory: $Path"
    }
}

function Copy-ReleaseArtifact {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Source,

        [Parameter(Mandatory = $true)]
        [string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Build artifact was not found: $Source"
    }

    $destinationParent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

Assert-SafeReleasePath -Path $releaseRoot
Assert-DirectoryExists -Path $gameRoot -Description 'Game directory'
Assert-DirectoryExists -Path (Join-Path $gameRoot 'MelonLoader') -Description 'MelonLoader directory'

$solutionPath = Join-Path $repositoryRoot 'IronNestFCS.sln'
$buildArguments = @(
    'build'
    $solutionPath
    '--configuration', 'Release'
    '--nologo'
    '--no-incremental'
    "-p:GameDir=$gameRoot"
    "-p:ArtifactsDir=$repositoryRoot\artifacts"
)

Write-Host 'Building IronNestFCS (Release)...' -ForegroundColor Cyan
& dotnet @buildArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}

try {
    New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null

    $fcsRoot = Join-Path $stagingRoot 'IronNestFCS'

    Copy-ReleaseArtifact `
        -Source (Join-Path $repositoryRoot 'artifacts\bin\IronNestFCS\Release\IronNestFCS.dll') `
        -Destination (Join-Path $fcsRoot 'Mods\IronNestFCS.dll')
    Copy-ReleaseArtifact `
        -Source (Join-Path $repositoryRoot 'artifacts\bin\IronNestFCS.Logic\Release\IronNestFCS.Logic.dll') `
        -Destination (Join-Path $fcsRoot 'UserData\IronNestFCS\IronNestFCS.Logic.dll')
    Copy-ReleaseArtifact `
        -Source (Join-Path $repositoryRoot 'artifacts\bin\IronNestFCS.Abstractions\Release\IronNestFCS.Abstractions.dll') `
        -Destination (Join-Path $fcsRoot 'UserLibs\IronNestFCS.Abstractions.dll')
    Copy-ReleaseArtifact `
        -Source (Join-Path $repositoryRoot 'artifacts\bin\IronNestFCS.Domain\Release\IronNestFCS.Domain.dll') `
        -Destination (Join-Path $fcsRoot 'UserLibs\IronNestFCS.Domain.dll')

    New-Item -ItemType Directory -Path $releaseParent -Force | Out-Null
    if (Test-Path -LiteralPath $releaseRoot) {
        Remove-Item -LiteralPath $releaseRoot -Recurse -Force
    }
    Move-Item -LiteralPath $stagingRoot -Destination $releaseRoot
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}

Write-Host "Release artifacts created at: $releaseRoot" -ForegroundColor Green
Get-ChildItem -LiteralPath $releaseRoot -Recurse -File |
    ForEach-Object { $_.FullName.Substring($releaseRoot.Length + 1) } |
    Sort-Object |
    ForEach-Object { Write-Host "  $_" }
