using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Speed.Views;

/// <summary>About JSPEED: logo, version and a link to JC Live. Opened from the Mac app menu and from Settings.</summary>
public partial class AboutWindow : Window
{
    public const string Website = "http://www.jc.live";

    private static AboutWindow? _open;

    public AboutWindow()
    {
        InitializeComponent();
        var v = typeof(AboutWindow).Assembly.GetName().Version;
        if (v != null) VersionText.Text = $"Version {v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        CopyrightText.Text = $"© {DateTime.Now.Year} JC Live";
    }

    /// <summary>Shows the About window, or brings it forward if it's already open.</summary>
    public static void ShowFor(Window? owner)
    {
        if (_open != null)
        {
            _open.Activate();
            return;
        }
        _open = new AboutWindow();
        _open.Closed += (_, _) => _open = null;
        if (owner != null) _open.Show(owner);
        else _open.Show();
    }

    private async void OpenWebsite(object? sender, RoutedEventArgs e)
    {
        try { await Launcher.LaunchUriAsync(new Uri(Website)); }
        catch { /* No browser available; nothing else to do. */ }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) Close();
    }
}
