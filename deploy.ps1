[CmdletBinding()]
param(
    [string]$GameDirectory = 'F:\SteamLibrary\steamapps\common\Iron Nest Heavy Turret Simulator',
    [string]$PackageRoot
)

# 显式部署：把 Release/IronNestFCS 包内的四个 DLL 拷入游戏目录。
# 普通构建（build.ps1）绝不写游戏目录；本脚本必须由用户批准后单独运行。
# 用法：.\deploy.ps1 [-GameDirectory <游戏根目录>] [-PackageRoot <包根目录，默认仓库根\Release\IronNestFCS>]

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$gameRoot = [IO.Path]::GetFullPath($GameDirectory).TrimEnd('\')

if ([string]::IsNullOrWhiteSpace($PackageRoot)) {
    $PackageRoot = Join-Path $repositoryRoot 'Release\IronNestFCS'
}
$packageRoot = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\')

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

Assert-DirectoryExists -Path $gameRoot -Description 'Game directory'
Assert-DirectoryExists -Path (Join-Path $gameRoot 'MelonLoader') -Description 'MelonLoader directory'
Assert-DirectoryExists -Path $packageRoot -Description 'Release package'

$files = @(
    @{ Source = 'Mods\IronNestFCS.dll'; Destination = 'Mods\IronNestFCS.dll' },
    @{ Source = 'UserData\IronNestFCS\IronNestFCS.Logic.dll'; Destination = 'UserData\IronNestFCS\IronNestFCS.Logic.dll' },
    @{ Source = 'UserLibs\IronNestFCS.Abstractions.dll'; Destination = 'UserLibs\IronNestFCS.Abstractions.dll' },
    @{ Source = 'UserLibs\IronNestFCS.Domain.dll'; Destination = 'UserLibs\IronNestFCS.Domain.dll' }
)

foreach ($file in $files) {
    $source = Join-Path $packageRoot $file.Source
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Package artifact was not found: $source"
    }
    $destination = Join-Path $gameRoot $file.Destination
    Write-Host "Deploying:" -ForegroundColor Cyan
    Write-Host "  from:      $source"
    Write-Host "  to:        $destination"
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Force
}

Write-Host 'Deployment complete.' -ForegroundColor Green
