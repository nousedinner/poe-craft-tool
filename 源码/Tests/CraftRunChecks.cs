using System.Text.Json;
using ShiKe.Tools.Craft;

internal static class CraftRunChecks
{
    public static void FormalTimingDefaultsToBalanced()
    {
        var timing = CraftTimingProfile.Default;
        Require(new[] { timing.ClipboardPollMs, timing.ItemMovementMs, timing.CopyBeforeMs, timing.CopyAfterMs }
            .SequenceEqual(new[] { 5, 10, 20, 10 }), "正式版必须固定使用已实测稳健组合");
        Require(typeof(CraftTool).GetProperty("SpeedExperiment") is null,
            "正式抽屉不应继续暴露实验选择");
        Require(typeof(CraftEngine).GetMethods().Single(method => method.Name == "Start").GetParameters().Length == 9,
            "正式启动接口不能夹带实验档位");
    }

    public static void FormalTimingKeepsDelayAndSafetyBudget()
    {
        var timing = CraftTimingProfile.Default;
        foreach (var configuredDelay in new[] { 10, 33, 47, 200 })
        {
            Require(timing.FirstWaitMs(configuredDelay) == configuredDelay,
                "正式时序不能覆盖用户配置的首次等待");
            Require(timing.FirstWaitMs(configuredDelay) + timing.ItemMovementMs + timing.CopyBeforeMs == configuredDelay + 30,
                "不能比已实测稳健组合更早发送复制");
            Require(CraftStateSync.CalculateChangeTimeoutMs(configuredDelay) == 900 &&
                CraftStateSync.RetryBackoffMs == 20 && CraftStateSync.CopyAttemptTimeoutMs == 120,
                "正式化不能改变同步预算／复制退避／单次复制上限");
        }
    }

    public static void FormalTimingDoesNotAddSavedSettings()
    {
        var source = new CraftTool { DelayMs = 47, ExhaustionThreshold = 12 };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) source.SaveSettings(writer);
        using var document = JsonDocument.Parse(stream.ToArray());
        Require(!document.RootElement.EnumerateObject().Any(property =>
            property.Name.Contains("experiment", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("profile", StringComparison.OrdinalIgnoreCase)),
            "稳健时序固定在内部，不能新增配置字段");
        var restored = new CraftTool();
        restored.LoadSettings(document.RootElement);
        Require(restored.DelayMs == 47 && restored.ExhaustionThreshold == 12, "现有设置必须正常往返");
    }

    public static void CompleteCycleIncludesClickCopyAndMatching()
    {
        double now = 0;
        var measurement = new CraftRunMeasurement(() => now);
        measurement.RecordBaseline("旧文本");
        measurement.CompleteMatching("旧文本", new AffixCheckResult());
        now = 100; // 启动基线、选通货等也进入任务平均。
        measurement.BeginClick("改造石");
        now = 150;
        measurement.ClickSent();
        measurement.CopyAttempt();
        measurement.ClipboardRead();
        now = 220;
        measurement.ConfirmTransition("新文本");
        now = 230;
        measurement.CompleteMatching("新文本", new AffixCheckResult { PrimaryHits = 1 });
        var summary = measurement.Stop("匹配成功");
        Require(summary.SentClicks == 1 && summary.ConfirmedTransitions == 1 && summary.MatchedCycles == 1,
            "启动基线不得计为洗装循环");
        Equal(230, summary.ActiveMs, "任务计时必须包含选通货等开销");
        Equal(130, summary.MatchCycleMedianMs!.Value, "循环必须从装备点击前到匹配结束");
        Equal(1000d / 230, summary.MatchedPerSecond, "总速度不能拿解析耗时的倒数计算");
    }

    public static void ReadsAndRejectedCopiesAreDifferentCounters()
    {
        double now = 0;
        var measurement = new CraftRunMeasurement(() => now);
        measurement.BeginClick("改造石");
        measurement.ClickSent();
        measurement.CopyAttempt();
        for (var index = 0; index < 20; index++) measurement.ClipboardRead();
        measurement.EmptyCopy();
        measurement.CopyAttempt();
        measurement.ClipboardRead();
        measurement.RejectCopy(unchanged: true);
        measurement.CopyAttempt();
        measurement.ClipboardRead();
        measurement.RejectCopy(unchanged: false);
        measurement.SyncFailed();
        now = 900;
        var summary = measurement.Stop("转换失败");
        Require(summary.CopyAttempts == 3 && summary.ClipboardReads == 22, "读取次数与重新复制次数被混淆");
        Require(summary.RejectedCopies == 2 && summary.UnchangedCopies == 1 && summary.EmptyCopies == 1,
            "读取空值不能直接累加拒绝计数，转换不符也须单列");
        Require(summary.MatchedCycles == 0 && summary.ConfirmedTransitions == 0 && summary.SyncFailures == 1,
            "失败点击不得制造吞吐量");
    }

    public static void ForegroundPauseIsExcludedAndStopIsStable()
    {
        double now = 0;
        var measurement = new CraftRunMeasurement(() => now);
        measurement.BeginClick("改造石");
        measurement.ClickSent();
        now = 30;
        measurement.PauseForeground();
        measurement.PauseForeground();
        now = 1030;
        measurement.ResumeForeground();
        now = 1130;
        measurement.ConfirmTransition("已确认");
        measurement.CompleteMatching("已确认", new AffixCheckResult());
        var summary = measurement.Stop("已停止");
        Equal(1000, summary.ForegroundPauseMs, "重复暂停不能双重扣时");
        Equal(130, summary.ActiveMs, "前台等待不能拖低活动期间速度");
        Equal(130, summary.MatchCycleP95Ms!.Value, "循环也必须扣除前台等待");
        now = 2000;
        Equal(summary.ElapsedMs, measurement.Stop("已停止").ElapsedMs, "报告落盘不应继续增加耗时");
        var pausedAtStop = new CraftRunMeasurement(() => now);
        pausedAtStop.PauseForeground();
        now += 500;
        Equal(0, pausedAtStop.Stop("取消").ActiveMs, "前台等待中取消也必须正确扣时");
    }

    public static void TransitionOnlyAndCancelledClicksDoNotBecomeMatchedCycles()
    {
        double now = 0;
        var measurement = new CraftRunMeasurement(() => now);
        measurement.BeginClick("重铸石");
        measurement.ClickSent();
        now = 150;
        measurement.ConfirmTransition("普通物品");
        now = 500; // 后续选通货的开销不能加到重铸的单次延迟。
        measurement.BeginClick("点金石");
        now = 520;
        var summary = measurement.Stop("取消");
        Require(summary.SentClicks == 1 && summary.ConfirmedTransitions == 1 && summary.MatchedCycles == 0,
            "仅转换确认或点击前取消不能计为完成匹配");
        using var report = JsonDocument.Parse(measurement.SerializeReport(summary, new { profile = "基线" }));
        var samples = report.RootElement.GetProperty("samples");
        Equal(150, samples[0].GetProperty("activeMs").GetDouble(), "单次转换延迟不得包含后续选通货");
        Require(!samples[1].GetProperty("clickSent").GetBoolean(), "取消前未发送的点击须如实记录");
    }

    public static void TraceRetainsLatestItemAndSeparateTimingSamples()
    {
        double now = 0;
        var measurement = new CraftRunMeasurement(() => now);
        measurement.RecordBaseline("原文0");
        for (var index = 1; index <= 35; index++)
        {
            measurement.BeginClick("改造石");
            measurement.ClickSent();
            now += 100;
            measurement.ConfirmTransition($"原文{index}");
            measurement.CompleteMatching($"原文{index}", new AffixCheckResult());
        }
        var summary = measurement.Stop("成功");
        using var report = JsonDocument.Parse(measurement.SerializeReport(summary, new { profile = "基线" }));
        var trace = report.RootElement.GetProperty("recentItemStates");
        Require(trace.GetArrayLength() == 30, "原文轨迹应限量保留，避免运行内存持续膨胀");
        Require(trace[29].GetProperty("afterText").GetString() == "原文35", "最终命中原文不能被挤掉");
        Require(report.RootElement.GetProperty("samples").GetArrayLength() == 35, "计时样本须独立于原文轨迹保留");
    }

    public static void ReportsAreSavedAndWriteFailureDoesNotThrow()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "speed-report-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            double now = 0;
            var measurement = new CraftRunMeasurement(() => now);
            now = 100;
            var summary = measurement.Stop("取消");
            var saved = measurement.SaveReport(summary, new { profile = "原等待" }, root);
            Require(saved.Path is not null && File.Exists(saved.Path), "停止后必须生成可读报告");
            using var report = JsonDocument.Parse(File.ReadAllText(saved.Path!));
            Require(report.RootElement.GetProperty("summary").GetProperty("matchedCycles").GetInt32() == 0,
                "零次操作不能伪造速度");
            var occupied = Path.Combine(root, "occupied-file");
            File.WriteAllText(occupied, "不能作为目录");
            var failed = measurement.SaveReport(summary, new { profile = "原等待" }, occupied);
            Require(failed.Path is null && failed.Text.Contains("未保存", StringComparison.Ordinal),
                "保存失败应报告事实，不能令停止流程抛异常");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Equal(double expected, double actual, string message)
        => Require(Math.Abs(expected - actual) < 0.00001, $"{message}: expected={expected}, actual={actual}");
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
