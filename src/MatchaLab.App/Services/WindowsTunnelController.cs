using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text;
using Avalonia.Threading;

namespace MatchaLab.App.Services;

[SupportedOSPlatform("windows")]

public sealed class WindowsTunnelController : ITunnelController
{
    private const string ServiceName = "MatchaLabTunnel";
    private const string AdapterName = "MatchaLab";
    private System.Timers.Timer? _monitor;
    private int _polling;
    private ulong _rx, _tx;
    private long _lastHandshake;
    private string? _uapiPrefix;
    private string? _lastConfig;
    private CancellationTokenSource? _startCts;
    private uint _endpointAddr;
    private MIB_IPFORWARDROW? _endpointRoute;

    public TunnelStatus Status { get; private set; } = TunnelStatus.Disconnected;
    public ulong RxBytes => _rx;
    public ulong TxBytes => _tx;
    public event Action<TunnelStatus>? StatusChanged;

    private static string ConfDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MatchaLab");
    private static string ConfPath => Path.Combine(ConfDir, AdapterName + ".conf");

    public WindowsTunnelController()
    {
        if (IsServiceRunning())
        {
            try { _endpointAddr = ParseEndpointAddr(File.ReadAllText(ConfPath)); } catch { }
            Status = TunnelStatus.Connected;
            StartMonitor();
        }
    }

    public async Task StartAsync(string awgConfig)
    {
        _lastConfig = awgConfig;
        _endpointAddr = ParseEndpointAddr(awgConfig);
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _startCts, cts)?.Cancel();
        var ct = cts.Token;
        Set(TunnelStatus.Connecting);
        try
        {
            await Task.Run(() =>
            {
                EnsureNativeLibraries();
                Directory.CreateDirectory(ConfDir);
                RestrictAcl(ConfDir);
                File.WriteAllText(ConfPath, awgConfig);

                StartServiceWithRetry(ct);
                AwaitFirstHandshake(ct);
            }, ct);
        }
        catch (OperationCanceledException)
        {
            await Task.Run(SafeRemove);
            Set(TunnelStatus.Disconnected);
            throw;
        }
        catch
        {
            await Task.Run(SafeRemove);
            if (ct.IsCancellationRequested)
            {
                Set(TunnelStatus.Disconnected);
                throw new OperationCanceledException();
            }
            Set(TunnelStatus.Error);
            throw;
        }
        _rx = _tx = 0;
        StartMonitor();
        Set(TunnelStatus.Connected);
    }

    private void StartServiceWithRetry(CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            RemoveService();
            WaitAdapterGone(ct);
            AddEndpointRoute();
            ct.ThrowIfCancellationRequested();
            InstallService();
            using var sc = new ServiceController(ServiceName);
            try
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                try { RemoveService(); } catch { }
            }
        }
        throw new System.TimeoutException(
            "Служба туннеля не запустилась (сетевой адаптер не создался). Подождите пару секунд и попробуйте снова.", last);
    }

    private void AwaitFirstHandshake(CancellationToken ct)
    {
        _lastHandshake = 0; _uapiPrefix = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var assertRouteAt = DateTime.UtcNow;
        var sawUapi = false;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsServiceRunning())
            {
                StartServiceWithRetry(ct);
                assertRouteAt = DateTime.UtcNow;
            }
            if (TryUapiStats())
            {
                sawUapi = true;
                if (_lastHandshake > 0) { PreferTunnelInterface(); return; }
            }
            if (DateTime.UtcNow >= assertRouteAt)
            {
                AddEndpointRoute();
                assertRouteAt = DateTime.UtcNow.AddSeconds(2);
            }
            Thread.Sleep(400);
        }
        throw new System.TimeoutException(sawUapi
            ? "Сервер не ответил на рукопожатие. Проверьте интернет или смените сервер; " +
              "если параллельно работает другой VPN (AmneziaVPN и т.п.) — отключите его."
            : "Служба туннеля не поднялась (сетевой адаптер не создался). Подождите пару секунд и попробуйте снова.");
    }

    private static void EnsureNativeLibraries()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        foreach (var dll in new[] { "tunnel.dll", "wintun.dll" })
        {
            var dst = Path.Combine(exeDir, dll);
            if (File.Exists(dst)) continue;
            var src = Path.Combine(AppContext.BaseDirectory, dll);
            try { if (File.Exists(src)) File.Copy(src, dst); }
            catch { }
        }
        if (!File.Exists(Path.Combine(exeDir, "tunnel.dll")) || !File.Exists(Path.Combine(exeDir, "wintun.dll")))
            throw new FileNotFoundException(
                "Не удалось разложить tunnel.dll / wintun.dll рядом с приложением. " +
                "Переместите MatchaLab.exe в папку с правом записи (например, Документы) и попробуйте снова.");
    }

    public async Task StopAsync()
    {
        _lastConfig = null;
        _startCts?.Cancel();
        StopMonitor();
        await Task.Run(SafeRemove);
        _rx = _tx = 0;
        _lastHandshake = 0;
        Set(TunnelStatus.Disconnected);
    }

    private void SafeRemove()
    {
        try { RemoveService(); } catch { }
        try { RemoveEndpointRoute(); } catch { }
        try { File.Delete(ConfPath); } catch { }
    }

    private static void RestrictAcl(string dir)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("icacls",
                $"\"{dir}\" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F")
            { CreateNoWindow = true, UseShellExecute = false });
            p?.WaitForExit(5000);
        }
        catch { }
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
            if (!IsServiceRunning())
            {
                if (_lastConfig is { } cfg && TryRestartService(cfg)) return;
                StopMonitor();
                _lastHandshake = 0;
                Set(TunnelStatus.Disconnected);
                return;
            }
            ReadStats();
            if (_lastHandshake > 0)
            {
                var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _lastHandshake;
                if (age > 185 && Status == TunnelStatus.Connected)
                {
                    AddEndpointRoute();
                    Set(TunnelStatus.Reasserting);
                }
                else if (age <= 185 && Status == TunnelStatus.Reasserting) Set(TunnelStatus.Connected);
            }
        }
        catch { }
        finally { Interlocked.Exchange(ref _polling, 0); }
    }

    private bool TryRestartService(string cfg)
    {
        Set(TunnelStatus.Reasserting);
        try
        {
            Directory.CreateDirectory(ConfDir);
            RestrictAcl(ConfDir);
            File.WriteAllText(ConfPath, cfg);
            RemoveService();
            WaitAdapterGone();
            AddEndpointRoute();
            InstallService();
            using var sc = new ServiceController(ServiceName);
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            PreferTunnelInterface();
            Set(TunnelStatus.Connected);
            return true;
        }
        catch
        {
            SafeRemove();
            return false;
        }
    }

    private void ReadStats()
    {
        if (TryUapiStats()) return;
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

    private bool TryUapiStats()
    {
        var prefixes = _uapiPrefix is null ? new[] { "WireGuard", "AmneziaWG" } : new[] { _uapiPrefix };
        foreach (var prefix in prefixes)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(
                    ".", $"ProtectedPrefix\\Administrators\\{prefix}\\{AdapterName}", PipeDirection.InOut);
                pipe.Connect(250);
                var req = Encoding.ASCII.GetBytes("get=1\n\n");
                pipe.Write(req, 0, req.Length);
                using var r = new StreamReader(pipe, Encoding.ASCII);
                ulong rx = 0, tx = 0;
                long hs = 0;
                string? line;
                while ((line = r.ReadLine()) is not null && line.Length > 0)
                {
                    if (line.StartsWith("rx_bytes=")) rx += ulong.Parse(line["rx_bytes=".Length..]);
                    else if (line.StartsWith("tx_bytes=")) tx += ulong.Parse(line["tx_bytes=".Length..]);
                    else if (line.StartsWith("last_handshake_time_sec="))
                        hs = Math.Max(hs, long.Parse(line["last_handshake_time_sec=".Length..]));
                }
                _uapiPrefix = prefix;
                _rx = rx; _tx = tx; _lastHandshake = hs;
                return true;
            }
            catch { }
        }
        return false;
    }

    private static bool IsServiceRunning()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
        }
        catch { return false; }
    }

    private void Set(TunnelStatus s)
    {
        Status = s;
        Dispatcher.UIThread.Post(() => StatusChanged?.Invoke(s));
    }

    private static void InstallService()
    {
        var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
        var bin = $"\"{exe}\" /service \"{ConfPath}\"";

        var scm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero)
            throw new InvalidOperationException("Нет доступа к менеджеру служб — запустите приложение от администратора.");
        try
        {
            var svc = IntPtr.Zero;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                svc = CreateService(scm, ServiceName, "MatchaLab Tunnel", SERVICE_ALL_ACCESS,
                    SERVICE_WIN32_OWN_PROCESS, SERVICE_DEMAND_START, SERVICE_ERROR_NORMAL,
                    bin, null, IntPtr.Zero, null, null, null);
                if (svc != IntPtr.Zero) break;
                var err = Marshal.GetLastWin32Error();
                if (err != ERROR_SERVICE_MARKED_FOR_DELETE)
                    throw new InvalidOperationException($"Не удалось создать службу туннеля (Win32 {err}).");
                Thread.Sleep(500);
            }
            if (svc == IntPtr.Zero)
                throw new InvalidOperationException("Служба туннеля не освободилась после прошлого сеанса — попробуйте ещё раз.");
            try
            {
                var sid = new SERVICE_SID_INFO { ServiceSidType = SERVICE_SID_TYPE_UNRESTRICTED };
                if (!ChangeServiceConfig2(svc, SERVICE_CONFIG_SERVICE_SID_INFO, ref sid))
                    throw new InvalidOperationException(
                        $"Не удалось задать SID-тип службы (Win32 {Marshal.GetLastWin32Error()}).");
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    private static void RemoveService()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                try
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(12));
                }
                catch { }
            }
        }
        catch { }

        var scm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero) return;
        try
        {
            var svc = OpenService(scm, ServiceName, SERVICE_ALL_ACCESS);
            if (svc != IntPtr.Zero)
            {
                DeleteService(svc);
                CloseServiceHandle(svc);
            }
        }
        finally { CloseServiceHandle(scm); }
    }

    private static void WaitAdapterGone(CancellationToken ct = default)
    {
        for (var i = 0; i < 16; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (NetworkInterface.GetAllNetworkInterfaces().All(ni => ni.Name != AdapterName)) return;
            Thread.Sleep(500);
        }
    }

    private static void PreferTunnelInterface()
    {
        foreach (var family in new[] { "ipv4", "ipv6" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("netsh",
                    $"interface {family} set interface \"{AdapterName}\" metric=1")
                { CreateNoWindow = true, UseShellExecute = false });
                p?.WaitForExit(3000);
            }
            catch { }
        }
    }

    private void AddEndpointRoute()
    {
        if (_endpointAddr == 0) return;
        if (FindDefaultRoute(TunnelIfIndex()) is not { } via) return;
        RemoveEndpointRoute();
        var row = via;
        row.dwForwardDest = _endpointAddr;
        row.dwForwardMask = uint.MaxValue;
        row.dwForwardPolicy = 0;
        row.dwForwardType = via.dwForwardNextHop == 0 ? 3u : 4u;
        row.dwForwardProto = 3;
        row.dwForwardAge = 0;
        var err = CreateIpForwardEntry(ref row);
        if (err == ERROR_OBJECT_ALREADY_EXISTS) err = SetIpForwardEntry(ref row);
        if (err == 0) _endpointRoute = row;
    }

    private void RemoveEndpointRoute()
    {
        if (_endpointRoute is { } row)
        {
            _endpointRoute = null;
            DeleteIpForwardEntry(ref row);
            return;
        }
        if (_endpointAddr == 0) return;
        foreach (var r in ReadForwardTable())
        {
            if (r.dwForwardDest != _endpointAddr || r.dwForwardMask != uint.MaxValue || r.dwForwardProto != 3) continue;
            var stale = r;
            DeleteIpForwardEntry(ref stale);
        }
    }

    private static MIB_IPFORWARDROW? FindDefaultRoute(int excludeIfIndex)
    {
        MIB_IPFORWARDROW? best = null;
        foreach (var row in ReadForwardTable())
        {
            if (row.dwForwardDest != 0 || row.dwForwardMask != 0) continue;
            if (excludeIfIndex >= 0 && row.dwForwardIfIndex == (uint)excludeIfIndex) continue;
            if (best is null || row.dwForwardMetric1 < best.Value.dwForwardMetric1) best = row;
        }
        return best;
    }

    private static List<MIB_IPFORWARDROW> ReadForwardTable()
    {
        var rows = new List<MIB_IPFORWARDROW>();
        uint size = 0;
        if (GetIpForwardTable(IntPtr.Zero, ref size, false) != ERROR_INSUFFICIENT_BUFFER) return rows;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetIpForwardTable(buf, ref size, true) != 0) return rows;
            var count = Marshal.ReadInt32(buf);
            var rowSize = Marshal.SizeOf<MIB_IPFORWARDROW>();
            for (var i = 0; i < count; i++)
                rows.Add(Marshal.PtrToStructure<MIB_IPFORWARDROW>(buf + 4 + i * rowSize));
        }
        finally { Marshal.FreeHGlobal(buf); }
        return rows;
    }

    private static int TunnelIfIndex()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                if (ni.Name == AdapterName)
                    return ni.GetIPProperties().GetIPv4Properties().Index;
        }
        catch { }
        return -1;
    }

    private static uint ParseEndpointAddr(string cfg)
    {
        foreach (var raw in cfg.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("Endpoint", StringComparison.OrdinalIgnoreCase)) continue;
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var host = line[(eq + 1)..].Trim();
            var colon = host.LastIndexOf(':');
            if (colon > 0) host = host[..colon];
            if (System.Net.IPAddress.TryParse(host, out var ip) &&
                ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                return BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
        }
        return 0;
    }

    private const uint ERROR_INSUFFICIENT_BUFFER = 122;
    private const uint ERROR_OBJECT_ALREADY_EXISTS = 5010;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_IPFORWARDROW
    {
        public uint dwForwardDest, dwForwardMask, dwForwardPolicy, dwForwardNextHop,
            dwForwardIfIndex, dwForwardType, dwForwardProto, dwForwardAge, dwForwardNextHopAS,
            dwForwardMetric1, dwForwardMetric2, dwForwardMetric3, dwForwardMetric4, dwForwardMetric5;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIpForwardTable(IntPtr table, ref uint size, bool ordered);

    [DllImport("iphlpapi.dll")]
    private static extern uint CreateIpForwardEntry(ref MIB_IPFORWARDROW row);

    [DllImport("iphlpapi.dll")]
    private static extern uint SetIpForwardEntry(ref MIB_IPFORWARDROW row);

    [DllImport("iphlpapi.dll")]
    private static extern uint DeleteIpForwardEntry(ref MIB_IPFORWARDROW row);

    private const int ERROR_SERVICE_MARKED_FOR_DELETE = 1072;
    private const uint SERVICE_CONFIG_SERVICE_SID_INFO = 5;
    private const uint SERVICE_SID_TYPE_UNRESTRICTED = 1;
    private const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
    private const uint SERVICE_ALL_ACCESS = 0xF01FF;
    private const uint SERVICE_WIN32_OWN_PROCESS = 0x10;
    private const uint SERVICE_DEMAND_START = 0x3;
    private const uint SERVICE_ERROR_NORMAL = 0x1;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint dwAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateService(
        IntPtr scm, string serviceName, string displayName, uint access,
        uint serviceType, uint startType, uint errorControl, string binaryPath,
        string? loadOrderGroup, IntPtr tagId, string? dependencies,
        string? serviceStartName, string? password);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr scm, string serviceName, uint access);

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_SID_INFO { public uint ServiceSidType; }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ChangeServiceConfig2(IntPtr service, uint infoLevel, ref SERVICE_SID_INFO info);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeleteService(IntPtr service);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
