using System.Text;
using ShiKe.Services;

namespace ShiKe.Tools.Craft;

/// <summary>
/// 保存 Mode 3 改造阶段的疑似漏判样本。
/// 独立目录最多保留 30 份，只删除本记录器自己创建的 mode3-miss-*.txt。
/// 写入失败只记录普通诊断日志，绝不能中断洗装流程。
/// </summary>
internal static class Mode3DiagnosticRecorder
{
    private const int MaxSamples = 30;
    private static readonly object Gate = new();

    public static void Capture(
        string itemText,
        CraftRules rules,
        AffixCheckResult result,
        int explicitAffixCount,
        int threshold,
        int runId,
        int useCount)
    {
        lock (Gate)
        {
            try
            {
                var directory = Path.Combine(AppContext.BaseDirectory, "data", "mode3-diagnostics");
                Directory.CreateDirectory(directory);

                var fileName = $"mode3-miss-{DateTime.Now:yyyyMMdd-HHmmss-fff}-run{runId}-use{useCount}.txt";
                var path = Path.Combine(directory, fileName);
                File.WriteAllText(path, BuildContents(
                    itemText, rules, result, explicitAffixCount, threshold, runId, useCount));

                TrimOldestSamples(directory);
                Diag.Log($"[Mode3诊断] 已保存未达阈值样本: data\\mode3-diagnostics\\{fileName}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Diag.Log($"[Mode3诊断] 样本保存失败: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static string BuildContents(
        string itemText,
        CraftRules rules,
        AffixCheckResult result,
        int explicitAffixCount,
        int threshold,
        int runId,
        int useCount)
    {
        var builder = new StringBuilder();
        builder.AppendLine("拾刻 Mode 3 改造阶段未达阈值诊断样本");
        builder.AppendLine($"记录时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        builder.AppendLine($"运行/使用次数: {runId}/{useCount}");
        builder.AppendLine($"显式词缀数: {explicitAffixCount}");
        builder.AppendLine($"魔法阶段阈值: {threshold}");
        builder.AppendLine($"主命中: {result.PrimaryHits} [{string.Join(", ", result.MatchedPrimaryRules)}]");
        builder.AppendLine($"次命中: {result.SecondaryHits} [{string.Join(", ", result.MatchedSecondaryRules)}]");
        builder.AppendLine($"主词缀规则: {string.Join(" | ", rules.PrimaryAffixes.Select(rule => rule.Text))}");
        builder.AppendLine($"次词缀规则: {string.Join(" | ", rules.SecondaryAffixes.Select(rule => rule.Text))}");
        builder.AppendLine("----- 完整 Ctrl+Alt+C 物品文本 -----");
        builder.AppendLine(itemText.Trim());
        return builder.ToString();
    }

    private static void TrimOldestSamples(string directory)
    {
        var files = Directory.GetFiles(directory, "mode3-miss-*.txt")
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.Name, StringComparer.Ordinal)
            .ToArray();

        var removeCount = Math.Max(0, files.Length - MaxSamples);
        for (var i = 0; i < removeCount; i++)
            files[i].Delete();
    }
}
