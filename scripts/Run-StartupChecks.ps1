[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'Artifact-Paths.ps1')
$artifactRoot = Get-ArtifactRoot $projectRoot
$profileRoot = Assert-ArtifactPath $artifactRoot (Join-Path $artifactRoot '验证记录\启动性能')
if (Test-Path -LiteralPath $profileRoot) { Assert-GeneratedDirectory $artifactRoot $profileRoot '启动性能' }
else { $null = New-GeneratedDirectory $artifactRoot $profileRoot '启动性能' }
$candidate = Assert-ArtifactPath $profileRoot (Join-Path $profileRoot '最终发布')
$inputDirectory = Assert-ArtifactPath $profileRoot (Join-Path $profileRoot '输入数据')
$executable = Join-Path $artifactRoot '拾刻.exe'
$project = [xml](Get-Content -LiteralPath (Join-Path $projectRoot '拾刻.csproj') -Raw -Encoding UTF8)
$version = [string]$project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if ((Get-Item -LiteralPath $executable).VersionInfo.FileVersion -ne "$version.0") { throw '日常程序版本与当前源码不一致，请先生成发布包' }

function Get-UserDataSnapshot {
    return @(foreach ($dataRoot in @((Join-Path $projectRoot 'data'), (Join-Path $artifactRoot 'data'))) {
        Get-ChildItem -LiteralPath $dataRoot -File -Recurse -Force | ForEach-Object {
            [pscustomobject]@{ path = $_.FullName; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        }
    })
}

$before = Get-UserDataSnapshot
$running = @(Get-Process -Name '拾刻' -ErrorAction SilentlyContinue | Where-Object {
    [string]::Equals($_.Path, (Join-Path $candidate '拾刻.exe'), [StringComparison]::OrdinalIgnoreCase)
})
if ($running.Count -gt 0) { throw '启动测量副本正在运行，拒绝清理' }
foreach ($entry in @(@($candidate, '启动性能副本'), @($inputDirectory, '启动测量输入'))) {
    if (Test-Path -LiteralPath $entry[0]) { Remove-GeneratedDirectory $artifactRoot $entry[0] $entry[1] }
    $null = New-GeneratedDirectory $artifactRoot $entry[0] $entry[1]
}
Copy-Item -LiteralPath $executable -Destination (Join-Path $candidate '拾刻.exe')
Copy-Item -LiteralPath (Join-Path $artifactRoot 'sounds') -Destination (Join-Path $candidate 'sounds') -Recurse
Copy-Item -LiteralPath (Join-Path $artifactRoot 'data') -Destination (Join-Path $inputDirectory 'data') -Recurse

& (Join-Path $PSScriptRoot 'Measure-Startup.ps1') -Name '最终发布' -Count 7
& (Join-Path $PSScriptRoot 'Measure-Startup.ps1') -Name '最终发布' -Count 3 -CheckLifecycle
$after = Get-UserDataSnapshot
if ($before.Count -ne $after.Count) { throw '数据文件数量发生变化，请检查正在运行的其他实例；不能记为保护通过' }
$afterHashes = @{}
foreach ($file in $after) { $afterHashes[$file.path] = $file.sha256 }
foreach ($file in $before) {
    if (-not $afterHashes.ContainsKey($file.path) -or $afterHashes[$file.path] -ne $file.sha256) { throw "既有数据哈希发生变化：$($file.path)" }
}
$performance = Get-Content -LiteralPath (Join-Path $profileRoot '最终发布-results.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$lifecycle = Get-Content -LiteralPath (Join-Path $profileRoot '最终发布-lifecycle-results.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$summary = [ordered]@{
    schemaVersion = 1
    status = 'passed'
    measuredUtc = [DateTime]::UtcNow.ToString('o')
    programVersion = $version
    executableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
    renderRuns = $performance.count
    medianFirstFrameMilliseconds = $performance.medianFirstFrameMilliseconds
    lifecycleRuns = $lifecycle.count
    originalUserDataFiles = $before.Count
    originalUserDataUnchanged = $true
    scope = '已交付 EXE 的隔离 WPF 首帧、首帧前关闭、恢复和重复关闭、正常退出；不注册热键、不联网、不发送输入，冷启动/托盘鼠标/真实游戏仍需人工'
}
[IO.File]::WriteAllText((Join-Path $profileRoot 'startup-check-result.json'), ($summary | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($true))
Write-Host '启动与托盘关闭检查通过；原用户数据保持。'
