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

    private void UpdateKind()
    {
        if (AddressBox == null) return; // radio events fire during InitializeComponent
        bool serial = Kind == RecorderKind.Shogun;
        AddressLabel.Text = serial ? "Serial port of the RS-422 adapter" : "IP address";
        AddressBox.IsVisible = !serial;
        PortRow.IsVisible = serial;
        TestButton.IsVisible = true;
        TestResult.IsVisible = false;
    }

    private void RescanPorts(object? sender, RoutedEventArgs? e)
    {
        var ports = ShogunRecorder.AvailablePorts();
        PortBox.ItemsSource = ports;
        PortBox.SelectedItem = ports.FirstOrDefault(p => p.Contains("usbserial", StringComparison.OrdinalIgnoreCase)) ?? ports.FirstOrDefault();
    }

    private string Address => Kind == RecorderKind.Shogun ? PortBox.SelectedItem as string ?? "" : (AddressBox.Text ?? "").Trim();

    private RecorderConfig? BuildConfig(out string? problem)
    {
        problem = null;
        var name = (NameBox.Text ?? "").Trim();
        if (name.Length == 0) problem = "Give the recorder a name. It's how it appears on screen and on the output.";
        else if (Address.Length == 0)
            problem = Kind == RecorderKind.Shogun ? "Choose the serial port for the RS-422 adapter." : "Enter the recorder's IP address.";
        if (problem != null) return null;
        return new RecorderConfig { Kind = Kind, Name = name, Address = Address, IncludeInGroup = GroupBox.IsChecked == true };
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
                ? "No reply on that port. Check the cable wiring and that RS-422 remote is on in AtomOS. If this Shogun is already in JSPEED, the port is in use."
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
        var config = BuildConfig(out var problem);
        if (config == null)
        {
            ErrorText.Text = problem;
            ErrorText.IsVisible = true;
            return;
        }
        Close(config);
    }

    private void Cancel(object? sender, RoutedEventArgs e) => Close(null);
}
