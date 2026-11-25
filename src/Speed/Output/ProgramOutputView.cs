using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Speed.Core;
using Speed.Devices;

namespace Speed.Output;

/// <summary>What the program output graphic needs to draw.</summary>
public interface IOutputSource
{
    string ProductionName { get; }
    bool ShowTakeLength { get; }
    bool ShowTimeOfDay { get; }
    bool ShowDate { get; }
    string TakeLengthText { get; }
    GroupStatus Status { get; }
    IReadOnlyList<Recorder> OutputRecorders { get; }
    IReadOnlyList<StreamingEncoder> OutputEncoders { get; }
}

/// <summary>
/// The status graphic sent to the UltraStudio. Drawn in a 1920×1080 space and scaled,
/// so the same control renders the in-app preview and the full-size output frame.
/// </summary>
public sealed class ProgramOutputView : Control
{
    public const double RefWidth = 1920, RefHeight = 1080;

    public IOutputSource? Source { get; set; }

    private static readonly FontFamily Sans = new("avares://JSPEED/Assets/Fonts#Geist");
    private static readonly FontFamily Mono = new("avares://JSPEED/Assets/Fonts#Geist Mono");
    private static readonly Typeface SansSemi = new(Sans, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface SansMedium = new(Sans, FontStyle.Normal, FontWeight.Medium);
    private static readonly Typeface SansBold = new(Sans, FontStyle.Normal, FontWeight.Bold);
    private static readonly Typeface MonoRegular = new(Mono, FontStyle.Normal, FontWeight.Normal);
    // The JSPEED lockup face, for the ON AIR block.
    private static readonly Typeface BrandSemi = new(new FontFamily("avares://JSPEED/Assets/Fonts#Poppins"), FontStyle.Normal, FontWeight.SemiBold);

    private static IBrush B(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));
    private static readonly IBrush Black = B("#000000");
    private static readonly IBrush White = B("#FFFFFF");
    private static readonly IBrush Muted = B("#B9C0CB");
    private static readonly IBrush RecRed = B("#C42B31");
    private static readonly IBrush RecSub = B("#FFD9DB");
    private static readonly IBrush Amber = B("#F5A524");
    private static readonly IBrush AmberText = B("#FFC266");
    private static readonly IBrush OnAmber = B("#1A1306");
    private static readonly IBrush RowIdle = B("#17191D");
    private static readonly IBrush IdlePill = B("#3A4250");
    private static readonly IBrush Divider = B("#2A2E35");
    private static readonly IBrush TrackOnRed = B("#40FFFFFF");
    private static readonly IBrush TrackOnDark = B("#33383F");
    private static readonly IBrush Dim = B("#7D8592");
    private static readonly IBrush OnAmberSub = B("#3A2A0E");

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        context.FillRectangle(Black, new Rect(size));
        if (Source == null || size.Width <= 0 || size.Height <= 0) return;

        var scale = Math.Min(size.Width / RefWidth, size.Height / RefHeight);
        var offsetX = (size.Width - RefWidth * scale) / 2;
        var offsetY = (size.Height - RefHeight * scale) / 2;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY)))
            Draw(context, Source);
    }

    private static FormattedText Text(string text, Typeface face, double size, IBrush brush, double maxWidth = 0)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, Math.Max(1, size), brush);
        if (maxWidth > 0)
        {
            ft.MaxTextWidth = maxWidth;
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
        }
        return ft;
    }

    private static void Draw(DrawingContext dc, IOutputSource src)
    {
        // Title-safe area: 5% of width, 5% of height.
        const double left = 96, top = 54, right = RefWidth - 96, bottom = RefHeight - 54;
        var status = src.Status;
        var recorders = src.OutputRecorders;

        // ---- Header ----
        const double headerH = 112;
        var blocks = new List<(string Label, string Value)>();
        if (src.ShowTimeOfDay) blocks.Add(("Time of day", DateTime.Now.ToString("HH:mm:ss")));
        var name = src.ProductionName.Trim();
        bool takeOnRight = src.ShowTakeLength && (src.ShowTimeOfDay || src.ShowDate || name.Length == 0);
        if (takeOnRight) blocks.Add(("Take length", src.TakeLengthText));

        // Status pills, right-aligned.
        var pillBrush = status.Level switch
        {
            StatusLevel.Recording => RecRed,
            StatusLevel.Attention => Amber,
            _ => IdlePill,
        };
        var pillText = Text(status.Headline, SansBold, 40, status.Level == StatusLevel.Attention ? OnAmber : White);
        var pillW = pillText.Width + 68;
        const double pillH = 64;
        var pillsX = right - pillW;
        FormattedText? warnText = null;
        if (status.MediaWarning != null && status.Level != StatusLevel.Attention)
            warnText = Text(status.MediaWarning, SansSemi, 22, AmberText);

        double pillY = warnText == null ? top + (headerH - pillH) / 2 : top + 4;
        dc.DrawRectangle(pillBrush, null, new Rect(pillsX, pillY, pillW, pillH), pillH / 2, pillH / 2);
        dc.DrawText(pillText, new Point(pillsX + 34, pillY + (pillH - pillText.Height) / 2));
        if (warnText != null)
        {
            var ww = warnText.Width + 36;
            var wr = new Rect(right - ww, pillY + pillH + 8, ww, 36);
            dc.DrawRectangle(null, new Pen(Amber, 2), wr, 18, 18);
            dc.DrawText(warnText, new Point(wr.X + 18, wr.Y + (wr.Height - warnText.Height) / 2));
        }

        // Readouts (time of day, take length) to the left of the pills.
        double x = Math.Min(pillsX, warnText != null ? right - warnText.Width - 36 : pillsX) - 40;
        for (int i = blocks.Count - 1; i >= 0; i--)
        {
            var label = Text(blocks[i].Label, SansMedium, 20, Muted);
            var value = Text(blocks[i].Value, MonoRegular, 46, White);
            var w = Math.Max(label.Width, value.Width);
            x -= w;
            var y = top + (headerH - (label.Height + value.Height)) / 2;
            dc.DrawText(label, new Point(x, y));
            dc.DrawText(value, new Point(x, y + label.Height));
            x -= 36;
            if (i > 0) dc.FillRectangle(Divider, new Rect(x + 17, top + 20, 2, headerH - 40));
        }

        // Title and subline on the left.
        var titleMax = Math.Max(200, x - left - 24);
        string? subline = src.ShowDate ? DateTime.Now.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture) : null;
        var title = name.Length > 0 ? Text(name, SansSemi, 52, White, titleMax) : null;
        FormattedText? sub = null;
        FormattedText? subTake = null;
        if (subline != null) sub = Text(subline, SansMedium, 26, Muted, titleMax);
        else if (src.ShowTakeLength && !takeOnRight)
        {
            sub = Text("Take length  ", SansMedium, 26, Muted);
            subTake = Text(src.TakeLengthText, MonoRegular, 26, White);
        }
        double contentH = (title?.Height ?? 0) + (sub?.Height ?? 0);
        double ty = top + (headerH - contentH) / 2;
        if (title != null)
        {
            dc.DrawText(title, new Point(left, ty));
            ty += title.Height;
        }
        if (sub != null)
        {
            dc.DrawText(sub, new Point(left, ty));
            if (subTake != null) dc.DrawText(subTake, new Point(left + sub.Width, ty));
        }

        // ---- ON AIR bars along the bottom, one per encoder ----
        double areaBottom = bottom;
        var streams = StreamSummary.Compute(src.OutputEncoders);
        if (streams.Count > 0)
        {
            const double barGap = 12, maxStack = 360;
            double barH = Math.Min(96, (maxStack - barGap * (streams.Count - 1)) / streams.Count);
            double y = bottom - streams.Count * barH - (streams.Count - 1) * barGap;
            areaBottom = y - 18;
            foreach (var stream in streams)
            {
                DrawStreamBar(dc, new Rect(left, y, right - left, barH), stream);
                y += barH + barGap;
            }
        }

        // ---- Recorders ----
        double areaTop = top + headerH + 22;
        var area = new Rect(left, areaTop, right - left, areaBottom - areaTop);
        if (recorders.Count == 0)
        {
            var empty = Text("Add a recorder in JSPEED to show its status here.", SansMedium, 36, Muted);
            dc.DrawText(empty, new Point(area.Center.X - empty.Width / 2, area.Center.Y - empty.Height / 2));
            return;
        }

        int columns = recorders.Count <= 6 ? 1 : recorders.Count <= 8 ? 2 : recorders.Count <= 12 ? 3 : 4;
        if (columns == 1) DrawRows(dc, area, recorders);
        else DrawTiles(dc, area, recorders, columns);
    }

    private static (IBrush Fill, IBrush Sub, bool Failed) Style(Recorder r)
    {
        if (r.IsRecording) return (RecRed, RecSub, false);
        bool failed = r.IsOnline && r.FailedToStart;
        return (RowIdle, failed ? AmberText : Muted, failed);
    }

    private static void DrawRows(DrawingContext dc, Rect area, IReadOnlyList<Recorder> recorders)
    {
        const double gap = 18;
        double rowH = Math.Min((area.Height - gap * (recorders.Count - 1)) / recorders.Count, 230);
        for (int i = 0; i < recorders.Count; i++)
        {
            var r = recorders[i];
            var rect = new Rect(area.X, area.Y + i * (rowH + gap), area.Width, rowH);
            var (fill, sub, failed) = Style(r);
            dc.DrawRectangle(fill, null, rect, 22, 22);
            double padL = 56;
            if (failed || (!r.IsOnline))
            {
                using (dc.PushClip(new RoundedRect(rect, 22)))
                    dc.FillRectangle(failed ? Amber : Dim, new Rect(rect.X, rect.Y, 18, rect.Height));
                padL = 74;
            }

            var fg = r.IsOnline ? White : Dim;
            var tc = Text(r.TimecodeText, MonoRegular, Math.Min(rowH * 0.62, 132), fg);
            var tcX = rect.Right - 56 - tc.Width;
            dc.DrawText(tc, new Point(tcX, rect.Center.Y - tc.Height / 2));

            var nameSize = Math.Min(rowH * 0.30, 58);
            var subSize = Math.Min(rowH * 0.15, 28);
            var textMax = tcX - rect.X - padL - 32;
            var nameText = Text(r.Name, SansSemi, nameSize, fg, textMax);
            var stateText = Text(r.StateText, SansMedium, subSize, sub);
            var blockH = nameText.Height + 10 + stateText.Height;
            var y = rect.Center.Y - blockH / 2;
            dc.DrawText(nameText, new Point(rect.X + padL, y));
            y += nameText.Height + 10;
            dc.DrawText(stateText, new Point(rect.X + padL, y));

            double lineX = rect.X + padL + stateText.Width + 28;
            if (r.ReportsMedia && r.IsOnline)
            {
                lineX += DrawMedia(dc, r, lineX, y + stateText.Height / 2, 180, subSize, r.IsRecording) + 28;
            }
            if (r.IsOnline && r.CodecShort is { } codec)
            {
                var codecText = Text(codec, SansMedium, subSize, sub, Math.Max(40, tcX - lineX - 24));
                if (lineX + 40 < tcX - 24) dc.DrawText(codecText, new Point(lineX, y));
            }
        }
    }

    private static void DrawTiles(DrawingContext dc, Rect area, IReadOnlyList<Recorder> recorders, int columns)
    {
        int rows = (int)Math.Ceiling(recorders.Count / (double)columns);
        const double gapX = 24, gapY = 18;
        double w = (area.Width - gapX * (columns - 1)) / columns;
        double h = (area.Height - gapY * (rows - 1)) / rows;
        for (int i = 0; i < recorders.Count; i++)
        {
            var r = recorders[i];
            int col = i % columns, row = i / columns;
            var rect = new Rect(area.X + col * (w + gapX), area.Y + row * (h + gapY), w, h);
            var (fill, sub, failed) = Style(r);
            dc.DrawRectangle(fill, null, rect, 18, 18);
            if (failed || !r.IsOnline)
                using (dc.PushClip(new RoundedRect(rect, 18)))
                    dc.FillRectangle(failed ? Amber : Dim, new Rect(rect.X, rect.Y, 12, rect.Height));

            var fg = r.IsOnline ? White : Dim;
            double pad = 26;
            var inner = new Rect(rect.X + pad, rect.Y + 16, rect.Width - pad * 2, rect.Height - 32);

            var kind = Text(r.IsOnline ? r.CodecShort ?? r.ModelName : r.StateText, SansMedium, Math.Min(h * 0.11, 20), sub,
                inner.Width * 0.55);
            var nameText = Text(r.Name, SansSemi, Math.Min(h * 0.18, 34), fg, inner.Width - kind.Width - 12);
            dc.DrawText(nameText, new Point(inner.X, inner.Y));
            dc.DrawText(kind, new Point(inner.Right - kind.Width, inner.Y + nameText.Height - kind.Height));

            var tcSize = Math.Min(h * 0.38, inner.Width / 6.6);
            var tc = Text(r.TimecodeText, MonoRegular, tcSize, fg);
            dc.DrawText(tc, new Point(inner.X, inner.Center.Y - tc.Height / 2 + 2));

            var lineSize = Math.Min(h * 0.11, 22);
            var lineY = inner.Bottom - lineSize * 0.75;
            if (!r.IsOnline) continue;
            if (r.ReportsMedia)
                DrawMedia(dc, r, inner.X, lineY, inner.Width, lineSize, r.IsRecording, fillWidth: true);
            else
            {
                var note = Text(failed ? "Not recording" : "Media not reported", SansMedium, lineSize * 0.9, sub);
                dc.DrawText(note, new Point(inner.X, lineY - note.Height / 2));
            }
        }
    }

    /// <summary>
    /// The ON AIR bar: an ON AIR block in the JSPEED lockup style (red letters on white) on a red
    /// bar, then the stream's name from the key table, then duration and bitrate on the right.
    /// Amber when connecting, interrupted, on an unknown key or split; dark with an outlined
    /// OFF AIR when nothing is streaming.
    /// </summary>
    private static void DrawStreamBar(DrawingContext dc, Rect r, StreamSummary s)
    {
        var (bg, fg, sub) = s.Level switch
        {
            StreamLevel.OnAir => (RecRed, White, RecSub),
            StreamLevel.Warning => (Amber, OnAmber, OnAmberSub),
            _ => (RowIdle, Dim, Muted),
        };
        double h = r.Height;
        dc.DrawRectangle(bg, null, r, 18, 18);

        // ON AIR / OFF AIR block
        bool live = s.IsLive;
        var word = Text(live ? "ON AIR" : "OFF AIR", BrandSemi, h * 0.42, live ? bg : Dim);
        double blockH = h * 0.62, padX = h * 0.22;
        var block = new Rect(r.X + h * 0.19, r.Center.Y - blockH / 2, word.Width + padX * 2, blockH);
        double radius = blockH * 0.12;
        if (live) dc.DrawRectangle(White, null, block, radius, radius);
        else dc.DrawRectangle(null, new Pen(Dim, 3), block.Deflate(1.5), radius, radius);
        dc.DrawText(word, new Point(block.X + padX, block.Center.Y - word.Height / 2));

        // Duration and bitrate, right-aligned
        double x = r.Right - h * 0.36;
        if (s.Bitrate is { } rate)
        {
            var t = Text(rate, SansSemi, h * 0.22, sub);
            x -= t.Width;
            dc.DrawText(t, new Point(x, r.Center.Y - t.Height / 2 + h * 0.03));
            x -= 28;
        }
        if (s.Duration is { } dur)
        {
            var t = Text(dur, MonoRegular, h * 0.44, fg);
            x -= t.Width;
            dc.DrawText(t, new Point(x, r.Center.Y - t.Height / 2));
            x -= 28;
        }

        // Stream name and the line under it
        double tx = block.Right + h * 0.3, maxW = Math.Max(80, x - tx - 12);
        var title = Text(s.Title, SansSemi, h * 0.36, live ? fg : Text2Off, maxW);
        var line = Text(s.Subtitle, SansMedium, h * 0.19, sub, maxW);
        double ty = r.Center.Y - (title.Height + line.Height) / 2;
        dc.DrawText(title, new Point(tx, ty));
        dc.DrawText(line, new Point(tx, ty + title.Height));
    }

    private static readonly IBrush Text2Off = B("#C3C9D3");

    /// <summary>Fill bar (when the device reports a fraction) plus the time or percent left. Returns the width drawn.</summary>
    private static double DrawMedia(DrawingContext dc, Recorder r, double x, double centerY, double width, double size,
        bool onRed, bool fillWidth = false)
    {
        var low = r.MediaLow;
        FormattedText label;
        Rect? pill = null;
        if (low)
        {
            label = Text(r.MediaText, SansBold, size, OnAmber);
            pill = new Rect(0, 0, label.Width + size, label.Height + 4);
        }
        else label = Text(r.MediaText, SansSemi, size, White);

        double labelW = pill?.Width ?? label.Width;
        double barW = r.HasMediaBar ? (fillWidth ? Math.Max(40, width - labelW - 14) : width) : 0;
        if (r.HasMediaBar)
        {
            var track = new Rect(x, centerY - 5, barW, 10);
            dc.DrawRectangle(onRed ? TrackOnRed : TrackOnDark, null, track, 5, 5);
            var frac = r.MediaFraction ?? 0;
            if (frac > 0)
                dc.DrawRectangle(low ? Amber : White, null, new Rect(x, centerY - 5, barW * frac, 10), 5, 5);
        }
        double lx = x + (barW > 0 ? barW + 14 : 0);
        if (pill is { } p)
        {
            var pr = new Rect(lx, centerY - p.Height / 2, p.Width, p.Height);
            dc.DrawRectangle(Amber, null, pr, p.Height / 2, p.Height / 2);
            dc.DrawText(label, new Point(pr.X + size / 2, pr.Y + 2));
        }
        else dc.DrawText(label, new Point(lx, centerY - label.Height / 2));
        return lx + labelW - x;
    }
}
