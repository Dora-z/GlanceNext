param([ValidateSet('Rules','Ui','Hardware','Recovery')][string]$Mode='Rules')
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
if($Mode -eq 'Rules') {
    & (Join-Path $taskRoot '.tools\dotnet\dotnet.exe') run --project (Join-Path $taskRoot 'tests\GlanceNext.Tests\GlanceNext.Tests.csproj') -c Release
    if($LASTEXITCODE -ne 0){throw 'Rule tests failed'}
} else {
    $executable=Join-Path $taskRoot 'dist\GlanceNext\GlanceNext.Desktop.exe'
    if(-not(Test-Path -LiteralPath $executable)){throw 'Run scripts/build.ps1 -Publish first'}
    $argument=switch($Mode){'Ui'{'--ui-check'}'Recovery'{'--recovery-check'}default{'--smoke'}}
    Push-Location $taskRoot
    try {
        $startedAt=Get-Date
        $process=Start-Process -FilePath $executable -ArgumentList $argument -WorkingDirectory $taskRoot -WindowStyle Hidden -PassThru
        if(-not $process.WaitForExit(45000)){throw 'Verification timed out; inspect artifacts/verification and diagnostics.log'}
        if($process.ExitCode -ne 0){throw "Application exited with $($process.ExitCode)"}
        $reportName=switch($Mode){'Ui'{'ui-report.json'}'Recovery'{'recovery-report.json'}default{'hardware-report.json'}}
        $reportPath=Join-Path $taskRoot ('artifacts\verification\'+$reportName)
        if(-not(Test-Path -LiteralPath $reportPath) -or (Get-Item -LiteralPath $reportPath).LastWriteTime -lt $startedAt){throw 'No fresh verification report was produced'}
        $report=Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        if($Mode -eq 'Hardware' -and (-not $report.IsRunning -or -not $report.IsStable -or -not $report.AutomationSuppressed)){throw ('Infrared validation failed: '+$report.StatusDetail)}
        if($Mode -eq 'Recovery' -and (-not $report.Passed -or -not $report.AutomationSuppressed -or -not $report.SettingsPreserved)){throw 'Recovery validation failed'}
        Write-Host ($Mode+' verification passed: '+$reportPath)
    } finally {Pop-Location}
}
