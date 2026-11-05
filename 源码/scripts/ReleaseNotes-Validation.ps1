# 无写入的发布前检查；既验证源说明，也可验证待复用EXE自己的Win32版本资源。
function Assert-ReleaseNotes([string]$NotesPath, [string]$Version, [string]$ExePath='') {
    if ($Version -notmatch '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z') { throw '版本说明目标版本无效' }
    $bytes = [IO.File]::ReadAllBytes([IO.Path]::GetFullPath($NotesPath))
    $rawText = [Text.UTF8Encoding]::new($false,$true).GetString($bytes)
    if ($rawText.Length -gt 12128) { throw '版本说明原始文本超过长度限制' }
    $text = $rawText.TrimStart([char]0xFEFF).Replace("`r`n","`n").Replace("`r","`n")
    $newline = $text.IndexOf("`n")
    if ($newline -lt 0 -or $text.Substring(0,$newline) -cne ('Version: '+$Version)) { throw "版本说明首行必须为 Version: $Version" }
    $body = $text.Substring($newline+1).Trim()
    if ($text.Length -gt 12128 -or $body.Length -eq 0 -or $body.Length -gt 12000) { throw '版本说明正文为空或超过长度限制' }
    for ($index=0; $index -lt $body.Length; $index++) {
        $character=$body[$index]
        if ([char]::IsControl($character) -and $character -ne "`n" -and $character -ne "`t") { throw '版本说明含控制字符' }
        if ([char]::IsHighSurrogate($character)) {
            $index++
            if ($index -ge $body.Length -or -not [char]::IsLowSurrogate($body[$index])) { throw '版本说明Unicode不完整' }
        } elseif ([char]::IsLowSurrogate($character)) { throw '版本说明Unicode不完整' }
    }
    $sha=[Security.Cryptography.SHA256]::Create()
    try { $hash=[BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-','') } finally { $sha.Dispose() }
    if ($ExePath) {
        $info=[Diagnostics.FileVersionInfo]::GetVersionInfo([IO.Path]::GetFullPath($ExePath))
        if ($info.FileVersion -ne "$Version.0") { throw '待发布EXE版本与目标版本不一致' }
        $markers=[regex]::Matches([string]$info.Comments,'\[ReleaseNotesVersion=([^;\]]+);Sha256=([A-F0-9]{64})\]')
        if ($markers.Count -ne 1 -or $markers[0].Groups[1].Value -cne $Version -or $markers[0].Groups[2].Value -cne $hash) {
            throw '待发布EXE缺少匹配的版本说明绑定；请用该版本说明重新构建，不能复用旧EXE'
        }
    }
    return [pscustomobject]@{ Version=$Version; Body=$body; Sha256=$hash; Path=[IO.Path]::GetFullPath($NotesPath) }
}
