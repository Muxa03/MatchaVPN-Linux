using System.IO;
using System.Runtime.Versioning;
using Avalonia.Threading;

namespace MatchaLab.App.Services;

[SupportedOSPlatform("linux")]
public sealed class LinuxTunnelController : ITunnelController
{
    private const string Iface = LinuxPrivilegedHelper.AwgIface;
    private System.Timers.Timer? _monitor;
    private int _polling;
    private ulong _rx, _tx;
    private bool _userStopped;

    public TunnelStatus Status { get; private set; } = TunnelStatus.Disconnected;
    public ulong RxBytes => _rx;
    public ulong TxBytes => _tx;
    public event Action<TunnelStatus>? StatusChanged;

    private static string ConfDir
    {
        get
        {
            var d = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MatchaLab");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private static string ConfPath => Path.Combine(ConfDir, Iface + ".conf");

    public LinuxTunnelController()
    {
        if (IfaceExists())
        {
            Status = TunnelStatus.Connected;
            StartMonitor();
        }
    }

    public async Task StartAsync(string awgConfig)
    {
        _userStopped = false;
        Set(TunnelStatus.Connecting);
        try
        {
            await Task.Run(() =>
            {
                File.WriteAllText(ConfPath, awgConfig);
                try { File.SetUnixFileMode(ConfPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }

                var (code, err) = LinuxPkexec.Run($"--awg-up \"{ConfPath}\"");
                if (_userStopped) throw new OperationCanceledException();
                if (code == 3)
                    throw new InvalidOperationException(
                        "Сервер не ответил на рукопожатие. Проверьте интернет или смените сервер; " +
                        "если параллельно работает другой VPN — отключите его.");
                if (code != 0)
                    throw new InvalidOperationException(TranslateUpError(code, err));
            });
        }
        catch (OperationCanceledException)
        {
            await Task.Run(() => LinuxPkexec.Run($"--awg-down \"{ConfPath}\""));
            Set(TunnelStatus.Disconnected);
            throw;
        }
        catch
        {
            await Task.Run(() => LinuxPkexec.Run($"--awg-down \"{ConfPath}\""));
            if (_userStopped) { Set(TunnelStatus.Disconnected); throw new OperationCanceledException(); }
            Set(TunnelStatus.Error);
            throw;
        }
        _rx = _tx = 0;
        StartMonitor();
        Set(TunnelStatus.Connected);
    }

    public async Task StopAsync()
    {
        _userStopped = true;
        StopMonitor();
        await Task.Run(() => LinuxPkexec.Run($"--awg-down \"{ConfPath}\""));
        _rx = _tx = 0;
        Set(TunnelStatus.Disconnected);
    }

    private void StartMonitor()
    {
        if (_monitor is not null) return;
        _monitor = new System.Timers.Timer(1000);
        _monitor.Elapsed += (_, _) => Poll();
        _monitor.Start();
    }

    private void StopMonitor() { _monitor?.Stop(); _monitor?.Dispose(); _monitor = null; }

    private void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            if (!IfaceExists())
            {
                if (_userStopped) return;
                StopMonitor();
                Set(TunnelStatus.Disconnected);
                return;
            }
            ReadStats();
        }
        catch { }
        finally { Interlocked.Exchange(ref _polling, 0); }
    }

    private void ReadStats()
    {
        var rx = ReadCounter("rx_bytes");
        var tx = ReadCounter("tx_bytes");
        if (rx.HasValue) _rx = rx.Value;
        if (tx.HasValue) _tx = tx.Value;
    }

    private static ulong? ReadCounter(string name)
    {
        try
        {
            var p = $"/sys/class/net/{Iface}/statistics/{name}";
            return File.Exists(p) && ulong.TryParse(File.ReadAllText(p).Trim(), out var v) ? v : null;
        }
        catch { return null; }
    }

    private static bool IfaceExists() => Directory.Exists($"/sys/class/net/{Iface}");

    private static string TranslateUpError(int code, string err)
    {
        if (err.Contains("No authentication agent", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("polkit-agent-helper", StringComparison.OrdinalIgnoreCase))
            return "Не удалось запросить права: нет агента polkit. На niri/Hyprland добавьте в автозапуск " +
                   "polkit-агент (hyprpolkitagent, lxqt-policykit-agent и т.п.) либо установите MatchaLab " +
                   "через sudo ./install.sh — тогда пароль не потребуется вовсе.";
        if (err.Contains("Operation not permitted") || err.Contains("dismissed") || err.Contains("not authorized"))
            return "Не выданы права администратора. Разрешите запрос pkexec для подключения VPN.";
        if (err.Contains("amneziawg-go") && (err.Contains("not found") || err.Contains("No such file")))
            return "Компонент amneziawg-go не найден рядом с приложением. Переустановите MatchaLab " +
                   "(sudo ./install.sh) — движок туннеля идёт в комплекте.";
        if (err.Contains("Address already in use") || (err.Contains("RTNETLINK") && err.Contains("File exists")))
            return "Интерфейс занят прошлой сессией. Отключите VPN и попробуйте снова.";
        var tail = err.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return tail.Length == 0
            ? "Не удалось поднять туннель MatchaWG."
            : "Не удалось поднять туннель MatchaWG:\n" + string.Join("\n", tail.TakeLast(4));
    }

    private void Set(TunnelStatus s)
    {
        Status = s;
        Dispatcher.UIThread.Post(() => StatusChanged?.Invoke(s));
    }
}
