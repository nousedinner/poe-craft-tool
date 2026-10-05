# 开发/验收入口共用；生成物统一到用户指定的目录，不操作日常 data。
function Get-ArtifactRoot([string]$ProjectRoot) {
    return [IO.Path]::GetFullPath((Join-Path $ProjectRoot 'bin\Release\net10.0-windows'))
}

function Assert-ArtifactPath([string]$Root, [string]$Path) {
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $resolved = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not $resolved.StartsWith($rootPath + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '生成物路径超出指定目录' }
    $current = $resolved
    while ($current.Length -ge $rootPath.Length) {
        if (Test-Path -LiteralPath $current) {
            if (((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw '生成物目录包含链接，已保留原目录' }
        }
        if ([string]::Equals($current, $rootPath, [StringComparison]::OrdinalIgnoreCase)) { break }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $resolved
}

function Assert-GeneratedDirectory([string]$Root, [string]$Directory, [string]$Purpose) {
    $resolved = Assert-ArtifactPath $Root $Directory
    $marker = Join-Path $resolved '.shike-output.json'
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) { throw "目录没有生成标记，已保留：$resolved" }
    $metadata = Get-Content -LiteralPath $marker -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($metadata.schemaVersion -ne 1 -or $metadata.purpose -ne $Purpose) { throw "目录用途不一致，已保留：$resolved" }
    if (@(Get-ChildItem -LiteralPath $resolved -Recurse -Force | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count -gt 0) { throw '生成物目录包含链接，已保留原目录' }
}

function New-GeneratedDirectory([string]$Root, [string]$Directory, [string]$Purpose) {
    $resolved = Assert-ArtifactPath $Root $Directory
    if (Test-Path -LiteralPath $resolved) { throw "生成目录已存在：$resolved" }
    $null = New-Item -ItemType Directory -Path $resolved -Force
    $metadata = [ordered]@{ schemaVersion = 1; purpose = $Purpose }
    [IO.File]::WriteAllText((Join-Path $resolved '.shike-output.json'), ($metadata | ConvertTo-Json), [Text.UTF8Encoding]::new($true))
    return $resolved
}

function Remove-GeneratedDirectory([string]$Root, [string]$Directory, [string]$Purpose) {
    Assert-GeneratedDirectory $Root $Directory $Purpose
    Remove-Item -LiteralPath ([IO.Path]::GetFullPath($Directory)) -Recurse -Force
}

function New-CheckResultDirectory([string]$Root, [string]$Name, [string[]]$LogNames) {
    $directory = Assert-ArtifactPath $Root (Join-Path $Root ('验证记录\' + $Name))
    if (Test-Path -LiteralPath $directory) {
        Assert-GeneratedDirectory $Root $directory $Name
        # 只保留一份上次摘要/日志；副本目录固定复用，避免无限堆积。
        $previous = Assert-ArtifactPath $Root (Join-Path $Root ('验证记录\上次' + $Name))
        if (Test-Path -LiteralPath $previous) { Assert-GeneratedDirectory $Root $previous ('上次' + $Name) }
        else { $null = New-GeneratedDirectory $Root $previous ('上次' + $Name) }
        foreach ($logName in $LogNames) {
            $destination = Assert-ArtifactPath $previous (Join-Path $previous $logName)
            if (Test-Path -LiteralPath $destination -PathType Leaf) { Remove-Item -LiteralPath $destination }
            $source = Join-Path $directory $logName
            if (Test-Path -LiteralPath $source -PathType Leaf) { Copy-Item -LiteralPath $source -Destination $destination }
        }
        Remove-GeneratedDirectory $Root $directory $Name
    }
    return New-GeneratedDirectory $Root $directory $Name
}
