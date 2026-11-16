using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Speed.Core;
using Speed.Devices;

namespace Speed.Views;

public partial class AddRecorderWindow : Window
{
    public AddRecorderWindow()
    {
        InitializeComponent();
        RescanPorts(null, null);
        UpdateKind();
    }

    private RecorderKind Kind =>
        KindShogun.IsChecked == true ? RecorderKind.Shogun :
        KindKiPro.IsChecked == true ? RecorderKind.KiPro :
        RecorderKind.HyperDeck;

    private void KindChanged(object? sender, RoutedEventArgs e) => UpdateKind();

    /// <summary>Record channels on the cable: 1 to 4 for a Shogun, always 1 otherwise.</summary>
    private int ChannelCount =>
        Kind != RecorderKind.Shogun ? 1 :
        Ch4.IsChecked == true ? 4 : Ch3.IsChecked == true ? 3 : Ch2.IsChecked == true ? 2 : 1;

    private void ChannelsChanged(object? sender, RoutedEventArgs e) => UpdateNames();

    /// <summary>One name box per channel; the first is the existing name box.</summary>
    private void UpdateNames()
    {
        if (ExtraNames == null) return; // radio events fire during InitializeComponent
        int count = ChannelCount;
        var kept = ExtraNames.Children.OfType<TextBox>().Select(t => t.Text).ToList();
        ExtraNames.Children.Clear();
        for (int i = 2; i <= count; i++)
        {
            ExtraNames.Children.Add(new TextBox
            {
                Watermark = $"Channel {i}, for example Cam {(char)('A' + i - 1)}",
                Text = i - 2 < kept.Count ? kept[i - 2] : "",
                Height = 44,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            });
        }
        NameLabel.Text = count > 1 ? "Channel names, as they appear on screen" : "Name on screen";
        NameBox.Watermark = count > 1 ? "Channel 1, for example Cam A" : "For example, Cam A";
        ChannelsNote.IsVisible = count > 1;
    }

    private void UpdateKind()
    {
        if (AddressBox == null) return; // radio events fire during InitializeComponent
        bool serial = Kind == RecorderKind.Shogun;
        AddressLabel.Text = serial ? "Serial port of the RS-422 adapter" : "IP address";
        AddressBox.IsVisible = !serial;
        PortRow.IsVisible = serial;
        ChannelsRow.IsVisible = serial;
        TestButton.IsVisible = true;
        TestResult.IsVisible = false;
        UpdateNames();
    }

    private void RescanPorts(object? sender, RoutedEventArgs? e)
    {
        var ports = ShogunRecorder.AvailablePorts();
        PortBox.ItemsSource = ports;
        PortBox.SelectedItem = ports.FirstOrDefault(p => p.Contains("usbserial", StringComparison.OrdinalIgnoreCase)) ?? ports.FirstOrDefault();
    }

    private string Address => Kind == RecorderKind.Shogun ? PortBox.SelectedItem as string ?? "" : (AddressBox.Text ?? "").Trim();

    private List<RecorderConfig>? BuildConfigs(out string? problem)
    {
        problem = null;
        var names = new List<string> { (NameBox.Text ?? "").Trim() };
        names.AddRange(ExtraNames.Children.OfType<TextBox>().Select(t => (t.Text ?? "").Trim()));
        int count = ChannelCount;

        if (names.Take(count).Any(n => n.Length == 0))
            problem = count > 1
                ? "Give every channel a name. It's how each one appears on screen and on the output."
                : "Give the recorder a name. It's how it appears on screen and on the output.";
        else if (names.Take(count).Distinct(StringComparer.OrdinalIgnoreCase).Count() < count)
            problem = "Give each channel a different name, so they can be told apart on the output.";
        else if (Address.Length == 0)
            problem = Kind == RecorderKind.Shogun ? "Choose the serial port for the RS-422 adapter." : "Enter the recorder's IP address.";
        if (problem != null) return null;

        return names.Take(count).Select((name, i) => new RecorderConfig
        {
            Kind = Kind,
            Name = name,
            Address = Address,
            IncludeInGroup = GroupBox.IsChecked == true,
            Channel = count > 1 ? i + 1 : 0,
        }).ToList();
    }

    private async void TestConnection(object? sender, RoutedEventArgs e)
    {
        if (Address.Length == 0)
        {
            ShowResult(Kind == RecorderKind.Shogun ? "Choose a serial port first." : "Enter an IP address first.", false);
            return;
        }
        var config = new RecorderConfig { Kind = Kind, Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Test" : NameBox.Text!.Trim(), Address = Address };
        var recorder = RecorderFactory.Create(config);
        TestButton.IsEnabled = false;
        ShowResult("Connecting…", null);
        recorder.Start();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!recorder.IsOnline && DateTime.UtcNow < deadline) await Task.Delay(150);
        if (recorder.IsOnline) await Task.Delay(600); // give the first status poll time to land

        if (recorder.IsOnline)
            ShowResult($"Connected. The {recorder.ModelName} reports it's {recorder.StateText.ToLowerInvariant()}.", true);
        else
            ShowResult(Kind == RecorderKind.Shogun
                ? "No reply on that port. Check the cable wiring and that RS-422 remote is on in AtomOS."
                : "No reply at that address. Check the IP address and that the recorder is on the same network.", false);

        await recorder.ShutdownAsync();
        TestButton.IsEnabled = true;
    }

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

    private void Add(object? sender, RoutedEventArgs e)
    {
        var configs = BuildConfigs(out var problem);
        if (configs == null)
        {
            ErrorText.Text = problem;
            ErrorText.IsVisible = true;
            return;
        }
        Close(configs);
    }

    private void Cancel(object? sender, RoutedEventArgs e) => Close(null);
}
