using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace MatchaLab.App.Services;

[SupportedOSPlatform("linux")]
internal static class LinuxPrivilegedHelper
{
    public const string AwgIface = "matchalab";
    public const string Hy2Iface = "MatchaHy2";

    private const string RunDir = "/run/matchalab";
    private const string Hy2Pid = RunDir + "/hy2.pid";
    public const string Hy2Log = "/tmp/matchalab-singbox.log";
    public const string AwgLog = "/tmp/matchalab-awg.log";
    private const string ResolvConf = "/etc/resolv.conf";
    private const string ResolvBackup = "/etc/resolv.conf.matchalab-orig";
    private const string ResolvAbsent = RunDir + "/resolv.conf.absent";

    private static readonly string[] UapiSockets =
    {
        $"/var/run/amneziawg/{AwgIface}.sock",
        $"/var/run/wireguard/{AwgIface}.sock",
    };

    public static int AwgUp(string confPath)
    {
        if (!File.Exists(confPath)) { Console.Error.WriteLine($"conf not found: {confPath}"); return 1; }

        AwgConf conf;
        try { conf = AwgConf.Parse(File.ReadAllText(confPath)); }
        catch (Exception ex) { Console.Error.WriteLine($"bad conf: {ex.Message}"); return 1; }
        if (conf.PrivateKey is null || conf.PublicKey is null || conf.Endpoint is null)
        {
            Console.Error.WriteLine("bad conf: нет PrivateKey/PublicKey/Endpoint");
            return 1;
        }

        Directory.CreateDirectory(RunDir);

        TearDownIface();
        var err = BringUp(conf);
        if (err is not null)
        {
            Console.Error.WriteLine(err + LogTail());
            TearDownAll(conf);
            return 1;
        }

        if (AwaitHandshake(conf, TimeSpan.FromSeconds(30))) return 0;

        TearDownAll(conf);
        return 3;
    }

    public static int AwgDown(string confPath)
    {
        AwgConf? conf = null;
        try { if (File.Exists(confPath)) conf = AwgConf.Parse(File.ReadAllText(confPath)); } catch { }
        TearDownIface();
        RestoreDns();
        if (conf is not null) ApplyEndpointRoute(conf, add: false);
        return 0;
    }

    private static string? BringUp(AwgConf conf)
    {
        var bin = ResolveBinary("amneziawg-go");

        try { File.Delete(AwgLog); } catch { }
        var (code, _, serr) = Run(15000, new Dictionary<string, string> { ["LOG_LEVEL"] = "verbose" },
            "/bin/sh", "-c", "exec \"$0\" \"$1\" >>\"$2\" 2>&1 </dev/null", bin, AwgIface, AwgLog);
        if (code != 0)
            return $"amneziawg-go не запустился (код {code}). {serr}".Trim();

        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < deadline && (UapiSocket() is null || !IfaceExists())) Thread.Sleep(150);
        if (UapiSocket() is null || !IfaceExists())
            return "amneziawg-go: интерфейс не создался.";

        var resp = Uapi(BuildSetRequest(conf));
        if (resp is null) return "uapi: демон не ответил.";
        var errno = ParseErrno(resp);
        if (errno != 0) return $"uapi: конфигурация отвергнута (errno={errno}).";

        foreach (var a in conf.Addresses) Run(5000, null, "ip", "address", "replace", a, "dev", AwgIface);
        Run(5000, null, "ip", "link", "set", "dev", AwgIface, "mtu", conf.Mtu.ToString(), "up");

        ApplyEndpointRoute(conf, add: true);
        AddAllowedRoutes(conf);
        SetDns(conf);
        return null;
    }

    private static void TearDownIface()
    {
        var sock = UapiSocket();
        if (sock is not null)
        {
            try { File.Delete(sock); } catch { }
            for (var i = 0; i < 20 && IfaceExists(); i++) Thread.Sleep(200);
        }
        if (IfaceExists())
        {
            Run(5000, null, "ip", "link", "delete", "dev", AwgIface);
            for (var i = 0; i < 10 && IfaceExists(); i++) Thread.Sleep(200);
        }
    }

    private static void TearDownAll(AwgConf conf)
    {
        TearDownIface();
        RestoreDns();
        ApplyEndpointRoute(conf, add: false);
    }

    private static bool IfaceExists() => Directory.Exists($"/sys/class/net/{AwgIface}");

    private static string? UapiSocket()
    {
        foreach (var p in UapiSockets)
            if (File.Exists(p)) return p;
        return null;
    }

    private static string? Uapi(string request)
    {
        var path = UapiSocket();
        if (path is null) return null;
        try
        {
            using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            sock.Connect(new UnixDomainSocketEndPoint(path));
            sock.SendTimeout = 3000;
            sock.ReceiveTimeout = 3000;
            var req = Encoding.ASCII.GetBytes(request);
            var sent = 0;
            while (sent < req.Length) sent += sock.Send(req, sent, req.Length - sent, SocketFlags.None);
            var sb = new StringBuilder();
            var buf = new byte[4096];
            while (true)
            {
                int n;
                try { n = sock.Receive(buf); } catch { break; }
                if (n <= 0) break;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                if (sb.Length >= 2 && sb[^1] == '\n' && sb[^2] == '\n') break;
            }
            return sb.ToString();
        }
        catch { return null; }
    }

    private static string BuildSetRequest(AwgConf c)
    {
        var sb = new StringBuilder("set=1\n");
        sb.Append("private_key=").Append(HexKey(c.PrivateKey!)).Append('\n');
        if (c.ListenPort is not null) sb.Append("listen_port=").Append(c.ListenPort).Append('\n');
        foreach (var (key, val) in c.Obfs) sb.Append(key).Append('=').Append(val).Append('\n');
        sb.Append("replace_peers=true\n");
        sb.Append("public_key=").Append(HexKey(c.PublicKey!)).Append('\n');
        if (c.PresharedKey is not null) sb.Append("preshared_key=").Append(HexKey(c.PresharedKey)).Append('\n');
        sb.Append("endpoint=").Append(ResolveEndpoint(c.Endpoint!)).Append('\n');
        sb.Append("persistent_keepalive_interval=").Append(c.Keepalive ?? "25").Append('\n');
        sb.Append("replace_allowed_ips=true\n");
        foreach (var ip in c.AllowedIps) sb.Append("allowed_ip=").Append(ip).Append('\n');
        sb.Append('\n');
        return sb.ToString();
    }

    private static string HexKey(string base64) =>
        Convert.ToHexString(Convert.FromBase64String(base64)).ToLowerInvariant();

    private static int ParseErrno(string resp)
    {
        foreach (var line in resp.Split('\n'))
            if (line.StartsWith("errno=") && int.TryParse(line["errno=".Length..], out var e))
                return e;
        return -1;
    }

    private static string ResolveEndpoint(string endpoint)
    {
        var host = endpoint;
        var port = "";
        if (endpoint.StartsWith('['))
        {
            var close = endpoint.IndexOf(']');
            if (close > 1)
            {
                host = endpoint[1..close];
                port = endpoint[(close + 1)..];
            }
        }
        else
        {
            var colon = endpoint.LastIndexOf(':');
            if (colon > 0)
            {
                host = endpoint[..colon];
                port = endpoint[colon..];
            }
        }
        if (IPAddress.TryParse(host, out var ip))
            return ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]{port}" : $"{ip}{port}";
        try
        {
            var all = Dns.GetHostAddresses(host);
            var v4 = all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            var any = v4 ?? all.FirstOrDefault();
            if (any is not null)
                return any.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{any}]{port}" : $"{any}{port}";
        }
        catch { }
        return endpoint;
    }

    private static bool AwaitHandshake(AwgConf conf, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        var assertRouteAt = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            if (!IfaceExists() || UapiSocket() is null)
            {
                TearDownIface();
                if (BringUp(conf) is not null) return false;
                assertRouteAt = DateTime.UtcNow;
            }
            var resp = Uapi("get=1\n\n");
            if (resp is not null)
                foreach (var line in resp.Split('\n'))
                    if (line.StartsWith("last_handshake_time_sec=") &&
                        long.TryParse(line["last_handshake_time_sec=".Length..], out var sec) && sec > 0)
                        return true;
            if (DateTime.UtcNow >= assertRouteAt)
            {
                ApplyEndpointRoute(conf, add: true);
                assertRouteAt = DateTime.UtcNow.AddSeconds(2);
            }
            Thread.Sleep(500);
        }
        return false;
    }

    private static void AddAllowedRoutes(AwgConf conf)
    {
        var sb = new StringBuilder();
        foreach (var cidr in conf.AllowedIps)
            sb.Append(cidr.Contains(':') ? "-6 route replace " : "route replace ")
              .Append(cidr).Append(" dev ").Append(AwgIface).Append('\n');
        if (sb.Length == 0) return;
        var batch = Path.Combine(RunDir, "routes.batch");
        File.WriteAllText(batch, sb.ToString());
        Run(10000, null, "ip", "-force", "-batch", batch);
    }

    private static void ApplyEndpointRoute(AwgConf conf, bool add)
    {
        var host = conf.Endpoint;
        if (host is null) return;
        if (host.StartsWith('['))
        {
            var close = host.IndexOf(']');
            host = close > 1 ? host[1..close] : host;
        }
        else
        {
            var colon = host.LastIndexOf(':');
            if (colon > 0) host = host[..colon];
        }
        if (!IPAddress.TryParse(host, out var ip))
        {
            try
            {
                ip = Dns.GetHostAddresses(host)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            }
            catch { ip = null; }
        }
        if (ip is null) return;

        var v6 = ip.AddressFamily == AddressFamily.InterNetworkV6;
        var prefix = v6 ? "/128" : "/32";
        if (add)
        {
            var gw = DefaultGateway(v6);
            if (gw is null) return;
            Run(5000, null, "ip", v6 ? "-6" : "-4", "route", "replace", $"{ip}{prefix}",
                "via", gw.Value.gw, "dev", gw.Value.dev);
        }
        else
        {
            Run(5000, null, "ip", v6 ? "-6" : "-4", "route", "del", $"{ip}{prefix}");
        }
    }

    private static (string gw, string dev)? DefaultGateway(bool v6)
    {
        var (code, outp, _) = Run(5000, null, "ip", v6 ? "-6" : "-4", "route", "show", "default");
        if (code != 0) return null;
        foreach (var line in outp.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string? gw = null, dev = null;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i] == "via") gw = parts[i + 1];
                else if (parts[i] == "dev") dev = parts[i + 1];
            }
            if (gw is not null && dev is not null && dev != AwgIface) return (gw, dev);
        }
        return null;
    }

    private static void SetDns(AwgConf conf)
    {
        if (conf.Dns.Count == 0) return;

        if (File.Exists("/run/systemd/resolve/io.systemd.Resolve"))
        {
            var args = new List<string> { "dns", AwgIface };
            args.AddRange(conf.Dns);
            var (c1, _, _) = Run(5000, null, "resolvectl", args.ToArray());
            var (c2, _, _) = Run(5000, null, "resolvectl", "domain", AwgIface, "~.");
            if (c1 == 0 && c2 == 0) return;
        }

        try
        {
            if (!File.Exists(ResolvBackup) && !File.Exists(ResolvAbsent))
            {
                try { File.Move(ResolvConf, ResolvBackup); }
                catch { try { File.WriteAllText(ResolvAbsent, ""); } catch { } }
            }
            File.WriteAllText(ResolvConf,
                "# MatchaLab VPN\n" + string.Concat(conf.Dns.Select(d => $"nameserver {d}\n")));
        }
        catch { }
    }

    private static void RestoreDns()
    {
        try
        {
            var ours = false;
            try { ours = File.ReadLines(ResolvConf).FirstOrDefault()?.Contains("MatchaLab") == true; }
            catch { }
            if (File.Exists(ResolvBackup))
            {
                if (ours) { try { File.Delete(ResolvConf); } catch { } }
                if (!File.Exists(ResolvConf)) File.Move(ResolvBackup, ResolvConf);
                else File.Delete(ResolvBackup);
            }
            else if (File.Exists(ResolvAbsent))
            {
                if (ours) { try { File.Delete(ResolvConf); } catch { } }
                File.Delete(ResolvAbsent);
            }
        }
        catch { }
    }

    private static string LogTail(int lines = 5)
    {
        try
        {
            using var fs = new FileStream(AwgLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var r = new StreamReader(fs);
            var all = r.ReadToEnd().Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return all.Length == 0 ? "" : "\n" + string.Join("\n", all.TakeLast(lines));
        }
        catch { return ""; }
    }

    private sealed class AwgConf
    {
        public string? PrivateKey, ListenPort, PublicKey, PresharedKey, Endpoint, Keepalive;
        public int Mtu = 1420;
        public readonly List<string> Addresses = new();
        public readonly List<string> Dns = new();
        public readonly List<string> AllowedIps = new();
        public readonly List<(string key, string val)> Obfs = new();

        private static readonly HashSet<string> ObfsKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "jc", "jmin", "jmax", "s1", "s2", "s3", "s4",
            "h1", "h2", "h3", "h4",
            "i1", "i2", "i3", "i4", "i5", "j1", "j2", "j3", "itime",
        };

        public static AwgConf Parse(string text)
        {
            var c = new AwgConf();
            var section = "";
            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
                if (line.StartsWith('[')) { section = line.ToLowerInvariant(); continue; }
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim();
                var val = line[(eq + 1)..].Trim();
                if (section == "[interface]")
                {
                    if (Eq(key, "PrivateKey")) c.PrivateKey = val;
                    else if (Eq(key, "Address")) c.Addresses.AddRange(SplitList(val));
                    else if (Eq(key, "DNS")) c.Dns.AddRange(SplitList(val));
                    else if (Eq(key, "MTU") && int.TryParse(val, out var m)) c.Mtu = m;
                    else if (Eq(key, "ListenPort")) c.ListenPort = val;
                    else if (ObfsKeys.Contains(key)) c.Obfs.Add((key.ToLowerInvariant(), val));
                }
                else if (section == "[peer]")
                {
                    if (Eq(key, "PublicKey")) c.PublicKey = val;
                    else if (Eq(key, "PresharedKey")) c.PresharedKey = val;
                    else if (Eq(key, "AllowedIPs")) c.AllowedIps.AddRange(SplitList(val));
                    else if (Eq(key, "Endpoint")) c.Endpoint = val;
                    else if (Eq(key, "PersistentKeepalive")) c.Keepalive = val;
                }
            }
            return c;
        }

        private static bool Eq(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

        private static IEnumerable<string> SplitList(string v) =>
            v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static int Hy2Run(string jsonPath)
    {
        if (!File.Exists(jsonPath)) { Console.Error.WriteLine($"config not found: {jsonPath}"); return 1; }
        Directory.CreateDirectory(RunDir);

        var bin = ResolveBinary("sing-box");
        var log = new FileStream(Hy2Log, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var psi = new ProcessStartInfo(bin, $"run -c \"{jsonPath}\"")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var proc = Process.Start(psi);
        if (proc is null) { Console.Error.WriteLine("failed to start sing-box"); return 1; }

        var writer = new StreamWriter(log) { AutoFlush = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (writer) writer.WriteLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (writer) writer.WriteLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        File.WriteAllText(Hy2Pid, proc.Id.ToString());

        void Shutdown() { try { if (!proc.HasExited) proc.Kill(true); } catch { } try { File.Delete(Hy2Pid); } catch { } }
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; Shutdown(); });
        using var intr = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; Shutdown(); });
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();

        proc.WaitForExit();
        try { File.Delete(Hy2Pid); } catch { }
        return proc.ExitCode;
    }

    public static int Hy2Stop()
    {
        try
        {
            if (File.Exists(Hy2Pid) && int.TryParse(File.ReadAllText(Hy2Pid).Trim(), out var pid))
            {
                try { using var p = Process.GetProcessById(pid); p.Kill(true); p.WaitForExit(4000); } catch { }
            }
        }
        catch { }
        try { File.Delete(Hy2Pid); } catch { }
        return 0;
    }

    private static string ResolveBinary(string name)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(bundled) ? bundled : name;
    }

    private static (int code, string stdout, string stderr) Run(
        int timeoutMs, IReadOnlyDictionary<string, string>? env, string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (!string.IsNullOrEmpty(AppContext.BaseDirectory))
            {
                var path = Environment.GetEnvironmentVariable("PATH") ?? "";
                psi.Environment["PATH"] = AppContext.BaseDirectory.TrimEnd('/') + ":" + path;
            }
            if (env is not null)
                foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

            using var p = Process.Start(psi);
            if (p is null) return (-1, "", "start failed");
            var so = p.StandardOutput.ReadToEnd();
            var se = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, so, "timeout"); }
            return (p.ExitCode, so, se);
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }
}
