[CmdletBinding()]
param([switch]$CandidateOnly)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'Artifact-Paths.ps1')
$projectPath = Join-Path $projectRoot '拾刻.csproj'
$projectXml = [xml](Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8)
$version = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
$experimentProperty = $projectXml.SelectSingleNode('/Project/PropertyGroup/CraftSpeedExperimentBuild')
if (-not $experimentProperty -or $experimentProperty.InnerText -ne 'true') { throw '当前源码不是速度实验分支' }

$artifactRoot = Get-ArtifactRoot $projectRoot
$experimentRoot = Assert-ArtifactPath $artifactRoot (Join-Path $artifactRoot '速度实验')
$stage = Assert-ArtifactPath $artifactRoot (Join-Path $artifactRoot '速度实验发布暂存')
$programName = if ($CandidateOnly) { "拾刻-速度实验-候选-v$version.exe" } else { '拾刻-速度实验.exe' }
$program = Assert-ArtifactPath $artifactRoot (Join-Path $experimentRoot $programName)
$running = @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($programName)) -ErrorAction SilentlyContinue | Where-Object {
    [string]::Equals($_.Path, $program, [StringComparison]::OrdinalIgnoreCase)
})
if ($running.Count -gt 0) { throw '请先退出速度实验程序，再重新生成；日常拾刻无需退出' }
if (Test-Path -LiteralPath $experimentRoot) { Assert-GeneratedDirectory $artifactRoot $experimentRoot '速度实验' }
if (Test-Path -LiteralPath $stage) { Remove-GeneratedDirectory $artifactRoot $stage '速度实验发布暂存' }
$null = New-GeneratedDirectory $artifactRoot $stage '速度实验发布暂存'

# 只检查、读取日常文件，输出路径始终在独立实验目录内。
$dailyNames = @('拾刻.exe', '拾刻-win-x64.zip', 'data\settings.json', 'data\rules.json', 'data\coordinates.json')
$dailyHashes = @{}
foreach ($name in $dailyNames) {
    $path = Join-Path $artifactRoot $name
    if (Test-Path -LiteralPath $path -PathType Leaf) { $dailyHashes[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
}

& dotnet publish $projectPath -c Release -p:PublishProfile=FrameworkDependent --output $stage
if ($LASTEXITCODE -ne 0) { throw '实验编译失败，日常程序与已有实验配置保持' }
$publishedProgram = Join-Path $stage '拾刻.exe'
if ((Get-Item -LiteralPath $publishedProgram).VersionInfo.FileVersion -ne "$version.0") { throw '实验版本与源码不一致' }
$firstBuild = -not (Test-Path -LiteralPath $experimentRoot)
if ($firstBuild) { $null = New-GeneratedDirectory $artifactRoot $experimentRoot '速度实验' }
Copy-Item -LiteralPath $publishedProgram -Destination $program -Force

# 内置资源只补缺失文件；重新生成不会覆盖实验配置、测速报告或个人音效。
foreach ($resourceRoot in @('sounds', 'data\presets')) {
    foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $stage $resourceRoot) -File -Recurse)) {
        $name = $file.FullName.Substring($stage.Length + 1)
        $target = Assert-ArtifactPath $experimentRoot (Join-Path $experimentRoot $name)
        if (-not (Test-Path -LiteralPath $target)) {
            $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force
            Copy-Item -LiteralPath $file.FullName -Destination $target
        }
    }
}

if ($firstBuild) {
    # 首次建立本地配置副本；不复制旧日志和诊断样本。
    foreach ($name in @('data\settings.json', 'data\rules.json', 'data\coordinates.json')) {
        $source = Join-Path $artifactRoot $name
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            $target = Assert-ArtifactPath $experimentRoot (Join-Path $experimentRoot $name)
            $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force
            Copy-Item -LiteralPath $source -Destination $target
        }
    }
    foreach ($resourceRoot in @('sounds', 'data\presets')) {
        $sourceRoot = Join-Path $artifactRoot $resourceRoot
        if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) { continue }
        foreach ($file in @(Get-ChildItem -LiteralPath $sourceRoot -File -Recurse)) {
            $name = $file.FullName.Substring($artifactRoot.Length + 1)
            $target = Assert-ArtifactPath $experimentRoot (Join-Path $experimentRoot $name)
            $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force
            Copy-Item -LiteralPath $file.FullName -Destination $target -Force
        }
    }
}

foreach ($path in $dailyHashes.Keys) {
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $dailyHashes[$path]) {
        throw '生成期间日常文件发生变化；本脚本未写入日常路径，请核对是否有其他实例正在保存'
    }
}
Remove-GeneratedDirectory $artifactRoot $stage '速度实验发布暂存'
[pscustomobject]@{
    Version = $version; Program = $program; FirstBuild = $firstBuild; CandidateOnly = [bool]$CandidateOnly
    Sha256 = (Get-FileHash -LiteralPath $program -Algorithm SHA256).Hash
    Reports = Join-Path $experimentRoot 'data\speed-experiments'
    DailyFilesUnchanged = $true
} | ConvertTo-Json -Compress
