using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using Avalonia.Threading;

namespace MatchaLab.App.Services;

[SupportedOSPlatform("linux")]
public sealed class LinuxSingBoxTunnelController : ITunnelController
{
    private const string Iface = LinuxPrivilegedHelper.Hy2Iface;
    private Process? _proc;
    private System.Timers.Timer? _monitor;
    private int _polling;
    private ulong _rx, _tx;
    private volatile bool _stopRequested;

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

    private static string ConfPath => Path.Combine(ConfDir, "singbox.json");

    public async Task StartAsync(string singBoxJson)
    {
        _stopRequested = false;
        Set(TunnelStatus.Connecting);
        try
        {
            await Task.Run(() =>
            {
                File.WriteAllText(ConfPath, singBoxJson);
                try { File.SetUnixFileMode(ConfPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }

                var proc = LinuxPkexec.Start($"--hy2-run \"{ConfPath}\"")
                    ?? throw new InvalidOperationException("Не удалось запросить права (pkexec) для Hysteria2.");
                _proc = proc;

                var deadline = DateTime.UtcNow.AddSeconds(25);
                while (DateTime.UtcNow < deadline)
                {
                    if (_stopRequested) throw new OperationCanceledException();
                    if (IfaceExists()) { Thread.Sleep(300); return; }
                    if (proc.HasExited)
                        throw new InvalidOperationException(
                            "Hysteria2 не запустился. Если параллельно работает другой VPN - отключите его.\n" + LogTail());
                    Thread.Sleep(500);
                }
                throw new TimeoutException("Туннель Hysteria2 не поднялся (адаптер не создался).\n" + LogTail());
            });
        }
        catch (OperationCanceledException)
        {
            await Task.Run(SafeStop);
            Set(TunnelStatus.Disconnected);
            throw;
        }
        catch
        {
            await Task.Run(SafeStop);
            if (_stopRequested) { Set(TunnelStatus.Disconnected); throw new OperationCanceledException(); }
            Set(TunnelStatus.Error);
            throw;
        }
        _rx = _tx = 0;
        StartMonitor();
        Set(TunnelStatus.Connected);
    }

    public async Task StopAsync()
    {
        _stopRequested = true;
        StopMonitor();
        await Task.Run(SafeStop);
        _rx = _tx = 0;
        Set(TunnelStatus.Disconnected);
    }

    private void SafeStop()
    {
        LinuxPkexec.Run("--hy2-stop");
        try { if (_proc is { HasExited: false }) _proc.WaitForExit(4000); } catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null;
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
            if (_proc is null || _proc.HasExited || !IfaceExists())
            {
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

    private static string LogTail(int lines = 6)
    {
        try
        {
            using var fs = new FileStream(LinuxPrivilegedHelper.Hy2Log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var r = new StreamReader(fs);
            var all = r.ReadToEnd().Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return all.Length == 0 ? $"Лог: {LinuxPrivilegedHelper.Hy2Log}" : string.Join("\n", all.TakeLast(lines));
        }
        catch { return $"Лог: {LinuxPrivilegedHelper.Hy2Log}"; }
    }

    private void Set(TunnelStatus s)
    {
        Status = s;
        Dispatcher.UIThread.Post(() => StatusChanged?.Invoke(s));
    }
}
