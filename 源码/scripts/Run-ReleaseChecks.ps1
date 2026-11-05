[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=Join-Path $PSScriptRoot '..\ReleaseTools\ShiKe.ReleaseTools.csproj'
& dotnet run --project $project -c Release -- self-test
if ($LASTEXITCODE -ne 0) { throw '发布流水线受控检查失败' }
