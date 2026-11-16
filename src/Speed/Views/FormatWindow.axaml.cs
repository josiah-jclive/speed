using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Speed.Core;

namespace Speed.Views;

/// <summary>
/// Three deliberate steps before anything is erased: choose the drive, check what's on it
/// and type its name, then press and hold. The recorder re-checks it isn't recording just
/// before formatting, and HyperDecks add their own prepare/confirm token on top.
/// </summary>
public partial class FormatWindow : Window
{
    private sealed record Choice(Recorder Recorder, FormatTarget Target);

    private readonly IReadOnlyList<Recorder> _recorders;
    private readonly Recorder? _preferred;
    private Choice? _choice;
    private FormatDetails? _details;
    private string _fileSystem = "exFAT";
    private int _step = 1;
    private bool _busy;

    public FormatWindow() : this(Array.Empty<Recorder>(), null) { }

    public FormatWindow(IEnumerable<Recorder> recorders, Recorder? preferred)
    {
        InitializeComponent();
        _recorders = recorders.ToList();
        _preferred = preferred;
        Hold.Completed += async (_, _) => await RunFormatAsync();
        ShowStep(1);
        Opened += async (_, _) => await LoadDrivesAsync();
    }

    private IBrush Res(string key) => (IBrush)this.FindResource(key)!;

    // ---------- Step 1 ----------

    private async Task LoadDrivesAsync()
    {
        DriveList.Children.Clear();
        DriveList.Children.Add(new TextBlock { Text = "Looking for drives…", Classes = { "muted" } });
        var unavailable = new List<string>();
        var rows = new List<Control>();
        RadioButton? preferredRow = null;

        foreach (var r in _recorders)
        {
            if (!r.CanFormat)
            {
                unavailable.Add($"{r.Name}: {r.FormatUnavailableReason}");
                continue;
            }
            if (!r.IsOnline)
            {
                unavailable.Add($"{r.Name} isn't connected, so its drives can't be listed.");
                continue;
            }

            IReadOnlyList<FormatTarget> targets;
            try { targets = await r.GetFormatTargetsAsync(); }
            catch (Exception ex)
            {
                unavailable.Add($"{r.Name}: couldn't list drives ({ex.Message}).");
                continue;
            }
            if (targets.Count == 0) unavailable.Add($"{r.Name} has no mounted media.");

            foreach (var t in targets)
            {
                var recording = r.IsRecording;
                var row = MakeRow(t.Title, recording ? "Recording now. Stop it before formatting." : t.Detail,
                    recording ? "" : t.FreeText, !recording);
                row.Tag = new Choice(r, t);
                row.IsCheckedChanged += (_, _) => UpdateNext();
                if (!recording && r == _preferred && preferredRow == null) preferredRow = row;
                rows.Add(row);
            }
        }

        DriveList.Children.Clear();
        if (rows.Count == 0)
            DriveList.Children.Add(new TextBlock { Text = "There are no drives JSPEED can format right now.", FontSize = 14 });
        foreach (var row in rows) DriveList.Children.Add(row);
        if (preferredRow != null) preferredRow.IsChecked = true;

        // Group the Shogun note, as they all share one reason.
        var shoguns = _recorders.Where(r => r is Devices.ShogunRecorder).Select(r => r.Name).ToList();
        unavailable.RemoveAll(u => _recorders.Any(r => r is Devices.ShogunRecorder && u.StartsWith(r.Name + ":")));
        if (shoguns.Count > 0)
            unavailable.Insert(0, $"Shoguns don’t accept format commands over RS-422, so format {JoinNames(shoguns)} on the units themselves.");
        UnavailableText.Text = string.Join(Environment.NewLine, unavailable);
        UnavailableText.IsVisible = unavailable.Count > 0;
        UpdateNext();
    }

    private static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    private RadioButton MakeRow(string title, string detail, string right, bool enabled)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold });
        text.Children.Add(new TextBlock { Text = detail, FontSize = 13, Foreground = Res("Muted"), TextWrapping = TextWrapping.Wrap });
        grid.Children.Add(text);
        var r = new TextBlock { Text = right, FontSize = 13, Foreground = Res("Muted"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        Grid.SetColumn(r, 1);
        grid.Children.Add(r);
        return new RadioButton { Content = grid, GroupName = "drive", IsEnabled = enabled, Classes = { "choice" } };
    }

    private Choice? SelectedChoice =>
        DriveList.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as Choice;

    // ---------- Step 2 ----------

    private async Task PrepareStep2Async()
    {
        _choice = SelectedChoice;
        if (_choice == null) return;
        var (recorder, target) = (_choice.Recorder, _choice.Target);
        SumRecorder.Text = $"{recorder.Name}, {recorder.ModelName} at {recorder.Address}";
        SumDrive.Text = target.VolumeName is { Length: > 0 } v ? $"Slot {target.Slot}, volume {v}" : $"Slot {target.Slot}";
        SumContents.Text = "Checking…";
        ConfirmBox.Text = "";
        AckBox.IsChecked = false;

        FileSystemList.Children.Clear();
        _fileSystem = recorder.FileSystems.FirstOrDefault() ?? "exFAT";
        foreach (var fs in recorder.FileSystems)
        {
            var rb = new RadioButton
            {
                Content = new TextBlock { Text = fs, FontSize = 14 },
                GroupName = "fs",
                IsChecked = fs == _fileSystem,
                Classes = { "choice" },
                Padding = new Avalonia.Thickness(0),
            };
            var name = fs;
            rb.IsCheckedChanged += (_, _) => { if (rb.IsChecked == true) _fileSystem = name; };
            FileSystemList.Children.Add(rb);
        }

        ConfirmLabel.Text = $"Type {ConfirmWord} to confirm this is the right drive";
        try { _details = await recorder.GetFormatDetailsAsync(target); }
        catch { _details = null; }

        var clips = _details?.ClipCount;
        SumContents.Text = clips switch
        {
            null => "Couldn't read the clip list",
            0 => "No clips",
            1 => "1 clip",
            _ => $"{clips} clips",
        };
        AckText.Text = clips is > 0
            ? $"I’ve copied every clip I need from this drive. Formatting erases all {clips} clips and can’t be undone."
            : "I’ve copied everything I need from this drive. Formatting erases it and can’t be undone.";
        UpdateNext();
    }

    /// <summary>The volume name when the drive has one, otherwise the recorder's name.</summary>
    private string ConfirmWord =>
        _choice?.Target.VolumeName is { Length: > 0 } v ? v : _choice?.Recorder.Name ?? "";

    private bool ConfirmMatches => (ConfirmBox.Text ?? "").Trim() == ConfirmWord && ConfirmWord.Length > 0;

    private void ConfirmChanged(object? sender, TextChangedEventArgs e)
    {
        if (ConfirmMatch == null) return; // fires while the window is loading
        ConfirmMatch.IsVisible = ConfirmMatches;
        if (ConfirmMatches) ConfirmBox.BorderBrush = Res("Green");
        else ConfirmBox.ClearValue(TextBox.BorderBrushProperty);
        UpdateNext();
    }

    private void AckChanged(object? sender, RoutedEventArgs e) => UpdateNext();

    // ---------- Step 3 ----------

    private void PrepareStep3()
    {
        if (_choice == null) return;
        var volume = ConfirmWord;
        EraseTitle.Text = $"Erase {volume} on {_choice.Recorder.Name}?";
        var clips = _details?.ClipCount is > 0 ? $"All {_details.ClipCount} clips" : "Everything";
        EraseText.Text = $"{clips} on slot {_choice.Target.Slot} will be erased and the drive formatted as {_fileSystem}. Other recorders keep recording.";
        Hold.Reset();
        Hold.IsEnabled = true;
        ResultText.IsVisible = false;
        HoldHint.IsVisible = true;
        Hold.Focus();
    }

    private async Task RunFormatAsync()
    {
        if (_choice == null || _busy) return;
        _busy = true;
        BackButton.IsEnabled = CancelButton.IsEnabled = false;
        Hold.IsEnabled = false;
        HoldHint.IsVisible = false;
        try
        {
            if (_choice.Recorder.IsRecording)
                throw new InvalidOperationException($"{_choice.Recorder.Name} started recording. Nothing was erased.");
            await _choice.Recorder.FormatAsync(_choice.Target, _fileSystem);
            ShowResult($"Formatted. {ConfirmWord} is empty and ready to record.", true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Format of {_choice.Recorder.Name} slot {_choice.Target.Slot} failed: {ex.Message}");
            ShowResult($"The format didn’t happen. {ex.Message}", false);
        }
        finally
        {
            _busy = false;
            CancelButton.IsEnabled = true;
            CancelButton.Content = "Close";
        }
    }

    private void ShowResult(string text, bool ok)
    {
        ResultText.Text = text;
        ResultText.Foreground = ok ? Res("Green") : Res("AmberText");
        ResultText.IsVisible = true;
    }

    // ---------- Navigation ----------

    private void ShowStep(int step)
    {
        _step = step;
        Step1.IsVisible = step == 1;
        Step2.IsVisible = step == 2;
        Step3.IsVisible = step == 3;
        BackButton.IsVisible = step > 1;
        NextButton.IsVisible = step < 3;

        var done = Brush.Parse("#4D6A99");
        var off = Res("Line");
        var current = step == 3 ? Res("Amber") : Res("Blue");
        Bar1.Background = step == 1 ? current : done;
        Bar2.Background = step == 2 ? current : step > 2 ? done : off;
        Bar3.Background = step == 3 ? current : off;
        foreach (var (label, n) in new[] { (Label1, 1), (Label2, 2), (Label3, 3) })
        {
            label.Foreground = n == step ? Res("Text") : n < step ? Res("Muted") : Res("Dim");
            label.FontWeight = n == step ? FontWeight.SemiBold : FontWeight.Normal;
        }
        UpdateNext();
    }

    private void UpdateNext()
    {
        if (NextButton == null) return;
        NextButton.IsEnabled = _step switch
        {
            1 => SelectedChoice != null,
            2 => ConfirmMatches && AckBox.IsChecked == true,
            _ => false,
        };
    }

    private async void Next(object? sender, RoutedEventArgs e)
    {
        if (_step == 1 && SelectedChoice != null)
        {
            ShowStep(2);
            await PrepareStep2Async();
        }
        else if (_step == 2 && ConfirmMatches && AckBox.IsChecked == true)
        {
            ShowStep(3);
            PrepareStep3();
        }
    }

    private void Back(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Hold.Reset();
        if (_step > 1) ShowStep(_step - 1);
    }

    private void Cancel(object? sender, RoutedEventArgs e)
    {
        if (!_busy) Close();
    }
}
