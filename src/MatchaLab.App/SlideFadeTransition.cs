using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;

namespace MatchaLab.App;

public sealed class SlideFadeTransition : IPageTransition
{
    private const double Shift = 26;
    private static readonly TimeSpan Dur = TimeSpan.FromMilliseconds(280);

    public bool Forward { get; set; } = true;

    public async Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        var dir = Forward ? 1d : -1d;
        var fromT = EnsureTranslate(from);
        var toT = EnsureTranslate(to);

        if (to is not null)
        {
            to.Opacity = 0;
            toT!.X = Shift * dir;
            to.IsVisible = true;
        }

        TopLevel? host = null;
        if ((to ?? from) is Control c) host = TopLevel.GetTopLevel(c);

        if (host is not null)
        {
            var sw = Stopwatch.StartNew();
            while (!cancellationToken.IsCancellationRequested)
            {
                var p = Math.Min(1d, sw.Elapsed.TotalMilliseconds / Dur.TotalMilliseconds);
                var eOut = 1 - Math.Pow(1 - p, 3);
                var eIn = p * p * p;

                if (from is not null)
                {
                    from.Opacity = 1 - eIn;
                    fromT!.X = -Shift * dir * eIn;
                }
                if (to is not null)
                {
                    to.Opacity = eOut;
                    toT!.X = Shift * dir * (1 - eOut);
                }

                if (p >= 1) break;
                await NextFrameAsync(host, cancellationToken);
            }
        }

        if (from is not null)
        {
            from.IsVisible = false;
            from.Opacity = 1;
            fromT!.X = 0;
        }
        if (to is not null && !cancellationToken.IsCancellationRequested)
        {
            to.Opacity = 1;
            toT!.X = 0;
        }
    }

    private static TranslateTransform? EnsureTranslate(Visual? v)
    {
        if (v is null) return null;
        if (v.RenderTransform is not TranslateTransform t)
        {
            t = new TranslateTransform();
            v.RenderTransform = t;
        }
        t.X = 0;
        t.Y = 0;
        return t;
    }

    private static async Task NextFrameAsync(TopLevel host, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => tcs.TrySetResult());
        host.RequestAnimationFrame(_ => tcs.TrySetResult());
        await tcs.Task;
    }
}
