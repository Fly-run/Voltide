using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using Voltide.Core;

namespace Voltide.UI
{
    /// <summary>
    /// Renders Logo.svg (256 x 256: a green rounded plate with the bolt cut out in
    /// dark violet) at any size, and packs the results into a multi-size .ico.
    ///
    /// Drawn rather than downscaled from one bitmap: the tray asks for 16 px at 100%
    /// and 32 px at 200%, and a single 256 px source blurred badly at 16.
    /// </summary>
    public static class IconFactory
    {
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private const string BoltPath =
            "M104.13,163.15h-52.65c-4.24,0-7-4.46-5.1-8.25L130.53-13.1h96.53l-72.07,113.8" +
            "h63.06c4.87,0,7.5,5.71,4.33,9.41L33.7,330.7l70.43-167.55Z";

        private static readonly float[] Stops =
            { 0f, 0.07f, 0.15f, 0.24f, 0.32f, 0.41f, 0.5f, 0.59f, 0.66f, 0.73f, 0.8f, 0.87f, 0.94f, 1f };

        private static readonly Color[] Colors = new Color[]
        {
            Color.Black,
            Color.FromArgb(0x11, 0x0f, 0x24),
            Color.FromArgb(0x22, 0x1f, 0x46),
            Color.FromArgb(0x2f, 0x2a, 0x61),
            Color.FromArgb(0x38, 0x33, 0x74),
            Color.FromArgb(0x3e, 0x38, 0x80),
            Color.FromArgb(0x40, 0x3a, 0x84),
            Color.FromArgb(0x3e, 0x38, 0x80),
            Color.FromArgb(0x38, 0x33, 0x75),
            Color.FromArgb(0x30, 0x2b, 0x63),
            Color.FromArgb(0x23, 0x20, 0x49),
            Color.FromArgb(0x13, 0x11, 0x28),
            Color.Black,
            Color.Black
        };

        private static GraphicsPath _bolt;
        private static GraphicsPath _plate;

        private static void EnsurePaths()
        {
            if (_bolt != null) return;

            _bolt = SvgPath.Parse(BoltPath);
            _plate = RoundRect(0f, 0f, 256f, 256f, 50f);
        }

        /// <summary>Draws the logo at the requested pixel size.</summary>
        public static Bitmap Render(int size)
        {
            EnsurePaths();

            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);

            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(Color.Transparent);

                float k = size / 256f;
                g.ScaleTransform(k, k);

                using (SolidBrush green = new SolidBrush(Color.FromArgb(116, 221, 124)))
                {
                    g.FillPath(green, _plate);
                }

                GraphicsState state = g.Save();
                g.SetClip(_plate);

                using (LinearGradientBrush grad = new LinearGradientBrush(
                    new PointF(32.18f, 320.43f), new PointF(344.18f, -267.28f),
                    Color.Black, Color.Black))
                {
                    ColorBlend blend = new ColorBlend();
                    blend.Positions = Stops;
                    blend.Colors = Colors;
                    grad.InterpolationColors = blend;

                    g.FillPath(grad, _bolt);
                }

                g.Restore(state);
            }

            return bmp;
        }

        public static Icon ToIcon(int size)
        {
            using (Bitmap bmp = Render(size))
            {
                IntPtr handle = bmp.GetHicon();
                Icon icon = (Icon)Icon.FromHandle(handle).Clone();
                DestroyIcon(handle);
                return icon;
            }
        }

        /// <summary>
        /// Tray icon for one device: the top half draws the device shape (mouse,
        /// keyboard, headset, or a battery fallback), the percentage sits below it.
        /// A null percentage (asleep / unknown) draws a grey shape with no number.
        /// </summary>
        public static Icon ToIcon(int size, DeviceKind kind, int? percent)
        {
            using (Bitmap bmp = RenderDevice(size, kind, percent))
            {
                IntPtr handle = bmp.GetHicon();
                Icon icon = (Icon)Icon.FromHandle(handle).Clone();
                DestroyIcon(handle);
                return icon;
            }
        }

        /// <summary>Device-shape glyph at the requested pixel size.</summary>
        public static Bitmap RenderDevice(int size, DeviceKind kind, int? percent)
        {
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            float k = size / 32f;

            bool active = percent.HasValue;
            Color fill = active ? CardRenderer.PrimaryFor(percent.Value)
                                : Color.FromArgb(120, 120, 120);
            Color outline = active ? Color.FromArgb(225, 225, 225)
                                   : Color.FromArgb(120, 120, 120);

            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                switch (kind)
                {
                    case DeviceKind.Mouse: DrawMouse(g, k, fill, outline); break;
                    case DeviceKind.Keyboard: DrawKeyboard(g, k, fill, outline); break;
                    case DeviceKind.Headset: DrawHeadset(g, k, fill, outline); break;
                    default: RenderBatteryInto(g, k, percent, fill, outline); break;
                }

                if (active) DrawPercent(g, k, percent.Value);
            }

            return bmp;
        }

        // -------------------------------------------------------- shapes

        private static void DrawMouse(Graphics g, float k, Color fill, Color outline)
        {
            // Vertical rounded body, a centre split from the top, and a small wheel.
            RectangleF body = new RectangleF(11f * k, 3f * k, 10f * k, 16f * k);
            using (GraphicsPath path = RoundRect(body.X, body.Y, body.Width, body.Height, 5f * k))
            using (SolidBrush brush = new SolidBrush(fill))
            using (Pen pen = new Pen(outline, Math.Max(1f, 1.2f * k)))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            float midX = body.X + body.Width / 2f;
            using (Pen pen = new Pen(outline, Math.Max(1f, k)))
            {
                g.DrawLine(pen, midX, body.Y + 1f * k, midX, body.Y + 8f * k);
            }

            using (SolidBrush wheel = new SolidBrush(outline))
            {
                g.FillRectangle(wheel, midX - 0.9f * k, body.Y + 4f * k, 1.8f * k, 3.5f * k);
            }
        }

        private static void DrawKeyboard(Graphics g, float k, Color fill, Color outline)
        {
            RectangleF body = new RectangleF(5f * k, 7f * k, 22f * k, 11f * k);
            using (GraphicsPath path = RoundRect(body.X, body.Y, body.Width, body.Height, 2.5f * k))
            using (SolidBrush brush = new SolidBrush(fill))
            using (Pen pen = new Pen(outline, Math.Max(1f, 1.2f * k)))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            // Two rows of key caps.
            using (SolidBrush key = new SolidBrush(outline))
            {
                for (int row = 0; row < 2; row++)
                {
                    float ky = body.Y + 2.5f * k + row * 4.5f * k;
                    for (int col = 0; col < 5; col++)
                    {
                        float kx = body.X + 2.5f * k + col * 4f * k;
                        g.FillRectangle(key, kx, ky, 2.4f * k, 2.2f * k);
                    }
                }
            }
        }

        private static void DrawHeadset(Graphics g, float k, Color fill, Color outline)
        {
            // Headband arc across the top, two ear cups hanging at the sides.
            RectangleF band = new RectangleF(9f * k, 3f * k, 14f * k, 12f * k);
            using (Pen pen = new Pen(fill, Math.Max(1.5f, 2.2f * k)))
            {
                g.DrawArc(pen, band, 180f, 180f);
            }

            using (SolidBrush cup = new SolidBrush(fill))
            {
                g.FillRectangle(cup, 6.5f * k, 8f * k, 4f * k, 8f * k);
                g.FillRectangle(cup, 21.5f * k, 8f * k, 4f * k, 8f * k);
            }
            using (Pen pen = new Pen(outline, Math.Max(1f, 1f * k)))
            {
                g.DrawRectangle(pen, 6.5f * k, 8f * k, 4f * k, 8f * k);
                g.DrawRectangle(pen, 21.5f * k, 8f * k, 4f * k, 8f * k);
            }
        }

        private static void RenderBatteryInto(Graphics g, float k, int? percent, Color fill, Color outline)
        {
            float bodyX = 4f * k, bodyY = 4f * k, bodyW = 20f * k, bodyH = 13f * k;
            float nubW = 3f * k, nubH = 5f * k;
            float inset = 2f * k;

            if (percent.HasValue)
            {
                int p = Math.Max(0, Math.Min(100, percent.Value));
                float fillW = (bodyW - inset * 2f) * p / 100f;
                using (SolidBrush bar = new SolidBrush(fill))
                {
                    g.FillRectangle(bar, bodyX + inset, bodyY + inset, fillW, bodyH - inset * 2f);
                }
            }

            using (Pen pen = new Pen(outline, Math.Max(1f, 1.2f * k)))
            {
                g.DrawRectangle(pen, bodyX, bodyY, bodyW, bodyH);
            }
            using (SolidBrush nub = new SolidBrush(outline))
            {
                g.FillRectangle(nub, bodyX + bodyW, bodyY + (bodyH - nubH) / 2f, nubW, nubH);
            }
        }

        private static void DrawPercent(Graphics g, float k, int percent)
        {
            string text = percent.ToString();
            Font font = FontManager.Get(FontKind.MontserratSemiBold, 9.5f * k);
            SizeF sz = g.MeasureString(text, font);
            float x = (32f * k - sz.Width) / 2f;
            float y = 21f * k;

            Color halo = Color.FromArgb(17, 15, 36);
            using (SolidBrush haloBrush = new SolidBrush(halo))
            {
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        if (dx != 0 || dy != 0)
                            g.DrawString(text, font, haloBrush, x + dx * k, y + dy * k);
            }
            using (SolidBrush textBrush = new SolidBrush(Color.White))
            {
                g.DrawString(text, font, textBrush, x, y);
            }
        }

        /// <summary>
        /// Writes a Windows .ico containing one PNG entry per size. PNG entries are
        /// what Vista and later prefer; a single-size icon would be resampled by
        /// Explorer and the tray, which is exactly the blur we are avoiding.
        /// </summary>
        public static void WriteIco(string path, int[] sizes)
        {
            List<byte[]> images = new List<byte[]>();

            foreach (int size in sizes)
            {
                using (Bitmap bmp = Render(size))
                using (MemoryStream ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    images.Add(ms.ToArray());
                }
            }

            using (FileStream fs = File.Create(path))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                w.Write((ushort)0);              // reserved
                w.Write((ushort)1);              // type: icon
                w.Write((ushort)sizes.Length);

                long offset = 6 + 16 * sizes.Length;

                for (int i = 0; i < sizes.Length; i++)
                {
                    int s = sizes[i];
                    byte dim = (byte)(s >= 256 ? 0 : s);

                    w.Write(dim);                // width
                    w.Write(dim);                // height
                    w.Write((byte)0);            // palette
                    w.Write((byte)0);            // reserved
                    w.Write((ushort)1);          // colour planes
                    w.Write((ushort)32);         // bits per pixel
                    w.Write((uint)images[i].Length);
                    w.Write((uint)offset);

                    offset += images[i].Length;
                }

                foreach (byte[] image in images)
                {
                    w.Write(image);
                }
            }
        }

        private static GraphicsPath RoundRect(float x, float y, float w, float h, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2f;

            p.AddArc(x, y, d, d, 180f, 90f);
            p.AddArc(x + w - d, y, d, d, 270f, 90f);
            p.AddArc(x + w - d, y + h - d, d, d, 0f, 90f);
            p.AddArc(x, y + h - d, d, d, 90f, 90f);
            p.CloseFigure();

            return p;
        }
    }
}
