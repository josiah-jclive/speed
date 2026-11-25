using Speed.Devices;

namespace Speed.Core;

public enum StreamLevel { Off, OnAir, Warning }

/// <summary>
/// One ON AIR bar on the program output. Every encoder gets its own bar, so a primary and a
/// backup on the same stream key show as two bars and either one dropping is visible.
/// </summary>
public sealed record StreamSummary(StreamLevel Level, string Title, string Subtitle, string? Duration, string? Bitrate)
{
    public bool IsLive => Level != StreamLevel.Off;

    /// <summary>One bar per encoder, in the order they're listed. Empty when there are no encoders.</summary>
    public static IReadOnlyList<StreamSummary> Compute(IReadOnlyList<StreamingEncoder> encoders) =>
        encoders.Select(For).ToList();

    public static StreamSummary For(StreamingEncoder e)
    {
        if (!e.IsOnline)
            return new(StreamLevel.Off, "Not connected", e.Name, null, null);

        if (!e.IsLive)
            return new(StreamLevel.Off, e.StreamName, $"{e.Name}: ready", null, null);

        var subtitle =
            e.State == EncoderState.Interrupted ? $"{e.Name}: interrupted, reconnecting" :
            e.State == EncoderState.Connecting ? $"{e.Name}: connecting" :
            !e.KeyKnown ? $"{e.Name}: key ending {e.KeyTail} isn't in its list" :
            e.Name;
        var level = e.IsWarning ? StreamLevel.Warning : StreamLevel.OnAir;
        return new(level, e.StreamName, subtitle,
            e.Duration != null ? e.DurationText : null,
            e.Bitrate != null ? e.BitrateText : null);
    }
}
