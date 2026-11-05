using System.Reflection;

// 独立运行真实 Core 程序集；本项目没有 Windows/WPF 或桌面项目依赖。
var checks = new List<(string Name, Action Run)>();
foreach (var suite in new[] { typeof(AffixAllocationChecks), typeof(CraftLoopChecks), typeof(RuntimeChecks), typeof(PresetChecks) })
{
    foreach (var method in suite.GetMethods(BindingFlags.Public | BindingFlags.Static)
                 .Where(method => method.ReturnType == typeof(void) && method.GetParameters().Length == 0)
                 .OrderBy(method => method.Name, StringComparer.Ordinal))
        checks.Add(($"{suite.Name}.{method.Name}", () => method.Invoke(null, null)));
}

var failures = 0;
foreach (var check in checks)
{
    try { check.Run(); Console.WriteLine($"PASS {check.Name}"); }
    catch (Exception error)
    {
        failures++;
        Console.WriteLine($"FAIL {check.Name}: {(error is TargetInvocationException ? error.InnerException : error)}");
    }
}
Console.WriteLine($"RESULT total={checks.Count}, passed={checks.Count - failures}, failed={failures}");
return failures == 0 ? 0 : 1;
