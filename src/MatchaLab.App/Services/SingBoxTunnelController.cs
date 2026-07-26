using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using Avalonia.Threading;

namespace MatchaLab.App.Services;

[SupportedOSPlatform("windows")]
public sealed class SingBoxTunnelController : ITunnelController
{
    private const string AdapterName = "MatchaHy2";
    private Process? _proc;
    private StreamWriter? _log;
    private readonly object _logLock = new();
    private System.Timers.Timer? _monitor;
    private int _polling;
    private ulong _rx, _tx;
    private volatile bool _stopRequested;

    public TunnelStatus Status { get; private set; } = TunnelStatus.Disconnected;
    public ulong RxBytes => _rx;
    public ulong TxBytes => _tx;
    public event Action<TunnelStatus>? StatusChanged;

    private static string ConfDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MatchaLab");
    private static string ConfPath => Path.Combine(ConfDir, "singbox.json");
    private static string LogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MatchaLab", "singbox.log");

    public async Task StartAsync(string singBoxJson)
    {
        _stopRequested = false;
        Set(TunnelStatus.Connecting);
        try
        {
            await Task.Run(() =>
            {
                var exeDir = DeployBinaries();
                Directory.CreateDirectory(ConfDir);
                File.WriteAllText(ConfPath, singBoxJson);
                KillStray();
                WaitAdapterGone();

                OpenLog();
                var singbox = Path.Combine(exeDir, "sing-box.exe");
                var proc = Process.Start(new ProcessStartInfo(singbox, $"run -c \"{ConfPath}\"")
                {
                    WorkingDirectory = exeDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                }) ?? throw new InvalidOperationException("Не удалось запустить sing-box.exe.");
                proc.OutputDataReceived += (_, e) => WriteLog(e.Data);
                proc.ErrorDataReceived += (_, e) => WriteLog(e.Data);
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                _proc = proc;

                var deadline = DateTime.UtcNow.AddSeconds(25);
                while (DateTime.UtcNow < deadline)
                {
                    if (_stopRequested) throw new OperationCanceledException();
                    if (AdapterPresent()) { Thread.Sleep(300); return; }
                    if (proc.HasExited)
                        throw new InvalidOperationException(
                            "Hysteria2 не запустился. Если параллельно работает другой VPN — отключите его.\n" + LogTail());
                    Thread.Sleep(500);
                }
                throw new TimeoutException(
                    "Туннель Hysteria2 не поднялся (адаптер не создался).\n" + LogTail());
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
        try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); } catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null;
        lock (_logLock) { try { _log?.Dispose(); } catch { } _log = null; }
        KillStray();
        try { File.Delete(ConfPath); } catch { }
    }

    private void OpenLog()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            lock (_logLock)
            {
                _log?.Dispose();
                _log = new StreamWriter(new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                { AutoFlush = true };
            }
        }
        catch { }
    }

    private void WriteLog(string? line)
    {
        if (line is null) return;
        lock (_logLock) { try { _log?.WriteLine(line); } catch { } }
    }

    private static string LogTail(int lines = 6)
    {
        try
        {
            using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var r = new StreamReader(fs);
            var all = r.ReadToEnd().Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return all.Length == 0 ? $"Лог: {LogPath}" : string.Join("\n", all.TakeLast(lines));
        }
        catch { return $"Лог: {LogPath}"; }
    }

    private static void KillStray()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("sing-box"))
            {
                try { p.Kill(entireProcessTree: true); p.WaitForExit(3000); } catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
    }

    private static string DeployBinaries()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        foreach (var name in new[] { "sing-box.exe", "wintun.dll" })
        {
            var dst = Path.Combine(exeDir, name);
            if (File.Exists(dst)) continue;
            var src = Path.Combine(AppContext.BaseDirectory, name);
            try { if (File.Exists(src)) File.Copy(src, dst); } catch { }
        }
        if (!File.Exists(Path.Combine(exeDir, "sing-box.exe")))
            throw new FileNotFoundException(
                "Не найден sing-box.exe рядом с приложением. Переместите MatchaLab.exe в папку с правом записи и повторите.");
        return exeDir;
    }

    private static bool AdapterPresent()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(ni => ni.Name == AdapterName);
        }
        catch { return false; }
    }

    private static void WaitAdapterGone()
    {
        for (var i = 0; i < 16; i++)
        {
            if (NetworkInterface.GetAllNetworkInterfaces().All(ni => ni.Name != AdapterName)) return;
            Thread.Sleep(500);
        }
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
            if (_proc is null || _proc.HasExited)
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
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.Name != AdapterName) continue;
                var s = ni.GetIPStatistics();
                _rx = (ulong)Math.Max(0, s.BytesReceived);
                _tx = (ulong)Math.Max(0, s.BytesSent);
                return;
            }
        }
        catch { }
    }

    private void Set(TunnelStatus s)
    {
        Status = s;
        Dispatcher.UIThread.Post(() => StatusChanged?.Invoke(s));
    }
}
