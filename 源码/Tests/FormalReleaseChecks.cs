using System.Security.Cryptography;
using System.Text;
using ShiKe.Tools.Craft;

internal static class FormalReleaseChecks
{
    public static void PresentationKeepsAcceptedMarkup()
    {
        var expected = new Dictionary<string, string>
        {
            ["Tools/Craft/CraftPage.xaml"] = "8D93AAA8AFC1F697720DAA91735CB7606B98E6278363D549A6FBD3A85B3B66E2",
        };
        // 已确认的 XAML 布局和文字保持。架构重构允许 C# 实现变化：
        // 动态布局由 AppearanceChecks 的真实 WPF 检查保护，配置/运行由架构回归保护。
        // 不用更新代码文件哈希来宣称行为不变。
        var root = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "拾刻.csproj")))
        {
            if (File.Exists(Path.Combine(root.FullName, "源码", "拾刻.csproj")))
            {
                root = new DirectoryInfo(Path.Combine(root.FullName, "源码"));
                break;
            }
            root = root.Parent;
        }
        Require(root is not null, "未找到源码目录，不能跳过页面一致性检查");
        foreach (var (path, hash) in expected)
        {
            var text = File.ReadAllText(Path.Combine(root!.FullName, path))
                .TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
            var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            Require(actual == hash, $"{path}的布局／文字与已确认界面不同");
        }
    }

    public static void PendingCopyDoesNotAdvanceCurrency()
    {
        var rules = new CraftRules { Mode = CraftMode.AltAug, PrimaryHitCount = 1, SecondaryHitCount = 1 };
        var validCopy = new TaskCompletionSource<Mode2AlterationStep>(TaskCreationOptions.RunContinuationsAsynchronously);
        var alterations = 0;
        var augmentations = 0;
        var released = false;
        var task = Mode2PairedCurrencyLoop.RunAlterationAugmentationAsync(rules, _ => Task.CompletedTask,
            _ => { released = true; return Task.CompletedTask; },
            _ => Task.FromResult(++alterations == 1 ? Step(1, 0, 1) : Step(1, 1, 2)),
            _ => { augmentations++; return validCopy.Task; }, _ => { }, () => { }, CancellationToken.None);
        Require(!task.IsCompleted && alterations == 1 && augmentations == 1 && !released,
            "当前通货尚无有效复制结果时，不能提前终检、继续改造或释放会话");
        validCopy.SetResult(Step(1, 0, 2)); // 重复制完成，结果未达标，才允许下一发改造。
        var result = task.GetAwaiter().GetResult();
        Require(result.Result.MeetsFinalRules(rules) && alterations == 2 && augmentations == 1 && released,
            "有效结果确认后才能推进，不能跳过当前判定");

        var scourCopy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var alchemy = 0;
        var scourTask = Mode2PairedCurrencyLoop.RunScouringAlchemyAsync(rules, ItemRarity.Rare,
            _ => Task.CompletedTask, _ => Task.CompletedTask, _ => scourCopy.Task,
            _ => { alchemy++; return Task.FromResult(Step(1, 1, 2)); }, () => { }, CancellationToken.None);
        Require(!scourTask.IsCompleted && alchemy == 0, "重铸未确认时不能使用点金");
        scourCopy.SetResult();
        scourTask.GetAwaiter().GetResult();
        Require(alchemy == 1, "重铸确认后才能点金一次");
    }

    private static Mode2AlterationStep Step(int primary, int secondary, int count)
    {
        var text = "物品类别: 头盔\n稀有度: 魔法\n测试\n铁帽\n--------\n" +
            string.Join("\n", Enumerable.Range(0, count).Select(index =>
                $"{{ {(index == 0 ? "前缀" : "后缀")}属性 \"测试{index}\" (等阶: 1) }}\n词缀{index}"));
        return new(new CraftItemSnapshot(text), new AffixCheckResult { PrimaryHits = primary, SecondaryHits = secondary });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
