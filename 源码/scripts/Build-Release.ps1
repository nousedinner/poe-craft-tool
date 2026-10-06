[CmdletBinding()]
param([switch]$NoRestore)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$utf8WithBom = [Text.UTF8Encoding]::new($true)
# Git 和 dotnet 的中文输出也按 UTF-8 解码，兼容 Windows PowerShell 5.1。
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'Artifact-Paths.ps1')
$projectPath = Join-Path $projectRoot '拾刻.csproj'
$projectXml = [xml](Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8)
$releaseVersion = [string]$projectXml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if ($releaseVersion -notmatch '^\d+\.\d+\.\d+$') { throw '项目版本格式无效' }

# 源码资源与日常运行 data 分开；拒绝把未入库的个人预设或音效混入交付包。
$trackedResources = @(& git -C $projectRoot -c core.quotepath=false ls-files -- sounds data/presets)
if ($LASTEXITCODE -ne 0) { throw '无法读取版本库资源清单' }
$resourceNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($tracked in $trackedResources) { $null = $resourceNames.Add($tracked.Replace('\', '/')) }
$requiredResources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'sounds') -File -Recurse)
$requiredResources += @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'data\presets') -File -Recurse)
foreach ($resource in $requiredResources) {
    $relative = $resource.FullName.Substring($projectRoot.Length + 1).Replace('\', '/')
    if (-not $resourceNames.Contains($relative)) { throw "资源尚未入库，拒绝打包个人文件：$relative" }
}
$releaseRoot = Get-ArtifactRoot $projectRoot
$publishStage = Assert-ArtifactPath $releaseRoot (Join-Path $releaseRoot '发布暂存')
$publishDirectory = Assert-ArtifactPath $releaseRoot (Join-Path $releaseRoot '发布')
$archivePath = Assert-ArtifactPath $releaseRoot (Join-Path $releaseRoot '拾刻-win-x64.zip')
$archiveTemporary = Assert-ArtifactPath $releaseRoot (Join-Path $releaseRoot '发布暂存.zip')
$allowedPackageNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($name in @('.shike-output.json', '拾刻.exe', 'Upgrade-ShiKe.ps1', '升级拾刻.cmd', '使用与升级说明.txt', 'release-manifest.json') + @($requiredResources | ForEach-Object { $_.FullName.Substring($projectRoot.Length + 1).Replace('\', '/') })) { $null = $allowedPackageNames.Add($name) }

function Assert-DisposablePackage([string]$Directory) {
    Assert-GeneratedDirectory $releaseRoot $Directory '发布'
    foreach ($file in @(Get-ChildItem -LiteralPath $Directory -File -Recurse -Force)) {
        $relative = $file.FullName.Substring($Directory.Length + 1).Replace('\', '/')
        if (-not $allowedPackageNames.Contains($relative)) { throw "发布目录含额外文件，已保留：$($file.FullName)" }
    }
    $oldManifest = Join-Path $Directory 'release-manifest.json'
    if (Test-Path -LiteralPath $oldManifest -PathType Leaf) {
        $previous = Get-Content -LiteralPath $oldManifest -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($entry in $previous.files) {
            $oldFile = Assert-ArtifactPath $Directory (Join-Path $Directory ([string]$entry.path))
            if (-not (Test-Path -LiteralPath $oldFile -PathType Leaf) -or (Get-FileHash -LiteralPath $oldFile).Hash -ne $entry.sha256) { throw "发布目录中的文件已修改，已保留：$oldFile" }
        }
    }
}

# 只复用有生成标记且未被使用者修改的发布目录；日常 data 永远不清空。
foreach ($directory in @($publishStage, $publishDirectory)) { if (Test-Path -LiteralPath $directory) { Assert-DisposablePackage $directory } }
$running = @(Get-Process -Name '拾刻' -ErrorAction SilentlyContinue | Where-Object {
    [string]::Equals($_.Path, (Join-Path $releaseRoot '拾刻.exe'), [StringComparison]::OrdinalIgnoreCase) -or
    [string]::Equals($_.Path, (Join-Path $publishDirectory '拾刻.exe'), [StringComparison]::OrdinalIgnoreCase)
})
if ($running.Count -gt 0) { throw '请先从托盘退出指定输出目录中的拾刻，再生成新版' }
if (Test-Path -LiteralPath $publishStage) { Remove-GeneratedDirectory $releaseRoot $publishStage '发布' }
$null = New-GeneratedDirectory $releaseRoot $publishStage '发布'

$publishArguments = @('publish', $projectPath, '-c', 'Release', '-p:PublishProfile=FrameworkDependent', '--output', $publishStage)
if ($NoRestore) { $publishArguments += '--no-restore' }
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) { throw '发布编译失败，未生成交付包' }

$executable = Join-Path $publishStage '拾刻.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw '缺少主程序' }
$fileVersion = (Get-Item -LiteralPath $executable).VersionInfo.FileVersion
if ($fileVersion -ne "$releaseVersion.0") { throw "主程序文件版本不一致：$fileVersion" }

# 核对源码声明的资源，不使用开发机 bin 中的用户 data 作为发布来源。
foreach ($resource in $requiredResources) {
    $relative = $resource.FullName.Substring($projectRoot.Length + 1)
    $published = Join-Path $publishStage $relative
    if (-not (Test-Path -LiteralPath $published -PathType Leaf)) { throw "发布资源缺失：$relative" }
    if ((Get-FileHash -LiteralPath $published -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $resource.FullName -Algorithm SHA256).Hash) {
        throw "发布资源内容不一致：$relative"
    }
}
foreach ($privateName in @('settings.json', 'rules.json', 'coordinates.json', 'debug.log')) {
    if (Test-Path -LiteralPath (Join-Path $publishStage "data\$privateName")) { throw "发布目录包含用户数据：$privateName" }
}

$upgradeSource = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Upgrade-Release.ps1'), [Text.Encoding]::UTF8)
[IO.File]::WriteAllText((Join-Path $publishStage 'Upgrade-ShiKe.ps1'), $upgradeSource, $utf8WithBom)
@'
@echo off
setlocal
set "PSModulePath="
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -STA -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0Upgrade-ShiKe.ps1"
'@ | Set-Content -LiteralPath (Join-Path $publishStage '升级拾刻.cmd') -Encoding ASCII
$instructions = @"
拾刻 $releaseVersion（Windows x64）

本包需要 x64 .NET 10 桌面运行时。首次使用可直接运行 拾刻.exe。
升级已有版本：先退出拾刻，将本包解压到独立目录，再双击 升级拾刻.cmd，选择原程序目录。
升级保留原 data 目录及其中的设置、规则、坐标和预设；替换前会备份程序文件，失败时尝试恢复。
已有同名内置预设也保持原样。未自动删除旧版多余文件。

点 X 或 Alt+F4 直接退出；退出时停止工具并保存设置。
"@
[IO.File]::WriteAllText((Join-Path $publishStage '使用与升级说明.txt'), $instructions, $utf8WithBom)

$gitCommit = (& git -C $projectRoot rev-parse HEAD).Trim()
$worktreeDirty = -not [string]::IsNullOrWhiteSpace((& git -C $projectRoot status --porcelain | Out-String))
$entries = @(Get-ChildItem -LiteralPath $publishStage -File -Recurse | Where-Object { $_.Name -ne '.shike-output.json' } | ForEach-Object {
    [ordered]@{
        path = $_.FullName.Substring($publishStage.Length + 1).Replace('\', '/')
        size = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
})
$manifest = [ordered]@{
    schemaVersion = 1
    version = $releaseVersion
    runtime = 'win-x64'
    frameworkDependent = $true
    sourceCommit = $gitCommit
    worktreeDirty = $worktreeDirty
    createdUtc = [DateTime]::UtcNow.ToString('o')
    validation = 'compiled_and_resources_checked; runtime_and_upgrade_not_tested'
    files = $entries
}
[IO.File]::WriteAllText((Join-Path $publishStage 'release-manifest.json'), ($manifest | ConvertTo-Json -Depth 5), $utf8WithBom)
# .NET Framework 的压缩模块可能写入反斜杠；逐项使用 UTF-8 和标准 ZIP 路径。
Add-Type -AssemblyName System.IO.Compression
$archiveStream = [IO.File]::Open($archiveTemporary, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
$zip = $null
try {
    $zip = [IO.Compression.ZipArchive]::new($archiveStream, [IO.Compression.ZipArchiveMode]::Create, $false, [Text.Encoding]::UTF8)
    foreach ($entryPath in @($entries | ForEach-Object { $_.path }) + @('release-manifest.json')) {
        $publishedPath = Join-Path $publishStage $entryPath
        $zipEntry = $zip.CreateEntry($entryPath, [IO.Compression.CompressionLevel]::Optimal)
        $sourceStream = [IO.File]::OpenRead($publishedPath)
        $entryStream = $null
        try {
            $entryStream = $zipEntry.Open()
            $sourceStream.CopyTo($entryStream)
        }
        finally {
            if ($entryStream) { $entryStream.Dispose() }
            $sourceStream.Dispose()
        }
    }
}
finally {
    if ($zip) { $zip.Dispose() }
    $archiveStream.Dispose()
}
# 新包完成后再替换固定位置；同版本重打包不挤掉上一个版本的回退包。
if (Test-Path -LiteralPath $archivePath -PathType Leaf) {
    $previousArchive = Assert-ArtifactPath $releaseRoot (Join-Path $releaseRoot '备份\上次发布.zip')
    $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($previousArchive)) -Force
    $priorManifestPath = Join-Path $publishDirectory 'release-manifest.json'
    $sameVersion = $false
    if (Test-Path -LiteralPath $priorManifestPath -PathType Leaf) {
        $priorManifest = Get-Content -LiteralPath $priorManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $sameVersion = [string]$priorManifest.version -eq $releaseVersion
    }
    if (-not $sameVersion -or -not (Test-Path -LiteralPath $previousArchive -PathType Leaf)) {
        Copy-Item -LiteralPath $archivePath -Destination $previousArchive -Force
    }
}
if (Test-Path -LiteralPath $publishDirectory) { Remove-GeneratedDirectory $releaseRoot $publishDirectory '发布' }
Move-Item -LiteralPath $publishStage -Destination $publishDirectory
Move-Item -LiteralPath $archiveTemporary -Destination $archivePath -Force

# 更新用户日常启动入口；已有整个 data 保持原样，首次生成才复制内置预设。
Copy-Item -LiteralPath (Join-Path $publishDirectory '拾刻.exe') -Destination (Join-Path $releaseRoot '拾刻.exe') -Force
$keepData = Test-Path -LiteralPath (Join-Path $releaseRoot 'data')
foreach ($resource in $requiredResources) {
    $relative = $resource.FullName.Substring($projectRoot.Length + 1)
    if ($keepData -and $relative.StartsWith('data\', [StringComparison]::OrdinalIgnoreCase)) { continue }
    $destination = Assert-ArtifactPath $releaseRoot (Join-Path $releaseRoot $relative)
    $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force
    Copy-Item -LiteralPath (Join-Path $publishDirectory $relative) -Destination $destination -Force
}
foreach ($oldName in @('拾刻.dll', '拾刻.pdb', '拾刻.deps.json', '拾刻.runtimeconfig.json')) {
    $oldPath = Assert-ArtifactPath $releaseRoot (Join-Path $releaseRoot $oldName)
    if (Test-Path -LiteralPath $oldPath -PathType Leaf) { Remove-Item -LiteralPath $oldPath }
}
[pscustomobject]@{ Version = $releaseVersion; Directory = $publishDirectory; Program = (Join-Path $releaseRoot '拾刻.exe'); Archive = $archivePath; FileCount = $entries.Count; SizeBytes = (Get-Item -LiteralPath $archivePath).Length } | ConvertTo-Json -Compress
