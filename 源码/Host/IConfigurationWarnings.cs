namespace ShiKe.Host;

/// <summary>配置读取时发生的有界修正；宿主在正常就绪且实际告知后确认，不在页面加载时弹窗。</summary>
public sealed record ConfigurationWarning(string Id, string Message);

public interface IConfigurationWarnings
{
    IReadOnlyList<ConfigurationWarning> GetConfigurationWarnings();
    void AcknowledgeConfigurationWarning(string id);
}
