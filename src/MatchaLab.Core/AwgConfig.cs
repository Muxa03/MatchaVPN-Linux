using System.Text;

namespace MatchaLab.Core;

public static class AwgConfig
{
    public static string WithAllowedIPs(string config, IEnumerable<string> allowedIPs)
    {
        var joined = string.Join(", ", allowedIPs);
        var replaced = false;
        var sb = new StringBuilder();
        foreach (var line in NormalizeLines(config))
        {
            if (line.TrimStart().StartsWith("AllowedIPs", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append("AllowedIPs = ").Append(joined).Append('\n');
                replaced = true;
            }
            else
                sb.Append(line).Append('\n');
        }
        if (!replaced)
        {
            var outp = new StringBuilder();
            foreach (var line in NormalizeLines(sb.ToString()))
            {
                outp.Append(line).Append('\n');
                if (line.TrimStart().StartsWith("[Peer]", StringComparison.OrdinalIgnoreCase))
                    outp.Append("AllowedIPs = ").Append(joined).Append('\n');
            }
            return outp.ToString();
        }
        return sb.ToString();
    }

    public static string EnsureMtu(string config, int mtu = 1420)
    {
        var lines = NormalizeLines(config);
        if (lines.Any(l => l.TrimStart().StartsWith("MTU", StringComparison.OrdinalIgnoreCase)))
            return config;
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            sb.Append(line).Append('\n');
            if (line.TrimStart().StartsWith("[Interface]", StringComparison.OrdinalIgnoreCase))
                sb.Append("MTU = ").Append(mtu).Append('\n');
        }
        return sb.ToString();
    }

    public static string? EndpointHost(string config)
    {
        foreach (var line in NormalizeLines(config))
        {
            var t = line.TrimStart();
            if (!t.StartsWith("Endpoint", StringComparison.OrdinalIgnoreCase)) continue;
            var i = t.IndexOf('=');
            if (i < 0) continue;
            var v = t[(i + 1)..].Trim();
            var colon = v.LastIndexOf(':');
            return colon > 0 ? v[..colon] : v;
        }
        return null;
    }

    private static string[] NormalizeLines(string config)
        => config.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
