using Avalonia.Controls;
using Avalonia.Interactivity;
using Speed.ViewModels;

namespace Speed.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() : this(null) { }

    public SettingsWindow(MainViewModel? model)
    {
        InitializeComponent();
        if (model != null) DataContext = model.Settings;
        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        if (version != null) VersionText.Text = $"{version.Major}.{version.Minor}";
    }

    private void NavChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (GeneralPage == null) return;
        var i = Nav.SelectedIndex;
        GeneralPage.IsVisible = i == 0;
        OutputPage.IsVisible = i == 1;
        WarningsPage.IsVisible = i == 2;
        ShortcutsPage.IsVisible = i == 3;
    }

    private void Done(object? sender, RoutedEventArgs e) => Close();
}
