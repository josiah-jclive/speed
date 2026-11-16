using System.Runtime.InteropServices;
using System.Text;
using Speed.Core;

namespace Speed.Output;

public sealed record DeckLinkMode(string Name, int Width, int Height, long FrameDuration, long TimeScale, uint Id)
{
    public double FramesPerSecond => FrameDuration > 0 ? (double)TimeScale / FrameDuration : 0;
    public override string ToString() => Name;
}

public sealed record DeckLinkDevice(int Index, string Name, IReadOnlyList<DeckLinkMode> Modes)
{
    public override string ToString() => Name;
}

/// <summary>Talks to the native speed_decklink bridge shipped next to the app.</summary>
public static partial class DeckLinkOutput
{
    private const string Lib = "speed_decklink";

    [LibraryImport(Lib)] private static partial int spd_init();
    [LibraryImport(Lib)] private static partial int spd_device_name(int device, byte[] buffer, int length);
    [LibraryImport(Lib)] private static partial int spd_mode_count(int device);
    [LibraryImport(Lib)] private static partial int spd_mode_info(int device, int mode, byte[] name, int nameLength,
        out int width, out int height, out long frameDuration, out long timeScale, out uint modeId);
    [LibraryImport(Lib)] private static partial int spd_start(int device, uint modeId);
    [LibraryImport(Lib)] private static partial void spd_submit(byte[] bgra, int width, int height, int rowBytes);
    [LibraryImport(Lib)] private static partial void spd_stop();
    [LibraryImport(Lib)] private static partial long spd_late_frames();
    [LibraryImport(Lib)] private static partial void spd_shutdown();

    public static bool IsRunning { get; private set; }
    public static DeckLinkMode? CurrentMode { get; private set; }

    /// <summary>Why output isn't available, or null when the bridge and driver are present.</summary>
    public static string? Unavailable { get; private set; }

    public static IReadOnlyList<DeckLinkDevice> Discover()
    {
        try
        {
            int count = spd_init();
            if (count < 0)
            {
                Unavailable = "Install Blackmagic Desktop Video to use UltraStudio output.";
                return Array.Empty<DeckLinkDevice>();
            }
            Unavailable = count == 0 ? "No UltraStudio or DeckLink device found." : null;
            var devices = new List<DeckLinkDevice>();
            for (int d = 0; d < count; d++)
            {
                var nameBuf = new byte[256];
                spd_device_name(d, nameBuf, nameBuf.Length);
                var modes = new List<DeckLinkMode>();
                int modeCount = spd_mode_count(d);
                for (int m = 0; m < modeCount; m++)
                {
                    var modeBuf = new byte[128];
                    if (spd_mode_info(d, m, modeBuf, modeBuf.Length, out var w, out var h, out var dur, out var scale, out var id) != 0)
                        modes.Add(new DeckLinkMode(Text(modeBuf), w, h, dur, scale, id));
                }
                devices.Add(new DeckLinkDevice(d, Text(nameBuf), modes));
            }
            return devices;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Unavailable = "This build doesn't include the UltraStudio output bridge.";
            return Array.Empty<DeckLinkDevice>();
        }
    }

    public static bool Start(DeckLinkDevice device, DeckLinkMode mode)
    {
        Stop();
        int result = spd_start(device.Index, mode.Id);
        if (result != 0)
        {
            var why = result switch
            {
                3 => "the device wouldn't accept that video format",
                4 => "it couldn't allocate video frames",
                5 => "playback wouldn't start",
                _ => $"error {result}",
            };
            AppLog.Warn($"Output didn't start on {device.Name}: {why}.");
            return false;
        }
        IsRunning = true;
        CurrentMode = mode;
        AppLog.Info($"Output started on {device.Name}, {mode.Name}.");
        return true;
    }

    public static void Submit(byte[] bgra, int width, int height)
    {
        if (IsRunning) spd_submit(bgra, width, height, width * 4);
    }

    public static long LateFrames => IsRunning ? spd_late_frames() : 0;

    public static void Stop()
    {
        if (!IsRunning) return;
        spd_stop();
        IsRunning = false;
        CurrentMode = null;
        AppLog.Info("Output stopped.");
    }

    public static void Shutdown()
    {
        try
        {
            Stop();
            if (Unavailable == null) spd_shutdown();
        }
        catch { }
    }

    private static string Text(byte[] buffer)
    {
        int end = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }
}
