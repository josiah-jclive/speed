using Speed.Devices;

namespace Speed.Core;

public enum StreamLevel { Off, OnAir, Warning }

/// <summary>What the ON AIR bar on the program output says, worked out from every encoder.</summary>
public sealed record StreamSummary(StreamLevel Level, string Title, string Subtitle, string? Duration, string? Bitrate)
{
    public bool IsLive => Level != StreamLevel.Off;

    public static StreamSummary? Compute(IReadOnlyList<StreamingEncoder> encoders)
    {
        if (encoders.Count == 0) return null;
        var online = encoders.Where(e => e.IsOnline).ToList();
        var live = online.Where(e => e.IsLive).ToList();

        if (live.Count == 0)
        {
            if (online.Count == 0)
                return new(StreamLevel.Off, "Encoders not connected", Join(encoders.Select(e => e.Name)), null, null);
            var loaded = online.Select(e => e.StreamName).Distinct().ToList();
            var missing = encoders.Where(e => !e.IsOnline).Select(e => e.Name).ToList();
            var ready = $"Ready: {Join(online.Select(e => e.Name))}";
            return new(StreamLevel.Off, string.Join(" / ", loaded),
                missing.Count == 0 ? ready : $"{ready}. Not connected: {Join(missing)}", null, null);
        }

        var names = live.Select(e => e.StreamName).Distinct().ToList();
        var unknown = live.FirstOrDefault(e => !e.KeyKnown);
        var trouble = live.FirstOrDefault(e => e.State is EncoderState.Connecting or EncoderState.Interrupted);
        bool split = names.Count > 1;

        var level = unknown != null || trouble != null || split ? StreamLevel.Warning : StreamLevel.OnAir;
        var title = unknown != null && live.Count == 1 ? "Unknown stream key" : string.Join(" / ", names);
        var subtitle =
            unknown != null ? $"Key ending {unknown.KeyTail} isn't in {unknown.Name}'s list" :
            trouble != null ? $"{trouble.Name}: {(trouble.State == EncoderState.Interrupted ? "interrupted, reconnecting" : "connecting")}" :
            split ? "Encoders are on different streams" :
            Join(live.Select(e => e.Name));

        var longest = live.Where(e => e.Duration != null).OrderByDescending(e => e.Duration).FirstOrDefault();
        var rate = live.FirstOrDefault(e => e.IsOnAir && e.Bitrate != null) ?? live.FirstOrDefault(e => e.Bitrate != null);
        return new(level, title, subtitle, longest?.DurationText, rate?.BitrateText);
    }

    private static string Join(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            2 => $"{list[0]} + {list[1]}",
            _ => string.Join(", ", list.Take(list.Count - 1)) + " + " + list[^1],
        };
    }
}
