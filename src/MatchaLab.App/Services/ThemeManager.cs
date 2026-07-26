using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;

namespace MatchaLab.App.Services;

public sealed record Palette(
    string Id, string Name,
    string Bg, string Card, string Accent, string Cream, string Taro, string GlowHi, string GlowLo);

public static class ThemeManager
{
    public static readonly Palette[] All =
    {
        new("matcha",  "matcha latte",       "#285A71", "#204C5F", "#CFDA5A", "#FCE4C0", "#2B6E6E", "#E2EC7E", "#B8C541"),
        new("taro",    "taro matcha latte",  "#412B42", "#291E24", "#86A88E", "#F2E7EC", "#754A70", "#A6C4AD", "#6E9077"),
        new("pumpkin", "тыквенный латте",     "#141518", "#373A3E", "#FF8A00", "#FFF2DF", "#26282C", "#FFB04D", "#E07600"),
    };

    public static Palette Current { get; private set; } = All[1];

    private static readonly Dictionary<string, SolidColorBrush> Brushes = new();
    private static RadialGradientBrush? _glowSoft;
    private static RadialGradientBrush? _coreGlow;
    private static int _animGen;

    public static void Apply(string id, bool animate = false)
    {
        var p = All.FirstOrDefault(x => x.Id == id) ?? All[1];
        Current = p;

        var r = Application.Current!.Resources;
        var accent = Color.Parse(p.Accent);

        var targets = new Dictionary<string, Color>
        {
            ["Bg"] = Color.Parse(p.Bg),
            ["Card"] = Color.Parse(p.Card),
            ["Accent"] = accent,
            ["Cream"] = Color.Parse(p.Cream),
            ["Taro"] = Color.Parse(p.Taro),
            ["Ink"] = Color.Parse(p.Bg),
            ["GlowHi"] = Color.Parse(p.GlowHi),
            ["GlowLo"] = Color.Parse(p.GlowLo),
        };

        foreach (var key in targets.Keys)
            if (!Brushes.ContainsKey(key))
            {
                var b = new SolidColorBrush(targets[key]);
                Brushes[key] = b;
                r[key] = b;
            }

        static Color A(Color c, byte a) => new(a, c.R, c.G, c.B);
        static string Hex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        if (_glowSoft is null)
        {
            _glowSoft = new RadialGradientBrush
            {
                GradientStops = { new GradientStop(A(accent, 0x26), 0), new GradientStop(A(accent, 0x00), 1) }
            };
            r["GlowSoftBrush"] = _glowSoft;
        }
        if (_coreGlow is null)
        {
            _coreGlow = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(A(accent, 0x8C), 0),
                    new GradientStop(A(accent, 0x30), 0.55),
                    new GradientStop(A(accent, 0x00), 1),
                }
            };
            r["CoreGlowBrush"] = _coreGlow;
        }

        r["CoreGlowOn"] = BoxShadows.Parse($"0 0 60 8 {Hex(A(accent, 0x70))}");
        r["CoreGlowOff"] = BoxShadows.Parse("0 0 0 0 #00000000");
        r["MenuGlow"] = BoxShadows.Parse($"0 6 18 -6 {Hex(A(accent, 0x50))}");

        var glowTargets = new[] { A(accent, 0x26), A(accent, 0x00), A(accent, 0x8C), A(accent, 0x30), A(accent, 0x00) };

        var gen = ++_animGen;
        var host = (Application.Current.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

        if (!animate || host is null)
        {
            foreach (var (key, c) in targets) Brushes[key].Color = c;
            SetGlowStops(glowTargets);
            return;
        }

        var fromBrush = targets.Keys.ToDictionary(k => k, k => Brushes[k].Color);
        var fromGlow = CurrentGlowStops();
        var started = DateTime.UtcNow;
        const double durMs = 350;

        void Frame(TimeSpan _)
        {
            if (gen != _animGen) return;
            var t = Math.Min(1, (DateTime.UtcNow - started).TotalMilliseconds / durMs);
            var e = t * t * (3 - 2 * t);

            foreach (var (key, target) in targets)
                Brushes[key].Color = Lerp(fromBrush[key], target, e);
            SetGlowStops(Lerp(fromGlow, glowTargets, e));

            if (t < 1) host.RequestAnimationFrame(Frame);
        }
        host.RequestAnimationFrame(Frame);
    }

    private static Color[] CurrentGlowStops() => new[]
    {
        _glowSoft!.GradientStops[0].Color, _glowSoft.GradientStops[1].Color,
        _coreGlow!.GradientStops[0].Color, _coreGlow.GradientStops[1].Color, _coreGlow.GradientStops[2].Color,
    };

    private static void SetGlowStops(Color[] c)
    {
        _glowSoft!.GradientStops[0].Color = c[0];
        _glowSoft.GradientStops[1].Color = c[1];
        _coreGlow!.GradientStops[0].Color = c[2];
        _coreGlow.GradientStops[1].Color = c[3];
        _coreGlow.GradientStops[2].Color = c[4];
    }

    private static Color[] Lerp(Color[] a, Color[] b, double t)
    {
        var res = new Color[a.Length];
        for (var i = 0; i < a.Length; i++) res[i] = Lerp(a[i], b[i], t);
        return res;
    }

    private static Color Lerp(Color a, Color b, double t) => new(
        (byte)(a.A + (b.A - a.A) * t),
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));
}
