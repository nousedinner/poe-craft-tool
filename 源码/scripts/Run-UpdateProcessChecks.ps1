[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$source=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'Artifact-Paths.ps1')
$root=Get-ArtifactRoot $source
$directory=Join-Path $root ('验证记录\更新进程-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$null=New-GeneratedDirectory $root $directory '更新进程验证'
$project=Join-Path $source 'Tests\UpdateProcessProbe\UpdateProcessProbe.csproj'
$bootstrap=Join-Path $directory 'bootstrap'
$old=Join-Path $directory 'old'
$new=Join-Path $directory 'new'
& dotnet build $project -c Release -p:ProbeVersion=1.0.52 --output $bootstrap --nologo
if($LASTEXITCODE -ne 0){throw '进程fixture引导编译失败'}
& dotnet (Join-Path $bootstrap 'UpdateProcessProbe.dll') init-test-key $directory
if($LASTEXITCODE -ne 0){throw '临时测试密钥生成失败'}
$trust=Join-Path $directory 'TestTrust.g.cs'
& dotnet publish $project -c Release -p:ProbeVersion=1.0.52 "-p:ProbeTrustSource=$trust" --output $old --nologo
if($LASTEXITCODE -ne 0){throw '旧版本fixture编译失败'}
& dotnet publish $project -c Release -p:ProbeVersion=1.0.53 "-p:ProbeTrustSource=$trust" --output $new --nologo
if($LASTEXITCODE -ne 0){throw '新版本fixture编译失败'}
# 所有入口均为无UI测试fixture，未引用App/WPF/Hotkey/Input源码，也未使用正式发行密钥。
& dotnet (Join-Path $bootstrap 'UpdateProcessProbe.dll') suite (Join-Path $old 'UpdateProcessProbe.exe') (Join-Path $new 'UpdateProcessProbe.exe') (Join-Path $directory 'test-private.pkcs8') $directory
if($LASTEXITCODE -ne 0){throw "真实更新进程验证失败；本轮材料保留：$directory"}
Write-Host "真实进程与文件事务检查通过。报告：$directory\result.json"
