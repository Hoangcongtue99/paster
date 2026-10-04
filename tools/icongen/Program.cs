using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

// Writes a multi-resolution .ico. Each size is rendered on its own rather than
// downscaled from one big bitmap: at 16px a scaled-down glyph turns to mush,
// so the small sizes get proportionally thinner padding and a larger letter.
internal static class Program
{
    private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    private static readonly Color Face = Color.FromArgb(0x4C, 0x8D, 0xFF);
    private static readonly Color FaceDeep = Color.FromArgb(0x2F, 0x6B, 0xD8);

    private static int Main(string[] args)
    {
        var target = args.Length > 0 ? args[0] : "paster.ico";

        var frames = new List<byte[]>();
        foreach (var size in Sizes)
            frames.Add(RenderPng(size));

        WriteIco(target, Sizes, frames);

        Console.WriteLine($"wrote {target}");
        for (var i = 0; i < Sizes.Length; i++)
            Console.WriteLine($"  {Sizes[i],3}px  {frames[i].Length,6} bytes");

        return 0;
    }

    private static byte[] RenderPng(int size)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = size >= 32
                ? TextRenderingHint.AntiAliasGridFit
                : TextRenderingHint.SingleBitPerPixelGridFit;
            g.Clear(Color.Transparent);

            // Tiny sizes need almost all of the canvas to stay legible.
            var pad = size <= 24 ? size * 0.02f : size * 0.06f;
            var box = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);
            var radius = size <= 24 ? size * 0.16f : size * 0.22f;

            using (var path = RoundedRect(box, radius))
            using (var fill = new LinearGradientBrush(box, Face, FaceDeep, LinearGradientMode.Vertical))
                g.FillPath(fill, path);

            // Centre the glyph's own ink rather than its line box. DrawString
            // centres ascent+descent, and "P" has no descender, so that leaves
            // it visibly high - most obvious at 16px, where one pixel is a lot.
            var emSize = size * (size <= 24 ? 0.78f : 0.64f);

            using var family = new FontFamily("Segoe UI");
            using var glyph = new GraphicsPath();
            glyph.AddString("P", family, (int)FontStyle.Bold, emSize,
                PointF.Empty, StringFormat.GenericTypographic);

            var ink = glyph.GetBounds();
            if (ink.Width > 0 && ink.Height > 0)
            {
                using var move = new Matrix();
                // Rounded to whole pixels: at 16px a sub-pixel offset turns the
                // stem of the P into two grey columns instead of one white one.
                move.Translate(
                    (float)Math.Round(box.X + box.Width / 2 - (ink.X + ink.Width / 2)),
                    (float)Math.Round(box.Y + box.Height / 2 - (ink.Y + ink.Height / 2)));

                glyph.Transform(move);
            }

            using var white = new SolidBrush(Color.White);
            g.FillPath(white, glyph);
        }

        using var buffer = new MemoryStream();
        bitmap.Save(buffer, ImageFormat.Png);
        return buffer.ToArray();
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

    /// <summary>
    /// ICONDIR + one ICONDIRENTRY per size + the PNG payloads. PNG-compressed
    /// icon frames are understood by every Windows since Vista and keep the
    /// 256px frame from dominating the file size.
    /// </summary>
    private static void WriteIco(string path, int[] sizes, List<byte[]> frames)
    {
        using var file = File.Create(path);
        using var w = new BinaryWriter(file);

        w.Write((ushort)0);               // reserved
        w.Write((ushort)1);               // type: icon
        w.Write((ushort)sizes.Length);

        var offset = 6 + 16 * sizes.Length;

        for (var i = 0; i < sizes.Length; i++)
        {
            // 0 encodes 256 in a single byte.
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0);             // palette size
            w.Write((byte)0);             // reserved
            w.Write((ushort)1);           // colour planes
            w.Write((ushort)32);          // bits per pixel
            w.Write(frames[i].Length);
            w.Write(offset);

            offset += frames[i].Length;
        }

        foreach (var frame in frames)
            w.Write(frame);
    }
}
