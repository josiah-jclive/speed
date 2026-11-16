using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Speed.Views;

/// <summary>
/// A button that only fires after being held down for <see cref="HoldTime"/>.
/// Letting go early cancels. A fill shows progress.
/// </summary>
public sealed class HoldButton : Border
{
    private readonly Border _fill;
    private readonly TextBlock _label;
    private readonly DispatcherTimer _timer;
    private DateTime _started;
    private bool _holding;
    private bool _done;

    public TimeSpan HoldTime { get; set; } = TimeSpan.FromSeconds(3);
    public string IdleText { get; set; } = "Press and hold to format";

    public event EventHandler? Completed;

    public HoldButton()
    {
        Height = 76;
        CornerRadius = new CornerRadius(14);
        BorderThickness = new Thickness(2);
        BorderBrush = Brush.Parse("#F5A524");
        Background = Brush.Parse("#2A2113");
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);

        _fill = new Border
        {
            Background = Brush.Parse("#F5A524"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0,
        };
        _label = new TextBlock
        {
            FontSize = 19,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush.Parse("#FFE3B3"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Child = new Panel { Children = { _fill, _label } };
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Render, (_, _) => Tick());
        Reset();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _label.Text = IdleText;
        Avalonia.Automation.AutomationProperties.SetName(this, IdleText);
    }

    public void Reset()
    {
        _timer.Stop();
        _holding = false;
        _done = false;
        _fill.Width = 0;
        _label.Text = IdleText;
        _label.Foreground = Brush.Parse("#FFE3B3");
    }

    public void BeginHold()
    {
        if (_holding || _done || !IsEffectivelyEnabled) return;
        _holding = true;
        _started = DateTime.UtcNow;
        _timer.Start();
    }

    public void EndHold()
    {
        if (!_holding || _done) return;
        Reset();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Pointer.Capture(this);
        BeginHold();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        EndHold();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndHold();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Space or Key.Enter or Key.F)
        {
            BeginHold();
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key is Key.Space or Key.Enter or Key.F) EndHold();
    }

    private void Tick()
    {
        var elapsed = DateTime.UtcNow - _started;
        var progress = Math.Clamp(elapsed / HoldTime, 0, 1);
        _fill.Width = Bounds.Width * progress;
        var left = Math.Max(0, (HoldTime - elapsed).TotalSeconds);
        _label.Text = $"Keep holding to format   {left:0.0} s";
        _label.Foreground = progress > 0.5 ? Brush.Parse("#1A1306") : Brush.Parse("#FFE3B3");
        if (progress >= 1)
        {
            _timer.Stop();
            _holding = false;
            _done = true;
            _label.Text = "Formatting…";
            Completed?.Invoke(this, EventArgs.Empty);
        }
    }
}
