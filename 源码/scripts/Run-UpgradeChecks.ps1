[CmdletBinding()]
param([string]$PackageDirectory)

# 仅由用户主动运行：在固定验证目录的独立副本操作文件，不启动拾刻或发送输入。
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$utf8 = [Text.UTF8Encoding]::new($true)
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'Artifact-Paths.ps1')
$artifactRoot = Get-ArtifactRoot $projectRoot
$resultDirectory = New-CheckResultDirectory $artifactRoot '升级副本' @('checks.log', 'result.json')
$caseResults = [Collections.Generic.List[object]]::new()
$logLines = [Collections.Generic.List[string]]::new()
$report = [ordered]@{
    schemaVersion = 1
    status = 'not_run'
    startedUtc = [DateTime]::UtcNow.ToString('o')
    finishedUtc = $null
    powershellVersion = $PSVersionTable.PSVersion.ToString()
    packageDirectory = $null
    programVersion = $null
    manifestSha256 = $null
    total = 0
    passed = 0
    failed = 0
    cases = @()
    validationScope = 'isolated_file_upgrade_only; application_start_running_process_real_old_install_and_permissions_not_accepted'
    error = $null
}

function Resolve-TestPath([string]$Root, [string]$Relative) {
    if ([IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':') -or $Relative -match '(^|[\\/])\.\.([\\/]|$)') { throw '验证路径不是合法相对路径' }
    $resolved = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (-not $resolved.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '验证路径超出副本目录' }
    return $resolved
}

function New-TestDirectory([string]$Name) {
    $directory = Resolve-TestPath $resultDirectory $Name
    $null = New-Item -ItemType Directory -Path $directory
    return $directory
}

function Write-TestText([string]$Root, [string]$Relative, [string]$Text) {
    $path = Resolve-TestPath $Root $Relative
    $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($path)) -Force
    [IO.File]::WriteAllText($path, $Text, $utf8)
}

function Copy-TestPackage([string]$Name) {
    $copy = New-TestDirectory $Name
    foreach ($relative in @($manifest.files | ForEach-Object { [string]$_.path }) + @('release-manifest.json')) {
        $source = Resolve-TestPath $packageRoot $relative
        $destination = Resolve-TestPath $copy $relative
        $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force
        Copy-Item -LiteralPath $source -Destination $destination
    }
    return $copy
}

function New-TestTarget([string]$Name) {
    $target = New-TestDirectory $Name
    # 旧程序文件用固定字节代替；只验证文件替换，不宣称真实旧版可启动。
    Write-TestText $target '拾刻.exe' 'old-program-placeholder'
    Write-TestText $target 'Upgrade-ShiKe.ps1' 'old-upgrade-placeholder'
    return $target
}

function Get-TestSnapshot([string]$Root) {
    $files = @(Get-ChildItem -LiteralPath $Root -File -Recurse | ForEach-Object {
        $relative = $_.FullName.Substring($Root.TrimEnd('\').Length + 1)
        if (-not $relative.StartsWith('upgrade-backups\', [StringComparison]::OrdinalIgnoreCase)) {
            [ordered]@{ path = $relative; hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        }
    } | Sort-Object { $_.path })
    return ConvertTo-Json -InputObject $files -Depth 4 -Compress
}

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Invoke-TestUpgrade([string]$Source, [string]$Target) {
    $scriptPath = Resolve-TestPath $Source 'Upgrade-ShiKe.ps1'
    $null = & $scriptPath -TargetDirectory $Target
}

function Invoke-ExpectedUpgradeFailure([string]$Source, [string]$Target) {
    try { Invoke-TestUpgrade $Source $Target }
    catch { return $_.Exception.Message }
    throw '预期升级失败，但升级报告成功'
}

function Set-TestOrder([string]$Source, [string[]]$FirstPaths) {
    $path = Resolve-TestPath $Source 'release-manifest.json'
    $copyManifest = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    $first = @(foreach ($firstPath in $FirstPaths) { $copyManifest.files | Where-Object { $_.path -eq $firstPath } })
    Assert-Check ($first.Count -eq $FirstPaths.Count) '发布清单缺少用于失败恢复验证的文件'
    $copyManifest.files = @($first) + @($copyManifest.files | Where-Object { $_.path -notin $FirstPaths })
    [IO.File]::WriteAllText($path, ($copyManifest | ConvertTo-Json -Depth 5), $utf8)
}

$exitCode = 1
try {
    $projectXml = [xml](Get-Content -LiteralPath (Join-Path $projectRoot '拾刻.csproj') -Raw -Encoding UTF8)
    $version = [string]$projectXml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
    if ([string]::IsNullOrWhiteSpace($PackageDirectory)) {
        $PackageDirectory = Join-Path $artifactRoot '发布'
        if (-not (Test-Path -LiteralPath (Join-Path $PackageDirectory 'release-manifest.json') -PathType Leaf)) { throw "没有找到 $version 的发布包，请先完成发布编译" }
    }
    $packageRoot = [IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\')
    $manifestPath = Resolve-TestPath $packageRoot 'release-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.version -ne $version) { throw '副本验证的发布包版本与当前源码不同' }
    $report.packageDirectory = $packageRoot
    $report.programVersion = $version
    $report.manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash

    $cases = @(
        @{ Name = '无 BOM 的 UTF-8 中文清单升级并保留整个 data'; Run = {
            $source = Copy-TestPackage 'utf8-source'
            $path = Resolve-TestPath $source 'release-manifest.json'
            [IO.File]::WriteAllText($path, [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8), [Text.UTF8Encoding]::new($false))
            foreach ($keepBuiltIn in @($true, $false)) {
                $target = New-TestTarget ('utf8-target-' + $keepBuiltIn)
                Write-TestText $target 'data/settings.json' '{"host":{"target_process":"测试游戏.exe"}}'
                Write-TestText $target 'data/rules.json' '{"primary_affixes":["用户词缀"]}'
                Write-TestText $target 'data/coordinates.json' '{"item":[123,456]}'
                Write-TestText $target 'data/presets/自建.json' '{"primary_affixes":["自建词缀"]}'
                if ($keepBuiltIn) { Write-TestText $target 'data/presets/武器.json' '{"primary_affixes":["用户修改内置预设"]}' }
                $data = Resolve-TestPath $target 'data'
                $before = Get-TestSnapshot $data
                Invoke-TestUpgrade $source $target
                Assert-Check ((Get-TestSnapshot $data) -eq $before) '已有 data 内容变化，或删除的内置预设被补回'
                Assert-Check ((Get-FileHash -LiteralPath (Resolve-TestPath $target '拾刻.exe')).Hash -eq (Get-FileHash -LiteralPath (Resolve-TestPath $source '拾刻.exe')).Hash) '中文主程序未正确替换'
            }
        }}
        @{ Name = '空目录首次安装复制全部载荷'; Run = {
            $target = New-TestDirectory 'fresh-target'
            Invoke-TestUpgrade $packageRoot $target
            foreach ($entry in $manifest.files) {
                Assert-Check ((Get-FileHash -LiteralPath (Resolve-TestPath $target ([string]$entry.path))).Hash -eq $entry.sha256) "首次安装文件不一致：$($entry.path)"
            }
        }}
        @{ Name = '源文件缺失拒绝升级且不误报回滚'; Run = {
            $source = Copy-TestPackage 'missing-source'
            $target = New-TestTarget 'missing-target'
            $before = Get-TestSnapshot $target
            Remove-Item -LiteralPath (Resolve-TestPath $source '拾刻.exe')
            $message = Invoke-ExpectedUpgradeFailure $source $target
            Assert-Check ($message.Contains('发布文件缺失') -and $message.Contains('尚未替换任何目标文件')) '源文件缺失提示没有正确区分替换前失败'
            Assert-Check ((Get-TestSnapshot $target) -eq $before) '源文件缺失时改动了目标'
        }}
        @{ Name = '源文件哈希不符拒绝升级'; Run = {
            $source = Copy-TestPackage 'hash-source'
            $target = New-TestTarget 'hash-target'
            $before = Get-TestSnapshot $target
            Write-TestText $source '使用与升级说明.txt' 'damaged-source'
            $message = Invoke-ExpectedUpgradeFailure $source $target
            Assert-Check ($message.Contains('发布文件校验失败')) '源文件损坏没有被清单拦截'
            Assert-Check ((Get-TestSnapshot $target) -eq $before) '源文件损坏时改动了目标'
        }}
        @{ Name = '部分替换后失败恢复原文件'; Run = {
            $source = Copy-TestPackage 'replace-source'
            Set-TestOrder $source @('拾刻.exe', 'Upgrade-ShiKe.ps1')
            $target = New-TestTarget 'replace-target'
            $before = Get-TestSnapshot $target
            $lockedPath = Resolve-TestPath $target 'Upgrade-ShiKe.ps1'
            $locked = [IO.File]::Open($lockedPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            try { $message = Invoke-ExpectedUpgradeFailure $source $target }
            finally { $locked.Dispose() }
            Assert-Check ($message.Contains('已恢复本次替换的 1 个文件')) '没有实际完成一次替换后的恢复'
            Assert-Check ((Get-TestSnapshot $target) -eq $before) '部分替换失败未恢复原文件内容'
        }}
        @{ Name = '部分安装后失败移除本次新增文件'; Run = {
            $source = Copy-TestPackage 'newfile-source'
            Set-TestOrder $source @('使用与升级说明.txt', '拾刻.exe')
            $target = New-TestTarget 'newfile-target'
            $before = Get-TestSnapshot $target
            $lockedPath = Resolve-TestPath $target '拾刻.exe'
            $locked = [IO.File]::Open($lockedPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            try { $message = Invoke-ExpectedUpgradeFailure $source $target }
            finally { $locked.Dispose() }
            Assert-Check ($message.Contains('已恢复本次替换的 1 个文件')) '没有进入新增文件后的失败恢复'
            Assert-Check ((Get-TestSnapshot $target) -eq $before) '失败后留下了本次新增的载荷文件'
        }}
        @{ Name = '首次安装中途失败清空本次目录并允许重试'; Run = {
            $source = Copy-TestPackage 'fresh-failure-source'
            # 该文件与升级过程创建的备份目录冲突；在预设和 EXE 已复制后确定性失败。
            Write-TestText $source 'upgrade-backups' 'directory-conflict'
            $path = Resolve-TestPath $source 'release-manifest.json'
            $copyManifest = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
            $conflictPath = Resolve-TestPath $source 'upgrade-backups'
            $copyManifest.files = @($copyManifest.files) + @([pscustomobject]@{ path = 'upgrade-backups'; size = (Get-Item -LiteralPath $conflictPath).Length; sha256 = (Get-FileHash -LiteralPath $conflictPath).Hash })
            [IO.File]::WriteAllText($path, ($copyManifest | ConvertTo-Json -Depth 5), $utf8)
            Set-TestOrder $source @('data/presets/武器.json', '拾刻.exe', 'upgrade-backups')
            $target = New-TestDirectory 'fresh-failure-target'
            $message = Invoke-ExpectedUpgradeFailure $source $target
            Assert-Check ($message.Contains('已恢复本次替换的 2 个文件')) '没有进入预设和主程序安装后的失败恢复'
            Assert-Check (@(Get-ChildItem -LiteralPath $target -Force).Count -eq 0) '失败后残留目录，重试可能拒绝或漏装预设'
            Invoke-TestUpgrade $packageRoot $target
            Assert-Check (Test-Path -LiteralPath (Resolve-TestPath $target 'data/presets/武器.json') -PathType Leaf) '首次安装失败后重试未恢复内置预设'
        }}
        @{ Name = '相对路径别名不能绕过用户配置保护'; Run = {
            $source = Copy-TestPackage 'data-alias-source'
            $target = New-TestTarget 'data-alias-target'
            $before = Get-TestSnapshot $target
            Write-TestText $source 'data/settings.json' '{"source":"must_not_install"}'
            $path = Resolve-TestPath $source 'release-manifest.json'
            $copyManifest = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
            $privatePath = Resolve-TestPath $source 'data/settings.json'
            $copyManifest.files = @($copyManifest.files) + @([pscustomobject]@{ path = './data/settings.json'; size = (Get-Item -LiteralPath $privatePath).Length; sha256 = (Get-FileHash -LiteralPath $privatePath).Hash })
            [IO.File]::WriteAllText($path, ($copyManifest | ConvertTo-Json -Depth 5), $utf8)
            $message = Invoke-ExpectedUpgradeFailure $source $target
            Assert-Check ($message.Contains('发布包不得包含用户配置')) '规范化后的用户配置路径没有被拦截'
            Assert-Check ((Get-TestSnapshot $target) -eq $before) '非法载荷改动了目标目录'
        }}
    )
    $report.status = 'running'
    foreach ($case in $cases) {
        try {
            & $case.Run
            $caseResults.Add([pscustomobject]@{ name = $case.Name; passed = $true; error = $null })
            $line = 'PASS ' + $case.Name
        }
        catch {
            $caseResults.Add([pscustomobject]@{ name = $case.Name; passed = $false; error = $_.Exception.Message })
            $line = 'FAIL ' + $case.Name + ': ' + $_.Exception.Message
        }
        $logLines.Add($line)
        Write-Host $line
    }
    $report.total = $cases.Count
    $report.passed = @($caseResults | Where-Object { $_.passed }).Count
    $report.failed = $report.total - $report.passed
    $report.cases = @($caseResults.ToArray())
    $summary = "RESULT total=$($report.total), passed=$($report.passed), failed=$($report.failed)"
    $logLines.Add($summary)
    Write-Host $summary
    $report.status = if ($report.failed -eq 0) { 'passed' } else { 'failed' }
    if ($report.failed -eq 0) { $exitCode = 0 }
}
catch {
    $report.status = 'failed'
    $report.error = $_.Exception.Message
    $logLines.Add('SETUP FAIL: ' + $_.Exception.Message)
    Write-Host ('升级副本验证未完成：' + $_.Exception.Message) -ForegroundColor Red
}
finally {
    $report.finishedUtc = [DateTime]::UtcNow.ToString('o')
    [IO.File]::WriteAllLines((Join-Path $resultDirectory 'checks.log'), $logLines, $utf8)
    [IO.File]::WriteAllText((Join-Path $resultDirectory 'result.json'), ($report | ConvertTo-Json -Depth 6), $utf8)
    Write-Host "结果和所有副本保留在：$resultDirectory"
    Write-Host '以上仅验证副本中的文件操作；真实旧版升级、运行中拒绝和应用启动仍需分别确认。'
}
exit $exitCode
