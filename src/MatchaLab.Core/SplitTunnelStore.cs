using System.Net;
using System.Net.Sockets;

namespace MatchaLab.Core;

public sealed class SplitTunnelStore
{
    private readonly ApiClient _api;
    private List<string> _ruCidrs = FallbackRu;
    private List<string> _viaVpn = FallbackViaVpn;
    private readonly Dictionary<string, List<string>> _resolved = new();

    public bool RuEnabled { get; set; } = true;
    public List<string> CustomDomains { get; } = new();

    public SplitTunnelStore(ApiClient api) => _api = api;

    public bool IsOn => RuEnabled || CustomDomains.Count > 0;

    public void AddDomain(string raw)
    {
        var d = Normalize(raw);
        if (d.Length == 0 || !d.Contains('.') || CustomDomains.Contains(d)) return;
        CustomDomains.Add(d);
        _ = ResolveOneAsync(d);
    }

    public void RemoveDomain(string d)
    {
        CustomDomains.Remove(d);
        _resolved.Remove(d);
    }

    public int ResolvedCount(string domain) => _resolved.TryGetValue(domain, out var v) ? v.Count : 0;

    private async Task ResolveOneAsync(string d)
    {
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(d);
            var ips = addrs.Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                           .Select(a => a.ToString()).Distinct().ToList();
            if (ips.Count > 0) _resolved[d] = ips;
        }
        catch { }
    }

    public async Task ResolveDomainsAsync()
    {
        foreach (var d in CustomDomains) await ResolveOneAsync(d);
    }

    public IReadOnlyList<string> ViaVpn => _viaVpn;

    public List<string> DirectCidrs()
    {
        var outp = new List<string>();
        if (RuEnabled) outp.AddRange(_ruCidrs);
        foreach (var d in CustomDomains)
            if (_resolved.TryGetValue(d, out var ips))
                outp.AddRange(ips.Select(ip => $"{ip}/32"));
        return outp;
    }

    public List<string> TunnelAllowedIPs()
    {
        var d = DirectCidrs();
        if (d.Count == 0) return new List<string> { "0.0.0.0/1", "128.0.0.0/1", "::/1", "8000::/1" };
        var result = CidrMath.ComplementCidrs(d);
        result.AddRange(_viaVpn);
        result.Add("::/1");
        result.Add("8000::/1");
        return result.Distinct().ToList();
    }

    public List<string> ExcludedRoutes()
    {
        var d = DirectCidrs();
        return d.Count == 0 ? new List<string>() : CidrMath.SubtractCidrs(d, _viaVpn);
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var resp = await _api.GetServicesAsync(ct);
        if (resp is not null)
        {
            var ru = resp.Services.FirstOrDefault(s => s.Id == "ru-all");
            if (ru is { Cidrs.Count: > 0 }) _ruCidrs = ru.Cidrs;
            if (resp.ViaVpn is { Count: > 0 }) _viaVpn = resp.ViaVpn;
        }
        await ResolveDomainsAsync();
    }

    public static string Normalize(string raw)
    {
        var s = raw.Trim().ToLowerInvariant();
        var i = s.IndexOf("://", StringComparison.Ordinal);
        if (i >= 0) s = s[(i + 3)..];
        var slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];
        if (s.StartsWith("www.")) s = s[4..];
        return s;
    }

    public static readonly List<string> FallbackRu = new()
    {
        "5.45.192.0/18", "77.88.0.0/18", "87.250.224.0/19", "93.158.128.0/18", "95.108.128.0/17",
        "178.154.128.0/18", "213.180.192.0/19",
        "87.240.128.0/19", "87.240.160.0/19", "93.186.224.0/21",
        "94.100.176.0/21", "217.69.128.0/21",
        "178.176.0.0/20", "185.71.64.0/22",
        "185.156.208.0/22",
        "194.54.12.0/22", "185.157.96.0/24",
        "178.248.232.0/21", "195.19.96.0/20", "159.255.0.0/16",
        "213.59.248.0/21",
        "185.62.200.0/22",
        "185.73.192.0/22",
        "91.206.124.0/22", "195.208.64.0/22",
    };

    public static readonly List<string> FallbackViaVpn = new()
    {
        "2.16.0.0/13", "2.18.0.0/16", "2.20.0.0/14", "2.21.0.0/16",
        "23.0.0.0/12", "23.32.0.0/11", "23.64.0.0/14", "23.72.0.0/13", "23.192.0.0/11",
        "96.16.0.0/15", "88.221.0.0/16", "92.122.0.0/15", "95.100.0.0/15", "184.24.0.0/13", "104.64.0.0/10",
        "104.16.0.0/12", "162.158.0.0/15", "172.64.0.0/13", "141.101.64.0/18",
        "108.162.192.0/18", "173.245.48.0/20", "188.114.96.0/20", "190.93.240.0/20", "198.41.128.0/17",
        "151.101.0.0/16", "199.232.0.0/16", "146.75.0.0/16",
        "104.244.40.0/21", "192.133.76.0/22", "199.16.156.0/22", "199.59.148.0/22", "209.237.192.0/19", "69.195.160.0/19",
        "64.233.160.0/19", "66.102.0.0/20", "66.249.64.0/19", "72.14.192.0/18", "74.125.0.0/16",
        "108.177.0.0/17", "142.250.0.0/15", "172.217.0.0/16", "172.253.0.0/16", "173.194.0.0/16",
        "192.178.0.0/15", "209.85.128.0/17", "216.58.192.0/19", "216.239.32.0/19",
        "5.180.72.0/22", "18.239.18.0/24", "18.239.105.0/24",
        "44.227.0.0/16", "44.241.0.0/16", "52.33.0.0/16", "54.160.0.0/16",
        "71.18.0.0/16", "101.45.0.0/16", "147.160.176.0/20", "139.177.224.0/19",
        "103.136.220.0/22", "130.44.212.0/22", "199.103.24.0/23", "202.52.240.0/21",
    };
}
