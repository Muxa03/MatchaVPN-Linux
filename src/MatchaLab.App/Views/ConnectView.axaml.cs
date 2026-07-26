using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using MatchaLab.App.ViewModels;

namespace MatchaLab.App.Views;

public partial class ConnectView : UserControl
{
    private readonly RotateTransform _outerRot = new();
    private readonly RotateTransform _innerRot = new();
    private Window? _win;
    private bool _spinOn;
    private TimeSpan _lastFrame;
    private double _speedK = 1;

    public ConnectView()
    {
        InitializeComponent();
        SmoothScroll.Attach(PageScroll);

        RingOuter.RenderTransform = _outerRot;
        RingInner.RenderTransform = _innerRot;

        AttachSwap(PingNum, from: 0, dy: 8, to: 1);
        AttachSwap(SpeedNum, from: 0, dy: 8, to: 1);
        AttachSwap(TrafficNumText, from: 0, dy: 8, to: 1);
        AttachSwap(CountryText, from: 0, dy: 6, to: 1);
        AttachSwap(TimerText, from: 0.35, dy: 0, to: 0.75);
    }

    private MainWindow? Win => TopLevel.GetTopLevel(this) as MainWindow;

    private void Menu_Click(object? sender, RoutedEventArgs e) => Win?.GoSettings();
    private void Location_Click(object? sender, RoutedEventArgs e) => Win?.GoServers();
    private void Protocol_Click(object? sender, RoutedEventArgs e) => Win?.GoProtocol();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _win = TopLevel.GetTopLevel(this) as Window;
        if (_win is not null) _win.PropertyChanged += OnWindowPropertyChanged;
        PageScroll.Offset = default;
        Dispatcher.UIThread.Post(() => PageScroll.Offset = default, DispatcherPriority.ApplicationIdle);
        UpdateSpin();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_win is not null) _win.PropertyChanged -= OnWindowPropertyChanged;
        _win = null;
        _spinOn = false;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty) UpdateSpin();
    }

    private void UpdateSpin()
    {
        var visible = _win is { WindowState: not WindowState.Minimized };
        if (visible == _spinOn) return;
        _spinOn = visible;
        if (!visible) return;
        _lastFrame = TimeSpan.Zero;
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(SpinFrame);
    }

    private void SpinFrame(TimeSpan now)
    {
        if (!_spinOn) return;
        var dt = _lastFrame == TimeSpan.Zero ? 1.0 / 144 : Math.Clamp((now - _lastFrame).TotalSeconds, 0, 1.0 / 30);
        _lastFrame = now;

        var target = (DataContext as AppViewModel)?.IsBusy == true ? 5.0 : 1.0;
        _speedK += (target - _speedK) * (1 - Math.Exp(-dt / 0.25));
        _outerRot.Angle = (_outerRot.Angle + 11.0 * _speedK * dt) % 360;
        _innerRot.Angle = (_innerRot.Angle - 15.0 * _speedK * dt + 360) % 360;

        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(SpinFrame);
    }

    private void AttachSwap(TextBlock tb, double from, double dy, double to)
    {
        var first = true;
        tb.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBlock.TextProperty) return;
            if (first) { first = false; return; }
            if (!_spinOn) return;

            var trans = tb.Transitions;
            tb.Transitions = null;
            tb.Opacity = from;
            tb.RenderTransform = TransformOperations.Parse($"translateY({dy}px)");
            tb.Transitions = trans;
            Dispatcher.UIThread.Post(() =>
            {
                tb.Opacity = to;
                tb.RenderTransform = TransformOperations.Parse("translateY(0px)");
            }, DispatcherPriority.Render);
        };
    }
}
