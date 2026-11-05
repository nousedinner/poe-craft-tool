[CmdletBinding()]
param([string]$KeyPath=(Join-Path $env:USERPROFILE '.shike-release\release-key.dpapi'))
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$source=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'Artifact-Paths.ps1')
$root=Get-ArtifactRoot $source
$parent=Join-Path $root '验证记录\WPF更新验收'
if(Test-Path -LiteralPath $parent){Assert-GeneratedDirectory $root $parent 'WPF更新验收根'}else{$null=New-GeneratedDirectory $root $parent 'WPF更新验收根'}
$batch=New-GeneratedDirectory $root (Join-Path $parent ([Guid]::NewGuid().ToString('N'))) 'WPF更新验收'
$original=Join-Path $batch 'binaries\original'
$candidate=Join-Path $batch 'binaries\candidate'
$projectVersion=[xml](Get-Content -LiteralPath (Join-Path $source '拾刻.csproj') -Raw -Encoding UTF8)
$version=[string]$projectVersion.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
$parsedVersion=[Version]::Parse($version)
$next=('{0}.{1}.{2}' -f $parsedVersion.Major,$parsedVersion.Minor,($parsedVersion.Build+1))
$expectedReleaseNotes=[IO.File]::ReadAllText((Join-Path $source 'ReleaseNotes.txt')).TrimStart([char]0xFEFF).Replace("`r`n","`n").Replace("`r","`n").Trim()
if([string]::IsNullOrWhiteSpace($expectedReleaseNotes)){throw '版本说明资源不能为空'}
foreach($item in @(@($version,$original),@($next,$candidate))){
    & dotnet publish (Join-Path $source '拾刻.csproj') -c Release -p:PublishProfile=FrameworkDependent ("-p:Version="+$item[0]) ("-p:InformationalVersion="+$item[0]) --output $item[1] --nologo
    if($LASTEXITCODE -ne 0){throw '正式WPF候选构建失败'}
}
$oldExe=Join-Path $original '拾刻.exe'
$newExe=Join-Path $candidate '拾刻.exe'
$oldHash=(Get-FileHash -LiteralPath $oldExe).Hash
$newHash=(Get-FileHash -LiteralPath $newExe).Hash
$zipPath=Join-Path $batch 'candidate.zip'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::Open($zipPath,[IO.Compression.ZipArchiveMode]::Create)
try{
    foreach($file in Get-ChildItem -LiteralPath $candidate -File -Recurse){
        $name=$file.FullName.Substring($candidate.Length+1).Replace('\','/')
        $null=[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.FullName,$name,[IO.Compression.CompressionLevel]::Optimal)
    }
}finally{$zip.Dispose()}
$tool=Join-Path $root '发布工具\Release\net10.0-windows\ShiKe.ReleaseTools.dll'
if(-not(Test-Path -LiteralPath $tool)){throw '请先构建ReleaseTools，再运行WPF验收'}
$release=Join-Path $batch 'signed-release'
& dotnet $tool prepare --zip $zipPath --exe $newExe --version $next --key-path $KeyPath --output $release --mode exe-only --min-app-version $version
if($LASTEXITCODE -ne 0){throw '仅本地测试发行签名失败'}

function Write-JsonFile([string]$Path,$Value){
    [IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 35),[Text.UTF8Encoding]::new($false))
}
function Require([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
function Observe-OwnedProcesses([string]$CaseDirectory,$Tracked){
    foreach($process in @(Get-Process -Name 拾刻,runner -ErrorAction SilentlyContinue)){
        try{
            $path=$process.Path
            if($path -and $path.StartsWith($CaseDirectory+'\',[StringComparison]::OrdinalIgnoreCase) -and -not $Tracked.ContainsKey($process.Id)){
                $null=$process.Handle
                $Tracked.Add($process.Id,[pscustomobject]@{Process=$process;Path=$path;StartTicks=$process.StartTime.ToUniversalTime().Ticks})
            }else{$process.Dispose()}
        }catch{$process.Dispose()}
    }
}
function Run-App([string]$CaseDirectory,[string]$Stage,[string]$Nonce){
    $install=Join-Path $CaseDirectory 'installation with 中文 空格'
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName=Join-Path $install '拾刻.exe'
    $start.WorkingDirectory=$install
    $start.UseShellExecute=$false
    $start.CreateNoWindow=$true
    $start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $start.EnvironmentVariables['SHIKE_WPF_UPDATE_CASE']=Join-Path $CaseDirectory 'acceptance.json'
    $start.EnvironmentVariables['SHIKE_WPF_UPDATE_TOKEN']=$Nonce
    $start.EnvironmentVariables['SHIKE_WPF_UPDATE_STAGE']=$Stage
    $process=[Diagnostics.Process]::Start($start)
    if($null -eq $process){throw '不能启动本轮WPF副本'}
    $null=$process.Handle
    $tracked=[Collections.Generic.Dictionary[int,object]]::new()
    $tracked.Add($process.Id,[pscustomobject]@{Process=$process;Path=$start.FileName;StartTicks=$process.StartTime.ToUniversalTime().Ticks})
    $timer=[Diagnostics.Stopwatch]::StartNew()
    try{
        $quiet=0
        while($true){
            Observe-OwnedProcesses $CaseDirectory $tracked
            $active=@($tracked.Values|Where-Object{-not $_.Process.HasExited})
            if($active.Count -eq 0){$quiet++}else{$quiet=0}
            if($quiet -ge 15){break}
            if($timer.Elapsed.TotalSeconds -gt 110){throw '本轮WPF升级进程未按时结束'}
            Start-Sleep -Milliseconds 30
        }
        return @($tracked.Values|ForEach-Object{[pscustomobject]@{pid=$_.Process.Id;startTicks=$_.StartTicks;path=$_.Path;exitCode=$_.Process.ExitCode;exited=$_.Process.HasExited}})
    }catch{
        # 仅终止本次直接观察且绑定路径/启动时间的隔离进程；不触碰任何日常实例。
        foreach($item in $tracked.Values){
            if(-not $item.Process.HasExited -and $item.Process.StartTime.ToUniversalTime().Ticks -eq $item.StartTicks -and $item.Path.StartsWith($CaseDirectory+'\',[StringComparison]::OrdinalIgnoreCase)){
                $item.Process.Kill();$null=$item.Process.WaitForExit(5000)
            }
        }
        throw
    }finally{foreach($item in $tracked.Values){$item.Process.Dispose()}}
}

$protected=@('data\settings.json','data\rules.json','data\coordinates.json','data\presets\player.json','sounds\player-custom.mp3')
$results=@()
$restartResults=@()
foreach($scenario in @('success','startup-failure','exit-save-failure','signature-failure')){
    $case=Join-Path $batch ('case-'+$scenario)
    $install=Join-Path $case 'installation with 中文 空格'
    $null=New-Item -ItemType Directory -Path (Join-Path $install 'data\presets') -Force
    $null=New-Item -ItemType Directory -Path (Join-Path $install 'sounds') -Force
    Copy-Item -LiteralPath $oldExe -Destination (Join-Path $install '拾刻.exe')
    # 逐文件复制构建资源，不读取日常data。
    foreach($sound in Get-ChildItem -LiteralPath (Join-Path $original 'sounds') -File){Copy-Item -LiteralPath $sound.FullName -Destination (Join-Path $install 'sounds')}
    Write-JsonFile (Join-Path $install 'data\settings.json') ([ordered]@{
        craft=[ordered]@{enabled=$true;delay_ms=47;sound_enabled=$false;popup_enabled=$false;selected_sound='player-custom.mp3';clipboard_unchanged_threshold=10;mode2_scour_alch=$true;use_exalt=$true}
        clicker=[ordered]@{enabled=$true;hotkey='F8';hold_hotkey='F11';button='left';interval_ms=43;notifications_enabled=$false}
        keyloop=[ordered]@{enabled=$true;hotkey='F9';slots=@([ordered]@{enabled=$false;key='q';delay_s=1.7});notifications_enabled=$false}
        hideout=[ordered]@{enabled=$false;hotkey='F2';command='/hideout'}
        host=[ordered]@{target_process='fixture-game.exe';auto_detect_poe=$false;gradient_speed=4;hotkeys=[ordered]@{start='F5';stop='F6';coordinate='F7'}}
        player_sentinel=[ordered]@{value='must remain'}
    })
    Write-JsonFile (Join-Path $install 'data\rules.json') ([ordered]@{mode=1;single_currency='chaos';primary_affixes=@('验收生命');primary_hit_count=1;secondary_affixes=@('验收速度');secondary_hit_count=0;exclude_affixes=@('验收排除')})
    Write-JsonFile (Join-Path $install 'data\coordinates.json') ([ordered]@{item=@(800,400);chaos=@(400,300);alteration=@(450,300)})
    Write-JsonFile (Join-Path $install 'data\presets\player.json') ([ordered]@{primary_affixes=@('玩家预设');primary_hit_count=1;secondary_affixes=@();secondary_hit_count=0;exclude_affixes=@()})
    [IO.File]::WriteAllText((Join-Path $install 'sounds\player-custom.mp3'),'unchanged user resource sentinel')
    Copy-Item -LiteralPath $release -Destination (Join-Path $case 'release') -Recurse
    if($scenario -eq 'signature-failure'){
        $bad=Get-Content -LiteralPath (Join-Path $case 'release\version.json') -Raw|ConvertFrom-Json
        $signature=[Convert]::FromBase64String($bad.autoUpdate.signature);$signature[0]=$signature[0] -bxor 1
        $bad.autoUpdate.signature=[Convert]::ToBase64String($signature)
        Write-JsonFile (Join-Path $case 'release\version.json') $bad
    }
    $nonceBytes=New-Object byte[] 32
    $random=[Security.Cryptography.RandomNumberGenerator]::Create();try{$random.GetBytes($nonceBytes)}finally{$random.Dispose()}
    $nonce=[BitConverter]::ToString($nonceBytes).Replace('-','')
    Write-JsonFile (Join-Path $case 'acceptance.json') ([ordered]@{
        Owner='ShiKe.WpfUpdateAcceptance.v1';Nonce=$nonce;ExpiresUtc=[DateTimeOffset]::UtcNow.AddHours(1).ToString('o');Scenario=$scenario
        OriginalVersion=$version;CandidateVersion=$next;OriginalSha256=$oldHash;CandidateSha256=$newHash
        ManifestSha256=(Get-FileHash -LiteralPath (Join-Path $case 'release\version.json')).Hash
    })
    $warmup=@(Run-App $case 'warmup' $nonce)
    Require ($warmup.Count -eq 1 -and $warmup[0].exitCode -eq 0) 'WPF配置基线准备未完整结束'
    $warmupReports=@(Get-ChildItem -LiteralPath $case -Filter 'process-warmup-*.json'|ForEach-Object{Get-Content -LiteralPath $_.FullName -Raw|ConvertFrom-Json})
    Require ($warmupReports.Count -eq 1 -and $warmupReports[0].releaseNotesDialogs -eq 0 -and $warmupReports[0].dialogs -eq 0) '普通基线启动不应显示更新或版本说明'
    $baseline=@{};foreach($name in $protected){$baseline[$name]=(Get-FileHash -LiteralPath (Join-Path $install $name)).Hash}
    $processes=@(Run-App $case 'upgrade' $nonce)
    $reports=@(Get-ChildItem -LiteralPath $case -Filter 'process-upgrade-*.json'|ForEach-Object{Get-Content -LiteralPath $_.FullName -Raw|ConvertFrom-Json})
    $origin=@($reports|Where-Object role -eq origin)
    Require ($origin.Count -eq 1 -and $origin[0].settingsLoaded -and $origin[0].versionRequests -eq 1) '未贯通真实WPF配置加载与版本检查'
    Require ($origin[0].dialogs -eq 1) '未显示并响应唯一的真实更新提示'
    Require ($origin[0].releaseNotesDialogs -eq 0) '旧版本不能显示更新完成说明'
    Require (@($origin[0].events|Where-Object name -eq 'forced-update-blocked-old-ui').Count -eq 1) '旧主页面未进入强更阻断'
    foreach($report in $reports){Require ($report.deniedNativeCalls -eq 0 -and $report.unexpectedRequests -eq 0) '验收触发了被禁止的原生或外部入口'}
    foreach($name in $protected){Require ((Get-FileHash -LiteralPath (Join-Path $install $name)).Hash -eq $baseline[$name]) "配置或自定义资源变化：$name"}
    $sessions=@(Get-ChildItem -LiteralPath (Join-Path $install '.shike-updates') -Directory -ErrorAction SilentlyContinue|Where-Object Name -match '^[a-f0-9]{32}$')
    $phase=$null
    if($scenario -eq 'signature-failure'){
        Require ($sessions.Count -eq 0 -and $processes.Count -eq 1 -and $origin[0].packageRequests -eq 0) '签名失败仍开始更新事务'
    }else{
        Require ($sessions.Count -eq 1) '没有唯一真实更新会话'
        $phase=(Get-Content -LiteralPath (Join-Path $sessions[0].FullName 'state.json') -Raw|ConvertFrom-Json).Phase
        $expectedPhase=if($scenario -eq 'success'){'completed'}elseif($scenario -eq 'startup-failure'){'rolled-back'}else{'aborted'}
        Require ($phase -eq $expectedPhase) "更新事务结束状态不符：$phase"
        Require ($origin[0].packageRequests -eq 1) '未使用真实下载器校验本地HTTP包'
        $helpers=@($processes|Where-Object {[IO.Path]::GetFileName($_.path) -eq 'runner.exe'})
        Require ($helpers.Count -eq 1) '未观察到唯一真实更新助手'
        $expectedHelper=if($scenario -eq 'success'){0}elseif($scenario -eq 'startup-failure'){3}else{2}
        Require ($helpers[0].exitCode -eq $expectedHelper) '更新助手退出码异常'
    }
    $children=@($reports|Where-Object role -eq candidate)
    if($scenario -in @('success','startup-failure')){
        Require ($children.Count -eq 1 -and $children[0].settingsLoaded) '没有真实新版WPF配置加载'
        Require (@($children[0].events|Where-Object name -eq 'wpf-main-rendered').Count -eq 1) '新版没有真实WPF首帧'
        Require ($children[0].healthConfirmed -eq ($scenario -eq 'success')) '健康确认结果不符'
        Require ($children[0].versionRequests -eq 0 -and $children[0].packageRequests -eq 0) '新版验收触发额外HTTP'
        Require ($children[0].dialogs -eq 0) '新版不应重复显示旧更新提示'
        if($scenario -eq 'success'){
            Require ($children[0].releaseNotesDialogs -eq 1 -and @($children[0].releaseNotesEvidence).Count -eq 1) '成功更新后必须仅显示一次真实版本说明'
            $notes=$children[0].releaseNotesEvidence[0]
            Require ($notes.allowed -and $notes.contentRendered -and $notes.healthCommitted -and $notes.version -eq $next -and $notes.displayedVersion -eq "已更新至 v$next") '版本说明未在健康提交后正确显示版本'
            $renderedNotes=([string]$notes.text).Replace("`r`n","`n").Replace("`r","`n").Trim()
            Require ($renderedNotes -ceq $expectedReleaseNotes) '真实版本说明内容与本次内置资源不一致'
            $eventNames=[string[]]@($children[0].events|ForEach-Object{$_.name})
            $healthIndex=[Array]::IndexOf($eventNames,'health-committed')
            $notesIndex=[Array]::IndexOf($eventNames,'release-notes-rendered')
            $acknowledgedIndex=[Array]::IndexOf($eventNames,'release-notes-acknowledged')
            Require ($healthIndex -ge 0 -and $notesIndex -gt $healthIndex -and $acknowledgedIndex -gt $notesIndex) '版本说明必须在健康提交之后渲染并通过知道了关闭'
        }else{
            Require ($children[0].releaseNotesDialogs -eq 0 -and @($children[0].releaseNotesEvidence).Count -eq 0) '启动失败回滚不能显示更新完成说明'
        }
    }else{Require ($children.Count -eq 0) '拒绝更新时仍启动了新版'}
    $expectedHash=if($scenario -eq 'success'){$newHash}else{$oldHash}
    Require ((Get-FileHash -LiteralPath (Join-Path $install '拾刻.exe')).Hash -eq $expectedHash) '主EXE未正确更新或恢复'
    if($scenario -eq 'success'){
        $restartProcesses=@(Run-App $case 'restart' $nonce)
        $restartReports=@(Get-ChildItem -LiteralPath $case -Filter 'process-restart-*.json'|ForEach-Object{Get-Content -LiteralPath $_.FullName -Raw|ConvertFrom-Json})
        Require ($restartProcesses.Count -eq 1 -and $restartProcesses[0].exitCode -eq 0 -and $restartReports.Count -eq 1) '成功更新后的普通重启未完整退出'
        $restarted=$restartReports[0]
        Require ($restarted.role -eq 'candidate' -and $restarted.version -eq $next -and $restarted.settingsLoaded -and -not $restarted.healthConfirmed) '普通重启不应伪装成自动更新启动'
        Require ($restarted.versionRequests -eq 1 -and $restarted.packageRequests -eq 0 -and $restarted.dialogs -eq 0 -and $restarted.releaseNotesDialogs -eq 0) '同一新版普通重启不能重复弹版本说明或启动更新'
        Require ($restarted.deniedNativeCalls -eq 0 -and $restarted.unexpectedRequests -eq 0) '普通重启验收触发了未授权入口'
        Require (@($restarted.events|Where-Object name -eq 'wpf-main-rendered').Count -eq 1 -and @($restarted.events|Where-Object name -eq 'health-committed').Count -eq 0) '普通重启必须完成真实首帧且没有更新提交事件'
        foreach($name in $protected){Require ((Get-FileHash -LiteralPath (Join-Path $install $name)).Hash -eq $baseline[$name]) "普通重启改变了配置或资源：$name"}
        Require ((Get-FileHash -LiteralPath (Join-Path $install '拾刻.exe')).Hash -eq $newHash) '普通重启改变了本次候选程序'
        $restartResults += [pscustomobject]@{status='passed';processes=$restartProcesses;reports=$restartReports;mainExeSha256=$newHash}
    }
    $results += [pscustomobject]@{scenario=$scenario;status='passed';phase=$phase;warmupProcesses=$warmup;processes=$processes;reports=$reports;protectedFileHashes=$baseline;mainExeSha256=$expectedHash}
    Write-Host "$scenario 正式WPF更新验收通过"
}
Require ($restartResults.Count -eq 1) '必须完成一次成功候选的普通重启验证'
$summary=[ordered]@{status='passed';total=$results.Count;passed=$results.Count;restartChecks=$restartResults.Count;restartResults=$restartResults;originalVersion=$version;candidateVersion=$next;originalExe=$oldExe;candidateExe=$newExe;originalSha256=$oldHash;candidateSha256=$newHash;cases=$results;scope='production WPF App/Host/update dialogs/release notes/downloader/exit/helper/health flow and candidate ordinary restart; local HTTP transport adapters; native input and ordinary online services disabled; not original shipped v1.0.52 binary';productionSigningKeyUsed=$true;published=$false}
Write-JsonFile (Join-Path $batch 'result.json') $summary
Write-Host "WPF_UPDATE_RESULT total=$($results.Count), passed=$($results.Count), failed=0; report=$batch\result.json"
