using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Speed.Views;

/// <summary>Asks for a recorder's new name. Returns the name, or null when cancelled.</summary>
public partial class RenameWindow : Window
{
    private readonly HashSet<string> _taken;

    public RenameWindow() : this("", Array.Empty<string>()) { }

    public RenameWindow(string current, IEnumerable<string> otherNames)
    {
        InitializeComponent();
        _taken = new HashSet<string>(otherNames, StringComparer.OrdinalIgnoreCase);
        NameBox.Text = current;
        Opened += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void Save(object? sender, RoutedEventArgs e)
    {
        var name = (NameBox.Text ?? "").Trim();
        string? problem = name.Length == 0 ? "Enter a name."
            : _taken.Contains(name) ? $"Another recorder is already called {name}. Names need to be different so they can be told apart on the output."
            : null;
        if (problem != null)
        {
            ErrorText.Text = problem;
            ErrorText.IsVisible = true;
            return;
        }
        Close(name);
    }

    private void Cancel(object? sender, RoutedEventArgs e) => Close(null);
}
