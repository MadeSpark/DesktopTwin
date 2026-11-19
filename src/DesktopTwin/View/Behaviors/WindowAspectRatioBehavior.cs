using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopTwin.Interop;
using Microsoft.Xaml.Behaviors;

namespace DesktopTwin.View.Behaviors;

/// <summary>
/// 把窗口内容区锁定为固定宽高比。
///
/// 两条来源分开处理：
/// - 用户拖窗口边缘走 WM_SIZING，实时约束，比在 SizeChanged 里回写更平滑；
/// - 非拖拽的尺寸变化（改分辨率、Win+方向键、任务栏并排/层叠）不经过 WM_SIZING，
///   由 SizeChanged 按宽度反推高度补上。
/// </summary>
public sealed class WindowAspectRatioBehavior : Behavior<FrameworkElement>
{
    private const int WmSizing = 0x0214;
    private const int WmszLeft = 1;
    private const int WmszRight = 2;
    private const int WmszTop = 3;
    private const int WmszTopLeft = 4;
    private const int WmszTopRight = 5;
    private const int WmszBottom = 6;
    private const int WmszBottomLeft = 7;
    private const int WmszBottomRight = 8;

    /// <summary>
    /// 是否启用宽高比约束。
    /// </summary>
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.Register(
            nameof(IsEnabled),
            typeof(bool),
            typeof(WindowAspectRatioBehavior),
            new PropertyMetadata(false));

    /// <summary>
    /// 目标宽高比，默认 16:9。
    /// </summary>
    public static readonly DependencyProperty AspectRatioProperty =
        DependencyProperty.Register(
            nameof(AspectRatio),
            typeof(double),
            typeof(WindowAspectRatioBehavior),
            new PropertyMetadata(16d / 9d, OnAspectRatioChanged));

    private Window? _window;
    private HwndSource? _source;

    /// <summary>程序自己改尺寸时置位，避免 SizeChanged 再次触发校正形成回环。</summary>
    private bool _correctingSize;

    /// <summary>
    /// 是否启用宽高比约束。
    /// </summary>
    public bool IsEnabled
    {
        get => (bool)GetValue(IsEnabledProperty);
        set => SetValue(IsEnabledProperty, value);
    }

    /// <summary>
    /// 目标宽高比。
    /// </summary>
    public double AspectRatio
    {
        get => (double)GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    /// <summary>
    /// 比例本身变化（例如用户把分身分辨率从 16:9 改成 16:10）时也要重新校正一次，
    /// 否则只有下次尺寸变化才会生效。
    /// </summary>
    private static void OnAspectRatioChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WindowAspectRatioBehavior behavior)
        {
            behavior.ConstrainHeightToRatio();
        }
    }

    /// <inheritdoc />
    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject.Loaded += OnAssociatedObjectLoaded;
        AssociatedObject.Unloaded += OnAssociatedObjectUnloaded;

        if (AssociatedObject.IsLoaded)
        {
            AttachWindowHook();
        }
    }

    /// <inheritdoc />
    protected override void OnDetaching()
    {
        AssociatedObject.Loaded -= OnAssociatedObjectLoaded;
        AssociatedObject.Unloaded -= OnAssociatedObjectUnloaded;
        DetachWindowHook();
        base.OnDetaching();
    }

    private void OnAssociatedObjectLoaded(object sender, RoutedEventArgs e)
    {
        AttachWindowHook();
    }

    private void OnAssociatedObjectUnloaded(object sender, RoutedEventArgs e)
    {
        DetachWindowHook();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        DetachWindowHook();
    }

    private void AttachWindowHook()
    {
        if (_source is not null)
        {
            return;
        }

        _window = Window.GetWindow(AssociatedObject);
        _source = _window is null
            ? null
            : PresentationSource.FromVisual(_window) as HwndSource;
        if (_window is null || _source is null)
        {
            return;
        }

        _window.Closed += OnWindowClosed;
        _window.SizeChanged += OnWindowSizeChanged;
        _source.AddHook(WindowProc);
    }

    private void DetachWindowHook()
    {
        _source?.RemoveHook(WindowProc);
        _source = null;

        if (_window is not null)
        {
            _window.Closed -= OnWindowClosed;
            _window.SizeChanged -= OnWindowSizeChanged;
            _window = null;
        }
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 用户拖拽由 WM_SIZING 直接约束，这里只管「非拖拽」的尺寸变化：
        // 改分辨率、Win+方向键贴边、任务栏的并排/层叠，这些都不经过 WM_SIZING。
        if (!IsEnabled || _window is null || _correctingSize)
        {
            return;
        }

        // 最大化、全屏时尺寸由系统或全屏逻辑接管；全屏还会把 min==max 锁死，不能碰。
        if (_window.WindowState != WindowState.Normal || _window.ResizeMode == ResizeMode.NoResize)
        {
            return;
        }

        if (_window.MinHeight == _window.MaxHeight && _window.MinHeight > 0)
        {
            return;
        }

        if (!double.IsFinite(AspectRatio) || AspectRatio <= 0)
        {
            return;
        }

        // 用宽度反推高度。放到布局之后再改：SizeChanged 里直接改尺寸会被同一次布局覆盖。
        AssociatedObject.Dispatcher.BeginInvoke(
            ConstrainHeightToRatio,
            DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 按当前宽度把窗口高度校正到 <see cref="AspectRatio"/>。
    ///
    /// 宽度是基准：用户拖的、系统贴边给的宽度都保留，只调整高度。
    /// 但如果算出来的高度超出工作区，就反过来按高度定宽度，否则窗口会顶出屏幕。
    /// </summary>
    private void ConstrainHeightToRatio()
    {
        if (!IsEnabled || _window is null || _correctingSize)
        {
            return;
        }

        if (_window.WindowState != WindowState.Normal)
        {
            return;
        }

        if (!double.IsFinite(AspectRatio) || AspectRatio <= 0)
        {
            return;
        }

        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero || !TryGetSizingMetrics(handle, out var metrics))
        {
            return;
        }

        var transform = _source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var scaleX = transform.M11 > 0 ? transform.M11 : 1;
        var scaleY = transform.M22 > 0 ? transform.M22 : 1;

        // 按内容区算：窗口外框含标题栏与边框，直接拿外框尺寸推比例会把内容算歪。
        var frameWidth = metrics.FrameWidth / scaleX;
        var frameHeight = metrics.FrameHeight / scaleY;
        var contentWidth = _window.ActualWidth - frameWidth;
        if (contentWidth <= 0)
        {
            return;
        }

        // 用窗口所在显示器的工作区：SystemParameters.WorkArea 只给主显示器的。
        var screen = System.Windows.Forms.Screen.FromHandle(handle);

        var contentHeight = contentWidth / AspectRatio;
        var targetWidth = _window.ActualWidth;
        var targetHeight = contentHeight + frameHeight;
        var minHeight = Math.Max(0, _window.MinHeight);
        var minWidth = Math.Max(0, _window.MinWidth);

        // 下限：算出来的高度比 MinHeight 还小时，WPF 会把 Height 夹上去，
        // 那样内容区比例就歪了。改为按高度反推宽度，保持比例。
        if (targetHeight < minHeight)
        {
            targetHeight = minHeight;
            contentHeight = Math.Max(1, targetHeight - frameHeight);
            targetWidth = Math.Max(minWidth, contentHeight * AspectRatio + frameWidth);
        }

        // 上限：窗口位置是固定的，高度只能长到「底部贴住工作区底部」为止，
        // 否则多出来的部分会跑到屏幕外，画面被裁掉一截。
        var workAreaBottom = screen.WorkingArea.Bottom / scaleY;
        var maxHeightByPosition = workAreaBottom - _window.Top;
        if (maxHeightByPosition > 0 && targetHeight > maxHeightByPosition)
        {
            targetHeight = Math.Max(minHeight, maxHeightByPosition);
            contentHeight = Math.Max(1, targetHeight - frameHeight);
            targetWidth = Math.Max(minWidth, contentHeight * AspectRatio + frameWidth);
        }

        if (Math.Abs(targetHeight - _window.ActualHeight) < 1
            && Math.Abs(targetWidth - _window.ActualWidth) < 1)
        {
            return;
        }

        _correctingSize = true;
        try
        {
            _window.Width = targetWidth;
            _window.Height = targetHeight;
        }
        finally
        {
            _correctingSize = false;
        }
    }

    private nint WindowProc(
        nint hWnd,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message != WmSizing ||
            !IsEnabled ||
            _window?.WindowState != WindowState.Normal ||
            lParam == 0 ||
            !double.IsFinite(AspectRatio) ||
            AspectRatio <= 0 ||
            !TryGetSizingMetrics(hWnd, out var metrics))
        {
            return 0;
        }

        var sizingEdge = unchecked((int)wParam.ToInt64());
        var rect = Marshal.PtrToStructure<WindowNativeMethods.NativeRect>(lParam);
        if (!ConstrainRect(ref rect, sizingEdge, metrics))
        {
            return 0;
        }

        Marshal.StructureToPtr(rect, lParam, false);
        handled = true;
        return 1;
    }

    private bool TryGetSizingMetrics(nint hWnd, out SizingMetrics metrics)
    {
        metrics = default;
        if (_window is null ||
            _source?.CompositionTarget is null ||
            AssociatedObject.ActualWidth <= 0 ||
            AssociatedObject.ActualHeight <= 0 ||
            !WindowNativeMethods.TryGetWindowRect(hWnd, out var windowRect))
        {
            return false;
        }

        var contentTopLeft = AssociatedObject.PointToScreen(new Point(0, 0));
        var contentBottomRight = AssociatedObject.PointToScreen(
            new Point(AssociatedObject.ActualWidth, AssociatedObject.ActualHeight));
        var contentWidth = Math.Abs(contentBottomRight.X - contentTopLeft.X);
        var contentHeight = Math.Abs(contentBottomRight.Y - contentTopLeft.Y);
        if (contentWidth <= 0 || contentHeight <= 0)
        {
            return false;
        }

        var frameWidth = Math.Max(0, windowRect.Right - windowRect.Left - contentWidth);
        var frameHeight = Math.Max(0, windowRect.Bottom - windowRect.Top - contentHeight);
        var transformToDevice = _source.CompositionTarget.TransformToDevice;
        var minimumOuterWidth = _window.MinWidth * transformToDevice.M11;
        var minimumOuterHeight = _window.MinHeight * transformToDevice.M22;
        var minimumContentWidth = Math.Max(
            1,
            Math.Max(
                minimumOuterWidth - frameWidth,
                (minimumOuterHeight - frameHeight) * AspectRatio));

        metrics = new SizingMetrics(
            frameWidth,
            frameHeight,
            minimumContentWidth,
            minimumContentWidth / AspectRatio);
        return true;
    }

    private bool ConstrainRect(
        ref WindowNativeMethods.NativeRect rect,
        int sizingEdge,
        SizingMetrics metrics)
    {
        if (sizingEdge is < WmszLeft or > WmszBottomRight)
        {
            return false;
        }

        var requestedWidth = Math.Max(1, rect.Right - rect.Left);
        var requestedHeight = Math.Max(1, rect.Bottom - rect.Top);
        var widthDrivenSize = CalculateWidthDrivenSize(requestedWidth, metrics);
        var heightDrivenSize = CalculateHeightDrivenSize(requestedHeight, metrics);
        var useWidthAsDriver = sizingEdge is WmszLeft or WmszRight ||
                               sizingEdge is WmszTopLeft or WmszTopRight or WmszBottomLeft or WmszBottomRight &&
                               Math.Abs(widthDrivenSize.Height - requestedHeight) <=
                               Math.Abs(heightDrivenSize.Width - requestedWidth) / AspectRatio;
        var targetSize = useWidthAsDriver ? widthDrivenSize : heightDrivenSize;
        var targetWidth = Math.Max(1, (int)Math.Round(targetSize.Width));
        var targetHeight = Math.Max(1, (int)Math.Round(targetSize.Height));

        if (sizingEdge is WmszLeft or WmszTopLeft or WmszBottomLeft)
        {
            rect.Left = rect.Right - targetWidth;
        }
        else
        {
            rect.Right = rect.Left + targetWidth;
        }

        if (sizingEdge is WmszTop or WmszTopLeft or WmszTopRight)
        {
            rect.Top = rect.Bottom - targetHeight;
        }
        else
        {
            rect.Bottom = rect.Top + targetHeight;
        }

        return true;
    }

    private Size CalculateWidthDrivenSize(double requestedWidth, SizingMetrics metrics)
    {
        var contentWidth = Math.Max(
            metrics.MinimumContentWidth,
            requestedWidth - metrics.FrameWidth);
        return new Size(
            contentWidth + metrics.FrameWidth,
            contentWidth / AspectRatio + metrics.FrameHeight);
    }

    private Size CalculateHeightDrivenSize(double requestedHeight, SizingMetrics metrics)
    {
        var contentHeight = Math.Max(
            metrics.MinimumContentHeight,
            requestedHeight - metrics.FrameHeight);
        return new Size(
            contentHeight * AspectRatio + metrics.FrameWidth,
            contentHeight + metrics.FrameHeight);
    }

    private readonly record struct SizingMetrics(
        double FrameWidth,
        double FrameHeight,
        double MinimumContentWidth,
        double MinimumContentHeight);
}
