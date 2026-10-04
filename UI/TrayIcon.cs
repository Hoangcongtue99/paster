using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Paster.UI;

/// <summary>
/// Draws the tray icon at runtime instead of shipping a .ico, which keeps the
/// whole project plain text and lets the glyph scale to the current DPI.
/// </summary>
internal static class TrayIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>
    /// The real application icon, at whatever size the shell wants for the
    /// notification area. Falls back to drawing one if the resource cannot be
    /// read, so a missing icon can never stop the app from reaching the tray.
    /// </summary>
    public static Icon Create()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("paster.ico", UriKind.Relative));

            using var stream = resource?.Stream;
            if (stream is not null)
            {
                // Picking the matching frame beats letting the shell downscale
                // the 256px one, which smears the glyph at tray size.
                var wanted = SystemInformation.SmallIconSize;
                return new Icon(stream, wanted.Width, wanted.Height);
            }
        }
        catch
        {
            // Fall through to the drawn version.
        }

        return Draw();
    }

    private static Icon Draw(int size = 32)
    {
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            var pad = size * 0.06f;
            var box = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);

            using (var fill = new SolidBrush(Color.FromArgb(0x4C, 0x8D, 0xFF)))
            using (var path = RoundedRect(box, size * 0.22f))
                g.FillPath(fill, path);

            // Same glyph-ink centring as the real icon, so the fallback cannot
            // look subtly different from the resource it stands in for.
            using var family = new FontFamily("Segoe UI");
            using var glyph = new GraphicsPath();
            glyph.AddString("P", family, (int)FontStyle.Bold, size * 0.64f,
                PointF.Empty, StringFormat.GenericTypographic);

            var ink = glyph.GetBounds();
            if (ink.Width > 0 && ink.Height > 0)
            {
                using var move = new Matrix();
                move.Translate(
                    (float)Math.Round(box.X + box.Width / 2 - (ink.X + ink.Width / 2)),
                    (float)Math.Round(box.Y + box.Height / 2 - (ink.Y + ink.Height / 2)));

                glyph.Transform(move);
            }

            using var white = new SolidBrush(Color.White);
            g.FillPath(white, glyph);
        }

        // Icon.FromHandle does not own its handle, so clone into a managed icon
        // and release the native one immediately.
        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();

        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();

        return path;
    }
}

/// <summary>
/// Paints the tray menu itself rather than configuring a colour table.
///
/// ToolStripProfessionalRenderer consults ProfessionalColorTable only part of
/// the time: with visual styles on, the selected-item background comes from the
/// system instead, which paints white under light-coloured text. Overriding the
/// draw calls is the only way to get a menu that is dark in every state.
/// </summary>
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    private static readonly Color Surface = Color.FromArgb(0x2B, 0x2D, 0x31);
    private static readonly Color Hover = Color.FromArgb(0x3F, 0x42, 0x48);
    private static readonly Color Edge = Color.FromArgb(0x45, 0x48, 0x4E);
    private static readonly Color Text = Color.FromArgb(0xE8, 0xE8, 0xEA);
    private static readonly Color TextDisabled = Color.FromArgb(0x7A, 0x7D, 0x83);
    private static readonly Color Accent = Color.FromArgb(0x4C, 0x8D, 0xFF);

    public DarkMenuRenderer() : base(new ProfessionalColorTable { UseSystemColors = false })
    {
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Surface);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        // Painted the same as the menu body: a separately shaded gutter just
        // draws attention to the empty space beside most of the items.
        using var brush = new SolidBrush(Surface);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Edge);
        var r = e.AffectedBounds;
        e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var hot = e.Item.Selected && e.Item.Enabled;
        using var brush = new SolidBrush(hot ? Hover : Surface);
        e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Text : TextDisabled;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var pen = new Pen(Edge);
        e.Graphics.DrawLine(pen, 6, y, e.Item.Width - 6, y);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // The stock tick is a system-themed glyph that all but vanishes on a
        // dark background, so draw a plain one in the accent colour.
        var r = e.ImageRectangle;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var pen = new Pen(Accent, 2f);
        var left = r.Left + r.Width * 0.22f;
        var mid = r.Left + r.Width * 0.44f;
        var right = r.Right - r.Width * 0.18f;

        g.DrawLines(pen, new[]
        {
            new PointF(left, r.Top + r.Height * 0.52f),
            new PointF(mid, r.Bottom - r.Height * 0.26f),
            new PointF(right, r.Top + r.Height * 0.28f)
        });
    }
}
