using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Speed.Core;
using Speed.Devices;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace Speed.Views;

/// <summary>
/// Adds an encoder or edits one: name, IP address and its table of stream keys with a name
/// for each. Returns the new settings, or null when cancelled.
/// </summary>
public partial class EncoderWindow : Window
{
    private readonly HashSet<string> _takenNames;

    public EncoderWindow() : this(null, Array.Empty<string>()) { }

    public EncoderWindow(EncoderConfig? existing, IEnumerable<string> otherNames)
    {
        InitializeComponent();
        _takenNames = new HashSet<string>(otherNames, StringComparer.OrdinalIgnoreCase);
        if (existing != null)
        {
            Title = Heading.Text = "Encoder settings";
            SaveButton.Content = "Save";
            NameBox.Text = existing.Name;
            AddressBox.Text = existing.Address;
            foreach (var k in existing.Keys) AddRow(k.Name, k.Key);
        }
        KeyNote.Text = OperatingSystem.IsWindows()
            ? "Keys are stored encrypted, so only this Windows account on this PC can read them. JSPEED only ever shows a key's last 4 characters."
            : "JSPEED only ever shows a key's last 4 characters. On a Mac, keys are kept in JSPEED's settings file as typed.";
        UpdateEmpty();
    }

    private string Address => (AddressBox.Text ?? "").Trim();

    private TextBox AddRow(string name, string key)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,38"), ColumnSpacing = 10 };
        var nameBox = new TextBox
        {
            Text = name,
            Watermark = "For example, 11am Service",
            Height = 40,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var keyBox = new TextBox
        {
            Text = key,
            Watermark = "Paste the stream key",
            PasswordChar = '•',
            Height = 40,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(keyBox, 1);
        var remove = new Button
        {
            Classes = { "icon" },
            Content = new ShapePath
            {
                Data = Geometry.Parse("M6 6 L18 18 M18 6 L6 18"),
                Stroke = (IBrush)this.FindResource("Text")!,
                StrokeThickness = 2,
                StrokeLineCap = PenLineCap.Round,
                Width = 11,
                Height = 11,
                Stretch = Stretch.Uniform,
            },
        };
        ToolTip.SetTip(remove, "Remove this key");
        remove.Click += (_, _) =>
        {
            KeyRows.Children.Remove(grid);
            UpdateEmpty();
        };
        Grid.SetColumn(remove, 2);
        grid.Children.Add(nameBox);
        grid.Children.Add(keyBox);
        grid.Children.Add(remove);
        KeyRows.Children.Add(grid);
        UpdateEmpty();
        return nameBox;
    }

    private void UpdateEmpty() => NoKeys.IsVisible = KeyRows.Children.Count == 0;

    private List<(string Name, string Key)> Rows() => KeyRows.Children.OfType<Grid>()
        .Select(g => (((TextBox)g.Children[0]).Text?.Trim() ?? "", ((TextBox)g.Children[1]).Text?.Trim() ?? ""))
        .ToList();

    private static string Tail(string key) => "…" + key[^Math.Min(4, key.Length)..];

    private void AddKey(object? sender, RoutedEventArgs e) => AddRow("", "").Focus();

    private async void AddCurrentKey(object? sender, RoutedEventArgs e)
    {
        if (Address.Length == 0)
        {
            ShowResult("Enter the encoder's IP address first.", false);
            return;
        }
        CurrentKeyButton.IsEnabled = false;
        ShowResult("Reading the encoder's current stream key…", null);
        var (_, key, connected) = await StreamingEncoder.ProbeAsync(Address, TimeSpan.FromSeconds(5));
        CurrentKeyButton.IsEnabled = true;

        if (!connected) ShowResult(NoReply, false);
        else if (string.IsNullOrEmpty(key)) ShowResult("Connected, but the encoder has no stream key loaded.", false);
        else if (Rows().Any(r => r.Key == key)) ShowResult($"The encoder's current key ({Tail(key)}) is already in the list.", true);
        else
        {
            AddRow("", key).Focus();
            ShowResult($"Added the encoder's current key ({Tail(key)}). Give it a name.", true);
        }
    }

    private async void TestConnection(object? sender, RoutedEventArgs e)
    {
        if (Address.Length == 0)
        {
            ShowResult("Enter an IP address first.", false);
            return;
        }
        TestButton.IsEnabled = false;
        ShowResult("Connecting…", null);
        var (model, key, connected) = await StreamingEncoder.ProbeAsync(Address, TimeSpan.FromSeconds(5));
        TestButton.IsEnabled = true;
        if (!connected)
        {
            ShowResult(NoReply, false);
            return;
        }
        var who = model ?? "the encoder";
        var matchName = string.IsNullOrEmpty(key) ? "" : Rows().FirstOrDefault(r => r.Key == key).Name ?? "";
        ShowResult(
            string.IsNullOrEmpty(key) ? $"Connected to {who}. It has no stream key loaded." :
            matchName.Length > 0 ? $"Connected to {who}. It's set to “{matchName}”." :
            $"Connected to {who}. Its current key ({Tail(key)}) isn't in the list yet.", true);
    }

    private const string NoReply =
        "No reply at that address. Check the IP address, that the encoder is on, and that it's on the same network.";

    private void ShowResult(string text, bool? ok)
    {
        TestResult.Text = text;
        TestResult.Foreground = ok switch
        {
            true => (IBrush)this.FindResource("Green")!,
            false => (IBrush)this.FindResource("AmberText")!,
            _ => (IBrush)this.FindResource("Muted")!,
        };
        TestResult.IsVisible = true;
    }

    private void Save(object? sender, RoutedEventArgs e)
    {
        var name = (NameBox.Text ?? "").Trim();
        var rows = Rows().Where(r => r.Name.Length > 0 || r.Key.Length > 0).ToList();
        string? problem =
            name.Length == 0 ? "Give the encoder a name." :
            _takenNames.Contains(name) ? $"Another encoder is already called {name}." :
            Address.Length == 0 ? "Enter the encoder's IP address." :
            rows.Any(r => r.Name.Length == 0) ? "Give every stream key a name." :
            rows.Any(r => r.Key.Length == 0) ? "One of the names has no stream key." :
            rows.GroupBy(r => r.Key).Any(g => g.Count() > 1) ? "The same stream key is in the list twice." :
            null;
        if (problem != null)
        {
            ErrorText.Text = problem;
            ErrorText.IsVisible = true;
            return;
        }
        Close(new EncoderConfig
        {
            Name = name,
            Address = Address,
            Keys = rows.Select(r => new StreamKeyEntry { Name = r.Name, Key = r.Key }).ToList(),
        });
    }

    private void Cancel(object? sender, RoutedEventArgs e) => Close(null);
}
