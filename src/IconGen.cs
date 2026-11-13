// Renders the AllFind logo into a multi-size .ico plus a preview PNG.
// Sizes <= 64 are stored as classic 32-bit DIBs (readable everywhere, incl. tray/GDI+); 128/256 as PNG.
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AllFind
{
    static class IconGen
    {
        [STAThread]
        static void Main(string[] args)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
            var blobs = new List<byte[]>();
            foreach (int s in sizes) blobs.Add(s <= 64 ? Dib(Render(s), s) : Png(Render(s)));
            var prev = new PngBitmapEncoder(); prev.Frames.Add(BitmapFrame.Create(Render(512)));
            using (var f = File.Create(Path.ChangeExtension(args[0], ".png"))) prev.Save(f);
            using (var w = new BinaryWriter(File.Create(args[0])))
            {
                w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
                int off = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    byte b = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                    w.Write(b); w.Write(b); w.Write((byte)0); w.Write((byte)0);
                    w.Write((ushort)1); w.Write((ushort)32); w.Write((uint)blobs[i].Length); w.Write((uint)off);
                    off += blobs[i].Length;
                }
                foreach (var p in blobs) w.Write(p);
            }
        }

        static BitmapSource Render(int s)
        {
            int ss = s * 4; // supersample
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) Logo.Draw(dc, ss, s >= 32);
            var big = new RenderTargetBitmap(ss, ss, 96, 96, PixelFormats.Pbgra32);
            big.Render(dv);
            var dv2 = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(dv2, BitmapScalingMode.HighQuality);
            using (var dc = dv2.RenderOpen()) dc.DrawImage(big, new Rect(0, 0, s, s));
            var outBmp = new RenderTargetBitmap(s, s, 96, 96, PixelFormats.Pbgra32);
            outBmp.Render(dv2);
            return outBmp;
        }

        static byte[] Png(BitmapSource b)
        {
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(b));
            using (var ms = new MemoryStream()) { enc.Save(ms); return ms.ToArray(); }
        }

        static byte[] Dib(BitmapSource b, int s)
        {
            var straight = new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
            var px = new byte[s * s * 4];
            straight.CopyPixels(px, s * 4, 0);
            int maskStride = ((s + 31) / 32) * 4;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32);
                w.Write(0); w.Write(s * s * 4 + maskStride * s); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
                for (int y = s - 1; y >= 0; y--) w.Write(px, y * s * 4, s * 4); // bottom-up
                w.Write(new byte[maskStride * s]); // AND mask unused with 32-bit alpha
                return ms.ToArray();
            }
        }
    }
}
