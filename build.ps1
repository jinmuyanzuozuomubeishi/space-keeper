param([string]$DotnetPath = 'dotnet', [switch]$RecycleIntegration)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
Push-Location $PSScriptRoot
try {
    & $DotnetPath build SpaceKeeper.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    $arguments = @('run', '--project', 'SpaceKeeper.Tests', '-c', 'Release', '--no-build')
    if ($RecycleIntegration) { $arguments += @('--', '--recycle-integration') }
    & $DotnetPath @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Safety checks failed' }
    & $DotnetPath publish SpaceKeeper.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -o dist/SpaceKeeper-win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    Copy-Item README.md,LICENSE -Destination dist/SpaceKeeper-win-x64
    $sdkRoot = Split-Path ((Get-Command $DotnetPath).Source)
    if (Test-Path (Join-Path $sdkRoot 'LICENSE.txt')) { Copy-Item (Join-Path $sdkRoot 'LICENSE.txt') dist/SpaceKeeper-win-x64/DOTNET-LICENSE.txt }
    if (Test-Path (Join-Path $sdkRoot 'ThirdPartyNotices.txt')) { Copy-Item (Join-Path $sdkRoot 'ThirdPartyNotices.txt') dist/SpaceKeeper-win-x64/DOTNET-THIRD-PARTY-NOTICES.txt }
    Write-Host 'Portable app: dist/SpaceKeeper-win-x64/SpaceKeeper.exe'
} finally { Pop-Location }
