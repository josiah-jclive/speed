using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Speed.Core;
using Speed.ViewModels;

namespace Speed.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _previewTimer;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        _previewTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background,
            (_, _) => Preview.InvalidateVisual());
        _previewTimer.Start();
        AppLog.Entries.CollectionChanged += OnLogChanged;
        SetUpStopHold();
    }

    /// <summary>How long Stop all must be held before it fires.</summary>
    public static readonly TimeSpan StopHoldTime = TimeSpan.FromSeconds(1.5);

    private void SetUpStopHold()
    {
        // App-level brushes from Theme.axaml; the window isn't in the tree yet, so ask the app.
        IBrush Res(string key) => (IBrush)Application.Current!.FindResource(key)!;
        StopHold.HoldTime = StopHoldTime;
        StopHold.IdleText = "Stop all";
        StopHold.HintText = "Hold";
        StopHold.HoldingText = "Stopping";
        StopHold.DoneText = "Stopped";
        StopHold.ShowStopIcon = true;
        StopHold.UseFKey = false;
        StopHold.ResetAfterComplete = true;
        StopHold.LabelSize = 18;
        StopHold.Accent = Res("Text");
        StopHold.IdleBackground = Res("Panel2");
        StopHold.IdleBorder = Res("Line2");
        StopHold.IdleForeground = Res("Text");
        StopHold.OnAccentForeground = Res("Ground");
        StopHold.Completed += async (_, _) =>
        {
            if (Model is { } vm) await vm.StopAllCommand.ExecuteAsync(null);
        };
        StopHold.Reset();
    }

    private MainViewModel? Model => DataContext as MainViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Model is { } vm)
        {
            Preview.Source = vm;
            vm.IconLevelChanged += SetIcon;
        }
    }

    /// <summary>Taskbar icon: white dot when ready, red while recording, amber when something needs attention.</summary>
    private void SetIcon(StatusLevel level)
    {
        var name = level switch
        {
            StatusLevel.Recording => "recording",
            StatusLevel.Attention => "attention",
            _ => "ready",
        };
        try
        {
            using var stream = AssetLoader.Open(new Uri($"avares://JSPEED/Assets/Icons/state-{name}.png"));
            Icon = new WindowIcon(stream);
        }
        catch
        {
            // Keep the previous icon if the asset can't be loaded.
        }
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private async void OpenSettings(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } vm) return;
        if (sender is Control c) ToolTip.SetIsOpen(c, false);
        await new SettingsWindow(vm).ShowDialog(this);
        vm.SaveSettings();
    }

    private async void AddRecorder(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } vm) return;
        var config = await new AddRecorderWindow().ShowDialog<RecorderConfig?>(this);
        if (config != null) vm.AddRecorder(config);
    }

    private void ShowMore(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Recorder recorder } button) return;
        ToolTip.SetIsOpen(button, false);
        var format = new MenuItem { Header = "Format media…" };
        format.Click += async (_, _) => await FormatMediaAsync(recorder);
        var remove = new MenuItem { Header = "Remove recorder" };
        remove.Click += async (_, _) => await RemoveRecorderAsync(recorder);
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        menu.Items.Add(format);
        menu.Items.Add(remove);
        menu.ShowAt(button);
    }

    private async Task FormatMediaAsync(Recorder recorder)
    {
        if (Model is not { } vm) return;
        await new FormatWindow(vm.Recorders, recorder).ShowDialog(this);
    }

    private async Task RemoveRecorderAsync(Recorder recorder)
    {
        if (Model is not { } vm) return;
        if (recorder.IsRecording)
        {
            AppLog.Warn($"{recorder.Name} is recording. Stop it before removing it.");
            return;
        }
        await vm.RemoveRecorderCommand.ExecuteAsync(recorder);
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closing || Model is not { } vm) return;
        e.Cancel = true;
        _closing = true;
        _previewTimer.Stop();
        AppLog.Entries.CollectionChanged -= OnLogChanged;
        if (vm.Status.Recording > 0)
            AppLog.Warn("JSPEED closed while recorders were recording. Closing doesn't stop them.");
        await vm.ShutdownAsync();
        Close();
    }
}
