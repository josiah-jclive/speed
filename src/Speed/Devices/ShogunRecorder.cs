using System.IO.Ports;
using Speed.Core;

namespace Speed.Devices;

/// <summary>
/// One record channel of an Atomos Shogun on a USB to RS-422 adapter. A Shogun can record
/// 1 to 4 channels over one cable; each channel is its own card and output tile, and all
/// the channels on a serial port share one <see cref="ShogunPort"/>, so they start, stop
/// and report status and timecode together.
/// </summary>
public sealed class ShogunRecorder : Recorder
{
    private ShogunPort? _port;

    public ShogunRecorder(RecorderConfig config) : base(config) { }

    public override string ModelName => "Shogun";
    public override bool ReportsMedia => false;
    public override bool ReportsCodec => false;

    public override string Detail =>
        Config.Channel > 0 ? $"Shogun channel {Config.Channel} on {Address}" : $"Shogun on {Address}";

    public static string[] AvailablePorts()
    {
        var names = SerialPort.GetPortNames();
        // On macOS, open the call-out device (cu.*) rather than the dial-in one (tty.*).
        if (OperatingSystem.IsMacOS())
            names = names.Where(n => n.Contains("/cu.") && !n.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)).ToArray();
        return names.OrderBy(n => n).ToArray();
    }

    public override void Start()
    {
        if (_port != null) return;
        _port = ShogunPort.Acquire(Address, this);
        _port.Changed += Sync;
        Sync();
    }

    public override async Task ShutdownAsync()
    {
        var port = _port;
        if (port == null) return;
        _port = null;
        port.Changed -= Sync;
        await port.ReleaseAsync(this);
        Post(() => Link = LinkState.Offline);
    }

    // Channels on one cable share a single Record or Stop; the port sends it to the unit once.
    public override Task<bool> RecordAsync() => _port?.Send(Sony9Pin.Record) ?? Task.FromResult(false);
    public override Task<bool> StopAsync() => _port?.Send(Sony9Pin.Stop) ?? Task.FromResult(false);

    public override string FormatUnavailableReason =>
        "Shoguns don't accept format commands over RS-422. Format it on the unit.";

    /// <summary>Copies the shared connection's state onto this channel. Runs on the UI thread.</summary>
    private void Sync()
    {
        if (_port is not { } p) return;
        Link = p.Link;
        Transport = p.Transport;
        Timecode = p.Timecode;
        Warning = p.Warning;
        if (Transport == TransportState.Recording) FailedToStart = false;
    }
}
