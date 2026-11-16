using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Speed.Output;

/// <summary>
/// Renders the program output graphic off screen at the output's resolution and hands
/// each frame to the DeckLink bridge, which repeats the newest frame until the next arrives.
/// </summary>
public sealed class OutputPump
{
    private readonly ProgramOutputView _view = new();
    private RenderTargetBitmap? _bitmap;
    private byte[]? _pixels;
    private DispatcherTimer? _timer;
    private int _width, _height;

    public void Start(IOutputSource source, DeckLinkMode mode)
    {
        Stop();
        _width = mode.Width;
        _height = mode.Height;
        _view.Source = source;
        _view.Measure(new Size(_width, _height));
        _view.Arrange(new Rect(0, 0, _width, _height));
        _bitmap = new RenderTargetBitmap(new PixelSize(_width, _height), new Vector(96, 96));
        _pixels = new byte[_width * _height * 4];

        // The graphic only changes a few times a second, so cap rendering at 30 fps.
        var fps = Math.Clamp(mode.FramesPerSecond, 1, 30);
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1 / fps), DispatcherPriority.Render, (_, _) => RenderFrame());
        _timer.Start();
        RenderFrame();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
        _bitmap?.Dispose();
        _bitmap = null;
        _pixels = null;
    }

    private void RenderFrame()
    {
        if (_bitmap == null || _pixels == null) return;
        _bitmap.Render(_view);

        var handle = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
        try
        {
            _bitmap.CopyPixels(new PixelRect(0, 0, _width, _height), handle.AddrOfPinnedObject(), _pixels.Length, _width * 4);
        }
        finally
        {
            handle.Free();
        }

        // DeckLink wants BGRA. Skia renders BGRA on Windows and macOS, but swap if a platform hands back RGBA.
        if (_bitmap.Format is { } format && format == PixelFormat.Rgba8888)
        {
            for (int i = 0; i < _pixels.Length; i += 4)
                (_pixels[i], _pixels[i + 2]) = (_pixels[i + 2], _pixels[i]);
        }

        DeckLinkOutput.Submit(_pixels, _width, _height);
    }
}
