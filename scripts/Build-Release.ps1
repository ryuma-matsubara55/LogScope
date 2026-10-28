[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.1.0',
    [string]$ReleaseNotes
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    $output = Join-Path $projectRoot "artifacts\releases\$Version"
    # A fresh publish directory prevents deleted files from leaking into a later release.
    $publish = Join-Path $projectRoot ("artifacts\publish\" + [Guid]::NewGuid().ToString('N'))
    $settings = Get-Content src\LogScope.Wpf\distribution.json -Raw | ConvertFrom-Json
    if ($settings.repositoryUrl -notmatch '^https://github\.com/[^/]+/[^/]+$') { throw 'distribution.json: invalid repositoryUrl' }
    if (Test-Path (Join-Path $output 'releases.win.json')) { throw "Release $Version already exists. Use a new version or move the old output first." }
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Tool restore failed.' }
    dotnet publish src\LogScope.Wpf\LogScope.Wpf.csproj -c Release -r win-x64 --self-contained true -o $publish "-p:Version=$Version" -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    $packArgs = @('pack', '--packId', 'LogScope.Desktop', '--packVersion', $Version,
        '--packDir', $publish, '--mainExe', 'LogScope.Wpf.exe', '--packTitle', 'LogScope',
        '--runtime', 'win-x64', '--channel', 'win', '--outputDir', $output, '--noPortable')
    if ($ReleaseNotes) { $packArgs += @('--releaseNotes', (Resolve-Path -LiteralPath $ReleaseNotes).Path) }
    dotnet vpk @packArgs
    if ($LASTEXITCODE -ne 0) { throw 'Packaging failed.' }
    $installer = Join-Path $output 'LogScope.Desktop-win-Setup.exe'
    if (!(Test-Path $installer)) { throw "Installer not found: $installer" }
    # Keep the canonical name as well: the update metadata may refer to it.
    Copy-Item -LiteralPath $installer -Destination (Join-Path $output "LogScope-Windows-$Version-Setup.exe")
    @{ version = $Version; repositoryUrl = $settings.repositoryUrl } | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $output 'release-manifest.json') -Encoding UTF8
    Write-Host "Installer: $output\LogScope-Windows-$Version-Setup.exe"
    Write-Host 'Publish all generated release files together; an installer alone cannot serve updates.'
}
finally { Pop-Location }

