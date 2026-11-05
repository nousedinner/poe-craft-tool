using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace ShiKe.Services.Updates;

/// <summary>仅供拥有标记的隔离安装副本验收。保留生产更新事务，只替换外部入口并硬禁用原生输入。</summary>
internal sealed class UpdateAcceptanceContext
{
    internal const string CaseVariable = "SHIKE_WPF_UPDATE_CASE";
    internal const string TokenVariable = "SHIKE_WPF_UPDATE_TOKEN";
    internal const string StageVariable = "SHIKE_WPF_UPDATE_STAGE";
    private const string Owner = "ShiKe.WpfUpdateAcceptance.v1";
    private readonly CaseSettings _settings;
    private readonly object _gate = new();
    private readonly List<object> _events = [];
    private FileStream? _blockedSettings;
    private int _deniedNativeCalls;
    private int _versionRequests;
    private int _packageRequests;
    private int _unexpectedRequests;
    private int _dialogs;
    private int _releaseNotesDialogs;
    private bool _healthCommitted;
    private readonly List<object> _releaseNotesEvidence = [];
    private bool _reported;

    private UpdateAcceptanceContext(CaseSettings settings, string directory, string stage, string role)
    {
        _settings = settings;
        CaseDirectory = directory;
        Stage = stage;
        Role = role;
    }

    internal static UpdateAcceptanceContext? Current { get; private set; }
    internal string CaseDirectory { get; }
    internal string InstallationDirectory => Path.Combine(CaseDirectory, "installation with 中文 空格");
    internal string Stage { get; }
    internal string Role { get; }
    internal bool IsWarmup => Stage == "warmup";
    internal bool IsRestart => Stage == "restart";
    internal bool IsOrdinaryLaunch => Role == "origin" || IsRestart;
    internal bool FailBeforeHealthy => Role == "candidate" && _settings.Scenario == "startup-failure";
    internal string MutexSuffix => ".WpfUpdateAcceptance." + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(InstallationDirectory.ToUpperInvariant())))[..24];
    internal TaskCompletionSource MainRendered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource VersionChecked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static UpdateAcceptanceContext? LoadFromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable(CaseVariable);
        var token = Environment.GetEnvironmentVariable(TokenVariable);
        var stage = Environment.GetEnvironmentVariable(StageVariable);
        if (path is null && token is null && stage is null) return null;
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(token) || stage is not ("warmup" or "upgrade" or "restart"))
            throw new InvalidDataException("更新验收环境不完整，禁止退回普通启动");
        if (!Path.IsPathFullyQualified(path) || Path.GetFileName(path) != "acceptance.json")
            throw new InvalidDataException("更新验收配置路径无效");
        path = Path.GetFullPath(path);
        var caseDirectory = Path.GetDirectoryName(path)!;
        var batch = Directory.GetParent(caseDirectory) ?? throw new InvalidDataException("缺少验收批次");
        if (!Guid.TryParseExact(batch.Name, "N", out _) || batch.Parent?.Name != "WPF更新验收" ||
            batch.Parent.Parent?.Name != "验证记录" || batch.Parent.Parent.Parent?.Name != "net10.0-windows" ||
            batch.Parent.Parent.Parent.Parent?.Name != "Release" || batch.Parent.Parent.Parent.Parent.Parent?.Name != "bin")
            throw new InvalidDataException("验收只能运行在固定输出根的专用副本目录");
        UpdateSession.EnsureNoReparsePoints(path);
        var marker = Path.Combine(batch.FullName, ".shike-output.json");
        UpdateSession.EnsureNoReparsePoints(marker);
        using (var document = JsonDocument.Parse(File.ReadAllText(marker)))
        {
            if (document.RootElement.GetProperty("schemaVersion").GetInt32() != 1 ||
                document.RootElement.GetProperty("purpose").GetString() != "WPF更新验收")
                throw new InvalidDataException("更新验收目录归属不符");
        }
        if (new FileInfo(path).Length > 32768) throw new InvalidDataException("更新验收配置过大");
        var settings = JsonSerializer.Deserialize<CaseSettings>(File.ReadAllText(path)) ?? throw new InvalidDataException("更新验收配置为空");
        if (settings.Owner != Owner || settings.Nonce.Length != 64 || !settings.Nonce.All(Uri.IsHexDigit) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(settings.Nonce), Encoding.UTF8.GetBytes(token)) ||
            settings.ExpiresUtc <= DateTimeOffset.UtcNow || settings.ExpiresUtc > DateTimeOffset.UtcNow.AddHours(4) ||
            settings.Scenario is not ("success" or "startup-failure" or "exit-save-failure" or "signature-failure"))
            throw new InvalidDataException("更新验收身份、期限或场景无效");
        if (!Version.TryParse(settings.OriginalVersion, out var original) || !Version.TryParse(settings.CandidateVersion, out var next) ||
            next <= original || !IsHash(settings.OriginalSha256) || !IsHash(settings.CandidateSha256) || !IsHash(settings.ManifestSha256))
            throw new InvalidDataException("更新验收版本或文件身份无效");
        var expectedExecutable = Path.Combine(caseDirectory, "installation with 中文 空格", "拾刻.exe");
        if (!UpdateSession.SamePath(expectedExecutable, Environment.ProcessPath ?? ""))
            throw new InvalidDataException("验收不能作用于日常主程序或非本案安装目录");
        UpdateSession.EnsureNoReparsePoints(expectedExecutable);
        foreach (var item in Directory.EnumerateFileSystemEntries(caseDirectory, "*", SearchOption.AllDirectories))
            UpdateSession.EnsureNoReparsePoints(item);
        var hash = UpdateSession.HashFile(expectedExecutable);
        var role = hash.Equals(settings.OriginalSha256, StringComparison.OrdinalIgnoreCase) ? "origin" :
            hash.Equals(settings.CandidateSha256, StringComparison.OrdinalIgnoreCase) ? "candidate" :
            throw new InvalidDataException("验收程序不是声明的两份原样 EXE");
        if ((role == "origin" ? original : next) != new Version(NetworkService.CurrentVersion.Major,
                NetworkService.CurrentVersion.Minor, NetworkService.CurrentVersion.Build) || stage == "warmup" && role != "origin")
            throw new InvalidDataException("验收程序版本不符");
        var manifest = Path.Combine(caseDirectory, "release", "version.json");
        if (!UpdateSession.HashFile(manifest).Equals(settings.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("验收清单不是本案固定文件");
        if (stage == "restart")
        {
            if (role != "candidate" || settings.Scenario != "success")
                throw new InvalidDataException("普通重启验收仅允许成功更新后的本案候选程序");
            var updateRoot = Path.Combine(Path.GetDirectoryName(expectedExecutable)!, ".shike-updates");
            if (!Directory.Exists(updateRoot)) throw new InvalidDataException("普通重启验收缺少已完成的更新事务");
            var completed = Directory.EnumerateDirectories(updateRoot)
                .Where(directory => Guid.TryParseExact(Path.GetFileName(directory), "N", out _))
                .Select(directory => UpdateSession.LoadOwned(Path.Combine(directory, "session.json"),
                    ReleaseTrust.PublicKeyPem, ReleaseTrust.KeyId))
                .Count(session => UpdateSession.SamePath(session.TargetPath, expectedExecutable) &&
                    session.Manifest.Version == next && session.ReadPhase() == "completed" && session.HasCommitAcknowledgement);
            if (completed != 1) throw new InvalidDataException("普通重启验收未确认本案唯一成功提交的更新");
        }
        var context = new UpdateAcceptanceContext(settings, caseDirectory, stage, role);
        Current = context;
        context.Record("acceptance-validated", new { role, stage, executableSha256 = hash, scenario = settings.Scenario });
        return context;
    }

    internal NetworkService CreateNetwork() => new(new LocalHandler(this, packageOnly: false), Diag.Log);
    internal UpdatePackageDownloader CreateDownloader() => new(new LocalHandler(this, packageOnly: true));

    internal static void ForbidNativeOperation(string operation)
    {
        var context = Current;
        if (context is null) return;
        Interlocked.Increment(ref context._deniedNativeCalls);
        context.Record("unexpected-native-operation", operation);
        throw new InvalidOperationException("隔离更新验收禁止原生操作：" + operation);
    }

    internal void ObserveDialog(MessageDialog dialog)
    {
        dialog.ShowActivated = false;
        dialog.ContentRendered += (_, _) =>
        {
            if (dialog.Tag is "acceptance-responded") return;
            dialog.Tag = "acceptance-responded";
            Interlocked.Increment(ref _dialogs);
            var allowed = Role == "origin" && !IsWarmup && dialog.Title is "发现新版本" or "更新验证失败" or "更新失败";
            Record("dialog-rendered", new { dialog.Title, allowed, contentRendered = true });
            dialog.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                var panel = (StackPanel)dialog.FindName("ButtonPanel");
                var button = panel.Children.OfType<Button>().FirstOrDefault(item => (MessageBoxResult)item.Tag == MessageBoxResult.OK)
                    ?? panel.Children.OfType<Button>().FirstOrDefault(item => item.IsCancel);
                if (!allowed || button is null)
                {
                    Record("unexpected-dialog", dialog.Title);
                    dialog.Close();
                    Application.Current.Shutdown(75);
                    return;
                }
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }));
        };
    }

    internal void RecordMainRendered(bool settingsLoaded, bool readOnly, bool enabled)
    {
        Record("wpf-main-rendered", new { settingsLoaded, readOnly, enabled });
        MainRendered.TrySetResult();
    }

    internal void ObserveReleaseNotes(ReleaseNotesWindow dialog)
    {
        dialog.ShowActivated = false;
        var responded = false;
        dialog.ContentRendered += (_, _) =>
        {
            if (responded) return;
            responded = true;
            var count = Interlocked.Increment(ref _releaseNotesDialogs);
            var notes = dialog.FindName("NotesText") as TextBox;
            var versionLabel = dialog.FindName("VersionText") as TextBlock;
            var button = dialog.FindName("AcknowledgeButton") as Button;
            bool committed;
            lock (_gate) committed = _healthCommitted;
            var displayedVersion = versionLabel?.Text ?? "";
            var text = notes?.Text ?? "";
            var allowed = Role == "candidate" && Stage == "upgrade" && _settings.Scenario == "success" &&
                committed && count == 1 && dialog.Title == "更新完成" && dialog.RenderedOnce &&
                notes is { IsReadOnly: true, IsVisible: true } && notes.ActualWidth > 0 && notes.ActualHeight > 0 &&
                !string.IsNullOrWhiteSpace(text) && displayedVersion == $"已更新至 v{_settings.CandidateVersion}" &&
                button is { IsVisible: true } && button.Content as string == "知道了";
            var evidence = new
            {
                version = NetworkService.CurrentVersion.ToString(3), displayedVersion, text,
                textSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))),
                dialog.Title, allowed, contentRendered = dialog.RenderedOnce, healthCommitted = committed,
            };
            lock (_gate) _releaseNotesEvidence.Add(evidence);
            Record("release-notes-rendered", evidence);
            dialog.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (!allowed || button is null)
                {
                    Record("unexpected-release-notes", evidence);
                    dialog.Close();
                    Application.Current.Shutdown(76);
                    return;
                }
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Record("release-notes-acknowledged");
            }));
        };
    }

    internal void BeforeExitPreparation()
    {
        if (Role != "origin" || IsWarmup || _settings.Scenario != "exit-save-failure") return;
        _blockedSettings = new FileStream(Path.Combine(InstallationDirectory, "data", "settings.json"),
            FileMode.Open, FileAccess.Read, FileShare.None);
        Record("controlled-exit-save-lock", "settings.json held without sharing after helper ready");
    }

    internal void Record(string name, object? details = null)
    {
        lock (_gate)
        {
            if (name == "health-committed" && details is not null)
            {
                var values = JsonSerializer.SerializeToElement(details);
                _healthCommitted = values.TryGetProperty("committed", out var committed) && committed.ValueKind == JsonValueKind.True &&
                    values.TryGetProperty("readonlyReleased", out var released) && released.ValueKind == JsonValueKind.True;
            }
            _events.Add(new { name, details, elapsedUtc = DateTimeOffset.UtcNow });
        }
    }

    internal void WriteExitReport(int exitCode, bool settingsLoaded, bool healthConfirmed)
    {
        _blockedSettings?.Dispose();
        if (_reported) return;
        _reported = true;
        using var process = Process.GetCurrentProcess();
        object[] events;
        object[] releaseNotesEvidence;
        lock (_gate) { events = _events.ToArray(); releaseNotesEvidence = _releaseNotesEvidence.ToArray(); }
        var report = new
        {
            owner = Owner, role = Role, stage = Stage, scenario = _settings.Scenario,
            processId = process.Id, startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
            executable = Environment.ProcessPath, version = NetworkService.CurrentVersion.ToString(3),
            exitCode, settingsLoaded, healthConfirmed, deniedNativeCalls = _deniedNativeCalls,
            versionRequests = _versionRequests, packageRequests = _packageRequests,
            unexpectedRequests = _unexpectedRequests, dialogs = _dialogs, events,
            releaseNotesDialogs = _releaseNotesDialogs, releaseNotesEvidence,
            scope = "real production WPF App/Host/update flow; local HTTP adapters; native input/hotkeys disabled; no production network",
        };
        File.WriteAllText(Path.Combine(CaseDirectory, $"process-{Stage}-{Role}-{process.Id}.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private sealed class LocalHandler(UpdateAcceptanceContext context, bool packageOnly) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsoluteUri == "https://open.cancanneed.top/version.json" && !packageOnly)
            {
                Interlocked.Increment(ref context._versionRequests);
                await context.MainRendered.Task.WaitAsync(TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
                var text = context.IsWarmup || context.IsRestart
                    ? JsonSerializer.Serialize(new { latest = context.IsRestart ? context._settings.CandidateVersion : context._settings.OriginalVersion,
                        url = UpdateManifest.OfficialManualUrl })
                    : await File.ReadAllTextAsync(Path.Combine(context.CaseDirectory, "release", "version.json"), token).ConfigureAwait(false);
                context.Record("version-response");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
            }
            var expected = $"https://open.cancanneed.top/download/releases/{context._settings.CandidateVersion}/shike-win-x64.zip";
            if (packageOnly && request.Method == HttpMethod.Get && request.RequestUri?.AbsoluteUri == expected && context.Stage == "upgrade")
            {
                Interlocked.Increment(ref context._packageRequests);
                context.Record("package-response");
                var path = Path.Combine(context.CaseDirectory, "release", "shike-win-x64.zip");
                UpdateSession.EnsureNoReparsePoints(path);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)),
                };
            }
            Interlocked.Increment(ref context._unexpectedRequests);
            context.Record("unexpected-network-request", request.RequestUri?.AbsoluteUri);
            throw new InvalidOperationException("隔离更新验收禁止非本案 HTTP 请求");
        }
    }

    private sealed class CaseSettings
    {
        public string Owner { get; set; } = "";
        public string Nonce { get; set; } = "";
        public DateTimeOffset ExpiresUtc { get; set; }
        public string Scenario { get; set; } = "";
        public string OriginalVersion { get; set; } = "";
        public string CandidateVersion { get; set; } = "";
        public string OriginalSha256 { get; set; } = "";
        public string CandidateSha256 { get; set; } = "";
        public string ManifestSha256 { get; set; } = "";
    }
}
