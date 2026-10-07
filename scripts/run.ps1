param([switch]$Diagnose)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $taskRoot 'dist\GlanceNext\GlanceNext.Desktop.exe'
if (-not (Test-Path -LiteralPath $executable)) { & (Join-Path $PSScriptRoot 'build.ps1') -Publish }
if ($Diagnose) { & $executable --diagnose } else { & $executable }
