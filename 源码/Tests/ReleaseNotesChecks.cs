using System.Text;
using ShiKe;
using ShiKe.Services.Updates;

internal static class ReleaseNotesChecks
{
    public static void ContentIsBoundedNormalizedPlainText()
    {
        Require(ReleaseNotesContent.Parse("\uFEFF  修复一\r\n\r\n改进二\r\t说明🙂  ") == "修复一\n\n改进二\n\t说明🙂",
            "BOM、换行和首尾空白应规范化，正文段落和Unicode应保留");
        Require(ReleaseNotesContent.Parse("<b>只是纯文本</b>") == "<b>只是纯文本</b>",
            "说明不应解析或转换为标记内容");
        foreach (var empty in new string?[] { null, "", " \t\r\n", "\uFEFF \r\n" })
            Require(ReleaseNotesContent.Parse(empty) is null, "空说明应跳过");
        Require(ReleaseNotesContent.Parse(new string('字', ReleaseNotesContent.MaximumCharacters))?.Length ==
            ReleaseNotesContent.MaximumCharacters, "长度边界内应保留完整正文");
        Require(ReleaseNotesContent.Parse(new string('字', ReleaseNotesContent.MaximumCharacters + 1)) is null,
            "超长正文应跳过，不能截断关键信息");
        foreach (var invalid in new[] { "正文\0后续", "正文\u0001后续", "正文\ud800", "正文\udc00" })
            Require(ReleaseNotesContent.Parse(invalid) is null, "控制字符和不完整Unicode应拒绝");
    }

    public static void ReadFailuresAreNonFatalAndStreamsAreDisposed()
    {
        var messages = new List<string>();
        var stream = new ObservedStream(Encoding.UTF8.GetBytes("\uFEFF说明\r\n第二行"));
        Require(ReleaseNotesContent.Read(() => stream, messages.Add) == "说明\n第二行" && stream.WasDisposed,
            "正常读取应返回正文并释放资源流");
        Require(messages.Count == 0, "正常正文不应产生失败日志");
        Require(ReleaseNotesContent.Read(() => null, messages.Add) is null, "缺失资源应只跳过说明");
        Require(ReleaseNotesContent.Read(() => throw new IOException("模拟资源读取失败"), messages.Add) is null,
            "资源读取异常不能向已提交更新传播");
        var invalidUtf8 = new ObservedStream([0xC3, 0x28]);
        Require(ReleaseNotesContent.Read(() => invalidUtf8, messages.Add) is null && invalidUtf8.WasDisposed,
            "无效UTF-8应跳过并释放流");
        var tooLong = new ObservedStream(Encoding.UTF8.GetBytes(new string('x', ReleaseNotesContent.MaximumCharacters + 1)));
        Require(ReleaseNotesContent.Read(() => tooLong, messages.Add) is null && tooLong.WasDisposed,
            "读取长度也必须受限");
        Require(messages.Count == 4, "每次读取失败应记录简短诊断");
        Require(ReleaseNotesContent.Read(() => null, _ => throw new InvalidOperationException("日志失败")) is null,
            "说明日志失败同样不能影响成功更新");
    }

    public static void DisplayGateRequiresCommittedAutomaticUpdate()
    {
        for (var mask = 0; mask < 16; mask++)
        {
            var automatic = (mask & 1) != 0;
            var healthy = (mask & 2) != 0;
            var closing = (mask & 4) != 0;
            var shown = (mask & 8) != 0;
            Require(ReleaseNotesContent.ShouldShow(automatic, healthy, closing, shown, "说明") ==
                (automatic && healthy && !closing && !shown), "显示门禁必须同时满足自动升级已提交且尚未显示/关闭");
        }
        foreach (var text in new string?[] { null, "", " \r\n\t" })
            Require(!ReleaseNotesContent.ShouldShow(true, true, false, false, text), "空说明不能弹空窗口");
        Require(ReleaseNotesContent.CurrentVersion == AppIdentity.Version.ToString(3),
            "版本标题必须跟随应用入口版本，不能读取Core默认版本");
    }

    public static void DisplayStatePreventsReentryAndConsumesOnlyRenderedWindow()
    {
        var state = new ReleaseNotesDisplayState();
        Require(!state.TryBegin(false, true, false, "说明") && !state.AlreadyShown,
            "普通启动不应消耗自动升级的展示状态");
        Require(state.TryBegin(true, true, false, "说明"), "首次满足门禁应允许展示");
        Require(!state.TryBegin(true, true, false, "说明") && !state.AlreadyShown,
            "显示中的重入不能创建第二个窗口");
        state.Complete(renderedOnce: false);
        Require(!state.AlreadyShown && state.TryBegin(true, true, false, "说明"),
            "构造或显示失败不能伪记为已经显示");
        state.Complete(renderedOnce: true);
        state.Complete(renderedOnce: false);
        Require(state.AlreadyShown && !state.TryBegin(true, true, false, "说明"),
            "实际显示后本进程只显示一次，重复完成不能清除状态");
    }

    public static void ConcurrentDisplayRequestsHaveOnlyOneWinner()
    {
        var state = new ReleaseNotesDisplayState();
        var winners = 0;
        Parallel.For(0, 32, _ =>
        {
            if (state.TryBegin(true, true, false, "说明")) Interlocked.Increment(ref winners);
        });
        Require(winners == 1, "并发显示请求只能有一个获得展示机会");
        state.Complete(renderedOnce: true);
        Require(state.AlreadyShown, "唯一显示完成后应保留已展示状态");
    }

    private sealed class ObservedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool WasDisposed { get; private set; }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
