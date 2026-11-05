[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$KeyPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# 显式独立动作；普通构建/签名/发布绝不自动初始化或覆盖发行私钥。
$project = Join-Path $PSScriptRoot '..\ReleaseTools\ShiKe.ReleaseTools.csproj'
& dotnet run --project $project -c Release -- init-key --key-path $KeyPath
if ($LASTEXITCODE -ne 0) { throw '发行密钥初始化未完成' }
