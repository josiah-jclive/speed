using System.Text.RegularExpressions;

namespace Speed.Devices;

/// <summary>Turns recorders' internal format names into what's printed on the box.</summary>
public static partial class FormatNames
{
    /// <summary>
    /// HyperDeck "file format" values, e.g. QuickTimeProResHQ, QuickTimeDNxHR_HQX, H.264High,
    /// H.265_SDI. Unknown values are spaced out rather than dropped.
    /// </summary>
    public static (string Codec, string? Container) HyperDeckFileFormat(string raw)
    {
        var v = raw.Trim();
        string? container = null;
        if (v.StartsWith("QuickTime", StringComparison.OrdinalIgnoreCase)) { container = ".mov"; v = v[9..]; }
        else if (v.StartsWith("MXF", StringComparison.OrdinalIgnoreCase)) { container = ".mxf"; v = v[3..]; }
        else if (v.StartsWith("H.26", StringComparison.OrdinalIgnoreCase)) container = ".mp4";
        v = v.Trim('_', ' ');

        var known = v.ToLowerInvariant() switch
        {
            "proreshq" => "ProRes 422 HQ",
            "prores" => "ProRes 422",
            "proreslt" => "ProRes 422 LT",
            "proresproxy" => "ProRes 422 Proxy",
            "prores4444" => "ProRes 4444",
            "prores4444xq" => "ProRes 4444 XQ",
            "uncompressed" => "Uncompressed",
            _ => null,
        };
        if (known != null) return (known, container);

        if (v.StartsWith("DNx", StringComparison.OrdinalIgnoreCase))
        {
            // DNxHD220x, DNxHR_HQX, DNxHR_SQ ...
            var m = DnxRegex().Match(v);
            if (m.Success) return ($"{m.Groups[1].Value} {m.Groups[2].Value.Trim('_')}".Trim(), container);
        }
        if (v.StartsWith("H.26", StringComparison.OrdinalIgnoreCase))
        {
            var codec = v[..5];
            var rest = v[5..].Trim('_', ' ');
            return (rest.Length > 0 ? $"{codec} {Spaced(rest)}" : codec, container);
        }
        return (Spaced(v), container);
    }

    /// <summary>1080p2997 → 1080p29.97, 1080i5994 → 1080i59.94, 2160p25 → 2160p25, PAL stays PAL.</summary>
    public static string? VideoFormat(string raw)
    {
        var v = raw.Trim();
        if (v.Length == 0 || v.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        var m = VideoRegex().Match(v);
        if (!m.Success) return v;
        var rate = m.Groups[3].Value;
        if (rate.Length == 4) rate = $"{rate[..2]}.{rate[2..]}"; // 2997 → 29.97
        return $"{m.Groups[1].Value}{m.Groups[2].Value}{rate}";
    }

    /// <summary>Ki Pro value names are already readable; this only tidies spacing.</summary>
    public static string Tidy(string raw) => Regex.Replace(raw.Trim(), @"\s+", " ");

    private static string Spaced(string v) => Regex.Replace(v.Replace('_', ' '), "(?<=[a-z])(?=[A-Z])", " ").Trim();

    [GeneratedRegex(@"^(DNx(?:HD|HR))[_ ]?(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex DnxRegex();

    [GeneratedRegex(@"^(\d{3,4})([pi])(\d{2,4})$", RegexOptions.IgnoreCase)]
    private static partial Regex VideoRegex();
}
