using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ShiKe.Themes;

/// <summary>只移动背景画刷；页面离开、窗口失焦/隐藏/最小化时暂停，不干扰工具任务。</summary>
public static class FlowingGradient
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(FlowingGradient),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits, OnEnabledChanged));

    public static readonly DependencyProperty FlowProperty = DependencyProperty.RegisterAttached(
        "Flow", typeof(bool), typeof(FlowingGradient), new PropertyMetadata(false, OnFlowChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(MotionState), typeof(FlowingGradient));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetFlow(DependencyObject element) => (bool)element.GetValue(FlowProperty);
    public static void SetFlow(DependencyObject element, bool value) => element.SetValue(FlowProperty, value);

    private static void OnEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
        => (element.GetValue(StateProperty) as MotionState)?.Update();

    private static void OnFlowChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not Border border) return;
        if ((bool)args.NewValue)
        {
            var state = new MotionState(border);
            border.SetValue(StateProperty, state);
            state.Attach();
        }
        else if (border.GetValue(StateProperty) is MotionState state)
        {
            state.Detach();
            border.ClearValue(StateProperty);
        }
    }

    internal static bool IsAnimating(Border border)
        => border.GetValue(StateProperty) is MotionState { Running: true };

    private sealed class MotionState(Border border)
    {
        private Window? _window;
        private LinearGradientBrush? _brush;
        private Brush? _original;
        private AnimationClock? _start;
        private AnimationClock? _end;
        public bool Running { get; private set; }

        public void Attach()
        {
            border.Loaded += OnLoaded;
            border.Unloaded += OnUnloaded;
            border.IsVisibleChanged += OnVisibilityChanged;
            if (border.IsLoaded) Load();
        }

        public void Detach()
        {
            Unload();
            border.Loaded -= OnLoaded;
            border.Unloaded -= OnUnloaded;
            border.IsVisibleChanged -= OnVisibilityChanged;
            _start?.Controller?.Remove();
            _end?.Controller?.Remove();
            if (_brush is not null)
            {
                _brush.ApplyAnimationClock(LinearGradientBrush.StartPointProperty, null);
                _brush.ApplyAnimationClock(LinearGradientBrush.EndPointProperty, null);
                border.Background = _original;
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs args) => Load();
        private void OnUnloaded(object sender, RoutedEventArgs args) => Unload();
        private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => Update();
        private void OnWindowChanged(object? sender, EventArgs args) => Update();
        private void OnSystemChanged(object? sender, PropertyChangedEventArgs args) => Update();

        private void Load()
        {
            if (_brush is null && border.Background is LinearGradientBrush original)
            {
                _original = original;
                _brush = original.Clone(); // 每块面板拥有独立画刷，不动画共享资源。
                border.Background = _brush;
                _start = MakeClock(_brush.StartPoint, _brush.StartPoint + new Vector(.35, .25));
                _end = MakeClock(_brush.EndPoint, _brush.EndPoint + new Vector(.35, .25));
                _brush.ApplyAnimationClock(LinearGradientBrush.StartPointProperty, _start);
                _brush.ApplyAnimationClock(LinearGradientBrush.EndPointProperty, _end);
            }
            var window = Window.GetWindow(border);
            if (_window != window)
            {
                Unload();
                _window = window;
                if (_window is not null)
                {
                    _window.Activated += OnWindowChanged;
                    _window.Deactivated += OnWindowChanged;
                    _window.StateChanged += OnWindowChanged;
                    SystemParameters.StaticPropertyChanged += OnSystemChanged;
                }
            }
            Update();
        }

        private static AnimationClock MakeClock(Point from, Point to)
        {
            var animation = new PointAnimation(from, to, TimeSpan.FromSeconds(30))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Timeline.SetDesiredFrameRate(animation, 15);
            var clock = (AnimationClock)animation.CreateClock(true);
            clock.Controller?.Pause();
            return clock;
        }

        private void Unload()
        {
            Pause();
            if (_window is null) return;
            _window.Activated -= OnWindowChanged;
            _window.Deactivated -= OnWindowChanged;
            _window.StateChanged -= OnWindowChanged;
            SystemParameters.StaticPropertyChanged -= OnSystemChanged;
            _window = null;
        }

        public void Update()
        {
            var shouldRun = border.IsLoaded && border.IsVisible && GetEnabled(border) &&
                            SystemParameters.ClientAreaAnimation &&
                            _window is { IsActive: true, IsVisible: true, WindowState: not WindowState.Minimized };
            if (shouldRun && _start is not null && !Running)
            {
                _start.Controller?.Resume();
                _end?.Controller?.Resume();
                Running = true;
            }
            else if (!shouldRun) Pause();
        }

        private void Pause()
        {
            _start?.Controller?.Pause();
            _end?.Controller?.Pause();
            Running = false;
        }
    }
}
