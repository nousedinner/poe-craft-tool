[CmdletBinding()]
param([switch]$NoRestore, [switch]$PackageOnly)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'Artifact-Paths.ps1')
$projectPath = Join-Path $projectRoot '拾刻.csproj'
$projectXml = [xml](Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8)
$version = [string]$projectXml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw '项目版本格式无效' }

# 只打包版本库中的内置资源，日常 data 和个人音效不作为发布来源。
$tracked = @(& git -C $projectRoot -c core.quotepath=false ls-files -- sounds data/presets)
if ($LASTEXITCODE -ne 0) { throw '无法读取版本库资源清单' }
$trackedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($name in $tracked) { $null = $trackedNames.Add($name.Replace('\', '/')) }
$resources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'sounds') -File -Recurse)
$resources += @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'data\presets') -File -Recurse)
foreach ($resource in $resources) {
    $name = $resource.FullName.Substring($projectRoot.Length + 1).Replace('\', '/')
    if (-not $trackedNames.Contains($name)) { throw "拒绝打包未入库的个人资源：$name" }
}

$releaseRoot = Get-ArtifactRoot $projectRoot
$stage = Assert-ArtifactPath $releaseRoot (Join-Path $releaseRoot '发布暂存')
$archivePath = Assert-ArtifactPath $releaseRoot (Join-Path $releaseRoot '拾刻-win-x64.zip')
$temporaryArchive = Assert-ArtifactPath $releaseRoot (Join-Path $releaseRoot '发布暂存.zip')
$dailyProgram = Assert-ArtifactPath $releaseRoot (Join-Path $releaseRoot '拾刻.exe')
$allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$null = $allowed.Add('拾刻.exe')
$null = $allowed.Add('.shike-output.json')
foreach ($resource in $resources) { $null = $allowed.Add($resource.FullName.Substring($projectRoot.Length + 1).Replace('\', '/')) }

if (Test-Path -LiteralPath $stage) {
    Assert-GeneratedDirectory $releaseRoot $stage '发布'
    foreach ($file in @(Get-ChildItem -LiteralPath $stage -File -Recurse -Force)) {
        $name = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        if (-not $allowed.Contains($name)) { throw '发布暂存含额外文件，已保留原目录' }
    }
}
$running = @(Get-Process -Name '拾刻' -ErrorAction SilentlyContinue | Where-Object {
    [string]::Equals($_.Path, (Join-Path $stage '拾刻.exe'), [StringComparison]::OrdinalIgnoreCase) -or
    (-not $PackageOnly -and [string]::Equals($_.Path, $dailyProgram, [StringComparison]::OrdinalIgnoreCase))
})
if ($running.Count -gt 0) { throw '请先退出正在使用的拾刻，再生成新版' }
if (Test-Path -LiteralPath $temporaryArchive) { throw '发布暂存 ZIP 已存在，保留原文件' }
if (Test-Path -LiteralPath $stage) { Remove-GeneratedDirectory $releaseRoot $stage '发布' }
$null = New-GeneratedDirectory $releaseRoot $stage '发布'

if ($PackageOnly) {
    # 仅整理包时复用已交付的 EXE，不编译、不替换日常程序或用户资源。
    if (-not (Test-Path -LiteralPath $dailyProgram -PathType Leaf)) { throw '没有可复用的日常程序' }
    Copy-Item -LiteralPath $dailyProgram -Destination (Join-Path $stage '拾刻.exe')
    foreach ($resource in $resources) {
        $name = $resource.FullName.Substring($projectRoot.Length + 1)
        $target = Assert-ArtifactPath $stage (Join-Path $stage $name)
        $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force
        Copy-Item -LiteralPath $resource.FullName -Destination $target
    }
}
else {
    $arguments = @('publish', $projectPath, '-c', 'Release', '-p:PublishProfile=FrameworkDependent', '--output', $stage)
    if ($NoRestore) { $arguments += '--no-restore' }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw '发布编译失败，旧发布包保持' }
}

$program = Join-Path $stage '拾刻.exe'
if ((Get-Item -LiteralPath $program).VersionInfo.FileVersion -ne "$version.0") { throw '程序版本与项目不一致' }
foreach ($resource in $resources) {
    $name = $resource.FullName.Substring($projectRoot.Length + 1)
    $published = Assert-ArtifactPath $stage (Join-Path $stage $name)
    if ((Get-FileHash -LiteralPath $published -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $resource.FullName -Algorithm SHA256).Hash) { throw "发布资源不符：$name" }
}
$files = @(Get-ChildItem -LiteralPath $stage -File -Recurse -Force | Where-Object { $_.Name -ne '.shike-output.json' })
foreach ($file in $files) {
    $name = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
    if (-not $allowed.Contains($name)) { throw "发布含额外文件：$name" }
}
if ($files.Count -ne ($resources.Count + 1)) { throw '发布文件数量不符' }

# ZIP 只有 EXE、内置 sounds 与 presets；不生成安装/升级脚本、清单或额外文档。
Add-Type -AssemblyName System.IO.Compression
$output = [IO.File]::Open($temporaryArchive, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
$zip = $null
try {
    $zip = [IO.Compression.ZipArchive]::new($output, [IO.Compression.ZipArchiveMode]::Create, $false, [Text.Encoding]::UTF8)
    foreach ($file in $files) {
        $name = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
        $source = [IO.File]::OpenRead($file.FullName)
        $target = $null
        try { $target = $entry.Open(); $source.CopyTo($target) }
        finally { if ($target) { $target.Dispose() }; $source.Dispose() }
    }
}
finally { if ($zip) { $zip.Dispose() }; $output.Dispose() }

if (-not $PackageOnly) {
    Copy-Item -LiteralPath $program -Destination $dailyProgram -Force
    $keepData = Test-Path -LiteralPath (Join-Path $releaseRoot 'data')
    foreach ($resource in $resources) {
        $name = $resource.FullName.Substring($projectRoot.Length + 1)
        if ($keepData -and $name.StartsWith('data\', [StringComparison]::OrdinalIgnoreCase)) { continue }
        $target = Assert-ArtifactPath $releaseRoot (Join-Path $releaseRoot $name)
        $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force
        Copy-Item -LiteralPath (Join-Path $stage $name) -Destination $target -Force
    }
}
Move-Item -LiteralPath $temporaryArchive -Destination $archivePath -Force
Remove-GeneratedDirectory $releaseRoot $stage '发布'
[pscustomobject]@{ Version = $version; Program = $dailyProgram; Archive = $archivePath; FileCount = $files.Count; SizeBytes = (Get-Item -LiteralPath $archivePath).Length; PackageOnly = [bool]$PackageOnly } | ConvertTo-Json -Compress
