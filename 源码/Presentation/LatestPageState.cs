using System.Windows;
using System.Windows.Threading;

namespace ShiKe.Presentation;

/// <summary>后台只保存最新值；可见期间至多保留一个待处理回调，旧可见轮次的回调自动失效。</summary>
internal sealed class LatestStateDispatcher<T>
{
    private readonly object _gate = new();
    private readonly Func<Action, bool> _post;
    private readonly Action<T> _render;
    private T _latest = default!;
    private bool _hasLatest;
    private bool _active;
    private bool _queued;
    private bool _disposed;
    private long _generation;
    private long _version;
    private long _renderedVersion = -1;
    private int _postedCount;
    private int _renderedCount;

    internal LatestStateDispatcher(Func<Action, bool> post, Action<T> render)
    {
        _post = post;
        _render = render;
    }

    internal int PostedCount { get { lock (_gate) return _postedCount; } }
    internal int RenderedCount { get { lock (_gate) return _renderedCount; } }

    internal void Publish(T state)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _latest = state;
            _hasLatest = true;
            _version++;
            if (!_active || _queued) return;
            _queued = true;
            var generation = _generation;
            // post只允许异步入队，不等待UI；与切换可见性共用锁，避免隐藏后补投递旧状态。
            if (_post(() => Drain(generation, releaseQueueSlot: true))) _postedCount++;
            else _queued = false;
        }
    }

    // 只由UI线程调用。重新显示时同步恢复最新状态，不等待旧队列。
    internal void SetActive(bool active, Action? beforeActivationReplay = null)
    {
        long generation;
        lock (_gate)
        {
            if (_disposed || _active == active) return;
            _active = active;
            generation = ++_generation;
            _queued = false;
            if (!active) return;
            _renderedVersion = -1;
        }
        // 内部交错探针让验证能确定性覆盖“激活后、同步恢复前”的后台发布。
        beforeActivationReplay?.Invoke();
        // 同步恢复不拥有异步队列槽；并发Publish已经排队时不能清掉它的占位。
        Drain(generation, releaseQueueSlot: false);
    }

    private void Drain(long generation, bool releaseQueueSlot)
    {
        T state;
        lock (_gate)
        {
            if (_disposed || !_active || _generation != generation) return;
            if (releaseQueueSlot) _queued = false;
            if (!_hasLatest || _renderedVersion == _version) return;
            state = _latest;
            _renderedVersion = _version;
        }
        _render(state);
        lock (_gate) _renderedCount++;
    }

    internal void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _active = false;
            _queued = false;
            _generation++;
            _latest = default!;
        }
    }
}

/// <summary>把WPF可见性转换为纯布尔状态；后台Publish不会读取任何控件属性。</summary>
internal sealed class LatestPageState<T> : IDisposable
{
    private readonly FrameworkElement _page;
    private readonly Dispatcher _dispatcher;
    private readonly LatestStateDispatcher<T> _states;
    private Window? _window;
    private bool _disposed;

    internal LatestPageState(FrameworkElement page, Action<T> render)
    {
        _page = page;
        _dispatcher = page.Dispatcher;
        _states = new LatestStateDispatcher<T>(Post, render);
        page.Loaded += OnLoaded;
        page.Unloaded += OnUnloaded;
        page.IsVisibleChanged += OnVisibilityChanged;
        if (page.IsLoaded) AttachWindow();
        UpdateVisibility();
    }

    internal event Action? Disposed;
    internal int PostedCount => _states.PostedCount;
    internal int RenderedCount => _states.RenderedCount;
    internal void Publish(T state) => _states.Publish(state);

    private bool Post(Action callback)
    {
        try
        {
            _dispatcher.BeginInvoke(callback);
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        AttachWindow();
        UpdateVisibility();
    }

    private void AttachWindow()
    {
        var window = Window.GetWindow(_page);
        if (ReferenceEquals(window, _window)) return;
        DetachWindow();
        _window = window;
        if (_window is null) return;
        _window.StateChanged += OnWindowStateChanged;
        _window.IsVisibleChanged += OnVisibilityChanged;
        _window.Closed += OnWindowClosed;
    }

    private void DetachWindow()
    {
        if (_window is null) return;
        _window.StateChanged -= OnWindowStateChanged;
        _window.IsVisibleChanged -= OnVisibilityChanged;
        _window.Closed -= OnWindowClosed;
        _window = null;
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) => _states.SetActive(false);
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => UpdateVisibility();
    private void OnWindowStateChanged(object? sender, EventArgs args) => UpdateVisibility();
    private void OnWindowClosed(object? sender, EventArgs args) => Dispose();

    private void UpdateVisibility()
    {
        if (_disposed) return;
        _states.SetActive(_page.IsLoaded && _page.IsVisible && _window is { IsVisible: true } &&
                          _window.WindowState != WindowState.Minimized);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _states.Dispose();
        _page.Loaded -= OnLoaded;
        _page.Unloaded -= OnUnloaded;
        _page.IsVisibleChanged -= OnVisibilityChanged;
        DetachWindow();
        Disposed?.Invoke();
        Disposed = null;
    }
}
