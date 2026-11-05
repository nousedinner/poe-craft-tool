using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ShiKe.Services.Updates;

namespace ShiKe.ReleaseTools;

/// <summary>只读版本资源；绝不为检查说明而启动待发布的 EXE。</summary>
internal static class ReleaseNotesArtifact
{
    internal static void Validate(string notesPath, string exePath, string version)
    {
        var bytes = File.ReadAllBytes(notesPath);
        if (ReleaseNotesFormat.Parse(new UTF8Encoding(false, true).GetString(bytes), version) is null)
            throw new InvalidDataException("版本说明声明与待发布版本不符，或正文无效");
        var info = FileVersionInfo.GetVersionInfo(exePath);
        ValidateMetadata(info.FileVersion, info.Comments, version, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    internal static void ValidateMetadata(string? fileVersion, string? comments, string version, string notesHash)
    {
        if (fileVersion != version + ".0") throw new InvalidDataException("EXE版本与待发布版本不符");
        var markers = Regex.Matches(comments ?? "", @"\[ReleaseNotesVersion=([^;\]]+);Sha256=([A-F0-9]{64})\]");
        if (markers.Count != 1 || markers[0].Groups[1].Value != version || markers[0].Groups[2].Value != notesHash)
            throw new InvalidDataException("EXE未绑定这份版本说明，请重新构建；不能使用旧EXE冒充本次发布");
    }
}
