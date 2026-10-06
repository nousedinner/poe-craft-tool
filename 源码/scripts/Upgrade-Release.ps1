[CmdletBinding()]
param([string]$TargetDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$packageRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$showDialogs = [string]::IsNullOrWhiteSpace($TargetDirectory)
$upgradeJournal = [Collections.Generic.List[object]]::new()
$temporaryFiles = [Collections.Generic.List[string]]::new()
$createdDirectories = [Collections.Generic.List[string]]::new()
$verifiedFiles = [Collections.Generic.List[object]]::new()
$targetRoot = $null
$backupRoot = $null
$targetWasEmpty = $false

function Resolve-ContainedPath([string]$Root, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':') -or $Relative -match '(^|[\\/])\.\.([\\/]|$)') {
        throw '发布清单包含非法相对路径'
    }
    $resolved = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (-not $resolved.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '文件路径超出指定目录' }
    return $resolved
}

function Assert-NoReparsePoint([string]$Root, [string]$Path) {
    $currentPath = $Path
    while ($currentPath.Length -ge $Root.Length) {
        if (Test-Path -LiteralPath $currentPath) {
            if (((Get-Item -LiteralPath $currentPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw '升级目录包含目录链接，请选择普通程序目录' }
        }
        if ([string]::Equals($currentPath, $Root, [StringComparison]::OrdinalIgnoreCase)) { break }
        $currentPath = [IO.Path]::GetDirectoryName($currentPath)
    }
}

function Clear-UpgradeTemporaryFiles {
    foreach ($temporary in $temporaryFiles) {
        try {
            if (-not $targetRoot -or -not $temporary.StartsWith($targetRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '拒绝清理目标目录之外的路径' }
            Assert-NoReparsePoint $targetRoot $temporary
            if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary }
        }
        catch { "临时文件未能清理：$temporary，$($_.Exception.Message)" }
    }
}

function Install-OneFile([string]$Source, [string]$Relative) {
    $destination = Resolve-ContainedPath $targetRoot $Relative
    Assert-NoReparsePoint $targetRoot $destination
    if (Test-Path -LiteralPath $destination -PathType Container) { throw "目标文件被目录占用：$Relative" }
    $backup = Resolve-ContainedPath $backupRoot $Relative
    $wasPresent = Test-Path -LiteralPath $destination -PathType Leaf
    if ($wasPresent) {
        $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($backup)) -Force
        Copy-Item -LiteralPath $destination -Destination $backup
    }
    $destinationDirectory = [IO.Path]::GetDirectoryName($destination)
    $missingDirectory = $destinationDirectory
    while (-not [string]::Equals($missingDirectory, $targetRoot, [StringComparison]::OrdinalIgnoreCase) -and
           -not (Test-Path -LiteralPath $missingDirectory)) {
        if (-not $missingDirectory.StartsWith($targetRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '拒绝创建目标目录之外的路径' }
        $createdDirectories.Add($missingDirectory)
        $missingDirectory = [IO.Path]::GetDirectoryName($missingDirectory)
    }
    $null = New-Item -ItemType Directory -Path $destinationDirectory -Force
    $temporary = $destination + '.upgrade-' + [guid]::NewGuid().ToString('N') + '.tmp'
    $temporaryFiles.Add($temporary)
    Copy-Item -LiteralPath $Source -Destination $temporary
    # Windows PowerShell 5.1 会把 $null 转成空字符串；File.Replace 将其视为非法备份路径。
    # 原文件已单独备份，这里用 NullString 传递真正的 .NET null，保留原子替换。
    if ($wasPresent) { [IO.File]::Replace($temporary, $destination, [Management.Automation.Language.NullString]::Value) }
    else { [IO.File]::Move($temporary, $destination) }
    $upgradeJournal.Add([pscustomobject]@{ Destination = $destination; Backup = $backup; WasPresent = $wasPresent })
}

try {
    $manifestPath = Join-Path $packageRoot 'release-manifest.json'
    # PowerShell 5.1 的默认文本编码不是 UTF-8；清单中的中文路径必须显式读取。
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.runtime -ne 'win-x64' -or
        $manifest.frameworkDependent -isnot [bool] -or -not $manifest.frameworkDependent -or
        $manifest.version -notmatch '^\d+\.\d+\.\d+$') { throw '发布清单格式不受支持' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifest.files) {
        $source = Resolve-ContainedPath $packageRoot ([string]$entry.path)
        # 使用最终解析的相对路径，避免 ./data/settings.json 绕过数据保护或重复路径检查。
        $relative = $source.Substring($packageRoot.TrimEnd('\').Length + 1).Replace('\', '/')
        if (-not $seen.Add($relative)) { throw '发布清单存在重复路径' }
        if ($relative -eq 'release-manifest.json') { throw '发布清单不能把自身列为载荷文件' }
        Assert-NoReparsePoint $packageRoot $source
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "发布文件缺失：$($entry.path)" }
        if ((Get-Item -LiteralPath $source).Length -ne $entry.size -or
            (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.sha256) { throw "发布文件校验失败：$($entry.path)" }
        if ($relative.StartsWith('data/', [StringComparison]::OrdinalIgnoreCase) -and -not $relative.StartsWith('data/presets/', [StringComparison]::OrdinalIgnoreCase)) { throw '发布包不得包含用户配置或诊断文件' }
        $verifiedFiles.Add([pscustomobject]@{ Source = $source; Relative = $relative })
    }
    if (-not $seen.Contains('拾刻.exe')) { throw '发布清单缺少主程序' }
    if ((Get-Item -LiteralPath (Join-Path $packageRoot '拾刻.exe')).VersionInfo.FileVersion -ne "$($manifest.version).0") { throw '主程序与清单版本不一致' }

    if ($showDialogs) {
        Add-Type -AssemblyName System.Windows.Forms
        $picker = [Windows.Forms.FolderBrowserDialog]::new()
        try {
            $picker.Description = '选择原拾刻程序目录；首次安装请选择空目录'
            if ($picker.ShowDialog() -ne [Windows.Forms.DialogResult]::OK) { return }
            $TargetDirectory = $picker.SelectedPath
        }
        finally { $picker.Dispose() }
    }
    $targetRoot = [IO.Path]::GetFullPath($TargetDirectory).TrimEnd('\')
    if ($targetRoot -eq [IO.Path]::GetPathRoot($targetRoot).TrimEnd('\')) { throw '不能安装到磁盘根目录' }
    if ([string]::Equals($targetRoot, $packageRoot.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) { throw '请先解压到独立目录，再选择原程序目录' }
    if (-not (Test-Path -LiteralPath $targetRoot -PathType Container)) { throw '目标目录不存在' }
    Assert-NoReparsePoint $targetRoot $targetRoot
    $targetExecutable = Resolve-ContainedPath $targetRoot '拾刻.exe'
    $targetWasEmpty = @(Get-ChildItem -LiteralPath $targetRoot -Force).Count -eq 0
    if (-not (Test-Path -LiteralPath $targetExecutable) -and -not $targetWasEmpty) { throw '非空目录中没有拾刻.exe，请确认选中了原程序目录' }
    foreach ($running in @(Get-Process -Name '拾刻' -ErrorAction SilentlyContinue)) {
        try {
            if ($running.Path -and [string]::Equals($running.Path, $targetExecutable, [StringComparison]::OrdinalIgnoreCase)) { throw '请先从托盘退出原拾刻，再升级' }
        }
        finally { $running.Dispose() }
    }

    $dataDirectory = Resolve-ContainedPath $targetRoot 'data'
    $preserveData = Test-Path -LiteralPath $dataDirectory
    $backupRelative = 'upgrade-backups\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 6)
    $backupRoot = Resolve-ContainedPath $targetRoot $backupRelative
    Assert-NoReparsePoint $targetRoot $backupRoot
    $null = New-Item -ItemType Directory -Path $backupRoot
    foreach ($entry in $verifiedFiles) {
        if ($preserveData -and $entry.Relative.StartsWith('data/', [StringComparison]::OrdinalIgnoreCase)) { continue }
        Install-OneFile $entry.Source $entry.Relative
    }
    Install-OneFile $manifestPath 'release-manifest.json'
    $message = "拾刻 $($manifest.version) 已复制到：`n$targetRoot`n`n已有 data 目录保持原样。程序备份位于：`n$backupRoot"
    if ($showDialogs) { $null = [Windows.Forms.MessageBox]::Show($message, '升级完成') }
    else { Write-Output $message }
}
catch {
    $upgradeFailure = $_.Exception.Message
    $rollbackFailures = [Collections.Generic.List[string]]::new()
    for ($index = $upgradeJournal.Count - 1; $index -ge 0; $index--) {
        $change = $upgradeJournal[$index]
        try {
            if (-not $change.Destination.StartsWith($targetRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '拒绝恢复目标目录之外的路径' }
            Assert-NoReparsePoint $targetRoot $change.Destination
            if ($change.WasPresent) { Copy-Item -LiteralPath $change.Backup -Destination $change.Destination -Force }
            elseif (Test-Path -LiteralPath $change.Destination -PathType Leaf) { Remove-Item -LiteralPath $change.Destination }
        }
        catch { $rollbackFailures.Add($_.Exception.Message) }
    }
    # 先清理未安装的临时文件，避免它们使本次新建目录一直非空。
    foreach ($cleanupFailure in @(Clear-UpgradeTemporaryFiles)) { $rollbackFailures.Add($cleanupFailure) }
    # 仅删除本次创建且仍为空的目录；从叶子向根处理，不递归删除任何内容。
    $emptyDirectoryCandidates = @($createdDirectories | Sort-Object Length -Descending)
    if ($targetWasEmpty -and $backupRoot -and $rollbackFailures.Count -eq 0) {
        $emptyDirectoryCandidates += $backupRoot
        $emptyDirectoryCandidates += [IO.Path]::GetDirectoryName($backupRoot)
    }
    foreach ($directory in $emptyDirectoryCandidates) {
        try {
            if ($rollbackFailures.Count -gt 0 -and $targetWasEmpty -and $backupRoot -and
                ($directory -eq $backupRoot -or $directory -eq [IO.Path]::GetDirectoryName($backupRoot))) { continue }
            if (-not $directory.StartsWith($targetRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '拒绝清理目标目录之外的目录' }
            Assert-NoReparsePoint $targetRoot $directory
            if ((Test-Path -LiteralPath $directory -PathType Container) -and
                @(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) {
                Remove-Item -LiteralPath $directory
            }
        }
        catch { $rollbackFailures.Add("空目录清理失败：$($_.Exception.Message)") }
    }
    $recoveryMessage = if ($upgradeJournal.Count -eq 0) {
        '尚未替换任何目标文件，原文件保持不变。'
    }
    elseif ($rollbackFailures.Count -eq 0) {
        "已恢复本次替换的 $($upgradeJournal.Count) 个文件，原有 data 未覆盖。"
    }
    else {
        '已尝试恢复本次替换的文件，部分恢复失败；原有 data 未覆盖。'
    }
    $message = "升级失败：$upgradeFailure`n$recoveryMessage"
    if ($rollbackFailures.Count -gt 0) { $message += "`n恢复也遇到问题，请保留备份目录：$backupRoot`n" + ($rollbackFailures -join "`n") }
    if ($showDialogs) {
        Add-Type -AssemblyName System.Windows.Forms
        $null = [Windows.Forms.MessageBox]::Show($message, '升级失败')
    }
    throw $message
}
finally {
    foreach ($cleanupFailure in @(Clear-UpgradeTemporaryFiles)) { Write-Warning $cleanupFailure }
}
