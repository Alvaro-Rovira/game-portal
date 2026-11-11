using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;

namespace Hud
{
    // Paleta sacada del propio fondo: amanecer dorado sobre pinos.
    static class Theme
    {
        public static float S = 1f;                                        // escala DPI
        public static readonly Color Cream = Color.FromArgb(240, 231, 210);
        public static readonly Color Amber = Color.FromArgb(242, 178, 82);
        public static readonly Color Moss = Color.FromArgb(166, 196, 128);
        public static readonly Color Ember = Color.FromArgb(226, 112, 72);
        public static Color A(Color c, int a) { return Color.FromArgb(Math.Max(0, Math.Min(255, a)), c); }

        static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
        public static Font F(string family, float px, FontStyle st = FontStyle.Regular)
        {
            string k = family + "|" + px + "|" + (int)st;
            Font f;
            if (!fonts.TryGetValue(k, out f)) { f = new Font(family, px * S, st, GraphicsUnit.Pixel); fonts[k] = f; }
            return f;
        }
        public static Font Mono(float px, bool bold = false) { return F("Consolas", px, bold ? FontStyle.Bold : FontStyle.Regular); }
        public static Font Ui(float px) { return F("Segoe UI Variable Text", px); }
        public static Font UiSemi(float px) { return F("Segoe UI Variable Text Semibold", px); }
        public static Font Num(float px) { return F("Bahnschrift Light", px); }
        public static Font NumCond(float px) { return F("Bahnschrift SemiLight SemiConde", px); }
        public static Font Icons(float px) { return F("Segoe Fluent Icons", px); }
        public static int P(float v) { return (int)Math.Round(v * S); }

        static readonly Dictionary<int, SolidBrush> brushes = new Dictionary<int, SolidBrush>();
        public static SolidBrush B(Color c)
        {
            SolidBrush b;
            if (!brushes.TryGetValue(c.ToArgb(), out b)) { b = new SolidBrush(c); brushes[c.ToArgb()] = b; }
            return b;
        }
    }

    static class G
    {
        public static readonly StringFormat Typo = MakeTypo();
        static StringFormat MakeTypo()
        {
            var f = (StringFormat)StringFormat.GenericTypographic.Clone();
            f.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
            return f;
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            if (rad <= 0.1f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static SizeF Measure(Graphics g, string s, Font f) { return g.MeasureString(s, f, PointF.Empty, Typo); }

        public static void Text(Graphics g, string s, Font f, Color c, float x, float y) { g.DrawString(s, f, Theme.B(c), x, y, Typo); }

        public static void TextRight(Graphics g, string s, Font f, Color c, float right, float y)
        {
            var sz = Measure(g, s, f);
            g.DrawString(s, f, Theme.B(c), right - sz.Width, y, Typo);
        }

        // Texto con espaciado entre letras (estilo rotulo de instrumento)
        public static float Spaced(Graphics g, string s, Font f, Color c, float x, float y, float sp)
        {
            var b = Theme.B(c);
            foreach (char ch in s)
            {
                string t = ch.ToString();
                g.DrawString(t, f, b, x, y, Typo);
                x += Measure(g, t, f).Width + sp;
            }
            return x;
        }
        public static float SpacedWidth(Graphics g, string s, Font f, float sp)
        {
            float w = 0;
            foreach (char ch in s) w += Measure(g, ch.ToString(), f).Width + sp;
            return w - sp;
        }

        // Recorta con "…" para que quepa en un ancho
        public static string Fit(Graphics g, string s, Font f, float maxW)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (Measure(g, s, f).Width <= maxW) return s;
            int lo = 0, hi = s.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (Measure(g, s.Substring(0, mid).TrimEnd() + "…", f).Width <= maxW) lo = mid; else hi = mid - 1;
            }
            return s.Substring(0, lo).TrimEnd() + "…";
        }

        public static void Bar(Graphics g, float x, float y, float w, float h, float frac, Color fill, Color track)
        {
            using (var p = Round(new RectangleF(x, y, w, h), h / 2)) g.FillPath(Theme.B(track), p);
            frac = Math.Max(0, Math.Min(1, frac));
            if (frac * w >= h) using (var p = Round(new RectangleF(x, y, w * frac, h), h / 2)) g.FillPath(Theme.B(fill), p);
        }

        // Desenfoque rapido: 3 pasadas de caja ~ gaussiano. Trabaja sobre ARGB 32 bits.
        public static void Blur(Bitmap bmp, int radius)
        {
            if (radius < 1) return;
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            int w = bmp.Width, h = bmp.Height, stride = data.Stride;
            var buf = new byte[stride * h];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            var tmp = new byte[buf.Length];
            for (int pass = 0; pass < 3; pass++)
            {
                BoxH(buf, tmp, w, h, stride, radius);
                BoxV(tmp, buf, w, h, stride, radius);
            }
            Marshal.Copy(buf, 0, data.Scan0, buf.Length);
            bmp.UnlockBits(data);
        }

        static void BoxH(byte[] src, byte[] dst, int w, int h, int stride, int r)
        {
            int div = r * 2 + 1;
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0;
                    for (int i = -r; i <= r; i++) sum += src[row + Math.Max(0, Math.Min(w - 1, i)) * 4 + c];
                    for (int x = 0; x < w; x++)
                    {
                        dst[row + x * 4 + c] = (byte)(sum / div);
                        int add = Math.Min(w - 1, x + r + 1), rem = Math.Max(0, x - r);
                        sum += src[row + add * 4 + c] - src[row + rem * 4 + c];
                    }
                }
            }
        }

        static void BoxV(byte[] src, byte[] dst, int w, int h, int stride, int r)
        {
            int div = r * 2 + 1;
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < 4; c++)
                {
                    int col = x * 4 + c, sum = 0;
                    for (int i = -r; i <= r; i++) sum += src[Math.Max(0, Math.Min(h - 1, i)) * stride + col];
                    for (int y = 0; y < h; y++)
                    {
                        dst[y * stride + col] = (byte)(sum / div);
                        int add = Math.Min(h - 1, y + r + 1), rem = Math.Max(0, y - r);
                        sum += src[add * stride + col] - src[rem * stride + col];
                    }
                }
            }
        }

        // Sombra suave a partir de lo ya dibujado (para textos sin panel)
        public static Bitmap ShadowOf(Bitmap content, int radius, int alpha)
        {
            var sh = new Bitmap(content.Width, content.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(sh))
            {
                var cm = new ColorMatrix(new float[][] {
                    new float[] {0,0,0,0,0}, new float[] {0,0,0,0,0}, new float[] {0,0,0,0,0},
                    new float[] {0,0,0,alpha / 255f,0}, new float[] {0,0,0,0,1} });
                using (var ia = new ImageAttributes())
                {
                    ia.SetColorMatrix(cm);
                    g.DrawImage(content, new Rectangle(0, 0, sh.Width, sh.Height), 0, 0, sh.Width, sh.Height, GraphicsUnit.Pixel, ia);
                }
            }
            Blur(sh, radius);
            return sh;
        }

        // Icono de alta resolucion de cualquier exe / .lnk / app de la Store
        public static Bitmap ShellIcon(string parsingName, int size)
        {
            try
            {
                var iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
                N.IShellItemImageFactory f;
                N.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out f);
                IntPtr hb;
                int hr = f.GetImage(new N.SIZE(size, size), 0x1 /*BIGGERSIZEOK*/ | 0x4 /*ICONONLY*/, out hb);
                Marshal.ReleaseComObject(f);
                if (hr != 0 || hb == IntPtr.Zero) return null;
                try { return FromHBitmap(hb); } finally { N.DeleteObject(hb); }
            }
            catch (Exception e) { Log.W("icono " + parsingName + ": " + e.Message); return null; }
        }

        static Bitmap FromHBitmap(IntPtr hb)
        {
            var bm = new N.BITMAP();
            N.GetObject(hb, Marshal.SizeOf(typeof(N.BITMAP)), ref bm);
            int w = bm.bmWidth, h = bm.bmHeight;
            var bi = new N.BITMAPINFOHEADER();
            bi.biSize = Marshal.SizeOf(typeof(N.BITMAPINFOHEADER));
            bi.biWidth = w; bi.biHeight = -h; bi.biPlanes = 1; bi.biBitCount = 32;
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            IntPtr dc = N.GetDC(IntPtr.Zero);
            N.GetDIBits(dc, hb, 0, (uint)h, data.Scan0, ref bi, 0);
            N.ReleaseDC(IntPtr.Zero, dc);
            // si el bitmap no trae alfa (todo 0) lo hacemos opaco
            var buf = new byte[data.Stride * h];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            bool anyAlpha = false;
            for (int i = 3; i < buf.Length; i += 4) if (buf[i] != 0) { anyAlpha = true; break; }
            if (!anyAlpha) { for (int i = 3; i < buf.Length; i += 4) buf[i] = 255; Marshal.Copy(buf, 0, data.Scan0, buf.Length); }
            bmp.UnlockBits(data);
            return bmp;
        }
    }

    // El fondo de pantalla tal cual se ve en el monitor, para hacer los paneles de "cristal".
    static class Wall
    {
        public static string Key = "";
        static Bitmap screenImg;

        public static string CurrentKey()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                {
                    string path = (string)k.GetValue("WallPaper", "");
                    string style = Convert.ToString(k.GetValue("WallpaperStyle", "10"));
                    long t = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
                    return path + "|" + style + "|" + t + "|" + N.ScreenW + "x" + N.ScreenH;
                }
            }
            catch { return ""; }
        }

        public static void Load()
        {
            Release();
            Key = CurrentKey();
            int sw = N.ScreenW, sh = N.ScreenH;
            screenImg = new Bitmap(sw, sh, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(screenImg))
            {
                g.Clear(Color.FromArgb(24, 30, 22));
                try
                {
                    string[] parts = Key.Split('|');
                    if (File.Exists(parts[0]))
                    {
                        using (var img = Image.FromFile(parts[0]))
                        {
                            int style; int.TryParse(parts[1], out style);
                            float iw = img.Width, ih = img.Height, dw, dh;
                            if (style == 2) { dw = sw; dh = sh; }
                            else if (style == 6) { float s = Math.Min(sw / iw, sh / ih); dw = iw * s; dh = ih * s; }
                            else if (style == 0) { dw = iw; dh = ih; }
                            else { float s = Math.Max(sw / iw, sh / ih); dw = iw * s; dh = ih * s; }
                            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                            g.DrawImage(img, (sw - dw) / 2, (sh - dh) / 2, dw, dh);
                        }
                    }
                }
                catch (Exception e) { Log.E(e); }
            }
        }

        public static void Release() { if (screenImg != null) { screenImg.Dispose(); screenImg = null; } }
        public static Bitmap Screen { get { return screenImg; } }

        static double MeanLuma(Bitmap b, int m, int w, int h)
        {
            var data = b.LockBits(new Rectangle(m, m, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var row = new byte[w * 4];
            double sum = 0; long n = 0;
            for (int y = 0; y < h; y += 3)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < w; x += 3)
                {
                    int i = x * 4;
                    sum += (0.0722 * row[i] + 0.7152 * row[i + 1] + 0.2126 * row[i + 2]) / 255.0;
                    n++;
                }
            }
            b.UnlockBits(data);
            return n > 0 ? sum / n : 0.2;
        }

        // Panel de cristal: el trozo de fondo que queda detras, difuminado, oscurecido y con borde.
        public static Bitmap Glass(Rectangle r, float radius, int darken)
        {
            int m = Theme.P(36);
            var big = new Bitmap(r.Width + 2 * m, r.Height + 2 * m, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(big))
            {
                g.Clear(Color.FromArgb(24, 30, 22));
                if (screenImg != null) g.DrawImage(screenImg, new Rectangle(0, 0, big.Width, big.Height), new Rectangle(r.X - m, r.Y - m, big.Width, big.Height), GraphicsUnit.Pixel);
            }
            G.Blur(big, Theme.P(14));
            // oscurecer segun lo claro que sea el trozo de fondo: asi el texto se lee igual sobre cielo que sobre bosque
            if (darken < 0)
            {
                double lum = MeanLuma(big, m, r.Width, r.Height);
                const double target = 0.20, tint = 0.04;
                double a = lum > target ? (lum - target) / (lum - tint) : 0;
                darken = (int)(255 * Math.Max(0.30, Math.Min(0.86, a)));
            }
            var res = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(res))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                var rf = new RectangleF(0.5f, 0.5f, r.Width - 1, r.Height - 1);
                using (var path = G.Round(rf, radius))
                {
                    using (var tb = new TextureBrush(big, WrapMode.Clamp))
                    {
                        tb.TranslateTransform(-m, -m);
                        g.FillPath(tb, path);
                    }
                    g.FillPath(Theme.B(Color.FromArgb(darken, 8, 12, 9)), path);
                    using (var lg = new LinearGradientBrush(new RectangleF(0, 0, r.Width, r.Height), Color.FromArgb(22, 255, 245, 225), Color.FromArgb(0, 255, 245, 225), LinearGradientMode.Vertical))
                        g.FillPath(lg, path);
                    using (var lg = new LinearGradientBrush(new RectangleF(0, 0, r.Width, r.Height), Color.FromArgb(70, Theme.Cream), Color.FromArgb(18, Theme.Cream), LinearGradientMode.Vertical))
                    using (var pen = new Pen(lg, 1f))
                        g.DrawPath(pen, path);
                }
            }
            big.Dispose();
            return res;
        }
    }
}
