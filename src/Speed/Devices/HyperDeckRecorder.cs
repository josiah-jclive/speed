using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Speed.Core;

namespace Speed.Devices;

/// <summary>A reply from the HyperDeck Ethernet Protocol (TCP 9993).</summary>
public sealed record HyperDeckResponse(int Code, string Text, IReadOnlyDictionary<string, string> Fields, IReadOnlyList<string> Lines)
{
    public bool IsSuccess => Code is >= 200 and < 300;
    public string? Get(string key) => Fields.TryGetValue(key, out var v) ? v : null;
}

/// <summary>
/// A Blackmagic HyperDeck. Commands are answered in order, so each request waits on a
/// queue of pending replies; 5xx asynchronous notifications are handled as they arrive.
/// </summary>
public sealed class HyperDeckRecorder : Recorder
{
    private const int DefaultPort = 9993;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentQueue<TaskCompletionSource<HyperDeckResponse>> _pending = new();
    private StreamWriter? _writer;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _lastReply;
    private int _activeSlot = -1;

    public HyperDeckRecorder(RecorderConfig config) : base(config) { }

    public override string ModelName => "HyperDeck";
    public override bool ReportsMedia => true;

    public override void Start()
    {
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
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

    public override async Task<bool> RecordAsync() => await SimpleAsync("record");
    public override async Task<bool> StopAsync() => await SimpleAsync("stop");

    private async Task<bool> SimpleAsync(string command)
    {
        if (!IsConnected)
        {
            LogWarn($"\"{command}\" wasn't sent because the HyperDeck isn't connected.");
            return false;
        }
        try
        {
            var r = await SendAsync(command);
            if (!r.IsSuccess) LogWarn($"Rejected \"{command}\": {r.Code} {r.Text}.");
            return r.IsSuccess;
        }
        catch (Exception ex)
        {
            LogWarn($"\"{command}\" failed: {ex.Message}");
            return false;
        }
    }

    private bool IsConnected => _writer != null;

    // ---------- Connection ----------

    private (string Host, int Port) Endpoint()
    {
        var parts = Address.Trim().Split(':');
        if (parts.Length == 2 && int.TryParse(parts[1], out var p)) return (parts[0], p);
        return (Address.Trim(), DefaultPort);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Post(() => Link = LinkState.Connecting);
                var (host, port) = Endpoint();
                using var client = new TcpClient { NoDelay = true };
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    await client.ConnectAsync(host, port, timeout.Token);
                }

                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
                _lastReply = Environment.TickCount64;
                LogInfo($"Connected at {host}.");

                using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var readTask = ReadLoopAsync(reader, session.Token);
                var setupAndPoll = SetupAndPollAsync(session.Token);
                await Task.WhenAny(readTask, setupAndPoll);
                session.Cancel();
                try { await Task.WhenAll(readTask, setupAndPoll); } catch { }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogWarn($"Connection problem: {ex.Message}");
            }
            finally
            {
                _writer = null;
                FailPending();
            }

            Post(() => Link = LinkState.Offline);
            try { await Task.Delay(3000, ct); } catch { break; }
        }
        Post(() => Link = LinkState.Offline);
    }

    private async Task SetupAndPollAsync(CancellationToken ct)
    {
        // Notifications keep transport changes immediate; the display timecode one only
        // exists on newer firmware, so a rejection there is expected and ignored.
        await TrySendAsync("remote: enable: true");
        await TrySendAsync("notify: transport: true");
        await TrySendAsync("notify: slot: true");
        await TrySendAsync("notify: display timecode: true");
        await TrySendAsync("notify: configuration: true");
        Post(() => Link = LinkState.Online);

        var tick = 0;
        while (!ct.IsCancellationRequested)
        {
            var t = await TrySendAsync("transport info");
            if (t != null) ApplyTransport(t.Fields);
            if (tick % 8 == 0)
            {
                var s = await TrySendAsync("slot info");
                if (s != null) ApplySlot(s.Fields);
            }
            if (tick % 20 == 0)
            {
                var c = await TrySendAsync("configuration");
                if (c != null) ApplyConfiguration(c.Fields);
            }
            tick++;

            var silent = Environment.TickCount64 - Interlocked.Read(ref _lastReply) > 2000;
            Post(() => Link = silent ? LinkState.NoReply : LinkState.Online);
            await Task.Delay(250, ct);
        }
    }

    private async Task<HyperDeckResponse?> TrySendAsync(string command)
    {
        try { return await SendAsync(command); }
        catch { return null; }
    }

    private async Task<HyperDeckResponse> SendAsync(string command, int timeoutMs = 2000)
    {
        var writer = _writer ?? throw new IOException("Not connected.");
        var tcs = new TaskCompletionSource<HyperDeckResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _writeLock.WaitAsync();
        try
        {
            _pending.Enqueue(tcs);
            await writer.WriteLineAsync(command);
        }
        finally
        {
            _writeLock.Release();
        }
        var finished = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        if (finished != tcs.Task) throw new TimeoutException($"No reply to \"{command}\".");
        return await tcs.Task;
    }

    private void FailPending()
    {
        while (_pending.TryDequeue(out var tcs)) tcs.TrySetException(new IOException("Disconnected."));
    }

    // ---------- Reading ----------

    private async Task ReadLoopAsync(StreamReader reader, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct) ?? throw new IOException("The HyperDeck closed the connection.");
            if (line.Length < 3 || !int.TryParse(line.AsSpan(0, 3), out var code)) continue;

            var text = line.Length > 4 ? line[4..].Trim() : "";
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var lines = new List<string>();

            // A response whose first line ends with a colon carries a block of lines up to a blank line.
            if (text.EndsWith(':'))
            {
                text = text[..^1];
                while (true)
                {
                    var l = await reader.ReadLineAsync(ct) ?? throw new IOException("The HyperDeck closed the connection.");
                    if (l.Length == 0) break;
                    lines.Add(l);
                    var i = l.IndexOf(':');
                    if (i > 0) fields[l[..i].Trim()] = l[(i + 1)..].Trim();
                }
            }

            else if (code == 216)
            {
                // "216 format ready" is followed by the one-time format token on its own line.
                var token = await reader.ReadLineAsync(ct) ?? throw new IOException("The HyperDeck closed the connection.");
                lines.Add(token);
            }

            Interlocked.Exchange(ref _lastReply, Environment.TickCount64);
            var response = new HyperDeckResponse(code, text, fields, lines);

            if (code >= 500)
            {
                HandleNotification(response);
                continue;
            }
            if (_pending.TryDequeue(out var tcs)) tcs.TrySetResult(response);
        }
    }

    private void HandleNotification(HyperDeckResponse r)
    {
        if (r.Fields.Count == 0) return; // 500 connection info and similar
        if (r.Text.StartsWith("transport info", StringComparison.OrdinalIgnoreCase) ||
            r.Fields.ContainsKey("display timecode"))
            ApplyTransport(r.Fields);
        if (r.Text.StartsWith("slot info", StringComparison.OrdinalIgnoreCase))
            ApplySlot(r.Fields);
        if (r.Fields.ContainsKey("file format"))
            ApplyConfiguration(r.Fields);
    }

    private void ApplyTransport(IReadOnlyDictionary<string, string> f)
    {
        TransportState? state = null;
        if (f.TryGetValue("status", out var status))
        {
            state = status.ToLowerInvariant() switch
            {
                "record" => TransportState.Recording,
                "play" => TransportState.Playing,
                "stopped" or "preview" => TransportState.Stopped,
                _ => TransportState.Other,
            };
        }
        Timecode? tc = null;
        if ((f.TryGetValue("display timecode", out var d) && Core.Timecode.TryParse(d, out var parsed)) ||
            (f.TryGetValue("timecode", out d) && Core.Timecode.TryParse(d, out parsed)))
            tc = parsed;
        if (f.TryGetValue("slot id", out var slot) && int.TryParse(slot, out var s)) _activeSlot = s;
        string? video = f.TryGetValue("video format", out var vf) ? FormatNames.VideoFormat(vf) : null;
        if (video != null) Post(() => VideoFormat = video);

        if (state == null && tc == null) return;
        Post(() =>
        {
            if (state is { } st)
            {
                Transport = st;
                if (st == TransportState.Recording) FailedToStart = false;
            }
            if (tc is { } t) Timecode = t;
        });
    }

    private void ApplySlot(IReadOnlyDictionary<string, string> f)
    {
        // Async slot notifications may describe a slot other than the active one.
        if (f.TryGetValue("slot id", out var id) && int.TryParse(id, out var slot) && _activeSlot > 0 && slot != _activeSlot)
            return;

        var mounted = f.TryGetValue("status", out var status) && status.Equals("mounted", StringComparison.OrdinalIgnoreCase);
        TimeSpan? left = null;
        if (mounted && f.TryGetValue("recording time", out var secs) && double.TryParse(secs, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s))
            left = TimeSpan.FromSeconds(s);
        var label = f.TryGetValue("slot id", out var sid) ? $"Media, slot {sid}" : "Media";
        Post(() =>
        {
            MediaTimeLeft = left;
            MediaLabel = label;
        });
    }

    private void ApplyConfiguration(IReadOnlyDictionary<string, string> f)
    {
        if (!f.TryGetValue("file format", out var raw) || string.IsNullOrWhiteSpace(raw)) return;
        var (codec, container) = FormatNames.HyperDeckFileFormat(raw);
        Post(() =>
        {
            Codec = codec;
            Container = container;
        });
    }

    // ---------- Formatting ----------

    public override bool CanFormat => true;
    public override IReadOnlyList<string> FileSystems => ["exFAT", "HFS+"];

    public override async Task<IReadOnlyList<FormatTarget>> GetFormatTargetsAsync()
    {
        var targets = new List<FormatTarget>();
        if (!IsConnected) return targets;

        var slotCount = 2;
        var info = await TrySendAsync("device info");
        if (info?.Get("slot count") is { } sc && int.TryParse(sc, out var n) && n > 0) slotCount = n;

        for (var slot = 1; slot <= slotCount; slot++)
        {
            var r = await TrySendAsync($"slot info: slot id: {slot}");
            if (r == null || !"mounted".Equals(r.Get("status"), StringComparison.OrdinalIgnoreCase)) continue;
            var volume = r.Get("volume name");
            var free = double.TryParse(r.Get("recording time"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var secs)
                ? FormatDuration(TimeSpan.FromSeconds(secs)) + " free" : "";
            var detail = volume is { Length: > 0 } ? $"HyperDeck at {Address}, volume {volume}" : $"HyperDeck at {Address}";
            targets.Add(new FormatTarget(slot, $"{Name}, slot {slot}", detail, free, volume));
        }
        return targets;
    }

    public override async Task<FormatDetails?> GetFormatDetailsAsync(FormatTarget target)
    {
        var r = await TrySendAsync($"disk list: slot id: {target.Slot}");
        int? clips = null;
        if (r is { IsSuccess: true })
            clips = r.Lines.Count(l => l.Length > 0 && char.IsDigit(l[0]));
        return new FormatDetails(target.VolumeName ?? $"slot {target.Slot}", clips);
    }

    public override async Task FormatAsync(FormatTarget target, string fileSystem)
    {
        // Last-moment safety check, separate from the dialog's own.
        var t = await SendAsync("transport info");
        if ("record".Equals(t.Get("status"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{Name} is recording. Stop it before formatting.");

        // HyperDeck's own two-part format: prepare returns a one-time token that confirm must echo.
        var prepare = await SendAsync($"format: slot id: {target.Slot} prepare: {fileSystem}", 5000);
        if (prepare.Code != 216)
            throw new InvalidOperationException($"The HyperDeck refused to prepare the format ({prepare.Code} {prepare.Text}).");
        var token = prepare.Lines.FirstOrDefault(l => l.Trim().Length > 0)?.Trim();
        if (token != null && token.Contains(':')) token = token[(token.IndexOf(':') + 1)..].Trim();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("The HyperDeck didn't return a format token.");

        LogWarn($"Formatting slot {target.Slot} ({target.VolumeName}) as {fileSystem}.");
        var confirm = await SendAsync($"format: confirm: {token}", 60000);
        if (!confirm.IsSuccess)
            throw new InvalidOperationException($"The format didn't complete ({confirm.Code} {confirm.Text}).");
        LogInfo($"Slot {target.Slot} formatted.");
    }
}
