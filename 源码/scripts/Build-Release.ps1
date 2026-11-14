[CmdletBinding()]
param([switch]$NoRestore, [switch]$PackageOnly, [switch]$PromoteStable, [string]$OutputDirectory='', [string]$ReleaseNotesFile='')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'Artifact-Paths.ps1')
$projectPath = Join-Path $projectRoot '拾刻.csproj'
$projectXml = [xml](Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8)
$version = [string]$projectXml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw '项目版本格式无效' }
. (Join-Path $PSScriptRoot 'ReleaseNotes-Validation.ps1')
if (-not $ReleaseNotesFile) { $ReleaseNotesFile = Join-Path $projectRoot 'ReleaseNotes.txt' }
$ReleaseNotesFile = [IO.Path]::GetFullPath($ReleaseNotesFile)
$null = Assert-ReleaseNotes $ReleaseNotesFile $version

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

$stableRoot = Get-ArtifactRoot $projectRoot
$debugRoot = Initialize-DebugRoot $projectRoot
$releaseShareRoot = Get-ReleaseShareRoot $projectRoot
$dailyProgram = Assert-ArtifactPath $stableRoot (Join-Path $stableRoot '拾刻.exe')
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $releaseShareRoot $version }
$outputDirectory = Assert-ArtifactPath $releaseShareRoot ([IO.Path]::GetFullPath($OutputDirectory))
$stage = Assert-ArtifactPath $debugRoot (Join-Path $debugRoot ('发布暂存-' + $version))
$temporaryArchive = Assert-ArtifactPath $debugRoot (Join-Path $debugRoot ('发布暂存-' + $version + '.zip'))
$stableArchive = Assert-ArtifactPath $stableRoot (Join-Path $stableRoot '拾刻-win-x64.zip')
if ($PackageOnly) { $null = Assert-ReleaseNotes $ReleaseNotesFile $version $dailyProgram }
$allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$null = $allowed.Add('拾刻.exe')
$null = $allowed.Add('.shike-output.json')
foreach ($resource in $resources) { $null = $allowed.Add($resource.FullName.Substring($projectRoot.Length + 1).Replace('\', '/')) }

if (Test-Path -LiteralPath $stage) {
    Assert-GeneratedDirectory $debugRoot $stage '发布暂存'
    foreach ($file in @(Get-ChildItem -LiteralPath $stage -File -Recurse -Force)) {
        $name = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        if (-not $allowed.Contains($name)) { throw '发布暂存含额外文件，已保留原目录' }
    }
}
$running = @(Get-Process -Name '拾刻' -ErrorAction SilentlyContinue | Where-Object {
    [string]::Equals($_.Path, (Join-Path $stage '拾刻.exe'), [StringComparison]::OrdinalIgnoreCase) -or
    ($PromoteStable -and [string]::Equals($_.Path, $dailyProgram, [StringComparison]::OrdinalIgnoreCase))
})
if ($running.Count -gt 0) { throw '请先退出正在使用的拾刻，再生成新版' }
if (Test-Path -LiteralPath $temporaryArchive) { throw '发布暂存 ZIP 已存在，保留原文件' }
if (Test-Path -LiteralPath $outputDirectory) { throw "共享发布目录已存在，拒绝覆盖：$outputDirectory" }
if (Test-Path -LiteralPath $stage) { Remove-GeneratedDirectory $debugRoot $stage '发布暂存' }
$null = New-GeneratedDirectory $debugRoot $stage '发布暂存'

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
    $arguments = @('publish', $projectPath, '-c', 'Release', '-p:PublishProfile=FrameworkDependent', ('-p:ReleaseNotesFile='+$ReleaseNotesFile), '--output', $stage)
    if ($NoRestore) { $arguments += '--no-restore' }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw '发布编译失败，旧发布包保持' }
}

$program = Join-Path $stage '拾刻.exe'
if ((Get-Item -LiteralPath $program).VersionInfo.FileVersion -ne "$version.0") { throw '程序版本与项目不一致' }
$null = Assert-ReleaseNotes $ReleaseNotesFile $version $program
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

$outputParent = Assert-ArtifactPath $releaseShareRoot ([IO.Path]::GetDirectoryName($outputDirectory))
$null = New-Item -ItemType Directory -Path $outputParent -Force
if (Test-Path -LiteralPath $outputDirectory) { throw "共享发布版本目录已出现，拒绝覆盖：$outputDirectory" }
$transferDirectory = Assert-ArtifactPath $debugRoot (Join-Path $debugRoot ('发布传输暂存-' + $version + '-' + [Guid]::NewGuid().ToString('N')))
$null = New-Item -ItemType Directory -Path $transferDirectory
$publishedExe = Join-Path $transferDirectory '拾刻.exe'
$publishedArchive = Join-Path $transferDirectory 'shike-win-x64.zip'
[IO.File]::Copy($program, $publishedExe, $false)
[IO.File]::Copy($temporaryArchive, $publishedArchive, $false)
if (Test-Path -LiteralPath $outputDirectory) { throw "共享发布版本目录已出现，拒绝覆盖：$outputDirectory" }
[IO.Directory]::Move($transferDirectory, $outputDirectory)
$archivePath = Join-Path $outputDirectory 'shike-win-x64.zip'
$publishedExe = Join-Path $outputDirectory '拾刻.exe'

if ($PromoteStable) {
    # 只有显式要求时才替换自用稳定版；个人 data/ 与 sounds/ 原位保留。
    Copy-Item -LiteralPath $publishedExe -Destination $dailyProgram -Force
    Copy-Item -LiteralPath $archivePath -Destination $stableArchive -Force
}
Remove-Item -LiteralPath $temporaryArchive
Remove-GeneratedDirectory $debugRoot $stage '发布暂存'
[pscustomobject]@{
    Version = $version
    Program = $publishedExe
    Archive = $archivePath
    ReleaseDirectory = $outputDirectory
    StableProgram = $dailyProgram
    FileCount = $files.Count
    SizeBytes = (Get-Item -LiteralPath $archivePath).Length
    PackageOnly = [bool]$PackageOnly
    PromotedStable = [bool]$PromoteStable
} | ConvertTo-Json -Compress
