using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace ShiKe.Themes;

/// <summary>首次显示直接呈现已保存状态，后续切换使用原生 WPF 缓动；不订阅帧或加载事件。</summary>
public sealed class SwitchVisualStateManager : VisualStateManager
{
    protected override bool GoToStateCore(FrameworkElement control, FrameworkElement stateGroupsRoot,
        string stateName, VisualStateGroup group, VisualState state, bool useTransitions)
    {
        if (group is null || state is null) return false;
        if (control is not ToggleButton toggle || group.Name != "CheckStates" || group.CurrentState == state)
            return base.GoToStateCore(control, stateGroupsRoot, stateName, group, state, useTransitions);
        var animate = useTransitions && group.CurrentState is not null && toggle.IsLoaded && toggle.IsVisible;
        var thumb = toggle.Template.FindName("Thumb", toggle) as Ellipse;
        var track = toggle.Template.FindName("Track", toggle) as Border;
        var from = (thumb?.RenderTransform as TranslateTransform)?.X ?? 0d;
        var fromColor = (track?.Background as SolidColorBrush)?.Color ?? Color.FromRgb(0xB7, 0xC4, 0xCE);
        // 先同步逻辑状态，再明确设置当前绘制对象，避免条件展开时动画时钟晚一帧。
        var result = base.GoToStateCore(control, stateGroupsRoot, stateName, group, state, false);
        if (thumb?.RenderTransform is TranslateTransform transform && track is not null)
        {
            var enabled = stateName == "Checked";
            var target = enabled ? 19d : 0d;
            var targetColor = enabled ? Color.FromRgb(0x49, 0x90, 0xB1) : Color.FromRgb(0xB7, 0xC4, 0xCE);
            if (transform.IsFrozen) { transform = transform.Clone(); thumb.RenderTransform = transform; }
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.SetCurrentValue(TranslateTransform.XProperty, animate ? from : target);
            var brush = new SolidColorBrush(animate ? fromColor : targetColor);
            track.SetCurrentValue(Border.BackgroundProperty, brush);
            if (animate)
            {
                var transition = group.Transitions.Count > 0 ? group.Transitions[0] as VisualTransition : null;
                var duration = transition?.GeneratedDuration ?? new Duration(TimeSpan.FromMilliseconds(170));
                transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
                    { From = from, To = target, Duration = duration, EasingFunction = transition?.GeneratedEasingFunction });
                brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
                    { From = fromColor, To = targetColor, Duration = duration });
            }
        }
        return result;
    }
}
