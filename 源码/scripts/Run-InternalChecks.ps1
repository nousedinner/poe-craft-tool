[CmdletBinding()]
param([switch]$NoRestore)

# 用户已允许助手自行运行；实际通过仍须完整结果与日志，不能由编译推断。
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'Artifact-Paths.ps1')
$artifactRoot = Get-ArtifactRoot $projectRoot
$resultDirectory = New-CheckResultDirectory $artifactRoot '内部用例' @('build.log', 'tests.log', 'core-build.log', 'core-tests.log', 'result.json')
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
    buildExitCode = $null
    mainSuiteStatus = 'not_run'
    noRestore = [bool]$NoRestore
    coreSuite = [ordered]@{ status='not_run'; expectedCases=$null; total=$null; passed=$null; failed=$null; buildExitCode=$null; testExitCode=$null; error=$null }
    programSha256 = $null
    testsSha256 = $null
    testSourceSha256 = $null
    validationScope = 'desktop_internal_and_independent_core_suites; suites_reported_separately_with_shared_cases_not_unique_coverage; real_game_audio_and_network_not_run'
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
$suiteErrors = [Collections.Generic.List[string]]::new()
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

    $restoreOption = if ($NoRestore) { ' --no-restore' } else { '' }
    try {
    $report.mainSuiteStatus = 'building'
    Write-Host '正在编译内部用例，默认检查并恢复所需依赖；不启动日常拾刻或游戏。'
    $report.status = 'building'
    $build = Invoke-LoggedDotnet ('build "{0}" -c Release{1} --nologo' -f $testProject,$restoreOption) 'build.log'
    $report.buildExitCode = $build.ExitCode
    if ($build.ExitCode -ne 0) { throw '主套件编译失败，请查看 build.log；需要 .NET 10 SDK，-NoRestore 模式需事先恢复依赖' }

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
    $report.mainSuiteStatus = 'passed'
    Write-Host "主套件通过：$($report.passed)/$($report.total)。" -ForegroundColor Green
    } catch {
        $report.mainSuiteStatus = 'failed'
        $suiteErrors.Add($_.Exception.Message)
    }

    try {
        $coreProject = Join-Path $projectRoot 'CoreTests\ShiKe.CoreTests.csproj'
        $coreProgram = Get-Content -LiteralPath (Join-Path $projectRoot 'CoreTests\Program.cs') -Raw -Encoding UTF8
        $coreExpected = 0
        foreach ($suite in @([regex]::Matches($coreProgram, 'typeof\(([A-Z]\w*)\)') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)) {
            $suitePath = Join-Path $projectRoot ('CoreTests\'+$suite+'.cs')
            if (-not (Test-Path -LiteralPath $suitePath)) { $suitePath = Join-Path $projectRoot ('Tests\'+$suite+'.cs') }
            $suiteText = Get-Content -LiteralPath $suitePath -Raw -Encoding UTF8
            $coreExpected += [regex]::Matches($suiteText, 'public\s+static\s+void\s+\w+\s*\(\s*\)').Count
        }
        if ($coreExpected -lt 1) { throw '无法确认独立核心套件用例清单' }
        $report.coreSuite.expectedCases = $coreExpected
        $report.coreSuite.status = 'building'
        $coreBuild = Invoke-LoggedDotnet ('build "{0}" -c Release{1} --nologo' -f $coreProject,$restoreOption) 'core-build.log'
        $report.coreSuite.buildExitCode = $coreBuild.ExitCode
        if ($coreBuild.ExitCode -ne 0) { throw '独立核心套件编译失败，请查看 core-build.log' }
        $report.coreSuite.status = 'running'
        $coreDll = Join-Path $artifactRoot '核心用例\Release\net10.0\ShiKe.CoreTests.dll'
        $coreRun = Invoke-LoggedDotnet ('"{0}"' -f $coreDll) 'core-tests.log'
        $report.coreSuite.testExitCode = $coreRun.ExitCode
        $coreSummaries = [regex]::Matches($coreRun.Stdout, '(?m)^RESULT total=(\d+), passed=(\d+), failed=(\d+)\r?$')
        if ($coreSummaries.Count -ne 1) { throw '独立核心套件未返回唯一完整摘要' }
        $report.coreSuite.total = [int]$coreSummaries[0].Groups[1].Value
        $report.coreSuite.passed = [int]$coreSummaries[0].Groups[2].Value
        $report.coreSuite.failed = [int]$coreSummaries[0].Groups[3].Value
        if ($coreRun.ExitCode -ne 0 -or $report.coreSuite.total -ne $coreExpected -or
            $report.coreSuite.passed -ne $coreExpected -or $report.coreSuite.failed -ne 0) { throw '独立核心套件未全部通过，请查看 core-tests.log' }
        $report.coreSuite.status = 'passed'
        Write-Host "独立核心套件通过：$($report.coreSuite.passed)/$($report.coreSuite.total)（含复用主套件的16项，不合计为新增覆盖）。" -ForegroundColor Green
    } catch {
        $report.coreSuite.status = 'failed'
        $report.coreSuite.error = $_.Exception.Message
        $suiteErrors.Add($_.Exception.Message)
    }
    if ($suiteErrors.Count -gt 0) { throw ($suiteErrors -join '；') }
    $report.status = 'passed'
    $scriptExitCode = 0
    Write-Host '两套验证均通过；不代表真实游戏、音频或联网验收。' -ForegroundColor Green
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
