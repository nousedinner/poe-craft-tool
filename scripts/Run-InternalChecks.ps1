[CmdletBinding()]
param()

# 用户已允许助手自行运行；实际通过仍须完整结果与日志，不能由编译推断。
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'Artifact-Paths.ps1')
$artifactRoot = Get-ArtifactRoot $projectRoot
$resultDirectory = New-CheckResultDirectory $artifactRoot '内部用例' @('build.log', 'tests.log', 'result.json')
$encoding = [Text.UTF8Encoding]::new($true)
$report = [ordered]@{
    schemaVersion = 1
    status = 'not_run'
    startedUtc = [DateTime]::UtcNow.ToString('o')
    finishedUtc = $null
    programVersion = $null
    sourceCommit = $null
    worktreeDirty = $null
    expectedCases = $null
    total = $null
    passed = $null
    failed = $null
    testExitCode = $null
    programSha256 = $null
    testsSha256 = $null
    testSourceSha256 = $null
    validationScope = 'internal_cases_only; game_audio_network_and_upgrade_not_accepted'
    error = $null
}

function Invoke-LoggedDotnet([string]$Arguments, [string]$LogName) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $dotnetPath
    $startInfo.Arguments = $Arguments
    $startInfo.WorkingDirectory = $projectRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
    $startInfo.StandardErrorEncoding = [Text.Encoding]::UTF8
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { throw '无法启动 .NET 命令' }
        # 同时读取两个流，避免大量失败输出把子进程卡在满管道中。
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(300000)) {
            $process.Kill()
            $null = $process.WaitForExit(5000)
            throw '命令超过 5 分钟，已终止本次子进程'
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $resultDirectory $LogName), $stdout + "`r`n--- stderr ---`r`n" + $stderr, $encoding)
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Stdout = $stdout }
    }
    finally { $process.Dispose() }
}

$scriptExitCode = 1
try {
    $dotnetPath = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
    $projectXml = [xml](Get-Content -LiteralPath (Join-Path $projectRoot '拾刻.csproj') -Raw -Encoding UTF8)
    $report.programVersion = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
    $testProject = Join-Path $projectRoot 'Tests\ShiKe.InternalTests.csproj'
    $testSourcePath = Join-Path $projectRoot 'Tests\Program.cs'
    $source = Get-Content -LiteralPath $testSourcePath -Raw -Encoding UTF8
    $startIndex = $source.IndexOf('var tests =')
    $endIndex = $source.IndexOf('var failed =')
    if ($startIndex -lt 0 -or $endIndex -le $startIndex) { throw '无法定位内部用例清单' }
    $report.expectedCases = ([regex]::Matches($source.Substring($startIndex, $endIndex - $startIndex), '(?m)^\s*\("')).Count
    if ($report.expectedCases -lt 1) { throw '内部用例清单为空' }
    $report.testSourceSha256 = (Get-FileHash -LiteralPath $testSourcePath -Algorithm SHA256).Hash
    if (Get-Command git -CommandType Application -ErrorAction SilentlyContinue) {
        $commitText = @(& git -C $projectRoot rev-parse HEAD 2>$null)
        if ($LASTEXITCODE -eq 0) {
            $report.sourceCommit = ($commitText -join '').Trim()
            $dirtyText = @(& git -C $projectRoot -c core.safecrlf=false status --porcelain 2>$null)
            if ($LASTEXITCODE -eq 0) { $report.worktreeDirty = $dirtyText.Count -gt 0 }
        }
    }

    Write-Host '正在编译内部用例，不启动日常拾刻或游戏；通知用例会短暂显示独立测试窗口。'
    $report.status = 'building'
    $build = Invoke-LoggedDotnet ('build "{0}" -c Release --no-restore --nologo' -f $testProject) 'build.log'
    if ($build.ExitCode -ne 0) { throw '编译失败，请查看 build.log；首次检出需先恢复项目依赖，并安装 .NET 10 SDK' }

    $testOutput = Join-Path $artifactRoot '内部用例\Release\net10.0-windows'
    $programDll = Join-Path $testOutput '拾刻.dll'
    $testsDll = Join-Path $testOutput 'ShiKe.InternalTests.dll'
    if ((Get-Item -LiteralPath $programDll).VersionInfo.FileVersion -ne "$($report.programVersion).0") { throw '测试目录中的程序版本与当前源码不一致' }
    $report.programSha256 = (Get-FileHash -LiteralPath $programDll -Algorithm SHA256).Hash
    $report.testsSha256 = (Get-FileHash -LiteralPath $testsDll -Algorithm SHA256).Hash

    Write-Host "正在执行 $($report.expectedCases) 项内部用例。"
    $report.status = 'running'
    $testRun = Invoke-LoggedDotnet ('"{0}"' -f $testsDll) 'tests.log'
    $report.testExitCode = $testRun.ExitCode
    $summaries = [regex]::Matches($testRun.Stdout, '(?m)^RESULT total=(\d+), passed=(\d+), failed=(\d+)\r?$')
    if ($summaries.Count -ne 1) { throw '未收到唯一完整的测试摘要，不记为通过' }
    $report.total = [int]$summaries[0].Groups[1].Value
    $report.passed = [int]$summaries[0].Groups[2].Value
    $report.failed = [int]$summaries[0].Groups[3].Value
    if ($testRun.ExitCode -ne 0 -or $report.total -ne $report.expectedCases -or
        $report.passed -ne $report.total -or $report.failed -ne 0) { throw '内部用例未全部通过，请查看 tests.log' }
    $report.status = 'passed'
    $scriptExitCode = 0
    Write-Host "内部用例通过：$($report.passed)/$($report.total)。真实游戏、音频、网络和升级仍须分别验收。" -ForegroundColor Green
}
catch {
    $report.status = 'failed'
    $report.error = $_.Exception.Message
    Write-Host "内部验证未通过：$($report.error)" -ForegroundColor Red
}
finally {
    $report.finishedUtc = [DateTime]::UtcNow.ToString('o')
    [IO.File]::WriteAllText((Join-Path $resultDirectory 'result.json'), ($report | ConvertTo-Json -Depth 4), $encoding)
    Write-Host "结果目录：$resultDirectory"
}
exit $scriptExitCode
