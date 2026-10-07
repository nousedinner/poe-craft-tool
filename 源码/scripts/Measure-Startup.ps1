[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('基线', '预编译', '优化', '最终发布')]
    [string]$Name,
    [ValidateRange(3, 12)][int]$Count = 7,
    [switch]$CheckLifecycle
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'Artifact-Paths.ps1')
$artifactRoot = Get-ArtifactRoot $projectRoot
$profileRoot = Assert-ArtifactPath $artifactRoot (Join-Path $artifactRoot '验证记录\启动性能')
Assert-GeneratedDirectory $artifactRoot $profileRoot '启动性能'
$candidate = Assert-ArtifactPath $profileRoot (Join-Path $profileRoot $Name)
Assert-GeneratedDirectory $artifactRoot $candidate '启动性能副本'
$inputDirectory = Assert-ArtifactPath $profileRoot (Join-Path $profileRoot '输入数据')
Assert-GeneratedDirectory $artifactRoot $inputDirectory '启动测量输入'
$sourceData = Join-Path $inputDirectory 'data'
$executable = Join-Path $candidate '拾刻.exe'
$resultSuffix = if ($CheckLifecycle) { '-lifecycle-results.json' } else { '-results.json' }
$resultPath = Assert-ArtifactPath $profileRoot (Join-Path $profileRoot ($Name + $resultSuffix))
$reports = [Collections.Generic.List[object]]::new()

for ($iteration = 1; $iteration -le $Count; $iteration++) {
    # 拒绝覆盖任何已运行副本；超时只终止本次直接创建的测量子进程。
    $existing = @(Get-Process -Name '拾刻' -ErrorAction SilentlyContinue | Where-Object {
        [string]::Equals($_.Path, $executable, [StringComparison]::OrdinalIgnoreCase)
    })
    if ($existing.Count -gt 0) { throw '测量副本仍在运行，已保留原目录' }
    $dataDirectory = Assert-ArtifactPath $candidate (Join-Path $candidate 'data')
    Assert-GeneratedDirectory $artifactRoot $candidate '启动性能副本'
    if (Test-Path -LiteralPath $dataDirectory) { Remove-Item -LiteralPath $dataDirectory -Recurse -Force }
    Copy-Item -LiteralPath $sourceData -Destination $dataDirectory -Recurse
    $beforeRulesHash = $null
    if ($CheckLifecycle) {
        # 只修改隔离副本：用明确的非默认配置证明首帧前关闭不会把空 UI 写回。
        $settingsPath = Join-Path $dataDirectory 'settings.json'
        $settingsProbe = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $settingsProbe.craft | Add-Member -NotePropertyName delay_ms -NotePropertyValue 47 -Force
        $settingsProbe.craft | Add-Member -NotePropertyName mode2_scour_alch -NotePropertyValue $true -Force
        $settingsProbe.craft | Add-Member -NotePropertyName use_exalt -NotePropertyValue $true -Force
        $settingsProbe.host | Add-Member -NotePropertyName gradient_speed -NotePropertyValue 4 -Force
        [IO.File]::WriteAllText($settingsPath, ($settingsProbe | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($true))
        $rulesPath = Join-Path $dataDirectory 'rules.json'
        $rulesProbe = Get-Content -LiteralPath $rulesPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $rulesProbe | Add-Member -NotePropertyName primary_affixes -NotePropertyValue @('关闭前保留的主词缀') -Force
        $rulesProbe | Add-Member -NotePropertyName primary_hit_count -NotePropertyValue 1 -Force
        [IO.File]::WriteAllText($rulesPath, ($rulesProbe | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($true))
        $beforeRulesHash = (Get-FileHash -LiteralPath $rulesPath).Hash
    }
    $reportPath = Assert-ArtifactPath $candidate (Join-Path $candidate 'startup-profile.json')
    if (Test-Path -LiteralPath $reportPath) { Remove-Item -LiteralPath $reportPath }

    $launchTimer = [Diagnostics.Stopwatch]::StartNew()
    $profileArguments = @('--startup-profile')
    if ($CheckLifecycle) { $profileArguments += '--startup-lifecycle-check' }
    $process = Start-Process -FilePath $executable -ArgumentList $profileArguments -WorkingDirectory $candidate -WindowStyle Hidden -PassThru
    try {
        if (-not $process.WaitForExit(30000)) {
            $process.Kill()
            $process.WaitForExit()
            throw '隔离启动测量超时；仅终止本次子进程'
        }
        $launchTimer.Stop()
        if ($process.ExitCode -ne 0) { throw "隔离测量退出码 $($process.ExitCode)" }
        if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) { throw '未收到真实首帧报告' }
        $report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($report.processId -ne $process.Id) { throw '报告不属于本次测量子进程' }
        $firstFrame = @($report.stages | Where-Object { $_.Name -eq '主窗口首帧完成' })
        $firstFrameMilliseconds = $null
        if (-not $CheckLifecycle) {
            if ($firstFrame.Count -ne 1) { throw '缺少唯一首帧时间，拒绝计入结果' }
            $firstFrameMilliseconds = $firstFrame[0].ProcessMilliseconds
        }
        foreach ($stage in @('主窗口关闭退出请求', '关闭流程清理完成')) {
            if (@($report.stages | Where-Object { $_.Name -eq $stage }).Count -ne 1) { throw "关闭退出检查未通过：$stage" }
        }
        if ($CheckLifecycle) {
            if (@($report.stages | Where-Object { $_.Name -eq '首帧前关闭请求' }).Count -ne 1 -or $firstFrame.Count -ne 0) { throw '没有在首帧前完成关闭退出' }
            if ((Get-FileHash -LiteralPath $rulesPath).Hash -ne $beforeRulesHash) { throw '首帧前关闭覆盖了已有规则' }
            $saved = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($saved.craft.delay_ms -ne 47 -or $saved.craft.mode2_scour_alch -ne $true -or $saved.craft.use_exalt -ne $true) { throw '首帧前关闭覆盖了已加载的洗装设置' }
            if ($saved.host.gradient_speed -ne 4) { throw '首帧前关闭覆盖了已保存的流动速度' }
        }
        $reports.Add([ordered]@{
            iteration = $iteration
            launchThroughExitMilliseconds = [Math]::Round($launchTimer.Elapsed.TotalMilliseconds, 2)
            firstFrameMilliseconds = $firstFrameMilliseconds
            earlyCloseRulesAndSettingsPreserved = if ($CheckLifecycle) { $true } else { $null }
            report = $report
        })
        if ($CheckLifecycle) { Write-Host ("{0} 第 {1}/{2} 次：首帧前关闭退出通过" -f $Name, $iteration, $Count) }
        else { Write-Host ("{0} 第 {1}/{2} 次：进程到首帧 {3:N0}ms，关闭退出通过" -f $Name, $iteration, $Count, $firstFrameMilliseconds) }
    }
    finally { $process.Dispose() }
}

$times = @($reports | Where-Object { $null -ne $_.firstFrameMilliseconds } | ForEach-Object { [double]$_.firstFrameMilliseconds } | Sort-Object)
$middle = [int][Math]::Floor($times.Count / 2)
$median = if ($times.Count -eq 0) { $null } elseif ($times.Count % 2 -eq 0) { ($times[$middle - 1] + $times[$middle]) / 2 } else { $times[$middle] }
$summary = [ordered]@{
    schemaVersion = 1
    candidate = $Name
    measuredUtc = [DateTime]::UtcNow.ToString('o')
    executableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
    executableBytes = (Get-Item -LiteralPath $executable).Length
    count = $Count
    lifecycleChecks = [bool]$CheckLifecycle
    medianFirstFrameMilliseconds = $median
    minFirstFrameMilliseconds = if ($times.Count -gt 0) { $times[0] } else { $null }
    maxFirstFrameMilliseconds = if ($times.Count -gt 0) { $times[-1] } else { $null }
    scope = '实际 WPF 关闭退出与清理；普通模式含首帧时间，生命周期模式为首帧前关闭；跳过热键、网络与真实输入；文件缓存不受控'
    runs = $reports.ToArray()
}
[IO.File]::WriteAllText($resultPath, ($summary | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($true))
if ($times.Count -gt 0) { Write-Host ("{0} 首帧中位数 {1:N0}ms，最短 {2:N0}ms，最长 {3:N0}ms" -f $Name, $median, $times[0], $times[-1]) }
else { Write-Host ("{0} 首帧前关闭退出 {1} 次通过" -f $Name, $Count) }
