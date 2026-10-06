using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ShiKe.Services;

/// <summary>记录从进程创建及托管入口到首帧的时间；不把后台请求计入首帧。</summary>
internal sealed class StartupPerformance
{
    private readonly long _entryTimestamp = Stopwatch.GetTimestamp();
    private readonly DateTimeOffset _entryUtc = DateTimeOffset.UtcNow;
    private readonly DateTimeOffset _processStartUtc;
    private readonly List<StartupStage> _stages = new();

    public StartupPerformance()
    {
        using var process = Process.GetCurrentProcess();
        _processStartUtc = process.StartTime.ToUniversalTime();
        Mark("托管入口");
    }

    public IReadOnlyList<StartupStage> Stages => _stages;

    public void Mark(string name)
    {
        var elapsed = Stopwatch.GetElapsedTime(_entryTimestamp).TotalMilliseconds;
        var processElapsed = (_entryUtc - _processStartUtc).TotalMilliseconds + elapsed;
        _stages.Add(new StartupStage(name, Math.Round(elapsed, 2), Math.Round(processElapsed, 2)));
    }

    public void WriteLog()
        => Diag.Log("[启动性能] " + string.Join(" | ", _stages.Select(stage =>
            $"{stage.Name}={stage.ManagedMilliseconds:0}ms/进程{stage.ProcessMilliseconds:0}ms")));

    public void WriteProfile()
    {
        var report = new
        {
            schemaVersion = 1,
            programVersion = NetworkService.CurrentVersion,
            processId = Environment.ProcessId,
            processStartUtc = _processStartUtc,
            managedEntryUtc = _entryUtc,
            runtime = RuntimeInformation.FrameworkDescription,
            executableSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(Environment.ProcessPath!))),
            validationScope = "isolated_wpf_first_render; no_hotkeys_network_or_real_input; file_cache_uncontrolled",
            stages = _stages,
        };
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-profile.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static bool IsIsolatedProfileDirectory(string directory)
    {
        try
        {
            var candidate = new DirectoryInfo(Path.GetFullPath(directory));
            if (candidate.Parent?.Name != "启动性能" || candidate.Parent.Parent?.Name != "验证记录")
                return false;
            for (var current = candidate; current is not null; current = current.Parent)
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(candidate.FullName, ".shike-output.json")));
            return marker.RootElement.GetProperty("schemaVersion").GetInt32() == 1 &&
                   marker.RootElement.GetProperty("purpose").GetString() == "启动性能副本";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
                                      InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            return false;
        }
    }
}

internal sealed record StartupStage(string Name, double ManagedMilliseconds, double ProcessMilliseconds);
