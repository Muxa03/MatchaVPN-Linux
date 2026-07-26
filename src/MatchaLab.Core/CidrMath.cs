using System.Numerics;

namespace MatchaLab.Core;

public static class CidrMath
{
    private struct V4Range { public uint Lo; public uint Hi; }

    public static (uint Base, int Prefix)? Parse(string s)
    {
        var parts = s.Split('/');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[1], out var prefix) || prefix < 0 || prefix > 32) return null;
        var oct = parts[0].Split('.');
        if (oct.Length != 4) return null;
        uint baseAddr = 0;
        foreach (var o in oct)
        {
            if (!uint.TryParse(o, out var v) || v > 255) return null;
            baseAddr = (baseAddr << 8) | v;
        }
        return (baseAddr, prefix);
    }

    private static V4Range RangeOf(uint baseAddr, int prefix)
    {
        uint mask = prefix == 0 ? 0u : (0xFFFFFFFFu << (32 - prefix));
        uint lo = baseAddr & mask;
        return new V4Range { Lo = lo, Hi = lo | ~mask };
    }

    private static string IpString(uint v)
        => $"{(v >> 24) & 255}.{(v >> 16) & 255}.{(v >> 8) & 255}.{v & 255}";

    private static IEnumerable<V4Range> ToRanges(IEnumerable<string> cidrs)
        => cidrs.Select(Parse).Where(x => x.HasValue)
                .Select(x => RangeOf(x!.Value.Base, x!.Value.Prefix));

    private static List<V4Range> Merge(IEnumerable<V4Range> ranges)
    {
        var sorted = ranges.OrderBy(r => r.Lo).ToList();
        var outp = new List<V4Range>();
        foreach (var r in sorted)
        {
            if (outp.Count > 0)
            {
                var last = outp[^1];
                bool adjacent = last.Hi != 0xFFFFFFFF && r.Lo <= last.Hi + 1;
                if (r.Lo <= last.Hi || adjacent)
                {
                    if (r.Hi > last.Hi) { last.Hi = r.Hi; outp[^1] = last; }
                    continue;
                }
            }
            outp.Add(r);
        }
        return outp;
    }

    private static List<V4Range> Complement(List<V4Range> merged)
    {
        var outp = new List<V4Range>();
        uint cursor = 0;
        foreach (var r in merged)
        {
            if (r.Lo > cursor) outp.Add(new V4Range { Lo = cursor, Hi = r.Lo - 1 });
            if (r.Hi == 0xFFFFFFFF) return outp;
            cursor = r.Hi + 1;
        }
        outp.Add(new V4Range { Lo = cursor, Hi = 0xFFFFFFFF });
        return outp;
    }

    private static IEnumerable<string> RangeToCidrs(V4Range r)
    {
        var outp = new List<string>();
        uint start = r.Lo, end = r.Hi;
        while (start <= end)
        {
            int align = start == 0 ? 32 : BitOperations.TrailingZeroCount(start);
            int prefix = 32 - Math.Min(32, align);
            ulong remaining = (ulong)end - start + 1;
            while ((1UL << (32 - prefix)) > remaining) prefix++;
            outp.Add($"{IpString(start)}/{prefix}");
            ulong next = (ulong)start + (1UL << (32 - prefix));
            if (next > 0xFFFFFFFF) break;
            start = (uint)next;
        }
        return outp;
    }

    public static List<string> ComplementCidrs(IEnumerable<string> bypass)
    {
        var ranges = ToRanges(bypass).ToList();
        if (ranges.Count == 0) return new List<string> { "0.0.0.0/0" };
        return Complement(Merge(ranges)).SelectMany(RangeToCidrs).ToList();
    }

    public static List<string> SubtractCidrs(IEnumerable<string> a, IEnumerable<string> b)
    {
        var ar = Merge(ToRanges(a));
        var br = Merge(ToRanges(b));
        var result = new List<V4Range>();
        foreach (var r in ar)
        {
            var segs = new List<V4Range> { r };
            foreach (var x in br)
            {
                var next = new List<V4Range>();
                foreach (var s in segs)
                {
                    if (x.Hi < s.Lo || x.Lo > s.Hi) { next.Add(s); continue; }
                    if (x.Lo > s.Lo) next.Add(new V4Range { Lo = s.Lo, Hi = x.Lo - 1 });
                    if (x.Hi < s.Hi) next.Add(new V4Range { Lo = x.Hi + 1, Hi = s.Hi });
                }
                segs = next;
                if (segs.Count == 0) break;
            }
            result.AddRange(segs);
        }
        return result.SelectMany(RangeToCidrs).ToList();
    }
}
