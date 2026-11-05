[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ZipPath,
    [Parameter(Mandatory=$true)][string]$ExePath,
    [Parameter(Mandatory=$true)][string]$KeyPath,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [ValidateSet('exe-only','files')][string]$Mode='exe-only',
    [string]$ManagedFiles,
    [string]$BasePackages,
    [string]$BaseManifests,
    [string]$MinAppVersion='1.0.52',
    [int]$MinUpdaterVersion=1,
    [int]$RuntimeMajor=10,
    [string]$Platform='win-x64',
    [int]$ConfigSchema=1
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = [xml](Get-Content -LiteralPath (Join-Path $source '拾刻.csproj') -Raw -Encoding UTF8)
$version = [string]$project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
$arguments = @('run','--project',(Join-Path $source 'ReleaseTools\ShiKe.ReleaseTools.csproj'),'-c','Release','--',
    'prepare','--zip',$ZipPath,'--exe',$ExePath,'--version',$version,'--key-path',$KeyPath,
    '--output',$OutputDirectory,'--mode',$Mode,'--min-app-version',$MinAppVersion,
    '--min-updater-version',[string]$MinUpdaterVersion,'--runtime-major',[string]$RuntimeMajor,
    '--platform',$Platform,'--config-schema',[string]$ConfigSchema)
if ($ManagedFiles) { $arguments += @('--managed-files',$ManagedFiles) }
if ($BasePackages) { $arguments += @('--base-packages',$BasePackages) }
if ($BaseManifests) { $arguments += @('--base-manifests',$BaseManifests) }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw '发布清单准备失败，未触及服务器' }
