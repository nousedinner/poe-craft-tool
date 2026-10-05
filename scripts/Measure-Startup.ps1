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
        if ($firstFrame.Count -ne 1) { throw '缺少唯一首帧时间，拒绝计入结果' }
        if ($CheckLifecycle) {
            foreach ($stage in @('首帧前关闭被托盘接管', '恢复窗口与重复关闭验证完成')) {
                if (@($report.stages | Where-Object { $_.Name -eq $stage }).Count -ne 1) { throw "生命周期检查未通过：$stage" }
            }
        }
        $reports.Add([ordered]@{
            iteration = $iteration
            launchThroughExitMilliseconds = [Math]::Round($launchTimer.Elapsed.TotalMilliseconds, 2)
            firstFrameMilliseconds = $firstFrame[0].ProcessMilliseconds
            report = $report
        })
        Write-Host ("{0} 第 {1}/{2} 次：进程到首帧 {3:N0}ms" -f $Name, $iteration, $Count, $firstFrame[0].ProcessMilliseconds)
    }
    finally { $process.Dispose() }
}

$times = @($reports | ForEach-Object { [double]$_.firstFrameMilliseconds } | Sort-Object)
$middle = [int][Math]::Floor($times.Count / 2)
$median = if ($times.Count % 2 -eq 0) { ($times[$middle - 1] + $times[$middle]) / 2 } else { $times[$middle] }
$summary = [ordered]@{
    schemaVersion = 1
    candidate = $Name
    measuredUtc = [DateTime]::UtcNow.ToString('o')
    executableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
    executableBytes = (Get-Item -LiteralPath $executable).Length
    count = $Count
    lifecycleChecks = [bool]$CheckLifecycle
    medianFirstFrameMilliseconds = $median
    minFirstFrameMilliseconds = $times[0]
    maxFirstFrameMilliseconds = $times[-1]
    scope = '实际 WPF 首帧；跳过热键与网络；数据为隔离副本；文件缓存不受控，不能代表重启后的冷启动或双击全链路'
    runs = $reports.ToArray()
}
[IO.File]::WriteAllText($resultPath, ($summary | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($true))
Write-Host ("{0} 首帧中位数 {1:N0}ms，最短 {2:N0}ms，最长 {3:N0}ms" -f $Name, $median, $times[0], $times[-1])
