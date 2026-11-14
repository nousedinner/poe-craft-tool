using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using ShiKe.Services;

internal static class NetworkTimeoutChecks
{
    public static void VersionCheckAllowsSlowHeaders()
    {
        using var service = new NetworkService(new StubHttpMessageHandler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(6), token);
            return JsonReply("{\"latest\":\"99.0\",\"url\":\"https://example.com/new\"}");
        }), _ => { });

        var timer = Stopwatch.StartNew();
        var result = service.CheckVersionAsync().GetAwaiter().GetResult();
        timer.Stop();

        Require(result is not null, "超过旧 5 秒但未超过 10 秒的响应应能完成版本检查");
        Require(timer.Elapsed >= TimeSpan.FromSeconds(5) && timer.Elapsed < TimeSpan.FromSeconds(10),
            $"慢响应测试耗时不在预期范围：{timer.Elapsed}");
    }

    public static void VersionCheckTimeoutCoversBodyRead()
    {
        var logs = new List<string>();
        using var service = new NetworkService(new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NeverCompletingReadStream()) })), logs.Add);

        var timer = Stopwatch.StartNew();
        var result = service.CheckVersionAsync().GetAwaiter().GetResult();
        timer.Stop();

        Require(result is null, "响应体持续不返回时应跳过本次版本检查");
        Require(timer.Elapsed >= TimeSpan.FromSeconds(9) && timer.Elapsed < TimeSpan.FromSeconds(16),
            $"响应体应受 10 秒总期限约束，实际耗时：{timer.Elapsed}");
        Require(logs.Any(line => line.Contains("请求超过 10 秒", StringComparison.Ordinal)),
            "版本检查超时日志应报告 10 秒期限");
    }

    public static void PingAndAdsKeepFiveSecondTimeout()
    {
        var pingLogs = new List<string>();
        using (var service = new NetworkService(new StubHttpMessageHandler(async (_, token) =>
        {
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }), pingLogs.Add))
        {
            var timer = Stopwatch.StartNew();
            service.SendPingAsync().GetAwaiter().GetResult();
            timer.Stop();
            RequireFiveSecondTimeout(timer.Elapsed, pingLogs, "启动统计");
        }

        var adsLogs = new List<string>();
        using (var service = new NetworkService(new StubHttpMessageHandler(async (_, token) =>
        {
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
            return JsonReply("{\"ads\":[]}");
        }), adsLogs.Add))
        {
            var timer = Stopwatch.StartNew();
            var ads = service.FetchAdsAsync().GetAwaiter().GetResult();
            timer.Stop();
            Require(ads.Count == 0, "超时广告请求应返回空列表");
            RequireFiveSecondTimeout(timer.Elapsed, adsLogs, "广告获取");
        }
    }

    private static void RequireFiveSecondTimeout(TimeSpan elapsed, IReadOnlyCollection<string> logs, string operation)
    {
        Require(elapsed >= TimeSpan.FromSeconds(4) && elapsed < TimeSpan.FromSeconds(9),
            $"{operation} 应保留 5 秒超时，实际耗时：{elapsed}");
        Require(logs.Any(line => line.Contains(operation, StringComparison.Ordinal) &&
                                 line.Contains("请求超过 5 秒", StringComparison.Ordinal)),
            $"{operation} 超时应保留 5 秒诊断");
    }

    private static HttpResponseMessage JsonReply(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class NeverCompletingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
