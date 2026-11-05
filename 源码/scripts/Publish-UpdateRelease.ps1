[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ReleaseDirectory,
    [string]$SshHost='myserver',
    [string]$RemoteRoot='/var/www/poead',
    [string]$CloudflareZoneId,
    [string]$CloudflareTokenEnvironment='SHIKE_CF_API_TOKEN',
    [switch]$Execute,
    [switch]$Rollback,
    [string]$Transaction
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# 默认只验证本地材料并输出计划；必须显式 -Execute 才允许 SSH/SCP/HTTP 与线上切换。
# token只从指定环境变量读取，不出现在命令行、日志、清单或VPS上。
$project = Join-Path $PSScriptRoot '..\ReleaseTools\ShiKe.ReleaseTools.csproj'
$operation = if ($Rollback) { 'rollback' } else { 'publish' }
$arguments = @('run','--project',$project,'-c','Release','--',$operation,
    '--manifest',(Join-Path $ReleaseDirectory 'version.json'),
    '--zip',(Join-Path $ReleaseDirectory 'shike-win-x64.zip'),
    '--host',$SshHost,'--remote-root',$RemoteRoot,'--token-env',$CloudflareTokenEnvironment)
if ($Execute) { $arguments += '--execute' }
if ($CloudflareZoneId) { $arguments += @('--zone-id',$CloudflareZoneId) }
if ($Transaction) { $arguments += @('--transaction',$Transaction) }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw '发布未完成；请按输出的事务状态处理，不能忽略缓存门槛后手工宣布latest' }
