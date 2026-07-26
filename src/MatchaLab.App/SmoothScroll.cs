using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MatchaLab.App;

public static class SmoothScroll
{
    private static readonly ConditionalWeakTable<ScrollViewer, Driver> Attached = new();

    public static void Attach(ScrollViewer sv)
    {
        if (!Attached.TryGetValue(sv, out _)) Attached.Add(sv, new Driver(sv));
    }

    private sealed class Driver
    {
        private const double Step = 116;
        private const double Tau = 0.085;

        private readonly ScrollViewer _sv;
        private double _target;
        private bool _running;
        private TimeSpan _last;

        public Driver(ScrollViewer sv)
        {
            _sv = sv;
            sv.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        }

        private void OnWheel(object? sender, PointerWheelEventArgs e)
        {
            if (e.Delta.Y == 0) return;
            var max = Math.Max(0, _sv.Extent.Height - _sv.Viewport.Height);
            if (max <= 0) return;

            if (!_running) _target = _sv.Offset.Y;
            _target = Math.Clamp(_target - e.Delta.Y * Step, 0, max);
            e.Handled = true;

            if (_running) return;
            _running = true;
            _last = TimeSpan.Zero;
            TopLevel.GetTopLevel(_sv)?.RequestAnimationFrame(Frame);
        }

        private void Frame(TimeSpan now)
        {
            if (!_running) return;
            var tl = TopLevel.GetTopLevel(_sv);
            if (tl is null) { _running = false; return; }

            var dt = _last == TimeSpan.Zero ? 1.0 / 144 : Math.Clamp((now - _last).TotalSeconds, 0, 1.0 / 30);
            _last = now;

            var max = Math.Max(0, _sv.Extent.Height - _sv.Viewport.Height);
            var goal = Math.Clamp(_target, 0, max);
            var y = goal + (_sv.Offset.Y - goal) * Math.Exp(-dt / Tau);
            if (Math.Abs(y - goal) < 0.4) { y = goal; _running = false; }
            _sv.Offset = new Vector(_sv.Offset.X, y);

            if (_running) tl.RequestAnimationFrame(Frame);
        }
    }
}
