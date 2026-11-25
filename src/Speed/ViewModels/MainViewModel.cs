using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Speed.Core;
using Speed.Devices;
using Speed.Output;

namespace Speed.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IOutputSource
{
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _take = new();
    private readonly OutputPump _pump = new();
    private int _takeRecorders;

    public MainViewModel()
    {
        Settings = AppSettings.Load();
        Recorders.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoRecorders));
        foreach (var config in Settings.Recorders) Attach(RecorderFactory.Create(config));
        Encoders.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasEncoders));
        foreach (var config in Settings.Encoders) AttachEncoder(new StreamingEncoder(config));
        _status = GroupStatus.Compute(Recorders);
        StartupRegistration.Apply(Settings.StartWithWindows);

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Refresh());
        _timer.Start();

        RefreshOutputDevices();
        if (Settings.StartOutputOnLaunch && SelectedDevice != null && SelectedMode != null) StartOutput();
    }

    public AppSettings Settings { get; }
    public ObservableCollection<Recorder> Recorders { get; } = new();
    public ObservableCollection<LogEntry> Log => AppLog.Entries;
    public bool HasNoRecorders => Recorders.Count == 0;
    public ObservableCollection<StreamingEncoder> Encoders { get; } = new();
    public bool HasEncoders => Encoders.Count > 0;

    // ---------- Group status ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordAllEnabled))]
    private bool _armed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecordingState), nameof(IsAttentionState), nameof(IsReadyState))]
    private GroupStatus _status;

    [ObservableProperty]
    private string _takeLengthText = "00:00:00";

    [ObservableProperty]
    private bool _takeRunning;

    public bool IsRecordingState => Status.Level == StatusLevel.Recording;
    public bool IsAttentionState => Status.Level == StatusLevel.Attention;
    public bool IsReadyState => Status.Level is StatusLevel.Ready or StatusLevel.Idle;

    public bool RecordAllEnabled => Armed || !Settings.RequireArm;
    public bool RequireArm => Settings.RequireArm;

    public string TitleName => string.IsNullOrWhiteSpace(Settings.ProductionName) ? "" : Settings.ProductionName;

    /// <summary>Raised when the icon state changes, so the window can swap its icon.</summary>
    public event Action<StatusLevel>? IconLevelChanged;
    private StatusLevel _iconLevel = (StatusLevel)(-1);

    // IOutputSource
    public string ProductionName => Settings.ProductionName;
    public bool ShowTakeLength => Settings.ShowTakeLength;
    public bool ShowTimeOfDay => Settings.ShowTimeOfDay;
    public bool ShowDate => Settings.ShowDate;
    public IReadOnlyList<Recorder> OutputRecorders => Recorders;
    public IReadOnlyList<StreamingEncoder> OutputEncoders => Encoders;

    private void Refresh()
    {
        foreach (var r in Recorders)
        {
            bool low = r.IsOnline && (
                (r.MediaTimeLeft is { } left && left.TotalMinutes < Settings.LowMediaMinutes) ||
                (r.MediaPercentFree is { } pct && pct < Settings.LowMediaPercent));
            if (r.MediaLow != low) r.MediaLow = low;
        }

        var status = GroupStatus.Compute(Recorders);
        if (status != Status) Status = status;

        // The take clock runs while anything in the group is recording.
        if (status.Recording > 0 && !_take.IsRunning)
        {
            _take.Restart();
            TakeRunning = true;
        }
        else if (status.Recording == 0 && _take.IsRunning)
        {
            _take.Stop();
            TakeRunning = false;
            AppLog.Info($"Take ended after {TakeLengthText}.");
        }
        if (status.Recording != _takeRecorders) _takeRecorders = status.Recording;
        var t = _take.Elapsed;
        TakeLengthText = $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

        if (status.IconLevel != _iconLevel)
        {
            _iconLevel = status.IconLevel;
            IconLevelChanged?.Invoke(_iconLevel);
        }

        UpdateOutputStatus();
    }

    // ---------- Recording ----------

    [RelayCommand]
    private void ToggleArm() => Armed = !Armed;

    [RelayCommand]
    private async Task RecordAll()
    {
        if (!RecordAllEnabled)
        {
            AppLog.Warn("Arm record first. Record all is ignored while disarmed.");
            return;
        }
        var group = Recorders.Where(r => r.IncludeInGroup).ToList();
        if (group.Count == 0)
        {
            AppLog.Warn("No recorders are in the record group.");
            return;
        }
        foreach (var r in group) r.FailedToStart = false;
        AppLog.Info($"Record all sent to {group.Count} recorder{(group.Count == 1 ? "" : "s")}.");
        await Task.WhenAll(group.Select(r => r.RecordAsync()));
        await CheckStartedAsync(group);
    }

    [RelayCommand]
    private async Task StopAll()
    {
        var group = Recorders.Where(r => r.IncludeInGroup).ToList();
        AppLog.Info($"Stop all sent to {group.Count} recorder{(group.Count == 1 ? "" : "s")}.");
        foreach (var r in group) r.FailedToStart = false;
        await Task.WhenAll(group.Select(r => r.StopAsync()));
        if (Settings.DisarmAfterStop) Armed = false;
    }

    [RelayCommand]
    private async Task RecordOne(Recorder recorder)
    {
        if (!RecordAllEnabled)
        {
            AppLog.Warn("Arm record first.");
            return;
        }
        recorder.FailedToStart = false;
        await recorder.RecordAsync();
        await CheckStartedAsync([recorder]);
    }

    [RelayCommand]
    private async Task StopOne(Recorder recorder)
    {
        recorder.FailedToStart = false;
        await recorder.StopAsync();
    }

    private async Task CheckStartedAsync(IReadOnlyList<Recorder> sent)
    {
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, Settings.StartCheckSeconds)));
        var missing = sent.Where(r => !r.IsRecording).ToList();
        foreach (var r in missing)
        {
            r.FailedToStart = true;
            var reason = !r.IsOnline ? "it isn't connected" : r.MediaText.StartsWith("Full") ? "its media is full" : "no reason was reported";
            AppLog.Warn($"{r.Name} didn't start recording within {Settings.StartCheckSeconds} seconds ({reason}).");
        }
        if (missing.Count == 0 && sent.Count > 1)
            AppLog.Info($"{(sent.Count == Recorders.Count(r => r.IncludeInGroup) ? "All" : sent.Count.ToString())} recorders are recording.");
    }

    // ---------- Recorder list ----------

    public void AddRecorder(RecorderConfig config)
    {
        Settings.Recorders.Add(config);
        Settings.Save();
        Attach(RecorderFactory.Create(config));
        AppLog.Info($"Added {config.Name}.");
    }

    [RelayCommand]
    private async Task RemoveRecorder(Recorder recorder)
    {
        Recorders.Remove(recorder);
        Settings.Recorders.Remove(recorder.Config);
        Settings.Save();
        await recorder.ShutdownAsync();
        AppLog.Info($"Removed {recorder.Name}.");
    }

    public void RenameRecorder(Recorder recorder, string name)
    {
        var old = recorder.Name;
        recorder.Name = name;
        if (recorder.Name != old) AppLog.Info($"Renamed {old} to {recorder.Name}.");
    }

    /// <summary>
    /// Moves a recorder in the list. The order is the order of the cards and of the
    /// rows or tiles on the program output, and it's saved.
    /// </summary>
    public void MoveRecorder(Recorder recorder, int to)
    {
        int from = Recorders.IndexOf(recorder);
        if (from < 0) return;
        to = Math.Clamp(to, 0, Recorders.Count - 1);
        if (from == to) return;
        Recorders.Move(from, to);
        Settings.Recorders.Clear();
        Settings.Recorders.AddRange(Recorders.Select(r => r.Config));
        Settings.Save();
    }

    private void Attach(Recorder recorder)
    {
        recorder.ConfigChanged += Settings.Save;
        Recorders.Add(recorder);
        recorder.Start();
    }

    // ---------- Encoders ----------

    public void AddEncoder(EncoderConfig config)
    {
        Settings.Encoders.Add(config);
        Settings.Save();
        AttachEncoder(new StreamingEncoder(config));
        AppLog.Info($"Added encoder {config.Name}.");
    }

    public async Task UpdateEncoderAsync(StreamingEncoder encoder, EncoderConfig edited)
    {
        await encoder.ApplyAsync(edited);
        Settings.Save();
        AppLog.Info($"Updated encoder {encoder.Name}: {edited.Keys.Count} stream key{(edited.Keys.Count == 1 ? "" : "s")}.");
    }

    public async Task RemoveEncoderAsync(StreamingEncoder encoder)
    {
        Encoders.Remove(encoder);
        Settings.Encoders.Remove(encoder.Config);
        Settings.Save();
        await encoder.ShutdownAsync();
        AppLog.Info($"Removed encoder {encoder.Name}.");
    }

    private void AttachEncoder(StreamingEncoder encoder)
    {
        encoder.ConfigChanged += Settings.Save;
        Encoders.Add(encoder);
        encoder.Start();
    }

    public void SaveSettings()
    {
        Settings.Save();
        StartupRegistration.Apply(Settings.StartWithWindows);
        OnPropertyChanged(nameof(TitleName));
        OnPropertyChanged(nameof(RecordAllEnabled));
        OnPropertyChanged(nameof(RequireArm));
        Refresh();
    }

    // ---------- Program output ----------

    public ObservableCollection<DeckLinkDevice> OutputDevices { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputModes))]
    private DeckLinkDevice? _selectedDevice;

    [ObservableProperty]
    private DeckLinkMode? _selectedMode;

    [ObservableProperty]
    private bool _outputRunning;

    [ObservableProperty]
    private string _outputStatus = "Not started";

    [ObservableProperty]
    private bool _outputHealthy;

    public IReadOnlyList<DeckLinkMode> OutputModes => SelectedDevice?.Modes ?? Array.Empty<DeckLinkMode>();

    public string? OutputUnavailable => DeckLinkOutput.Unavailable;

    partial void OnSelectedDeviceChanged(DeckLinkDevice? value)
    {
        SelectedMode = value?.Modes.FirstOrDefault(m => m.Name == Settings.OutputMode)
                       ?? value?.Modes.FirstOrDefault(m => m.Width == 1920 && m.Height == 1080)
                       ?? value?.Modes.FirstOrDefault();
    }

    [RelayCommand]
    private void RefreshOutputDevices()
    {
        if (OutputRunning) return;
        OutputDevices.Clear();
        foreach (var d in DeckLinkOutput.Discover()) OutputDevices.Add(d);
        SelectedDevice = OutputDevices.FirstOrDefault(d => d.Name == Settings.OutputDevice) ?? OutputDevices.FirstOrDefault();
        OnPropertyChanged(nameof(OutputUnavailable));
    }

    [RelayCommand]
    private void ToggleOutput()
    {
        if (OutputRunning) StopOutput();
        else StartOutput();
    }

    private void StartOutput()
    {
        if (SelectedDevice is not { } device || SelectedMode is not { } mode)
        {
            AppLog.Warn("Choose an output device and format first.");
            return;
        }
        if (!DeckLinkOutput.Start(device, mode)) return;
        _pump.Start(this, mode);
        OutputRunning = true;
        Settings.OutputDevice = device.Name;
        Settings.OutputMode = mode.Name;
        Settings.Save();
    }

    private void StopOutput()
    {
        _pump.Stop();
        DeckLinkOutput.Stop();
        OutputRunning = false;
    }

    private long _lastLate;

    private void UpdateOutputStatus()
    {
        if (!OutputRunning)
        {
            OutputStatus = OutputUnavailable ?? "Not started";
            OutputHealthy = false;
            return;
        }
        var late = DeckLinkOutput.LateFrames;
        OutputHealthy = late == _lastLate;
        OutputStatus = late == 0 ? "Sending, no dropped frames" : $"Sending, {late} late or dropped frame{(late == 1 ? "" : "s")}";
        _lastLate = late;
    }

    public async Task ShutdownAsync()
    {
        _timer.Stop();
        StopOutput();
        DeckLinkOutput.Shutdown();
        await Task.WhenAll(Recorders.Select(r => r.ShutdownAsync())
            .Concat(Encoders.Select(e => e.ShutdownAsync())));
        Settings.Save();
    }
}
