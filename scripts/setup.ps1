param([switch]$SkipSdk)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$sdkVersion = '10.0.401'
$sdkHash = '24b670ad3d923bfcf47df6c3b034152398b42f6dbc388e10d783aee1cfb5e5817d399fc0ae2a12cfa822a55e61d34830ccb15c50ef6efee437ab874bb7c79430'
$sdkDirectory = Join-Path $taskRoot '.tools\dotnet'
$sdkExecutable = Join-Path $sdkDirectory 'dotnet.exe'
if (-not $SkipSdk -and -not (Test-Path -LiteralPath $sdkExecutable)) {
    $archive = Join-Path $taskRoot '.tools\dotnet-sdk-10.0.401-win-x64.zip'
    New-Item -ItemType Directory -Force (Split-Path -Parent $archive) | Out-Null
    Invoke-WebRequest "https://builds.dotnet.microsoft.com/dotnet/Sdk/$sdkVersion/dotnet-sdk-$sdkVersion-win-x64.zip" -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $sdkHash) { throw 'SDK checksum mismatch' }
    Expand-Archive -LiteralPath $archive -DestinationPath $sdkDirectory -Force
}
$modelDirectory = Join-Path $taskRoot 'src\GlanceNext.Desktop\Models'
$modelPath = Join-Path $modelDirectory 'face_detection_yunet_2026may.onnx'
$modelHash = 'EBAFCE4E3C118D6554634BE5C27AB333B4C047A9A8C3FAF1D7CF93101C22F0F0'
New-Item -ItemType Directory -Force $modelDirectory | Out-Null
if (-not (Test-Path -LiteralPath $modelPath)) {
    Invoke-WebRequest 'https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models/face_detection_yunet/face_detection_yunet_2026may.onnx' -OutFile $modelPath
}
if ((Get-FileHash -LiteralPath $modelPath -Algorithm SHA256).Hash -ne $modelHash) { throw 'YuNet model checksum mismatch; do not run an unverified model' }
Write-Host 'SDK and model verified.'
