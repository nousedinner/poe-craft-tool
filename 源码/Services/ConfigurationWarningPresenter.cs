using ShiKe.Host;
using System.Windows;

namespace ShiKe.Services;

/// <summary>展示成功后才确认配置修正；显示失败保留原始持久化值供下次告知。</summary>
internal static class ConfigurationWarningPresenter
{
    internal static void Show(Window owner, IEnumerable<IConfigurationWarnings> providers, Func<bool> canDisplay)
        => Show(providers, message =>
        {
            if (!canDisplay()) return false;
            var rendered = false;
            var dialog = new MessageDialog(message, "配置已调整", MessageBoxButton.OK,
                MessageBoxImage.Warning, MessageBoxResult.OK, topmost: false)
            {
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            dialog.ContentRendered += (_, _) => rendered = true;
            dialog.ShowDialog();
            return rendered;
        });

    internal static void Show(IEnumerable<IConfigurationWarnings> providers, Func<string, bool> show)
    {
        foreach (var provider in providers)
        {
            foreach (var warning in provider.GetConfigurationWarnings())
            {
                try
                {
                    if (show(warning.Message)) provider.AcknowledgeConfigurationWarning(warning.Id);
                }
                catch (Exception error) { Diag.Log($"[配置提示] 显示失败，保留待确认修正：{error.Message}"); }
            }
        }
    }
}
