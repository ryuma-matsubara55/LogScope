[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    $settings = Get-Content src\LogScope.Wpf\distribution.json -Raw | ConvertFrom-Json
    $output = Join-Path $projectRoot "artifacts\releases\$Version"
    if (!(Test-Path (Join-Path $output 'releases.win.json'))) { throw 'Run Build-Release.ps1 first.' }
    if (!$env:VPK_TOKEN) { throw 'Set VPK_TOKEN to a GitHub token with release write permission. Never commit the token.' }
    $manifest = Get-Content (Join-Path $output 'release-manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.version -ne $Version -or $manifest.repositoryUrl -ne $settings.repositoryUrl) {
        throw 'The version or repository has changed since packaging. Rebuild before uploading.'
    }
    # The runtime and upload destination always use the same checked-in configuration.
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Tool restore failed.' }
    $uploadArgs = @('upload', 'github', '--repoUrl', $settings.repositoryUrl,
        '--outputDir', $output, '--channel', 'win', '--tag', "v$Version", '--releaseName', "LogScope v$Version")
    if ($Publish) { $uploadArgs += '--publish' }
    # VPK_TOKEN is read by vpk from the environment, not exposed on the command line.
    dotnet vpk @uploadArgs
    if ($LASTEXITCODE -ne 0) { throw 'GitHub release upload failed.' }
    Write-Host $(if ($Publish) { 'Published. Installed clients will receive this update.' } else { 'Draft created. Publish it on GitHub to deliver this update.' })
}
finally { Pop-Location }

