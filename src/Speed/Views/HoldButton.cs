using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Speed.Views;

/// <summary>
/// A button that only fires after being held down for <see cref="HoldTime"/>.
/// Letting go early cancels. A fill shows progress. Used for Format (amber, 3 s)
/// and Stop all (neutral, shorter); the defaults are the Format look.
/// </summary>
public sealed class HoldButton : Border
{
    private readonly Border _fill;
    private readonly TextBlock _label;
    private readonly TextBlock _hint;
    private readonly Rectangle _stopIcon;
    private readonly DispatcherTimer _timer;
    private DateTime _started;
    private bool _holding;
    private bool _done;

    public TimeSpan HoldTime { get; set; } = TimeSpan.FromSeconds(3);
    public string IdleText { get; set; } = "Press and hold to format";
    /// <summary>Shown while holding, followed by the seconds left.</summary>
    public string HoldingText { get; set; } = "Keep holding to format";
    public string DoneText { get; set; } = "Formatting…";
    /// <summary>Small text after the label when idle, such as "Hold".</summary>
    public string? HintText { get; set; }
    /// <summary>Whether holding F works as well as Space or Enter.</summary>
    public bool UseFKey { get; set; } = true;
    /// <summary>Show a stop square before the label.</summary>
    public bool ShowStopIcon { get; set; }
    /// <summary>Return to idle on its own after firing, for buttons used again and again.</summary>
    public bool ResetAfterComplete { get; set; }
    public double LabelSize { get; set; } = 19;

    public IBrush Accent { get; set; } = Brush.Parse("#F5A524");
    public IBrush IdleBackground { get; set; } = Brush.Parse("#2A2113");
    public IBrush IdleForeground { get; set; } = Brush.Parse("#FFE3B3");
    public IBrush OnAccentForeground { get; set; } = Brush.Parse("#1A1306");
    public IBrush? IdleBorder { get; set; }

    public event EventHandler? Completed;

    public HoldButton()
    {
        Height = 76;
        CornerRadius = new CornerRadius(14);
        BorderThickness = new Thickness(2);
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);

        _fill = new Border { HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        _label = new TextBlock { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _hint = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            Opacity = 0.75,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };
        _stopIcon = new Rectangle
        {
            Width = 14,
            Height = 14,
            RadiusX = 3,
            RadiusY = 3,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
        };
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _stopIcon, _label, _hint },
        };
        Child = new Panel { Children = { _fill, content } };
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Render, (_, _) => Tick());
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Avalonia.Automation.AutomationProperties.SetName(this, IdleText);
        Reset();
    }

    public void Reset()
    {
        _timer.Stop();
        _holding = false;
        _done = false;
        _fill.Width = 0;
        _fill.Background = Accent;
        Background = IdleBackground;
        BorderBrush = IdleBorder ?? Accent;
        _label.FontSize = LabelSize;
        _label.Text = IdleText;
        _hint.Text = HintText;
        _hint.IsVisible = !string.IsNullOrEmpty(HintText);
        _stopIcon.IsVisible = ShowStopIcon;
        SetForeground(IdleForeground);
    }

    private void SetForeground(IBrush brush)
    {
        _label.Foreground = brush;
        _hint.Foreground = brush;
        _stopIcon.Fill = brush;
    }

    public void BeginHold()
    {
        if (_holding || _done || !IsEffectivelyEnabled) return;
        _holding = true;
        _started = DateTime.UtcNow;
        _hint.IsVisible = false;
        BorderBrush = Accent;
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

    private bool IsHoldKey(Key key) => key is Key.Space or Key.Enter || (UseFKey && key == Key.F);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (IsHoldKey(e.Key))
        {
            BeginHold();
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (IsHoldKey(e.Key)) EndHold();
    }

    private void Tick()
    {
        var elapsed = DateTime.UtcNow - _started;
        var progress = Math.Clamp(elapsed / HoldTime, 0, 1);
        _fill.Width = Bounds.Width * progress;
        var left = Math.Max(0, (HoldTime - elapsed).TotalSeconds);
        _label.Text = $"{HoldingText}   {left:0.0} s";
        SetForeground(progress > 0.5 ? OnAccentForeground : IdleForeground);
        if (progress >= 1)
        {
            _timer.Stop();
            _holding = false;
            _done = true;
            _label.Text = DoneText;
            Completed?.Invoke(this, EventArgs.Empty);
            if (ResetAfterComplete)
                DispatcherTimer.RunOnce(Reset, TimeSpan.FromMilliseconds(700));
        }
    }
}
