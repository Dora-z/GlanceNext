param(
    [string]$Version,
    [string]$PublishDirectory,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (-not $Version) {
    [xml]$project = Get-Content (Join-Path $taskRoot 'src/GlanceNext.Desktop/GlanceNext.Desktop.csproj')
    $Version = [string]$project.Project.PropertyGroup.Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Release version must be major.minor.patch' }
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $taskRoot 'dist/GlanceNext' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $taskRoot 'dist/releases' }
$PublishDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
foreach ($required in @('GlanceNext.Desktop.exe', 'GlanceNext.Desktop.pri', 'MainWindow.xbf', 'ReminderWindow.xbf', 'Models/face_detection_yunet_2026may.onnx', 'Models/LICENSE.YuNet.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory $required))) { throw "Missing published resource: $required" }
}
$binaryVersion = (Get-Item -LiteralPath (Join-Path $PublishDirectory 'GlanceNext.Desktop.exe')).VersionInfo.ProductVersion.Split('+')[0]
if ($binaryVersion -ne $Version) { throw "Published executable version $binaryVersion does not match $Version" }

# Work in a fresh staging directory, leaving the running installation untouched.
$staging = Join-Path $taskRoot ('dist/release-staging/' + [guid]::NewGuid().ToString('N'))
$appDirectory = Join-Path $staging 'GlanceNext'
New-Item -ItemType Directory -Force $appDirectory, $OutputDirectory | Out-Null
Get-ChildItem -LiteralPath $PublishDirectory | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $appDirectory -Recurse -Force }
foreach ($document in @('LICENSE', 'THIRD_PARTY_NOTICES.md', 'README.md')) {
    Copy-Item -LiteralPath (Join-Path $taskRoot $document) -Destination $appDirectory
}
$licenseDirectory = Join-Path $appDirectory 'Licenses'
Copy-Item -LiteralPath (Join-Path $taskRoot 'licenses') -Destination $licenseDirectory -Recurse

# Preserve supplied license/NOTICE files from every restored dependency and runtime pack.
# Record only package IDs/versions, never the machine's absolute NuGet paths.
$assets = Get-Content (Join-Path $taskRoot 'src/GlanceNext.Desktop/obj/project.assets.json') -Raw | ConvertFrom-Json
$packagePaths = @($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' } | ForEach-Object { $_.Value.path })
foreach ($framework in $assets.project.frameworks.PSObject.Properties) {
    foreach ($dependency in $framework.Value.downloadDependencies) {
        $packagePaths += $dependency.name.ToLowerInvariant() + '/' + $dependency.version.Trim('[', ']').Split(',')[0].Trim()
    }
}
$packagePaths = @($packagePaths | Sort-Object -Unique)
foreach ($package in $packagePaths) {
    $packageDirectory = $null
    foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
        $candidate = Join-Path $folder $package
        if (Test-Path -LiteralPath $candidate) { $packageDirectory = $candidate; break }
    }
    if (-not $packageDirectory) { throw "Restored package not found: $package" }
    foreach ($file in Get-ChildItem -LiteralPath $packageDirectory -Recurse -File | Where-Object { $_.Name -match '(?i)(license|notice|copying|copyright)' -and $_.Extension -notin @('.dll', '.exe', '.pdb') }) {
        $relative = $file.FullName.Substring($packageDirectory.Length).TrimStart('\', '/')
        $destination = Join-Path (Join-Path $licenseDirectory $package) $relative
        New-Item -ItemType Directory -Force (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
}
foreach ($requiredLicense in @('opencvsharp/LICENSE.txt', 'ffmpeg/LICENSE.txt', 'ffmpeg/NOTICE.txt', 'microsoft.windowsappsdk/2.5.1/license.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $licenseDirectory $requiredLicense))) { throw "Missing release license: $requiredLicense" }
}
if (-not (Get-ChildItem -LiteralPath $licenseDirectory -Recurse -File | Where-Object { $_.FullName -match 'microsoft.netcore.app.runtime.win-x64.*THIRD-PARTY-NOTICES' })) {
    throw '.NET runtime third-party notices are missing'
}
$commit = if ($env:GITHUB_SHA) { $env:GITHUB_SHA } else { (git -C $taskRoot rev-parse HEAD).Trim() }
$manifest = [ordered]@{ Version = $Version; Platform = 'windows-x64'; Commit = $commit; BuildRun = $env:GITHUB_RUN_ID; Packages = $packagePaths }
[System.IO.File]::WriteAllText((Join-Path $appDirectory 'release-manifest.json'), ($manifest | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
$instructions = @'
GlanceNext / Windows 11 x64

完整解压此目录，运行 GlanceNext.Desktop.exe。无需另外安装 .NET 或 Windows App Runtime。
需要可独立取流的红外摄像头；首次使用进入「设备与校准」，完成诊断与校准后再开启保护。
紧急解除默认快捷键：Ctrl + Alt + G。关闭窗口进入托盘，右键托盘可暂停或退出。
画面只在本机内存处理；设置保存在当前用户 LocalAppData/GlanceNext，不随此包交付。
本项目使用头部朝向近似判断注意力，不是精确眼球追踪。

源码与反馈：https://github.com/Dora-z/GlanceNext
GlanceNext 源码采用 MIT；第三方许可证见 Licenses、Models/LICENSE.YuNet.txt 和 THIRD_PARTY_NOTICES.md。
构建版本与源码提交见 release-manifest.json。
'@
[System.IO.File]::WriteAllText((Join-Path $appDirectory 'README.txt'), $instructions, [System.Text.UTF8Encoding]::new($false))
$archive = Join-Path $OutputDirectory "GlanceNext-$Version-windows-x64.zip"
Compress-Archive -LiteralPath $appDirectory -DestinationPath $archive -CompressionLevel Optimal -Force
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText($archive + '.sha256', "$hash  $([System.IO.Path]::GetFileName($archive))`n", [System.Text.UTF8Encoding]::new($false))
Write-Host "Release package: $archive"
Write-Host "SHA-256: $hash"
