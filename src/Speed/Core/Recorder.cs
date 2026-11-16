using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Speed.Core;

public enum LinkState { Offline, Connecting, Online, NoReply }
public enum TransportState { Unknown, Stopped, Playing, Recording, Other }

/// <summary>One drive or slot that can be erased, as offered in the format flow.</summary>
public sealed record FormatTarget(int Slot, string Title, string Detail, string FreeText, string? VolumeName);

/// <summary>What step 2 of the format flow shows about a drive before erasing it.</summary>
public sealed record FormatDetails(string VolumeName, int? ClipCount);

/// <summary>
/// Base for every recorder. Device classes run their own background loops and report
/// changes through <see cref="Post"/>, so every property here changes on the UI thread.
/// </summary>
public abstract partial class Recorder : ObservableObject
{
    protected Recorder(RecorderConfig config) => Config = config;

    public RecorderConfig Config { get; }
    public abstract string ModelName { get; }
    public abstract bool ReportsMedia { get; }

    public string Name => Config.Name;
    public string Address => Config.Address;
    public virtual string Detail => $"{ModelName} on {Address}";

    public bool IncludeInGroup
    {
        get => Config.IncludeInGroup;
        set
        {
            if (Config.IncludeInGroup == value) return;
            Config.IncludeInGroup = value;
            OnPropertyChanged();
            ConfigChanged?.Invoke();
        }
    }

    /// <summary>Raised when something that is saved in settings changes.</summary>
    public event Action? ConfigChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnline), nameof(IsRecording), nameof(LinkText), nameof(StateText), nameof(TimecodeText), nameof(FormatText))]
    private LinkState _link;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecording), nameof(StateText))]
    private TransportState _transport;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimecodeText))]
    private Timecode? _timecode;

    /// <summary>Record time left on the active media, when the device reports it as time.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MediaText), nameof(MediaFraction), nameof(HasMediaBar), nameof(MediaBarValue))]
    private TimeSpan? _mediaTimeLeft;

    /// <summary>Free space on the active media in percent, when the device reports it that way.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MediaText), nameof(MediaFraction), nameof(HasMediaBar), nameof(MediaBarValue))]
    private double? _mediaPercentFree;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MediaText))]
    private string _mediaLabel = "Media";

    /// <summary>Recording codec as the device reports it, e.g. "ProRes 422 HQ".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormatText))]
    private string? _codec;

    /// <summary>File type, e.g. ".mov" or ".mxf".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormatText))]
    private string? _container;

    /// <summary>Video standard, e.g. "1080p29.97".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormatText))]
    private string? _videoFormat;

    /// <summary>Set by the app from the warning thresholds in settings.</summary>
    [ObservableProperty]
    private bool _mediaLow;

    /// <summary>Set when Record all was sent but this recorder hadn't started by the check.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private bool _failedToStart;

    /// <summary>A short device-reported problem such as remote control being disabled.</summary>
    [ObservableProperty]
    private string? _warning;

    public bool IsOnline => Link == LinkState.Online;
    public bool IsRecording => IsOnline && Transport == TransportState.Recording;

    public string TimecodeText => IsOnline && Timecode is { } tc ? tc.ToString() : "--:--:--:--";

    public string LinkText => Link switch
    {
        LinkState.Online => "Connected",
        LinkState.Connecting => "Connecting",
        LinkState.NoReply => "Not responding",
        _ => "Offline",
    };

    public string StateText
    {
        get
        {
            if (!IsOnline) return LinkText;
            if (FailedToStart && Transport != TransportState.Recording) return "Not recording";
            return Transport switch
            {
                TransportState.Recording => "Recording",
                TransportState.Playing => "Playing",
                TransportState.Stopped => "Stopped",
                TransportState.Other => "Busy",
                _ => "Unknown",
            };
        }
    }

    public string MediaText
    {
        get
        {
            if (!ReportsMedia) return $"Not reported over {(this is Devices.ShogunRecorder ? "RS-422" : "this connection")}";
            if (MediaTimeLeft is { } left) return left <= TimeSpan.Zero ? "Full" : FormatDuration(left) + " left";
            if (MediaPercentFree is { } pct) return pct <= 0 ? "Full, 0% free" : $"{pct:0}% free";
            return "No media";
        }
    }

    /// <summary>Whether this device's control protocol reports its codec.</summary>
    public virtual bool ReportsCodec => true;

    /// <summary>"ProRes 422 HQ, .mov, 1080p29.97", or as much of it as the device reports.</summary>
    public string FormatText
    {
        get
        {
            if (!ReportsCodec) return "Not reported over RS-422";
            var parts = new[] { Codec, Container, VideoFormat }.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            return parts.Count == 0 ? (IsOnline ? "Not reported" : "Unknown") : string.Join(", ", parts);
        }
    }

    /// <summary>Short form for the program output: codec and file type only.</summary>
    public string? CodecShort =>
        ReportsCodec && !string.IsNullOrWhiteSpace(Codec)
            ? (string.IsNullOrWhiteSpace(Container) ? Codec : $"{Codec} {Container}")
            : null;

    /// <summary>0 to 1 for the fill bar, or null when there's no honest fraction to draw.</summary>
    public double? MediaFraction => MediaPercentFree is { } pct ? Math.Clamp(pct / 100.0, 0, 1) : null;
    public bool HasMediaBar => MediaFraction.HasValue;
    public double MediaBarValue => MediaFraction ?? 0;

    public static string FormatDuration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m";
        return $"{Math.Max(0, (int)t.TotalSeconds)}s";
    }

    // Lifecycle and control, implemented per device.
    public abstract void Start();
    public abstract Task ShutdownAsync();
    public abstract Task<bool> RecordAsync();
    public abstract Task<bool> StopAsync();

    // Formatting. Only devices whose control protocol supports it override these.
    public virtual bool CanFormat => false;
    public virtual string FormatUnavailableReason => "This recorder can't be formatted remotely.";
    public virtual Task<IReadOnlyList<FormatTarget>> GetFormatTargetsAsync() =>
        Task.FromResult<IReadOnlyList<FormatTarget>>(Array.Empty<FormatTarget>());
    public virtual Task<FormatDetails?> GetFormatDetailsAsync(FormatTarget target) =>
        Task.FromResult<FormatDetails?>(null);
    public virtual Task FormatAsync(FormatTarget target, string fileSystem) =>
        throw new NotSupportedException(FormatUnavailableReason);
    public virtual IReadOnlyList<string> FileSystems => Array.Empty<string>();

    protected static void Post(Action action) => Dispatcher.UIThread.Post(action);

    protected void LogInfo(string message) => AppLog.Info($"{Name}: {message}");
    protected void LogWarn(string message) => AppLog.Warn($"{Name}: {message}");
}
