using System.Collections.Concurrent;
using System.IO.Ports;
using Speed.Core;

namespace Speed.Devices;

/// <summary>
/// An Atomos Shogun on a USB to RS-422 adapter, driven with Sony 9-pin.
/// One request is in flight at a time: user commands first, otherwise
/// alternating timecode and status polls.
/// </summary>
public sealed class ShogunRecorder : Recorder
{
    private readonly ConcurrentQueue<(Sony9PinCommand Command, TaskCompletionSource<bool> Done)> _queue = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _online;

    public ShogunRecorder(RecorderConfig config) : base(config) { }

    public override string ModelName => "Shogun";
    public override bool ReportsMedia => false;
    public override bool ReportsCodec => false;

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
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Factory.StartNew(() => Run(token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public override async Task ShutdownAsync()
    {
        _cts?.Cancel();
        if (_loop != null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }
        _loop = null;
    }

    public override Task<bool> RecordAsync() => Enqueue(Sony9Pin.Record);
    public override Task<bool> StopAsync() => Enqueue(Sony9Pin.Stop);

    public override string FormatUnavailableReason =>
        "Shoguns don't accept format commands over RS-422. Format it on the unit.";

    private Task<bool> Enqueue(Sony9PinCommand command)
    {
        if (!_online)
        {
            LogWarn($"{command.Name} wasn't sent because the Shogun isn't responding.");
            return Task.FromResult(false);
        }
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue((command, done));
        return done.Task;
    }

    private void Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            SerialPort? port = null;
            try
            {
                Post(() => Link = LinkState.Connecting);
                port = new SerialPort(Address, 38400, Parity.Odd, 8, StopBits.One)
                {
                    Handshake = Handshake.None,
                    ReadTimeout = 40,
                    WriteTimeout = 200,
                };
                port.Open();
                LogInfo($"Opened {Address}.");
                DrainQueue(false); // never fire a command queued while the link was down

                Exchange(port, Sony9Pin.DeviceType);
                var lastReply = Environment.TickCount64;
                var pollTimecode = true;

                while (!ct.IsCancellationRequested)
                {
                    Sony9PinCommand command;
                    TaskCompletionSource<bool>? done = null;
                    if (_queue.TryDequeue(out var item)) (command, done) = item;
                    else
                    {
                        command = pollTimecode ? Sony9Pin.TimeSense : Sony9Pin.StatusSense;
                        pollTimecode = !pollTimecode;
                    }

                    var reply = Exchange(port, command);
                    if (reply != null)
                    {
                        lastReply = Environment.TickCount64;
                        Handle(command, reply, done);
                    }
                    else if (done != null)
                    {
                        LogWarn($"No reply to {command.Name}.");
                        done.TrySetResult(false);
                    }

                    var online = Environment.TickCount64 - lastReply < 800;
                    if (online != _online)
                    {
                        _online = online;
                        if (online) LogInfo("Responding.");
                        else LogWarn("Stopped responding. Check the cable and that remote control is on.");
                        Post(() => Link = online ? LinkState.Online : LinkState.NoReply);
                    }
                    Thread.Sleep(8);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                LogWarn($"Lost {Address}: {ex.Message}");
            }
            finally
            {
                try { port?.Dispose(); } catch { }
            }

            _online = false;
            DrainQueue(false);
            Post(() => Link = LinkState.Offline);
            ct.WaitHandle.WaitOne(3000);
        }
        _online = false;
        Post(() => Link = LinkState.Offline);
    }

    private void DrainQueue(bool result)
    {
        while (_queue.TryDequeue(out var item)) item.Done.TrySetResult(result);
    }

    private void Handle(Sony9PinCommand command, Sony9PinReply reply, TaskCompletionSource<bool>? done)
    {
        switch (reply.Kind)
        {
            case ReplyKind.Ack:
                done?.TrySetResult(true);
                break;
            case ReplyKind.Nak:
                var why = Sony9Pin.DescribeNak(reply.Data.Length > 0 ? reply.Data[0] : (byte)0);
                LogWarn($"Rejected {command.Name}: {why}.");
                done?.TrySetResult(false);
                break;
            case ReplyKind.Status:
                var status = new DeckStatus(reply.Data);
                Post(() =>
                {
                    Transport = status.Transport;
                    Warning = status.IsLocal ? "Remote control is off on the Shogun" : null;
                    if (status.IsRecording) FailedToStart = false;
                });
                done?.TrySetResult(true);
                break;
            case ReplyKind.Timecode:
                var tc = Core.Timecode.FromBcd(reply.Data);
                Post(() => Timecode = tc);
                done?.TrySetResult(true);
                break;
            default:
                done?.TrySetResult(true);
                break;
        }
    }

    private static Sony9PinReply? Exchange(SerialPort port, Sony9PinCommand command)
    {
        port.DiscardInBuffer();
        var bytes = command.ToBytes();
        port.Write(bytes, 0, bytes.Length);

        var buffer = new List<byte>(16);
        var deadline = Environment.TickCount64 + 120;
        while (Environment.TickCount64 < deadline)
        {
            int b;
            try { b = port.ReadByte(); }
            catch (TimeoutException) { continue; }
            if (b < 0) break;
            buffer.Add((byte)b);

            int length = Sony9Pin.FrameLength(buffer[0]);
            if (buffer.Count < length) continue;

            var frame = buffer.GetRange(0, length).ToArray();
            if (Sony9Pin.Checksum(frame.AsSpan(0, length - 1)) != frame[length - 1])
                return null;
            return Sony9Pin.Decode(frame);
        }
        return null;
    }
}
