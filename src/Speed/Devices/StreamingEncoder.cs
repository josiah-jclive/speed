using System.Net.Sockets;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Speed.Core;

namespace Speed.Devices;

public enum EncoderState { Offline, Idle, Connecting, OnAir, Interrupted }

/// <summary>
/// A Blackmagic Streaming Encoder HD or 4K, watched over its Ethernet protocol (TCP 9977).
/// The encoder pushes changes to its stream state and settings as they happen; duration,
/// bitrate and cache are polled once a second. JSPEED only watches: it never starts or
/// stops a stream. The active stream key is looked up in this encoder's key table to show
/// the stream's name.
/// </summary>
public sealed partial class StreamingEncoder : ObservableObject
{
    private const int Port = 9977;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private StreamWriter? _writer;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _lastData;
    private bool _wasOnline;
    private bool _quiet; // a short test connection from the Add/Edit window logs nothing
    private EncoderState _loggedState = EncoderState.Offline;

    public StreamingEncoder(EncoderConfig config) => Config = config;

    public EncoderConfig Config { get; private set; }
    public string Name => Config.Name;
    public string Address => Config.Address;

    /// <summary>Raised when something saved in settings changes.</summary>
    public event Action? ConfigChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnline), nameof(LinkText), nameof(State), nameof(IsLive), nameof(IsOnAir),
        nameof(IsWarning), nameof(AirWord), nameof(StateText), nameof(DurationText), nameof(BitrateText), nameof(CacheText))]
    private LinkState _link;

    /// <summary>The encoder's own word: Idle, Connecting, Streaming, Flushing or Interrupted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State), nameof(IsLive), nameof(IsOnAir), nameof(IsWarning), nameof(AirWord),
        nameof(StateText), nameof(DurationText), nameof(BitrateText), nameof(CacheText))]
    private string _status = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    private TimeSpan? _duration;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BitrateText))]
    private long? _bitrate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CacheText))]
    private int? _cacheUsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlatformText))]
    private string? _platform;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlatformText))]
    private string? _server;

    /// <summary>The stream key loaded in the encoder right now. Never written to the log.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchedName), nameof(KeyKnown), nameof(KeyTail), nameof(StreamName),
        nameof(PlatformText), nameof(IsWarning))]
    private string? _currentKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    private string? _model;

    public bool IsOnline => Link == LinkState.Online;

    public EncoderState State => !IsOnline ? EncoderState.Offline : Status.ToLowerInvariant() switch
    {
        "streaming" => EncoderState.OnAir,
        "connecting" or "flushing" => EncoderState.Connecting,
        "interrupted" => EncoderState.Interrupted,
        _ => EncoderState.Idle,
    };

    /// <summary>Streaming, or trying to: connecting and interrupted count as live.</summary>
    public bool IsLive => State is EncoderState.OnAir or EncoderState.Connecting or EncoderState.Interrupted;
    public bool IsOnAir => State == EncoderState.OnAir;

    /// <summary>Amber: live but connecting or interrupted, or live on a key that isn't in the table.</summary>
    public bool IsWarning => State is EncoderState.Connecting or EncoderState.Interrupted || (IsOnAir && !KeyKnown);

    public string AirWord => IsLive ? "ON AIR" : "OFF AIR";

    /// <summary>The name for the loaded key from this encoder's table, or null if it isn't there.</summary>
    public string? MatchedName
    {
        get
        {
            var key = CurrentKey;
            if (string.IsNullOrEmpty(key)) return null;
            return Config.Keys.FirstOrDefault(k => k.Key.Length > 0 && string.Equals(k.Key, key, StringComparison.Ordinal))?.Name;
        }
    }

    public bool KeyKnown => MatchedName != null;

    public string KeyTail => CurrentKey is { Length: > 0 } k ? "…" + k[^Math.Min(4, k.Length)..] : "none";

    public string StreamName => MatchedName ?? (string.IsNullOrEmpty(CurrentKey) ? "No stream key" : "Unknown stream key");

    public string LinkText => Link switch
    {
        LinkState.Online => "Connected",
        LinkState.Connecting => "Connecting",
        LinkState.NoReply => "Not responding",
        _ => "Offline",
    };

    public string StateText => State switch
    {
        EncoderState.OnAir => "On air",
        EncoderState.Connecting => "Connecting",
        EncoderState.Interrupted => "Interrupted",
        EncoderState.Idle => "Off air",
        _ => LinkText,
    };

    public string DetailText => $"{Model ?? "Streaming Encoder"} on {Address}";

    public string DurationText => IsLive && Duration is { } d ? $"{(int)d.TotalHours:00}:{d.Minutes:00}:{d.Seconds:00}" : "--:--:--";
    public string BitrateText => IsLive && Bitrate is { } b ? $"{b / 1_000_000.0:0.0} Mb/s" : "—";
    public string CacheText => IsLive && CacheUsed is { } c ? $"{c}%" : "—";

    public string PlatformText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Platform)) parts.Add(Platform!);
            if (!string.IsNullOrWhiteSpace(Server)) parts.Add($"{Server} server");
            if (!string.IsNullOrEmpty(CurrentKey)) parts.Add($"key {KeyTail}");
            return parts.Count == 0 ? "Stream settings not reported yet" : string.Join(" · ", parts);
        }
    }

    /// <summary>Applies edited settings: name, address and key table.</summary>
    public async Task ApplyAsync(EncoderConfig edited)
    {
        bool moved = !string.Equals(edited.Address, Config.Address, StringComparison.OrdinalIgnoreCase);
        Config.Name = edited.Name;
        Config.Address = edited.Address;
        Config.Keys = edited.Keys;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Address));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(MatchedName));
        OnPropertyChanged(nameof(KeyKnown));
        OnPropertyChanged(nameof(StreamName));
        OnPropertyChanged(nameof(IsWarning));
        ConfigChanged?.Invoke();
        if (moved)
        {
            await ShutdownAsync();
            Start();
        }
    }

    // ---------- Connection ----------

    public void Start()
    {
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task ShutdownAsync()
    {
        _cts?.Cancel();
        if (_loop != null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }
        _loop = null;
        Post(() => Link = LinkState.Offline);
    }

    private static void Post(Action action) => Dispatcher.UIThread.Post(action);
    private void LogInfo(string message) { if (!_quiet) AppLog.Info($"{Name}: {message}"); }
    private void LogWarn(string message) { if (!_quiet) AppLog.Warn($"{Name}: {message}"); }

    private (string Host, int Port) Endpoint()
    {
        var parts = Address.Trim().Split(':');
        if (parts.Length == 2 && int.TryParse(parts[1], out var p)) return (parts[0], p);
        return (Address.Trim(), Port);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        Post(() => Link = LinkState.Connecting);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (host, port) = Endpoint();
                using var client = new TcpClient { NoDelay = true };
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    await client.ConnectAsync(host, port, timeout.Token);
                }

                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
                Interlocked.Exchange(ref _lastData, Environment.TickCount64);

                using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var read = ReadLoopAsync(reader, session.Token);
                var poll = PollLoopAsync(session.Token);
                await Task.WhenAny(read, poll);
                session.Cancel();
                try { await Task.WhenAll(read, poll); } catch { }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // Not reachable: normal while the encoder is switched off, so not logged.
            }
            finally
            {
                _writer = null;
            }

            // Log a lost connection once, not on every retry while the encoder is off. Retries every
            // 3 seconds, indefinitely, so an encoder that's only on once a week is picked up again
            // within a few seconds of coming online.
            if (_wasOnline) LogWarn("Connection lost. JSPEED keeps trying to reconnect.");
            _wasOnline = false;
            Post(() => Link = LinkState.Offline);
            try { await Task.Delay(3000, ct); } catch { break; }
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await SendAsync("STREAM STATE:");
            var silent = Environment.TickCount64 - Interlocked.Read(ref _lastData) > 5000;
            if (silent) throw new IOException("The encoder stopped answering.");
            await Task.Delay(1000, ct);
        }
    }

    private async Task SendAsync(string header)
    {
        var writer = _writer ?? throw new IOException("Not connected.");
        await _writeLock.WaitAsync();
        try { await writer.WriteAsync(header + "\n\n"); }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoopAsync(StreamReader reader, CancellationToken ct)
    {
        var lines = new List<string>();
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct) ?? throw new IOException("The encoder closed the connection.");
            Interlocked.Exchange(ref _lastData, Environment.TickCount64);
            line = line.TrimEnd('\r');
            if (line.Length > 0)
            {
                lines.Add(line);
                continue;
            }
            if (lines.Count == 0) continue;
            HandleBlock(lines);
            lines.Clear();
        }
    }

    private void HandleBlock(List<string> lines)
    {
        var header = lines[0].Trim();
        var f = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines.Skip(1))
        {
            int i = l.IndexOf(':');
            if (i > 0) f[l[..i].Trim()] = l[(i + 1)..].Trim();
        }

        switch (header.ToUpperInvariant())
        {
            case "END PRELUDE:":
                MarkOnline();
                break;

            case "IDENTITY:":
                if (f.TryGetValue("Model", out var model)) Post(() => Model = model);
                break;

            case "STREAM SETTINGS:":
                f.TryGetValue("Current Platform", out var platform);
                f.TryGetValue("Current Server", out var server);
                bool hasKey = f.TryGetValue("Stream Key", out var key);
                Post(() =>
                {
                    if (platform != null) Platform = platform;
                    if (server != null) Server = server;
                    if (hasKey) CurrentKey = key;
                });
                break;

            case "STREAM STATE:":
                f.TryGetValue("Status", out var status);
                TimeSpan? duration = f.TryGetValue("Duration", out var d) ? ParseDuration(d) : null;
                long? bitrate = f.TryGetValue("Bitrate", out var b) && long.TryParse(b, out var bv) ? bv : null;
                int? cache = f.TryGetValue("Cache Used", out var c) && int.TryParse(new string(c.Where(char.IsDigit).ToArray()), out var cv) ? cv : null;
                Post(() =>
                {
                    if (status != null) Status = status;
                    if (duration != null) Duration = duration;
                    if (bitrate != null) Bitrate = bitrate;
                    if (cache != null) CacheUsed = cache;
                    LogStateChange();
                });
                MarkOnline(); // an answer to our poll also proves the link
                break;
        }
    }

    private void MarkOnline()
    {
        if (!_wasOnline)
        {
            _wasOnline = true;
            LogInfo($"Connected at {Address}.");
        }
        Post(() => { if (Link != LinkState.Online) Link = LinkState.Online; });
    }

    /// <summary>Duration comes as DD:HH:MM:SS (or HH:MM:SS).</summary>
    private static TimeSpan? ParseDuration(string text)
    {
        var parts = text.Split(':');
        if (!parts.All(p => int.TryParse(p, out _))) return null;
        var n = parts.Select(int.Parse).ToArray();
        return n.Length switch
        {
            4 => new TimeSpan(n[0], n[1], n[2], n[3]),
            3 => new TimeSpan(n[0], n[1], n[2]),
            _ => null,
        };
    }

    /// <summary>Notes going on and off air in the activity log. Runs on the UI thread.</summary>
    private void LogStateChange()
    {
        var state = State;
        if (state == _loggedState) return;
        var before = _loggedState;
        _loggedState = state;
        switch (state)
        {
            case EncoderState.OnAir when KeyKnown:
                LogInfo($"On air: {StreamName}.");
                break;
            case EncoderState.OnAir:
                LogWarn($"On air with a stream key that isn't in its list (key {KeyTail}).");
                break;
            case EncoderState.Interrupted:
                LogWarn($"Stream interrupted: {StreamName}.");
                break;
            case EncoderState.Idle when before is EncoderState.OnAir or EncoderState.Interrupted:
                LogInfo($"Off air: {StreamName}.");
                break;
        }
    }

    /// <summary>
    /// Connects briefly to read the encoder's model and current stream key, for the
    /// Add/Edit window. Returns nulls if the encoder doesn't answer within the timeout.
    /// </summary>
    public static async Task<(string? Model, string? Key, bool Connected)> ProbeAsync(string address, TimeSpan timeout)
    {
        var probe = new StreamingEncoder(new EncoderConfig { Name = "Test", Address = address }) { _quiet = true };
        var deadline = DateTime.UtcNow + timeout;
        probe.Start();
        try
        {
            while (DateTime.UtcNow < deadline && !(probe.IsOnline && probe.CurrentKey != null))
                await Task.Delay(150);
            return (probe.Model, probe.CurrentKey, probe.IsOnline);
        }
        finally
        {
            await probe.ShutdownAsync();
        }
    }
}
