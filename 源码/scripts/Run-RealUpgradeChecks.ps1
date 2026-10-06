[CmdletBinding()]
param([Parameter(Mandatory = $true)][ValidateSet('Refusal', 'Upgrade')][string]$Mode)

# 只供用户主动运行：操作准备好的真实旧版副本，不启动应用、不操作日常目录。
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
. (Join-Path $PSScriptRoot 'Artifact-Paths.ps1')
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = Get-ArtifactRoot $projectRoot
$resultRoot = Assert-ArtifactPath $artifactRoot (Join-Path $artifactRoot '验证记录\真实升级')
Assert-GeneratedDirectory $artifactRoot $resultRoot '真实升级'
$target = Assert-ArtifactPath $resultRoot (Join-Path $resultRoot 'old-version')
$executable = Assert-ArtifactPath $target (Join-Path $target '拾刻.exe')
$report = [ordered]@{mode=$Mode;status='not_run';startedUtc=[DateTime]::UtcNow.ToString('o');finishedUtc=$null;oldVersion=$null;newVersion=$null;dataUnchanged=$null;error=$null;validationScope=if ($Mode -eq 'Refusal') {'real_running_target_file_guard_only'} else {'real_old_executable_file_upgrade_and_data_only; application_start_requires_user_confirmation'}}
$exitCode = 1

function Get-DataSnapshot {
    $data = Assert-ArtifactPath $target (Join-Path $target 'data')
    $rows = @(Get-ChildItem -LiteralPath $data -File -Recurse -Force | ForEach-Object {
        $path = Assert-ArtifactPath $data $_.FullName
        [ordered]@{path=$path.Substring($data.Length + 1);sha256=(Get-FileHash -LiteralPath $path).Hash}
    } | Sort-Object { $_.path })
    return ConvertTo-Json -InputObject $rows -Depth 3 -Compress
}

try {
    $preparation = Get-Content -LiteralPath (Join-Path $resultRoot 'preparation.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not [string]::Equals($preparation.targetDirectory, $target, [StringComparison]::OrdinalIgnoreCase)) { throw '准备记录与副本位置不一致，停止操作' }
    $package = Assert-ArtifactPath $artifactRoot (Join-Path $artifactRoot '发布')
    $manifestPath = Assert-ArtifactPath $package (Join-Path $package 'release-manifest.json')
    if ((Get-FileHash -LiteralPath $manifestPath).Hash -ne $preparation.newManifestSha256) { throw '发布包已变更，请先更新实机副本准备记录' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $upgrade = Assert-ArtifactPath $package (Join-Path $package 'Upgrade-ShiKe.ps1')
    $report.oldVersion = (Get-Item -LiteralPath $executable).VersionInfo.FileVersion
    if ($report.oldVersion -ne $preparation.oldFileVersion) { throw '该副本已升级或被修改，不重复执行；请保留已生成的结果' }
    $beforeExeHash = (Get-FileHash -LiteralPath $executable).Hash
    if ($beforeExeHash -ne $preparation.oldExecutableSha256) { throw '旧版 EXE 与准备记录不一致，停止操作并保留副本' }
    $isRunning = $false
    foreach ($process in @(Get-Process -Name '拾刻' -ErrorAction SilentlyContinue)) {
        try { if ([string]::Equals($process.Path, $executable, [StringComparison]::OrdinalIgnoreCase)) { $isRunning = $true } }
        finally { $process.Dispose() }
    }
    if ($Mode -eq 'Refusal') {
        if (-not $isRunning) { throw '请先打开 old-version 中的拾刻.exe，看到旧版界面后再运行此入口' }
        $failure = $null
        try { $null = & $upgrade -TargetDirectory $target }
        catch { $failure = $_.Exception.Message }
        if (-not $failure -or -not $failure.Contains('请先从托盘退出原拾刻，再升级') -or (Get-FileHash -LiteralPath $executable).Hash -ne $beforeExeHash) { throw '未确认运行中拒绝或原 EXE 保持，请保留副本和结果' }
        $report.newVersion = (Get-Item -LiteralPath $executable).VersionInfo.FileVersion
        Write-Host '通过：真实旧版正在运行，升级被拒绝，原程序没有替换。请从托盘退出旧版，再运行第 2 个入口。' -ForegroundColor Green
    }
    else {
        if ($isRunning) { throw '请先从托盘退出这个旧版副本，再运行退出后升级入口' }
        $beforeData = Get-DataSnapshot
        & $upgrade -TargetDirectory $target | ForEach-Object { Write-Host $_ }
        $report.newVersion = (Get-Item -LiteralPath $executable).VersionInfo.FileVersion
        $report.dataUnchanged = (Get-DataSnapshot) -ceq $beforeData
        if ($report.newVersion -ne "$($manifest.version).0" -or -not $report.dataUnchanged) { throw '升级版本或原 data 保持未通过，请保留副本和结果' }
        Write-Host '通过：真实旧版文件已升级，原 data 内容一致。现在请打开同一 old-version 目录中的拾刻.exe，人工确认启动、设置、坐标和预设。' -ForegroundColor Green
    }
    $report.status = 'passed'
    $exitCode = 0
}
catch {
    $report.status = 'failed'
    $report.error = $_.Exception.Message
    Write-Host ('未通过：' + $report.error) -ForegroundColor Red
}
finally {
    $report.finishedUtc = [DateTime]::UtcNow.ToString('o')
    $reportPath = Assert-ArtifactPath $resultRoot (Join-Path $resultRoot ($Mode + '-result.json'))
    [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($true))
    Write-Host "结果：$reportPath"
}
exit $exitCode
