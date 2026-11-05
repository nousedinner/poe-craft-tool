namespace ShiKe;

/// <summary>应用身份始终取自桌面入口程序集，不随网络或更新服务所在项目改变。</summary>
internal static class AppIdentity
{
    internal static Version Version { get; } =
        typeof(Bootstrapper).Assembly.GetName().Version ?? new Version(1, 0, 0);
}
