$src = @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

public static class FamilyMahjongIcon
{
    static GraphicsPath RoundRect(int x, int y, int w, int h, int r)
    {
        var p = new GraphicsPath();
        int d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static void DrawTile(Graphics g, int tx, int ty, int tw, int th, float angle, int alpha)
    {
        var state = g.Save();
        g.TranslateTransform(tx + tw / 2f, ty + th / 2f);
        g.RotateTransform(angle);
        g.TranslateTransform(-tw / 2f, -th / 2f);

        using (var shadow = RoundRect(6, 9, tw, th, 12))
        using (var sb = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
            g.FillPath(sb, shadow);

        using (var body = RoundRect(0, 0, tw, th, 12))
        using (var ivory = new LinearGradientBrush(new PointF(0, 0), new PointF(tw, th),
                   Color.FromArgb(alpha, 255, 254, 244), Color.FromArgb(alpha, 226, 216, 182)))
        using (var pen = new Pen(Color.FromArgb(180, 198, 166, 96), 3))
        {
            g.FillPath(ivory, body);
            g.DrawPath(pen, body);
        }
        g.Restore(state);
    }

    static Bitmap Draw(int size)
    {
        var bmp = new Bitmap(size, size);
        var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float s = size / 256f;
        g.ScaleTransform(s, s);

        using (var bg = RoundRect(6, 6, 244, 244, 52))
        using (var brush = new LinearGradientBrush(new PointF(20, 10), new PointF(236, 250),
                   Color.FromArgb(255, 12, 84, 46), Color.FromArgb(255, 4, 48, 26)))
        using (var gold = new Pen(Color.FromArgb(255, 232, 194, 102), 6))
        {
            g.FillPath(brush, bg);
            g.DrawPath(gold, bg);
        }
        using (var inner = RoundRect(21, 21, 214, 214, 40))
        using (var goldSoft = new Pen(Color.FromArgb(120, 232, 194, 102), 2))
            g.DrawPath(goldSoft, inner);

        DrawTile(g, 40, 60, 128, 164, -14f, 150);
        DrawTile(g, 88, 56, 128, 164, 12f, 170);

        var state = g.Save();
        g.TranslateTransform(136, 138);
        g.RotateTransform(-3f);
        g.TranslateTransform(-92, -108);

        using (var shadow = RoundRect(6, 9, 184, 216, 16))
        using (var sb = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            g.FillPath(sb, shadow);

        using (var face = RoundRect(0, 0, 184, 216, 16))
        using (var ivory = new LinearGradientBrush(new PointF(10, 0), new PointF(174, 216),
                   Color.FromArgb(255, 255, 253, 246), Color.FromArgb(255, 228, 218, 184)))
        using (var pen = new Pen(Color.FromArgb(255, 196, 160, 86), 5))
        {
            g.FillPath(ivory, face);
            g.DrawPath(pen, face);
        }

        using (var gloss = RoundRect(12, 12, 160, 56, 10))
        using (var gb = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
            g.FillPath(gb, gloss);

        using (var font = new Font("KaiTi", 118, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var red = new SolidBrush(Color.FromArgb(255, 178, 34, 34)))
        {
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("\u5BB6", font, red, new RectangleF(6, 14, 172, 216), sf);
        }
        g.Restore(state);
        g.Dispose();
        return bmp;
    }

    public static void Save(string icoPath, string pngPath)
    {
        int[] sizes = { 16, 32, 48, 64, 128, 256 };
        var pngs = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
        {
            using (var bmp = Draw(sizes[i]))
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                pngs[i] = ms.ToArray();
                if (sizes[i] == 256) bmp.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write((ushort)0);
            bw.Write((ushort)1);
            bw.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte wh = sizes[i] == 256 ? (byte)0 : (byte)sizes[i];
                bw.Write(wh); bw.Write(wh);
                bw.Write((byte)0); bw.Write((byte)0);
                bw.Write((ushort)1); bw.Write((ushort)32);
                bw.Write((uint)pngs[i].Length);
                bw.Write((uint)offset);
                offset += pngs[i].Length;
            }
            foreach (var png in pngs) bw.Write(png);
            bw.Flush();
            File.WriteAllBytes(icoPath, ms.ToArray());
        }
    }
}
'@

Add-Type -TypeDefinition $src -ReferencedAssemblies "System.Drawing", "System.Core" -ErrorAction Stop
$ico = "E:\cc_study\mj\src\Mahjong.App\Assets\FamilyMahjong.ico"
$png = "E:\cc_study\mj\app_icon_preview.png"
[FamilyMahjongIcon]::Save($ico, $png)
Write-Output ("ICO " + (Get-Item $ico).Length + " bytes")
Write-Output ("PNG " + (Get-Item $png).Length + " bytes")
