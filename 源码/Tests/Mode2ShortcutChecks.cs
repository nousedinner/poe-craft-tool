using ShiKe.Services;
using ShiKe.Tools.Craft;
using System.Windows;

internal static class Mode2ShortcutChecks
{
    public static void AlterationAugmentationKeepsOneShiftSession()
    {
        var session = new Session();
        var alterations = new Queue<Mode2AlterationStep>(new[]
        {
            Step(1, 0, "甲", "其他"), Step(1, 0, "甲"), Step(0, 1, "乙"),
        });
        var augmentations = new Queue<Mode2AlterationStep>(new[]
        {
            Step(1, 0, "甲", "其他"), Step(1, 1, "甲", "乙"),
        });
        var operations = new List<string>();
        var result = Mode2PairedCurrencyLoop.RunAlterationAugmentationAsync(Rules(), session.Begin,
            session.Release, _ =>
            {
                Require(session.Held, "改造时必须保持原Shift会话");
                operations.Add("改造");
                return Task.FromResult(alterations.Dequeue());
            }, _ =>
            {
                Require(session.Held, "增幅时不能释放再重按Shift");
                operations.Add("Alt增幅");
                return Task.FromResult(augmentations.Dequeue());
            }, _ => { }, () => { }, CancellationToken.None).GetAwaiter().GetResult();
        Require(operations.SequenceEqual(new[] { "改造", "改造", "Alt增幅", "改造", "Alt增幅" }),
            "两词缀部分命中应继续改造，增幅失败后应直接改造");
        Require(result.Result.MeetsFinalRules(Rules()), "应在增幅满足要求后结束");
        Require(session.Begins == 1 && session.Releases == 1 && !session.Held,
            "整个改造增幅任务只能建立一个会话并在结束时释放");
    }

    public static void TwoAffixesFinishWithoutAugmentation()
    {
        var session = new Session();
        var calls = 0;
        Mode2PairedCurrencyLoop.RunAlterationAugmentationAsync(Rules(), session.Begin, session.Release,
            _ => { calls++; return Task.FromResult(Step(1, 1, "甲", "乙")); },
            _ => throw new InvalidOperationException("两词缀完成不能误用增幅"),
            _ => { }, () => { }, CancellationToken.None).GetAwaiter().GetResult();
        Require(calls == 1 && session.Begins == 1 && session.Releases == 1, "完成后不能追加通货");
    }

    public static void ScouringAlchemySkipsWhiteAndKeepsSession()
    {
        foreach (var rarity in new[] { ItemRarity.Normal, ItemRarity.Magic, ItemRarity.Rare })
        {
            var session = new Session();
            var operations = new List<string>();
            var alchemyCount = 0;
            Mode2PairedCurrencyLoop.RunScouringAlchemyAsync(Rules(), rarity, session.Begin, session.Release,
                _ =>
                {
                    Require(session.Held, "Alt重铸必须保持Shift");
                    operations.Add("重铸");
                    return Task.CompletedTask;
                }, _ =>
                {
                    Require(session.Held, "点金不能重新建立Shift会话");
                    operations.Add("点金");
                    return Task.FromResult(++alchemyCount == 1
                        ? Step(0, 0, "其他甲", "其他乙") : Step(1, 1, "甲", "乙"));
                }, () => { }, CancellationToken.None).GetAwaiter().GetResult();
            var expected = rarity == ItemRarity.Normal
                ? new[] { "点金", "重铸", "点金" } : new[] { "重铸", "点金", "重铸", "点金" };
            Require(operations.SequenceEqual(expected), "白装应跳过首次重铸，后续每次点金前必须先重铸");
            Require(session.Begins == 1 && session.Releases == 1 && !session.Held, "点金重铸不能反复选币/按放Shift");
        }
    }

    public static void ScouringFailureBlocksAlchemy()
    {
        var session = new Session();
        var alchemyCalls = 0;
        var failure = new IOException("模拟重铸未确认");
        var actual = Capture<IOException>(() => Mode2PairedCurrencyLoop.RunScouringAlchemyAsync(
            Rules(), ItemRarity.Rare, session.Begin, session.Release,
            _ => Task.FromException(failure),
            _ => { alchemyCalls++; return Task.FromResult(Step(1, 1, "甲", "乙")); },
            () => { }, CancellationToken.None).GetAwaiter().GetResult());
        Require(ReferenceEquals(failure, actual) && alchemyCalls == 0, "重铸失败必须停止，不能点金或补点");
        Require(session.Releases == 1 && !session.Held, "重铸异常必须释放Shift");
    }

    public static void AugmentationFailureBlocksNextAlteration()
    {
        var session = new Session();
        var alterations = 0;
        var pairs = 0;
        Capture<IOException>(() => Mode2PairedCurrencyLoop.RunAlterationAugmentationAsync(Rules(),
            session.Begin, session.Release,
            _ => { alterations++; return Task.FromResult(Step(1, 0, "甲")); },
            _ => { pairs++; return Task.FromException<Mode2AlterationStep>(new IOException("模拟增幅未确认")); },
            _ => { }, () => { }, CancellationToken.None).GetAwaiter().GetResult());
        Require(alterations == 1 && pairs == 1 && session.Releases == 1,
            "增幅未确认不能再改造、重选或补用通货");
    }

    public static void CancelledPairDoesNotAdvanceOrReportSuccess()
    {
        foreach (var scourAlch in new[] { false, true })
        {
            using var cts = new CancellationTokenSource();
            var session = new Session();
            var baseCalls = 0;
            Capture<OperationCanceledException>(() =>
            {
                if (scourAlch)
                    Mode2PairedCurrencyLoop.RunScouringAlchemyAsync(Rules(), ItemRarity.Rare,
                        session.Begin, session.Release, _ => { cts.Cancel(); return Task.CompletedTask; },
                        _ => { baseCalls++; return Task.FromResult(Step(1, 1, "甲", "乙")); },
                        () => { }, cts.Token).GetAwaiter().GetResult();
                else
                    Mode2PairedCurrencyLoop.RunAlterationAugmentationAsync(Rules(), session.Begin, session.Release,
                        _ => { baseCalls++; return Task.FromResult(Step(1, 0, "甲")); },
                        _ => { cts.Cancel(); return Task.FromResult(Step(1, 1, "甲", "乙")); },
                        _ => { }, () => { }, cts.Token).GetAwaiter().GetResult();
            });
            Require(baseCalls == (scourAlch ? 0 : 1) && session.Releases == 1 && !session.Held,
                "配对过程中取消必须阻止下一次通货和完成报告，并使用不可取消清理");
        }
    }

    public static void CancelledBeforeSessionDoesNotSelectCurrency()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var session = new Session();
        Capture<OperationCanceledException>(() => Mode2PairedCurrencyLoop.RunAlterationAugmentationAsync(
            Rules(), session.Begin, session.Release, _ => throw new InvalidOperationException("不得输入"),
            _ => throw new InvalidOperationException("不得输入"), _ => { }, () => { }, cts.Token).GetAwaiter().GetResult());
        Require(session.Begins == 0 && session.Releases == 0, "已停止的任务不能选币或建立Shift会话");
    }

    public static void AltClickReleasesBeforeCtrlCCopy()
    {
        var events = new List<string>();
        var heldAlt = false;
        CraftAltClick.RunAsync(() => { heldAlt = true; events.Add("AltDown"); },
            () => { heldAlt = false; events.Add("AltUp"); },
            _ =>
            {
                Require(heldAlt, "配对左键期间必须按住Alt");
                events.Add("LeftDown"); events.Add("LeftUp");
                return Task.CompletedTask;
            }, (delay, _) =>
            {
                Require(delay == 20, "Alt切换准备不得改动原鼠标按压或复制时序");
                events.Add("AltSettle");
                return Task.CompletedTask;
            }, CancellationToken.None).GetAwaiter().GetResult();
        CraftCopyShortcut.Send(key =>
        {
            Require(!heldAlt, "复制不得借用快捷通货的Alt状态");
            events.Add($"{key}Down");
        }, key => events.Add($"{key}Up"), _ => throw new InvalidOperationException("正常复制不应清理异常"));
        Require(events.SequenceEqual(new[] { "AltDown", "AltSettle", "LeftDown", "LeftUp", "AltUp",
            $"{KeyCode.Control}Down", $"{KeyCode.C}Down", $"{KeyCode.C}Up", $"{KeyCode.Control}Up" }),
            "Alt必须在左键松开后、Ctrl+C之前释放；复制不能按放Shift或Alt");
    }

    public static void CancelledAltSettleDoesNotClickAndAlwaysReleases()
    {
        using var cts = new CancellationTokenSource();
        var clicks = 0;
        var releases = 0;
        Capture<OperationCanceledException>(() => CraftAltClick.RunAsync(() => { }, () => releases++,
            _ => { clicks++; return Task.CompletedTask; },
            (_, _) => { cts.Cancel(); return Task.CompletedTask; }, cts.Token).GetAwaiter().GetResult());
        Require(clicks == 0 && releases == 1, "按Alt后的取消不能发送左键，仍须释放Alt");
    }

    public static void AltInputFailureAlwaysReleases()
    {
        foreach (var failPress in new[] { false, true })
        {
            var releases = 0;
            var clicks = 0;
            var failure = new IOException("模拟Alt或鼠标输入失败");
            var actual = Capture<IOException>(() => CraftAltClick.RunAsync(
                () => { if (failPress) throw failure; }, () => releases++,
                _ => { clicks++; return Task.FromException(failure); },
                (_, _) => Task.CompletedTask, CancellationToken.None).GetAwaiter().GetResult());
            Require(ReferenceEquals(actual, failure) && releases == 1 && clicks == (failPress ? 0 : 1),
                "输入失败必须保留异常并释放Alt，不得追加点击");
        }
    }

    public static void CtrlCUsesOnlyControlAndCAndCleansFailures()
    {
        for (var failureAt = 0; failureAt < 4; failureAt++)
        {
            var counter = 0;
            var released = new List<ushort>();
            var failure = new IOException("模拟复制按键失败");
            void Input(ushort key)
            {
                Require(key is KeyCode.Control or KeyCode.C, "复制不应发送Shift或Alt");
                if (counter++ == failureAt) throw failure;
            }
            var actual = Capture<IOException>(() => CraftCopyShortcut.Send(Input, Input, released.Add));
            Require(ReferenceEquals(actual, failure) && released.SequenceEqual(new[] { KeyCode.C, KeyCode.Control }),
                "复制任一按键失败都要尽力释放C和Ctrl，不能误动Alt/Shift");
        }
    }

    public static void CoordinateRequirementsKeepOtherModes()
    {
        var rules = Rules();
        var coords = new Dictionary<string, Point> { ["item"] = new(400, 400), [Currency.Alteration] = new(100, 100) };
        Require(CraftEngine.CheckCoordinates(rules, coords, false, false) is null,
            "快捷改造增幅不应再要求增幅坐标");
        Require(CraftEngine.CheckCoordinates(rules, coords, true, false) == "点金石坐标", "重铸点金必须要求常驻点金坐标");
        coords = new() { ["item"] = new(400, 400), [Currency.Alchemy] = new(100, 100) };
        Require(CraftEngine.CheckCoordinates(rules, coords, true, false) is null,
            "快捷重铸点金不应再要求重铸坐标");
        Require(CraftEngine.CheckCoordinates(rules, coords, false, false) == "改造石坐标", "改造增幅仍要求改造坐标");
        rules.Mode = CraftMode.AltAugRegal;
        coords = new() { ["item"] = new(400, 400), [Currency.Alteration] = new(100, 100) };
        Require(CraftEngine.CheckCoordinates(rules, coords, false, false) == "增幅石坐标", "Mode3坐标契约保持");
        rules.Mode = CraftMode.Single;
        rules.SingleCurrency = Currency.Chaos;
        Require(CraftEngine.CheckCoordinates(rules, coords, false, false) == "混沌石坐标", "单通货坐标契约保持");
        Require(coords.Count == 2, "检查不能清除原坐标");
    }

    public static void KeepsVersion47SevenProfilesAndTenMs()
    {
        Require(Enum.GetValues<CraftSpeedExperiment>().Length == 7, "应从v1.0.47保留七个对照档");
        Require(CraftTimingProfile.For(CraftSpeedExperiment.Balanced).FirstWaitMs(10) == 10,
            "不能夹带v1.0.48的33ms下限");
        Require(CraftTimingProfile.For(CraftSpeedExperiment.Combined).FirstWaitMs(33) == 10,
            "v1.0.47组合档的实际首次等待保持");
        Require(CraftStateSync.CalculateChangeTimeoutMs(10) == 900 && CraftStateSync.RetryBackoffMs == 20,
            "不得改变同步预算或复制退避");
    }

    private static CraftRules Rules() => new() { Mode = CraftMode.AltAug, PrimaryHitCount = 1, SecondaryHitCount = 1 };

    private static Mode2AlterationStep Step(int primary, int secondary, params string[] descriptions)
    {
        var text = "物品类别: 头盔\n稀有度: 魔法\n测试\n铁帽\n--------\n" +
            string.Join("\n", descriptions.Select((description, index) =>
                $"{{ {(index == 0 ? "前缀" : "后缀")}属性 \"测试{index}\" (等阶: 1) }}\n{description}"));
        return new(new CraftItemSnapshot(text), new AffixCheckResult { PrimaryHits = primary, SecondaryHits = secondary });
    }

    private sealed class Session
    {
        public bool Held;
        public int Begins;
        public int Releases;
        public Task Begin(CancellationToken token) { token.ThrowIfCancellationRequested(); Begins++; Held = true; return Task.CompletedTask; }
        public Task Release(CancellationToken token)
        {
            Require(!token.CanBeCanceled, "清理必须使用不可取消令牌");
            Releases++; Held = false;
            return Task.CompletedTask;
        }
    }

    private static T Capture<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"预期{typeof(T).Name}异常");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
