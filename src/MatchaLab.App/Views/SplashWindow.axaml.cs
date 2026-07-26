using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media;

namespace MatchaLab.App.Views;

public partial class SplashWindow : Window
{
    private const double DurSec = 2.0;

    private readonly RotateTransform _rot = new();
    private readonly Stopwatch _sw = new();
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SplashWindow()
    {
        InitializeComponent();
        SpinRing.RenderTransform = _rot;
        Opened += (_, _) => { _sw.Start(); RequestAnimationFrame(Frame); };
    }

    public Task WaitAsync() => _done.Task;

    private void Frame(TimeSpan _)
    {
        var t = _sw.Elapsed.TotalSeconds;
        var p = Math.Min(1, t / DurSec);

        Root.Opacity = p > 0.9 ? 1 - SmoothStep((p - 0.9) / 0.1)
                     : t < 0.35 ? SmoothStep(t / 0.35)
                     : 1;
        _rot.Angle = t * 120 % 360;
        ProgressFill.Width = 180 * SmoothStep(p);

        if (p >= 1) { _done.TrySetResult(); return; }
        RequestAnimationFrame(Frame);
    }

    private static double SmoothStep(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }
}
