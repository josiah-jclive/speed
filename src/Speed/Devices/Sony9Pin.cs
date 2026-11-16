using Speed.Core;

namespace Speed.Devices;

public sealed record Sony9PinCommand(byte Cmd1, byte Cmd2, byte[] Data, string Name, bool IsPoll = false)
{
    /// <summary>CMD-1 high nibble is the group, low nibble the data count; the last byte is the checksum.</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[3 + Data.Length];
        bytes[0] = (byte)((Cmd1 & 0xF0) | (Data.Length & 0x0F));
        bytes[1] = Cmd2;
        Data.CopyTo(bytes, 2);
        bytes[^1] = Sony9Pin.Checksum(bytes.AsSpan(0, bytes.Length - 1));
        return bytes;
    }
}

public enum ReplyKind { Ack, Nak, DeviceType, Status, Timecode, Other }

public sealed record Sony9PinReply(ReplyKind Kind, byte[] Data);

/// <summary>Sony 9-pin (BVW) deck control: 38400 baud, 8 data bits, odd parity, 1 stop bit.</summary>
public static class Sony9Pin
{
    public static byte Checksum(ReadOnlySpan<byte> bytes)
    {
        int sum = 0;
        foreach (var b in bytes) sum += b;
        return (byte)sum;
    }

    public static readonly Sony9PinCommand Stop = new(0x20, 0x00, [], "Stop");
    public static readonly Sony9PinCommand Record = new(0x20, 0x02, [], "Record");
    public static readonly Sony9PinCommand DeviceType = new(0x00, 0x11, [], "Device type request");
    public static readonly Sony9PinCommand StatusSense = new(0x60, 0x20, [0x0A], "Status sense", true);
    public static readonly Sony9PinCommand TimeSense = new(0x60, 0x0C, [0x01], "Current time sense", true);

    /// <summary>Total length of a frame given its first byte.</summary>
    public static int FrameLength(byte first) => 3 + (first & 0x0F);

    public static Sony9PinReply Decode(ReadOnlySpan<byte> frame)
    {
        int group = frame[0] & 0xF0;
        byte cmd2 = frame[1];
        var data = frame[2..^1].ToArray();
        return (group, cmd2) switch
        {
            (0x10, 0x01) => new(ReplyKind.Ack, data),
            (0x10, 0x12) => new(ReplyKind.Nak, data),
            (0x10, 0x11) => new(ReplyKind.DeviceType, data),
            (0x70, 0x20) => new(ReplyKind.Status, data),
            (0x70, 0x00 or 0x01 or 0x04 or 0x06 or 0x14 or 0x16) when data.Length >= 4 => new(ReplyKind.Timecode, data),
            _ => new(ReplyKind.Other, data),
        };
    }

    public static string DescribeNak(byte bits)
    {
        var reasons = new List<string>();
        if ((bits & 0x01) != 0) reasons.Add("unknown command");
        if ((bits & 0x04) != 0) reasons.Add("checksum error");
        if ((bits & 0x10) != 0) reasons.Add("parity error");
        if ((bits & 0x20) != 0) reasons.Add("buffer overrun");
        if ((bits & 0x40) != 0) reasons.Add("framing error");
        if ((bits & 0x80) != 0) reasons.Add("timeout");
        return reasons.Count == 0 ? $"0x{bits:X2}" : string.Join(", ", reasons);
    }
}

/// <summary>The BVW-style status bytes returned by Status Sense.</summary>
public readonly record struct DeckStatus(byte[] Raw)
{
    private bool Bit(int b, int bit) => Raw.Length > b && (Raw[b] & (1 << bit)) != 0;
    public bool IsLocal => Bit(0, 0);
    public bool IsPlaying => Bit(1, 0);
    public bool IsRecording => Bit(1, 1);
    public bool IsFastForward => Bit(1, 2);
    public bool IsRewinding => Bit(1, 3);
    public bool IsStopped => Bit(1, 5);

    public TransportState Transport =>
        IsRecording ? TransportState.Recording :
        IsPlaying ? TransportState.Playing :
        IsFastForward || IsRewinding ? TransportState.Other :
        TransportState.Stopped;
}
