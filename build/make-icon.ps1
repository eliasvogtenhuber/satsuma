<#
.SYNOPSIS
    Draws src\Satsuma.App\Assets\satsuma.ico.

.DESCRIPTION
    The same mark the tray draws at runtime, written out at the sizes Windows picks
    from. The tray keeps drawing its own -- it has to, to stay crisp at any tray scale --
    but the exe needs a real file, and Explorer, the taskbar and the Alt-Tab list all
    read it from there.

    Run it when the mark changes. The result is committed, so an ordinary build needs
    nothing but the file.

    The work is in C# rather than PowerShell because an .ico is a binary format with
    byte-level fields, and PowerShell's habit of widening integers to doubles turns
    quietly wrong headers out that Windows then refuses.
#>

[CmdletBinding()]
param(
    [string] $Output = (Join-Path $PSScriptRoot '..\src\Satsuma.App\Assets\satsuma.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class SatsumaIcon
{
    /// <summary>The tray mark, laid out on a 32-unit grid and scaled from it.</summary>
    private static Bitmap Draw(int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);

        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var k = size / 32f;
            var body = new RectangleF(3 * k, 6 * k, 26 * k, 24 * k);

            using (var brush = new LinearGradientBrush(
                body, Color.FromArgb(0xFF, 0x9A, 0x3F), Color.FromArgb(0xEE, 0x6C, 0x1F), 60f))
            {
                g.FillEllipse(brush, body);
            }

            // A leaf, so it reads as fruit rather than as a dot.
            using (var leaf = new SolidBrush(Color.FromArgb(0x4C, 0xAF, 0x50)))
            {
                g.FillClosedCurve(leaf, new[]
                {
                    new PointF(16 * k, 7 * k), new PointF(23 * k, 2 * k),
                    new PointF(25 * k, 8 * k), new PointF(18 * k, 9 * k)
                });
            }

            using (var stem = new Pen(Color.FromArgb(0x5D, 0x40, 0x37), 2f * k))
            {
                g.DrawLine(stem, 16 * k, 8 * k, 15 * k, 4f * k);
            }
        }

        return bitmap;
    }

    /// <summary>
    /// One frame the old way: a BITMAPINFOHEADER whose height covers the colour rows
    /// and the mask rows, then 32-bit BGRA bottom-up, then a 1-bit mask. The alpha
    /// channel is what draws; the mask has to be there and has to be right, because
    /// the parts of Windows that predate alpha still read it.
    /// </summary>
    private static byte[] ToDib(Bitmap bitmap)
    {
        var w = bitmap.Width;
        var h = bitmap.Height;

        var locked = bitmap.LockBits(
            new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        var pixels = new byte[locked.Stride * h];
        System.Runtime.InteropServices.Marshal.Copy(locked.Scan0, pixels, 0, pixels.Length);
        bitmap.UnlockBits(locked);

        var maskStride = ((w + 31) / 32) * 4;

        var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);

        writer.Write(40u);                              // biSize
        writer.Write(w);                                // biWidth
        writer.Write(h * 2);                            // biHeight: colour then mask
        writer.Write((ushort)1);                        // biPlanes
        writer.Write((ushort)32);                       // biBitCount
        writer.Write(0u);                               // biCompression: BI_RGB
        writer.Write((uint)((w * 4 * h) + (maskStride * h)));
        writer.Write(0); writer.Write(0);               // pixels per metre
        writer.Write(0u); writer.Write(0u);             // palette

        for (var y = h - 1; y >= 0; y--)
        {
            writer.Write(pixels, y * locked.Stride, w * 4);
        }

        var row = new byte[maskStride];

        for (var y = h - 1; y >= 0; y--)
        {
            Array.Clear(row, 0, row.Length);

            for (var x = 0; x < w; x++)
            {
                // A set bit lets the background through.
                if (pixels[(y * locked.Stride) + (x * 4) + 3] == 0)
                {
                    row[x / 8] |= (byte)(0x80 >> (x % 8));
                }
            }

            writer.Write(row, 0, row.Length);
        }

        writer.Flush();
        var bytes = stream.ToArray();
        writer.Dispose();
        return bytes;
    }

    private static byte[] ToPng(Bitmap bitmap)
    {
        using (var stream = new MemoryStream())
        {
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
    }

    public static string Write(string path, int[] sizes)
    {
        var frames = new List<KeyValuePair<int, byte[]>>();

        foreach (var size in sizes)
        {
            // PNG for the 256, the only way it is a sane size and read everywhere
            // that wants one. A DIB for the rest: an all-PNG icon is legal and half
            // of Windows still will not open it.
            using (var bitmap = Draw(size))
            {
                frames.Add(new KeyValuePair<int, byte[]>(
                    size, size >= 256 ? ToPng(bitmap) : ToDib(bitmap)));
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));

        using (var file = File.Create(path))
        using (var writer = new BinaryWriter(file))
        {
            writer.Write((ushort)0);              // reserved
            writer.Write((ushort)1);              // type: icon
            writer.Write((ushort)frames.Count);

            var offset = 6 + (16 * frames.Count);

            foreach (var frame in frames)
            {
                // 256 is written as 0; that is what the format uses for it.
                var dimension = (byte)(frame.Key >= 256 ? 0 : frame.Key);

                writer.Write(dimension);          // width
                writer.Write(dimension);          // height
                writer.Write((byte)0);            // palette entries: truecolour
                writer.Write((byte)0);            // reserved
                writer.Write((ushort)1);          // colour planes
                writer.Write((ushort)32);         // bits per pixel
                writer.Write((uint)frame.Value.Length);
                writer.Write((uint)offset);

                offset += frame.Value.Length;
            }

            foreach (var frame in frames) writer.Write(frame.Value);
        }

        return string.Format("{0} frames, {1:N0} bytes", frames.Count, new FileInfo(path).Length);
    }
}
'@

$target = [System.IO.Path]::GetFullPath($Output)
$summary = [SatsumaIcon]::Write($target, @(16, 24, 32, 48, 64, 128, 256))

Write-Host "satsuma.ico: $summary" -ForegroundColor Green
Write-Host "  $target" -ForegroundColor DarkGray
