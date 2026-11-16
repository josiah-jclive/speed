using System.Collections.Concurrent;
using System.IO.Ports;
using Avalonia.Threading;
using Speed.Core;

namespace Speed.Devices;

/// <summary>
/// One RS-422 connection to a Shogun, shared by every channel card on that serial port.
/// A Shogun with several record channels answers as one deck over Sony 9-pin, so its
/// channels start, stop and report status and timecode together.
/// One request is in flight at a time: user commands first, otherwise alternating
/// timecode and status polls.
/// </summary>
internal sealed class ShogunPort
{
    private static readonly Dictionary<string, ShogunPort> OpenPorts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object PortsLock = new();

    private readonly List<ShogunRecorder> _channels = new();
    private readonly ConcurrentQueue<(Sony9PinCommand Command, TaskCompletionSource<bool> Done)> _queue = new();
    private readonly object _sendLock = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _online;

    // The last command sent, so several channels asking for the same Record or Stop
    // at once (Record all) send it to the unit only once.
    private Task<bool>? _lastTask;
    private Sony9PinCommand? _lastCommand;
    private long _lastAt;

    private ShogunPort(string address) => Address = address;

    public string Address { get; }

    // State, changed only on the UI thread.
    public LinkState Link { get; private set; }
    public TransportState Transport { get; private set; }
    public Timecode? Timecode { get; private set; }
    public string? Warning { get; private set; }

    /// <summary>Raised on the UI thread when any state above changes.</summary>
    public event Action? Changed;

    /// <summary>Joins a channel to the port for its address, opening the port if it's the first.</summary>
    public static ShogunPort Acquire(string address, ShogunRecorder channel)
    {
        lock (PortsLock)
        {
            if (!OpenPorts.TryGetValue(address, out var port))
            {
                port = new ShogunPort(address);
                OpenPorts[address] = port;
            }
            port._channels.Add(channel);
            if (port._channels.Count == 1) port.StartLoop();
            return port;
        }
    }

    /// <summary>Removes a channel; the port closes when its last channel goes.</summary>
    public async Task ReleaseAsync(ShogunRecorder channel)
    {
        bool last;
        lock (PortsLock)
        {
            _channels.Remove(channel);
            last = _channels.Count == 0;
            if (last) OpenPorts.Remove(Address);
        }
        if (!last) return;
        _cts?.Cancel();
        if (_loop != null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }
        _loop = null;
    }

    /// <summary>Names of the channels on this port, for the activity log.</summary>
    private string Label
    {
        get
        {
            lock (PortsLock)
                return _channels.Count == 0 ? $"Shogun on {Address}" : string.Join(" + ", _channels.Select(c => c.Name));
        }
    }

    private void LogInfo(string message) => AppLog.Info($"{Label}: {message}");
    private void LogWarn(string message) => AppLog.Warn($"{Label}: {message}");

    private void Update(Action change) => Dispatcher.UIThread.Post(() =>
    {
        change();
        Changed?.Invoke();
    });

    public Task<bool> Send(Sony9PinCommand command)
    {
        lock (_sendLock)
        {
            var now = Environment.TickCount64;
            if (_lastTask != null && ReferenceEquals(_lastCommand, command) && now - _lastAt < 1000)
                return _lastTask;

            Task<bool> task;
            if (!_online)
            {
                LogWarn($"{command.Name} wasn't sent because the Shogun isn't responding.");
                task = Task.FromResult(false);
            }
            else
            {
                var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _queue.Enqueue((command, done));
                task = done.Task;
            }
            _lastTask = task;
            _lastCommand = command;
            _lastAt = now;
            return task;
        }
    }

    private void StartLoop()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Factory.StartNew(() => Run(token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            SerialPort? port = null;
            try
            {
                Update(() => Link = LinkState.Connecting);
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
                        Update(() => Link = online ? LinkState.Online : LinkState.NoReply);
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
            Update(() => Link = LinkState.Offline);
            ct.WaitHandle.WaitOne(3000);
        }
        _online = false;
        Update(() => Link = LinkState.Offline);
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
                Update(() =>
                {
                    Transport = status.Transport;
                    Warning = status.IsLocal ? "Remote control is off on the Shogun" : null;
                });
                done?.TrySetResult(true);
                break;
            case ReplyKind.Timecode:
                var tc = Core.Timecode.FromBcd(reply.Data);
                Update(() => Timecode = tc);
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
