using System.Reflection;

namespace ShiKe.Services;

// 不创建HttpClient、不调用任何服务器。更新生产源码只需本次fixture的程序集版本。
public static class NetworkService
{
    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version!;
}
