param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$sdkExecutable = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdkExecutable)) { & (Join-Path $PSScriptRoot 'setup.ps1') }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$project = Join-Path $taskRoot 'src\GlanceNext.Desktop\GlanceNext.Desktop.csproj'
Push-Location $taskRoot
try {
    & $sdkExecutable restore $project --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
    & $sdkExecutable run --project (Join-Path $taskRoot 'tests\GlanceNext.Tests\GlanceNext.Tests.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Rule tests failed' }
    if ($Publish) {
        & $sdkExecutable publish $project -c Release --no-restore -o (Join-Path $taskRoot 'dist\GlanceNext')
    } else {
        & $sdkExecutable build $project -c Debug --no-restore
    }
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
} finally { Pop-Location }
