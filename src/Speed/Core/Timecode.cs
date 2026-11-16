namespace Speed.Core;

public readonly record struct Timecode(int Hours, int Minutes, int Seconds, int Frames, bool DropFrame = false)
{
    public override string ToString() =>
        $"{Hours:00}:{Minutes:00}:{Seconds:00}{(DropFrame ? ';' : ':')}{Frames:00}";

    /// <summary>Accepts 01:02:03:04, 01:02:03;04 or 01.02.03.04, with surrounding text ignored.</summary>
    public static bool TryParse(string? text, out Timecode timecode)
    {
        timecode = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var match = System.Text.RegularExpressions.Regex.Match(text, @"(\d{1,2})[:.](\d{2})[:.](\d{2})([:;.])(\d{2})");
        if (!match.Success) return false;
        int h = int.Parse(match.Groups[1].Value), m = int.Parse(match.Groups[2].Value);
        int s = int.Parse(match.Groups[3].Value), f = int.Parse(match.Groups[5].Value);
        if (h > 23 || m > 59 || s > 59) return false;
        timecode = new Timecode(h, m, s, f, match.Groups[4].Value == ";");
        return true;
    }

    /// <summary>Sony 9-pin order: frames, seconds, minutes, hours, each BCD.</summary>
    public static Timecode FromBcd(ReadOnlySpan<byte> b) => new(
        FromBcd((byte)(b[3] & 0x3F)), FromBcd((byte)(b[2] & 0x7F)),
        FromBcd((byte)(b[1] & 0x7F)), FromBcd((byte)(b[0] & 0x3F)), (b[0] & 0x40) != 0);

    private static int FromBcd(byte v) => (v >> 4) * 10 + (v & 0x0F);
}
