[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ReleaseDirectory,
    [string]$SshHost='myserver',
    [string]$RemoteRoot='/var/www/poead',
    [ValidateSet('headers','cloudflare')][string]$CacheStrategy='headers',
    [string]$CloudflareZoneId,
    [string]$CloudflareTokenEnvironment='SHIKE_CF_API_TOKEN',
    [switch]$Execute,
    [switch]$Rollback,
    [string]$Transaction
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# 默认只验证本地材料并输出计划；必须显式 -Execute 才允许 SSH/SCP/HTTP 与线上切换。
# headers默认仅依赖生效的响应头及普通公网GET验真；不读取CF凭据、不调用CF API。
# 首次变更固定入口策略仍须清理历史CDN缓存或等待其已知寿命，浏览器缓存另行处理。
# cloudflare策略的token只从指定环境变量读取，不出现在命令行、日志、清单或VPS上。
$project = Join-Path $PSScriptRoot '..\ReleaseTools\ShiKe.ReleaseTools.csproj'
$operation = if ($Rollback) { 'rollback' } else { 'publish' }
$arguments = @('run','--project',$project,'-c','Release','--',$operation,
    '--manifest',(Join-Path $ReleaseDirectory 'version.json'),
    '--zip',(Join-Path $ReleaseDirectory 'shike-win-x64.zip'),
    '--host',$SshHost,'--remote-root',$RemoteRoot,'--cache-strategy',$CacheStrategy)
if ($Execute) { $arguments += '--execute' }
if ($CacheStrategy -eq 'cloudflare') {
    $arguments += @('--token-env',$CloudflareTokenEnvironment)
    if ($CloudflareZoneId) { $arguments += @('--zone-id',$CloudflareZoneId) }
}
if ($Transaction) { $arguments += @('--transaction',$Transaction) }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw '发布未完成；请按输出的事务状态处理，不能忽略缓存门槛后手工宣布latest' }
