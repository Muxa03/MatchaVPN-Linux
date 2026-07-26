using System.Text.Json;

namespace MatchaLab.Core;

public static class Hy2Config
{
    public readonly record struct Link(string Host, int Port, string Password, string Sni,
                                        bool Insecure, string? Obfs, string? ObfsPassword);

    public static Link Parse(string share)
    {
        var s = share.Trim();
        var hash = s.IndexOf('#');
        if (hash >= 0) s = s[..hash];
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];

        string query = "";
        var q = s.IndexOf('?');
        if (q >= 0) { query = s[(q + 1)..]; s = s[..q]; }

        string password = "";
        var at = s.LastIndexOf('@');
        if (at >= 0) { password = Uri.UnescapeDataString(s[..at]); s = s[(at + 1)..]; }

        string host = s; int port = 443;
        var colon = s.LastIndexOf(':');
        if (colon >= 0 && int.TryParse(s[(colon + 1)..], out var p)) { host = s[..colon]; port = p; }

        var qp = ParseQuery(query);
        var sni = qp.GetValueOrDefault("sni", "");
        if (string.IsNullOrEmpty(sni)) sni = host;
        var insecure = qp.GetValueOrDefault("insecure", "") is "1" or "true";
        var obfs = qp.GetValueOrDefault("obfs", "");
        var obfsPw = qp.GetValueOrDefault("obfs-password", "");
        return new Link(host, port, password, sni, insecure,
                        string.IsNullOrEmpty(obfs) ? null : obfs,
                        string.IsNullOrEmpty(obfsPw) ? null : obfsPw);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            d[Uri.UnescapeDataString(kv[0])] = kv.Length == 2 ? Uri.UnescapeDataString(kv[1]) : "";
        }
        return d;
    }

    public static string Build(string share, IReadOnlyList<string> directCidrs,
                               IReadOnlyList<string> viaVpn, string stack = "system")
    {
        var l = Parse(share);

        var tls = new Dictionary<string, object>
        {
            ["enabled"] = true,
            ["server_name"] = l.Sni,
            ["insecure"] = l.Insecure,
            ["alpn"] = new[] { "h3" },
        };
        var proxy = new Dictionary<string, object>
        {
            ["type"] = "hysteria2",
            ["tag"] = "proxy",
            ["server"] = l.Host,
            ["server_port"] = l.Port,
            ["password"] = l.Password,
            ["tls"] = tls,
        };
        if (l.Obfs is not null)
            proxy["obfs"] = new Dictionary<string, object>
            {
                ["type"] = l.Obfs,
                ["password"] = l.ObfsPassword ?? "",
            };

        var rules = new List<Dictionary<string, object>>
        {
            new() { ["action"] = "sniff" },
            new() { ["protocol"] = "dns", ["action"] = "hijack-dns" },
            new() { ["ip_is_private"] = true, ["outbound"] = "direct" },
        };
        if (viaVpn.Count > 0)
            rules.Add(new() { ["ip_cidr"] = viaVpn.ToArray(), ["outbound"] = "proxy" });
        if (directCidrs.Count > 0)
            rules.Add(new() { ["ip_cidr"] = directCidrs.ToArray(), ["outbound"] = "direct" });

        var cfg = new Dictionary<string, object>
        {
            ["log"] = new Dictionary<string, object>
            {
                ["level"] = "info",
                ["timestamp"] = true,
            },
            ["dns"] = new Dictionary<string, object>
            {
                ["servers"] = new object[]
                {
                    new Dictionary<string, object> { ["type"] = "https", ["tag"] = "remote", ["server"] = "1.1.1.1", ["detour"] = "proxy" },
                    new Dictionary<string, object> { ["type"] = "udp", ["tag"] = "local", ["server"] = "1.1.1.1" },
                },
                ["final"] = "remote",
                ["strategy"] = "ipv4_only",
            },
            ["inbounds"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["type"] = "tun",
                    ["tag"] = "tun-in",
                    ["interface_name"] = "MatchaHy2",
                    ["address"] = new[] { "172.19.0.1/30" },
                    ["mtu"] = 1408,
                    ["auto_route"] = true,
                    ["strict_route"] = true,
                    ["stack"] = stack,
                },
            },
            ["outbounds"] = new object[]
            {
                proxy,
                new Dictionary<string, object> { ["type"] = "direct", ["tag"] = "direct" },
            },
            ["route"] = new Dictionary<string, object>
            {
                ["auto_detect_interface"] = true,
                ["default_domain_resolver"] = new Dictionary<string, object> { ["server"] = "local" },
                ["final"] = "proxy",
                ["rules"] = rules,
            },
        };

        return JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string? EndpointHost(string share)
    {
        try { return Parse(share).Host; } catch { return null; }
    }
}
