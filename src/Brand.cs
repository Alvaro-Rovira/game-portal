using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace Hud
{
    // Marca del portal: losa negra con un mando ambar. Se dibuja con vectores, asi que sale nitida a
    // cualquier tamaño: es el icono del .exe (dock / barra) y el logo de la cabecera del portal.
    static class Brand
    {
        static readonly Color Hi = Color.FromArgb(255, 204, 112), Lo = Color.FromArgb(238, 104, 42);

        // silueta del mando en un lienzo de 100 x 100 (mitad derecha; la izquierda es su espejo)
        static GraphicsPath PadPath(RectangleF r)
        {
            float[][] right =
            {
                new float[] { 50, 27 },
                new float[] { 57, 27,  62, 23,  70, 23 },
                new float[] { 80, 23,  87, 26,  90, 33 },
                new float[] { 95, 44,  98.5f, 58,  98.5f, 67 },
                new float[] { 98.5f, 76,  93, 81,  86.5f, 79.5f },
                new float[] { 81, 78,  77.5f, 72,  73.5f, 66.5f },
                new float[] { 70.5f, 62.5f,  66, 61,  61, 61 },
                new float[] { 56, 61,  53, 61,  50, 61 },
            };
            var pts = new List<PointF>();
            Func<float, float, PointF> map = (x, y) => new PointF(r.X + x / 100f * r.Width, r.Y + y / 100f * r.Height);
            pts.Add(map(right[0][0], right[0][1]));
            for (int i = 1; i < right.Length; i++)
                for (int k = 0; k < 6; k += 2) pts.Add(map(right[i][k], right[i][k + 1]));
            // izquierda: mismos tramos al reves y en espejo
            var left = new List<PointF>();
            for (int i = right.Length - 1; i >= 1; i--)
            {
                var seg = right[i];
                var prev = right[i - 1];
                float px = prev.Length == 2 ? prev[0] : prev[4], py = prev.Length == 2 ? prev[1] : prev[5];
                left.Add(map(100 - seg[2], seg[3])); left.Add(map(100 - seg[0], seg[1])); left.Add(map(100 - px, py));
            }
            // el tramo derecho termina en (50,61), el izquierdo empieza ahi; recorremos: derecha (arriba->abajo), izquierda (abajo->arriba)
            var all = new List<PointF>(pts);
            all.AddRange(left);
            var p = new GraphicsPath();
            p.AddBeziers(all.ToArray());
            p.CloseFigure();
            return p;
        }

        public static void Draw(Graphics g, RectangleF r, bool tile = true)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float s = r.Width;
            if (tile)
            {
                float rad = s * 0.23f;
                using (var p = G.Round(r, rad))
                {
                    using (var lg = new LinearGradientBrush(r, Color.FromArgb(34, 33, 31), Color.FromArgb(8, 8, 8), 90f)) g.FillPath(lg, p);
                    // halo calido detras del mando
                    using (var glow = new GraphicsPath())
                    {
                        var gr = new RectangleF(r.X - s * 0.02f, r.Y + s * 0.2f, s * 1.04f, s * 0.68f);
                        glow.AddEllipse(gr);
                        using (var pg = new PathGradientBrush(glow))
                        {
                            pg.CenterColor = Color.FromArgb(s >= 48 ? 70 : 45, 242, 150, 70);
                            pg.SurroundColors = new[] { Color.FromArgb(0, 242, 150, 70) };
                            var old = g.Clip; g.SetClip(p, CombineMode.Intersect);
                            g.FillPath(pg, glow);
                            g.Clip = old;
                        }
                    }
                    // filo de luz arriba
                    if (s >= 24)
                    {
                        var rin = RectangleF.Inflate(r, -Math.Max(0.5f, s * 0.006f), -Math.Max(0.5f, s * 0.006f));
                        using (var pin = G.Round(rin, rad - s * 0.006f))
                        using (var lg = new LinearGradientBrush(r, Color.FromArgb(70, 255, 240, 220), Color.FromArgb(0, 255, 240, 220), 90f))
                        {
                            var bl = new Blend(); bl.Positions = new[] { 0f, 0.35f, 1f }; bl.Factors = new[] { 0f, 0.85f, 1f }; lg.Blend = bl;
                            using (var pen = new Pen(lg, Math.Max(1f, s * 0.012f))) g.DrawPath(pen, pin);
                        }
                    }
                }
            }
            // el mando ocupa ~74 % del ancho
            float pw = s * (tile ? 0.76f : 1f);
            var pr = new RectangleF(r.X + (s - pw) / 2, r.Y + (r.Height - pw) / 2 + (tile ? s * 0.035f : 0), pw, pw);
            using (var body = PadPath(pr))
            {
                // sombra suave debajo (silueta difuminada)
                if (tile && s >= 32)
                {
                    int q = Math.Max(16, (int)(s / 4));
                    using (var mask = new Bitmap(q, q, PixelFormat.Format32bppArgb))
                    {
                        using (var mg = Graphics.FromImage(mask))
                        using (var m = new Matrix())
                        using (var sh = (GraphicsPath)body.Clone())
                        {
                            mg.SmoothingMode = SmoothingMode.AntiAlias;
                            m.Scale(q / s, q / s); m.Translate(-r.X, -r.Y + s * 0.03f);
                            sh.Transform(m);
                            using (var br = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) mg.FillPath(br, sh);
                        }
                        G.Blur(mask, Math.Max(1, q / 14));
                        var oldI = g.InterpolationMode; g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.DrawImage(mask, r.X, r.Y, s, s);
                        g.InterpolationMode = oldI;
                    }
                }
                var bb = body.GetBounds();
                using (var lg = new LinearGradientBrush(new RectangleF(bb.X, bb.Y - 1, bb.Width, bb.Height + 2), Hi, Lo, 90f)) g.FillPath(lg, body);
                // brillo en la mitad de arriba
                var old = g.Clip; g.SetClip(body, CombineMode.Intersect);
                using (var lg = new LinearGradientBrush(new RectangleF(bb.X, bb.Y - 1, bb.Width, bb.Height * 0.5f + 2), Color.FromArgb(90, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                    g.FillRectangle(lg, bb.X, bb.Y, bb.Width, bb.Height * 0.5f);

                // cruceta, botones y sticks hundidos
                Func<float, float, PointF> at = (x, y) => new PointF(pr.X + x / 100f * pr.Width, pr.Y + y / 100f * pr.Height);
                var ink = Color.FromArgb(235, 22, 16, 12);
                var inkB = Theme.B(ink);
                float u = pr.Width / 100f;
                if (s >= 20)
                {
                    var c = at(30, 42); float arm = 7.2f * u, th = 4.6f * u;
                    using (var cross = G.Round(new RectangleF(c.X - arm, c.Y - th / 2, arm * 2, th), th * 0.3f)) g.FillPath(inkB, cross);
                    using (var cross = G.Round(new RectangleF(c.X - th / 2, c.Y - arm, th, arm * 2), th * 0.3f)) g.FillPath(inkB, cross);
                    var b = at(70, 42); float o = 5.8f * u, br = 2.7f * u;
                    foreach (var d in new[] { new PointF(0, -o), new PointF(o, 0), new PointF(0, o), new PointF(-o, 0) })
                        g.FillEllipse(inkB, b.X + d.X - br, b.Y + d.Y - br, br * 2, br * 2);
                }
                if (s >= 40)
                {
                    float sr = 5.2f * u;
                    foreach (var sc in new[] { at(40, 53.5f), at(60, 53.5f) })
                    {
                        g.FillEllipse(inkB, sc.X - sr, sc.Y - sr, sr * 2, sr * 2);
                        using (var pen = new Pen(Color.FromArgb(120, 255, 220, 160), Math.Max(1f, u * 0.9f))) g.DrawEllipse(pen, sc.X - sr * 0.55f, sc.Y - sr * 0.55f, sr * 1.1f, sr * 1.1f);
                    }
                }
                g.Clip = old;
                // contorno fino para que no se empaste a tamaños pequeños
                using (var pen = new Pen(Color.FromArgb(90, 120, 50, 10), Math.Max(0.8f, s * 0.006f))) g.DrawPath(pen, body);
            }
        }

        public static Bitmap Render(int size, bool tile = true)
        {
            var b = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b)) { g.Clear(Color.Transparent); Draw(g, new RectangleF(0, 0, size, size), tile); }
            return b;
        }

        // .ico con varias resoluciones (PNG dentro, como los iconos modernos de Windows)
        public static void WriteIco(string file, string previewPng)
        {
            int[] sizes = { 256, 128, 64, 48, 40, 32, 24, 20, 16 };
            var pngs = new List<byte[]>();
            foreach (int sz in sizes)
                using (var b = Render(sz))
                using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); pngs.Add(ms.ToArray()); }
            using (var fs = File.Create(file))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length); w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (var p in pngs) w.Write(p);
            }
            if (previewPng != null)
                using (var big = Render(512))
                using (var sheet = new Bitmap(1100, 560, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(sheet))
                {
                    g.Clear(Color.FromArgb(60, 64, 58));
                    g.DrawImage(big, 20, 24);
                    int x = 560;
                    foreach (int sz in new[] { 128, 64, 48, 32, 16 })
                        using (var s = Render(sz)) { g.DrawImage(s, x, 24); x += sz + 18; }
                    // en el dock (sobre fondo claro tambien)
                    g.FillRectangle(Brushes.Gainsboro, 560, 330, 520, 200);
                    x = 580;
                    foreach (int sz in new[] { 128, 64, 48, 32 })
                        using (var s = Render(sz)) { g.DrawImage(s, x, 350); x += sz + 22; }
                    sheet.Save(previewPng, ImageFormat.Png);
                }
        }
    }
}
