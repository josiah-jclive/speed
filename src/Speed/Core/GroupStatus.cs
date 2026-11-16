namespace Speed.Core;

/// <summary>Ready, recording or needs attention: drives the status pill, the output header and the app icon.</summary>
public enum StatusLevel { Idle, Ready, Recording, Attention }

public sealed record GroupStatus(string Headline, string Detail, StatusLevel Level, string? MediaWarning, int Recording, int Total)
{
    /// <summary>The icon state: amber wins over red, so a partial record never looks like a good one.</summary>
    public StatusLevel IconLevel => MediaWarning != null && Level == StatusLevel.Recording ? StatusLevel.Attention : Level;

    public static GroupStatus Compute(IEnumerable<Recorder> recorders)
    {
        var all = recorders.ToList();
        var group = all.Where(r => r.IncludeInGroup).ToList();
        int n = group.Count;
        int recording = group.Count(r => r.IsRecording);
        int offline = group.Count(r => !r.IsOnline);
        int failed = group.Count(r => r.FailedToStart && !r.IsRecording);
        int low = group.Count(r => r.MediaLow);
        string? mediaWarning = low > 0 ? $"{low} low on media" : null;

        if (n == 0)
            return new("No recorders", all.Count == 0 ? "Add a recorder to get started" : "None are in the record group",
                StatusLevel.Idle, null, 0, 0);

        if (recording == n)
        {
            var headline = n == 1 ? "Recording" : $"All {n} recording";
            return new(headline, mediaWarning ?? "Every recorder in the group is rolling", StatusLevel.Recording, mediaWarning, recording, n);
        }

        if (recording > 0)
        {
            var missing = group.Where(r => !r.IsRecording).Select(r => r.Name).ToList();
            var who = missing.Count <= 2 ? string.Join(" and ", missing) : $"{missing.Count} recorders";
            return new($"{recording} of {n} recording", $"{who} {(missing.Count == 1 ? "isn't" : "aren't")} recording",
                StatusLevel.Attention, mediaWarning, recording, n);
        }

        if (offline > 0)
        {
            var who = group.Where(r => !r.IsOnline).Select(r => r.Name).ToList();
            var detail = who.Count <= 2 ? $"{string.Join(" and ", who)} {(who.Count == 1 ? "isn't" : "aren't")} connected" : $"{who.Count} recorders aren't connected";
            return new($"{offline} offline", detail, StatusLevel.Attention, mediaWarning, 0, n);
        }

        if (failed > 0)
            return new("Didn't start", $"{failed} recorder{(failed == 1 ? "" : "s")} didn't start recording", StatusLevel.Attention, mediaWarning, 0, n);

        var ready = n == 1 ? "1 recorder online, in the record group" : $"{n} recorders online, all in the record group";
        return new("Ready", mediaWarning ?? ready, mediaWarning != null ? StatusLevel.Attention : StatusLevel.Ready, mediaWarning, 0, n);
    }
}
