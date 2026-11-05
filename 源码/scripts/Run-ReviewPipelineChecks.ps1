[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$source=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'Artifact-Paths.ps1')
$root=Get-ArtifactRoot $source
$parent=Join-Path $root '验证记录\流水线校验'
if(Test-Path -LiteralPath $parent){Assert-GeneratedDirectory $root $parent '流水线校验根'}else{$null=New-GeneratedDirectory $root $parent '流水线校验根'}
$batch=New-GeneratedDirectory $root (Join-Path $parent ([Guid]::NewGuid().ToString('N'))) '流水线校验'
$fixture=New-GeneratedDirectory $root (Join-Path $batch 'fixture') '流水线校验副本'
$fixtureSource=Join-Path $fixture '源码'
foreach($directory in @('scripts','Tests','CoreTests')){$null=New-Item -ItemType Directory -Path (Join-Path $fixtureSource $directory) -Force}
foreach($name in @('Run-InternalChecks.ps1','Artifact-Paths.ps1')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $fixtureSource ('scripts\'+$name))}
$utf8=[Text.UTF8Encoding]::new($false)
function Write-Text([string]$Relative,[string]$Text){[IO.File]::WriteAllText((Join-Path $fixtureSource $Relative),$Text,$utf8)}
function Require([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}

# 真实调用统一脚本，只将两个产品测试程序集替换为本案极小、无UI且无外部输入的夹具。
Write-Text '拾刻.csproj' @'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Version>1.0.53</Version><BaseOutputPath>..\bin\Release\net10.0-windows\构建\</BaseOutputPath></PropertyGroup><ItemGroup><Compile Remove="Tests\**\*.cs"/><Compile Remove="CoreTests\**\*.cs"/></ItemGroup></Project>
'@
Write-Text 'Probe.cs' 'public sealed class ProductProbe { }'
Write-Text 'Tests\ShiKe.InternalTests.csproj' @'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><BaseOutputPath>..\..\bin\Release\net10.0-windows\内部用例\</BaseOutputPath></PropertyGroup><ItemGroup><ProjectReference Include="..\拾刻.csproj"/></ItemGroup></Project>
'@
Write-Text 'Tests\Program.cs' @'
var tests = new (string Name, Action Run)[]
{
    ("fixture", () => { }),
};
var failed = Environment.GetEnvironmentVariable("SHIKE_PIPELINE_MAIN_FAILURE") == "1" ? 1 : 0;
Console.WriteLine($"RESULT total=1, passed={1-failed}, failed={failed}");
return failed;
'@
Write-Text 'CoreTests\ShiKe.CoreTests.csproj' @'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><BaseOutputPath>..\..\bin\Release\net10.0-windows\核心用例\</BaseOutputPath></PropertyGroup></Project>
'@
Write-Text 'CoreTests\Program.cs' @'
var suites = new[] { typeof(ProbeChecks) };
var failed = Environment.GetEnvironmentVariable("SHIKE_PIPELINE_CORE_FAILURE") == "1" ? 1 : 0;
Console.WriteLine($"RESULT total=1, passed={1-failed}, failed={failed}");
return failed;
'@
Write-Text 'CoreTests\ProbeChecks.cs' 'public static class ProbeChecks { public static void Check() { } }'

$resultRoot=Join-Path $fixture 'bin\Release\net10.0-windows\验证记录\内部用例'
$cases=@()
foreach($case in @(
    @{name='fresh-no-restore';noRestore=$true;mainFailure=$false;coreFailure=$false;main='failed';core='failed';exit=1},
    @{name='fresh-default-restores';noRestore=$false;mainFailure=$false;coreFailure=$false;main='passed';core='passed';exit=0},
    @{name='main-failure-still-runs-core';noRestore=$false;mainFailure=$true;coreFailure=$false;main='failed';core='passed';exit=1},
    @{name='core-failure-fails-overall';noRestore=$false;mainFailure=$false;coreFailure=$true;main='passed';core='failed';exit=1}
)) {
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $start.Arguments='-NoProfile -ExecutionPolicy Bypass -File "'+(Join-Path $fixtureSource 'scripts\Run-InternalChecks.ps1')+'"'+$(if($case.noRestore){' -NoRestore'}else{''})
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $start.StandardOutputEncoding=[Text.Encoding]::UTF8;$start.StandardErrorEncoding=[Text.Encoding]::UTF8
    $start.EnvironmentVariables['SHIKE_PIPELINE_MAIN_FAILURE']=$(if($case.mainFailure){'1'}else{'0'})
    $start.EnvironmentVariables['SHIKE_PIPELINE_CORE_FAILURE']=$(if($case.coreFailure){'1'}else{'0'})
    $start.EnvironmentVariables['PSModulePath']=''
    $process=[Diagnostics.Process]::Start($start)
    try {
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        if(-not $process.WaitForExit(180000)){$process.Kill();throw '流水线夹具超时'}
        $output=$stdout.GetAwaiter().GetResult()+$stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $batch ($case.name+'.log')),$output,$utf8)
        $new=@(Get-ChildItem -LiteralPath $resultRoot -Filter result.json -Recurse)
        Require ($new.Count -eq 1) '每次调用必须生成唯一汇总'
        $report=Get-Content -LiteralPath $new[0].FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        Require ($process.ExitCode -eq $case.exit -and $report.mainSuiteStatus -eq $case.main -and $report.coreSuite.status -eq $case.core) ('两套结果/退出码聚合错误：'+$case.name)
        if($case.exit -eq 0){Require ($report.total -eq 1 -and $report.coreSuite.total -eq 1) '主套件total字段不能合并重复覆盖'}
        $cases += [pscustomobject]@{name=$case.name;status='passed';exitCode=$process.ExitCode;report=$report}
        Write-Host ('PASS '+$case.name)
    } finally {$process.Dispose()}
}
$summary=[ordered]@{status='passed';total=$cases.Count;passed=$cases.Count;cases=$cases;scope='isolated real entrypoint with minimal fixture projects; no product UI/input/network'}
[IO.File]::WriteAllText((Join-Path $batch 'result.json'),($summary|ConvertTo-Json -Depth 10),$utf8)
Write-Host "PIPELINE_RESULT total=$($cases.Count), passed=$($cases.Count), failed=0; report=$batch\result.json"
