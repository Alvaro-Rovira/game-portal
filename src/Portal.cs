using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace Hud
{
    // Arte de cada juego, preparado en segundo plano: fondo grande, luz ambiente, color de acento y logo.
    class Art
    {
        public Bitmap Banner;          // arte ancho ya escalado (con el borde de abajo fundido) o pantalla completa
        public bool Full;              // true: Banner ocupa toda la pantalla
        public Bitmap Ambient;         // version minuscula y difuminada: tiñe todo el fondo
        public Bitmap Logo, LogoShadow;
        public Color Accent = Color.FromArgb(242, 178, 82);
        public volatile bool Ready;
    }

    // Portal de juegos a pantalla completa. Solo existe mientras esta abierto: al cerrarlo o lanzar un juego
    // el proceso termina y no queda nada en memoria.
    class PortalWindow : Surface
    {
        const uint WM_ART = 0x8000 + 2, WM_COVER = 0x8000 + 1;
        const int T_ANIM = 3, T_HOVER = 4, T_CLOCK = 5;
        readonly string dir;
        List<Game> all = new List<Game>(), view = new List<Game>();
        readonly Dictionary<Game, Bitmap> covers = new Dictionary<Game, Bitmap>();
        readonly Dictionary<Game, Art> arts = new Dictionary<Game, Art>();
        readonly Dictionary<Game, Bitmap> coverLight = new Dictionary<Game, Bitmap>();   // miniatura de las caratulas generadas
        readonly object artLock = new object();
        string query = "", filter = "TODOS";
        int focus, hoverCard = -1, pendingFocus = -1;
        string hot;                                   // boton bajo el raton: "play", "folder", "more", "close", "search", "tab:STEAM"...
        Game launching, mostRecent;

        // geometria (pixeles fisicos)
        float M, cw, ch, gap, shelfTop;
        readonly List<RectangleF> cardR = new List<RectangleF>();
        readonly Dictionary<string, RectangleF> hits = new Dictionary<string, RectangleF>();

        // fondo actual / anterior para el fundido
        Bitmap bgCur, bgPrev, frame, grain;
        Game bgGame;

        // animacion
        int tOpen, tBg, tHero, tLaunch, lastMinute = -1;
        float scroll, scrollTarget;
        float[] scale = new float[0];
        bool animating, headless, wasActive;

        static readonly string[] Filters = { "TODOS", "STEAM", "EPIC", "OTROS" };
        static readonly string[] FilterNames = { "Todos", "Steam", "Epic", "Otros" };
        static readonly CultureInfo Es = new CultureInfo("es-ES");

        public PortalWindow(string d)
        {
            dir = d; Name = "Portal de juegos"; Topmost = true; Activatable = true;
            M = Theme.P(96); cw = Theme.P(172); ch = Theme.P(258); gap = Theme.P(22);
        }

        static int Now { get { return Environment.TickCount; } }
        static float Ease(float t) { t = Math.Max(0, Math.Min(1, t)); return 1 - (float)Math.Pow(1 - t, 3); }
        float Since(int t0, float ms) { return headless ? 1f : Math.Max(0, Math.Min(1, (Now - t0) / ms)); }

        // ------------------------------------------------------------------ carga
        public void Open(bool offscreen = false, int startFocus = 0, string startQuery = "", string startFilter = "TODOS")
        {
            headless = offscreen;
            X = 0; Y = 0; W = N.ScreenW; H = N.ScreenH;
            shelfTop = H - Theme.P(374);
            all = Library.Scan(dir);
            mostRecent = all.FirstOrDefault(g => g.LastPlayed > DateTime.MinValue);
            foreach (var g in all) covers[g] = MakeCover(g);
            grain = MakeGrain();
            frame = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
            query = startQuery; filter = startFilter;
            Refilter(false);
            focus = Math.Max(0, Math.Min(view.Count - 1, startFocus));
            if (view.Count > 0)
            {
                var first = view[focus];
                if (headless || !string.IsNullOrEmpty(first.EpicItem)) Library.FetchEpicArt(dir, first);
                if (headless) foreach (var g in all) { Library.FetchEpicArt(dir, g); covers[g] = MakeCover(g); }
                BuildArt(first);
                SetBg(first, true);
            }
            EnsureVisible(focus, true);
            for (int i = 0; i < scale.Length; i++) scale[i] = i == focus ? 1.08f : 1f;
            tOpen = tHero = Now; tBg = Now - 10000;
            if (headless)
            {
                foreach (var g in all) if (!ArtOf(g).Ready) BuildArt(g);
                SetBg(view.Count > 0 ? view[focus] : null, true);
                tBg = Now - 10000;
                return;
            }
            Create();
            Paint(0);
            N.SetForegroundWindow(Hwnd);
            Animate();
            N.SetTimer(Hwnd, new IntPtr(T_CLOCK), 1000, IntPtr.Zero);
            // el resto del arte se prepara en segundo plano, empezando por los que tienes delante
            var order = view.Skip(focus).Concat(view.Take(focus)).Concat(all.Where(g => !view.Contains(g))).ToList();
            var th = new Thread(() => Prefetch(order)) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
            th.Start();
        }

        // mide lo que cuesta un fotograma durante un fundido (para comprobar que va fluido)
        public string Bench()
        {
            var other = view.Count > 1 ? view[1] : null;
            if (other != null && !ArtOf(other).Ready) BuildArt(other);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var nb = Compose(other); long first = sw.ElapsedMilliseconds;
            sw.Restart(); nb.Dispose(); nb = Compose(other); long compose = sw.ElapsedMilliseconds;
            Log.W("medir: primer fondo " + first + " ms");
            bgPrev = bgCur; bgCur = nb; headless = false; tBg = Now; tOpen = Now - 10000; tHero = Now;
            sw.Restart();
            for (int i = 0; i < 30; i++) { tBg = Now - 100; Paint(); }
            return "componer fondo " + compose + " ms, fotograma con fundido " + (sw.ElapsedMilliseconds / 30.0).ToString("0.0") + " ms";
        }

        public void SaveFrame(string png)
        {
            Paint();
            Canvas.Save(png, ImageFormat.Png);
        }

        void Prefetch(List<Game> order)
        {
            foreach (var g in order)
            {
                if (Hwnd == IntPtr.Zero) return;
                try
                {
                    bool fresh = Library.FetchEpicArt(dir, g);
                    if (fresh) { lock (artLock) arts.Remove(g); N.PostMessage(Hwnd, WM_COVER, new IntPtr(all.IndexOf(g)), IntPtr.Zero); }
                    if (!ArtOf(g).Ready || fresh) { BuildArt(g); N.PostMessage(Hwnd, WM_ART, new IntPtr(all.IndexOf(g)), IntPtr.Zero); }
                }
                catch (Exception e) { Log.W("arte " + g.Name + ": " + e.Message); }
            }
        }

        Art ArtOf(Game g)
        {
            lock (artLock)
            {
                Art a;
                if (!arts.TryGetValue(g, out a)) { a = new Art(); arts[g] = a; }
                return a;
            }
        }

        static Bitmap LoadImage(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try { using (var fs = File.OpenRead(path)) using (var img = Image.FromStream(fs)) return new Bitmap(img); }
            catch { return null; }
        }

        // OJO: se llama desde el hilo de fondo; aqui no se pueden usar los pinceles/fuentes cacheados de Theme
        void BuildArt(Game g)
        {
            var a = new Art();
            using (var hero = LoadImage(g.Hero))
            using (var cover = hero == null ? LoadImage(g.Cover) : null)
            {
                var src = hero ?? cover;
                if (src == null)
                {
                    // sin arte: la luz sale de la caratula generada (copia propia, este hilo no toca la de pantalla)
                    Bitmap cv;
                    lock (artLock) coverLight.TryGetValue(g, out cv);
                    if (cv != null) a.Ambient = Shrink(cv, 64, 36);
                }
                else a.Ambient = Shrink(src, 64, 36);
                if (a.Ambient != null) { G.Blur(a.Ambient, 3); a.Accent = AccentOf(a.Ambient); }
                if (hero != null)
                {
                    float aspect = hero.Width / (float)hero.Height;
                    if (aspect >= 2f)
                    {
                        int bh = (int)Math.Ceiling(W / aspect);
                        a.Banner = Cover(hero, W, bh, 0.5f);
                        FadeBottom(a.Banner, 0.42f);
                    }
                    else { a.Banner = Cover(hero, W, H, 0.4f); a.Full = true; }
                }
            }
            using (var logo = LoadImage(g.Logo))
                if (logo != null)
                {
                    float sc = Math.Min(Theme.P(170) / (float)logo.Height, Theme.P(560) / (float)logo.Width);
                    int lw = Math.Max(1, (int)(logo.Width * sc)), lh = Math.Max(1, (int)(logo.Height * sc));
                    a.Logo = new Bitmap(lw, lh, PixelFormat.Format32bppPArgb);
                    using (var gr = Graphics.FromImage(a.Logo)) { gr.InterpolationMode = InterpolationMode.HighQualityBicubic; gr.PixelOffsetMode = PixelOffsetMode.HighQuality; gr.DrawImage(logo, 0, 0, lw, lh); }
                    int pad = Theme.P(28);
                    using (var padded = new Bitmap(lw + pad * 2, lh + pad * 2, PixelFormat.Format32bppArgb))
                    {
                        using (var gr = Graphics.FromImage(padded)) gr.DrawImageUnscaled(a.Logo, pad, pad);
                        a.LogoShadow = G.ShadowOf(padded, Theme.P(12), 170);
                    }
                }
            a.Ready = true;
            lock (artLock)
            {
                Art old;
                if (arts.TryGetValue(g, out old) && old != a) arts[g] = a; else arts[g] = a;
            }
        }

        static Bitmap Shrink(Image src, int w, int h)
        {
            var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var gr = Graphics.FromImage(b))
            {
                gr.InterpolationMode = InterpolationMode.HighQualityBilinear;
                float sc = Math.Max(w / (float)src.Width, h / (float)src.Height);
                gr.DrawImage(src, (w - src.Width * sc) / 2, (h - src.Height * sc) / 2, src.Width * sc, src.Height * sc);
            }
            return b;
        }

        // escala para cubrir w x h recortando lo que sobre
        static Bitmap Cover(Image src, int w, int h, float focusY)
        {
            var b = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var gr = Graphics.FromImage(b))
            {
                gr.InterpolationMode = InterpolationMode.HighQualityBicubic;
                gr.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float sc = Math.Max(w / (float)src.Width, h / (float)src.Height);
                float dw = src.Width * sc, dh = src.Height * sc;
                gr.DrawImage(src, (w - dw) / 2, (h - dh) * focusY, dw, dh);
            }
            return b;
        }

        // el arte ancho se va desvaneciendo por abajo para fundirse con la luz ambiente
        static unsafe void FadeBottom(Bitmap b, float from)
        {
            var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
            int y0 = (int)(b.Height * from);
            for (int y = y0; y < b.Height; y++)
            {
                float t = (y - y0) / (float)Math.Max(1, b.Height - 1 - y0);
                int f = (int)(256 * (1 - t * t * (3 - 2 * t)));
                byte* row = (byte*)d.Scan0 + y * d.Stride;
                for (int x = 0; x < b.Width * 4; x++) row[x] = (byte)(row[x] * f >> 8);
            }
            b.UnlockBits(d);
        }

        static Color AccentOf(Bitmap amb)
        {
            double r = 0, g = 0, bl = 0, wsum = 0;
            for (int y = 0; y < amb.Height; y += 2)
                for (int x = 0; x < amb.Width; x += 2)
                {
                    var c = amb.GetPixel(x, y);
                    float s = c.GetSaturation(), v = Math.Max(c.R, Math.Max(c.G, c.B)) / 255f;
                    double w = s * s * v * v + 0.0005;
                    r += c.R * w; g += c.G * w; bl += c.B * w; wsum += w;
                }
            var avg = Color.FromArgb((int)(r / wsum), (int)(g / wsum), (int)(bl / wsum));
            float h = avg.GetHue(), sat = Math.Max(0.38f, Math.Min(0.78f, avg.GetSaturation() * 1.4f));
            if (avg.GetSaturation() < 0.08f) return Color.FromArgb(242, 178, 82);
            return FromHsv(h, sat, 0.98f);
        }

        static Color FromHsv(float h, float s, float v)
        {
            int hi = (int)(h / 60) % 6; float f = h / 60 - (int)(h / 60);
            float p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
            float r, g, b;
            switch (hi) { case 0: r = v; g = t; b = p; break; case 1: r = q; g = v; b = p; break; case 2: r = p; g = v; b = t; break; case 3: r = p; g = q; b = v; break; case 4: r = t; g = p; b = v; break; default: r = v; g = p; b = q; break; }
            return Color.FromArgb((int)(r * 255), (int)(g * 255), (int)(b * 255));
        }

        static Bitmap MakeGrain()
        {
            var b = new Bitmap(192, 192, PixelFormat.Format32bppArgb);
            var rnd = new Random(7);
            for (int y = 0; y < b.Height; y++)
                for (int x = 0; x < b.Width; x++)
                {
                    int v = rnd.Next(256);
                    b.SetPixel(x, y, v > 128 ? Color.FromArgb(rnd.Next(5, 11), 255, 255, 255) : Color.FromArgb(rnd.Next(6, 14), 0, 0, 0));
                }
            return b;
        }

        // ------------------------------------------------------------------ caratulas
        // pinta 'src' recortada para llenar 'w x h' con esquinas redondeadas
        static Bitmap Fill(Bitmap src, int w, int h, float radius, float focusY)
        {
            var b = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var tmp = Cover(src, w, h, focusY))
            using (var g = Graphics.FromImage(b))
            using (var tb = new TextureBrush(tmp, WrapMode.Clamp))
            using (var p = G.Round(new RectangleF(0, 0, w, h), radius))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(tb, p);
            }
            return b;
        }

        Bitmap MakeCover(Game game)
        {
            int w = (int)cw, h = (int)ch;
            float rad = Theme.P(10);
            using (var img = LoadImage(game.Cover))
                if (img != null) return Fill(img, w, h, rad, 0.3f);
            // sin caratula: la hacemos con el icono del juego, a juego con sus colores
            string src = !string.IsNullOrEmpty(game.Icon) ? game.Icon : game.Launch;
            bool pic = Regex.IsMatch(src, @"\.(png|jpe?g|bmp)$", RegexOptions.IgnoreCase);
            Bitmap icon = src.Contains("://") ? null : pic ? LoadImage(src) : G.ShellIcon(src, 256);
            if (icon != null && !pic && !IsRealIcon(icon)) { icon.Dispose(); icon = null; }
            Color tint = Color.FromArgb(242, 178, 82);
            if (icon != null) using (var tiny = Shrink(icon, 16, 16)) tint = AccentOf(tiny);
            var b = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                var r = new RectangleF(0, 0, w, h);
                using (var p = G.Round(r, rad))
                {
                    var old = g.Clip; g.SetClip(p);
                    using (var lg = new LinearGradientBrush(r, Mix(tint, Color.Black, 0.62f), Mix(tint, Color.Black, 0.9f), 90f)) g.FillRectangle(lg, r);
                    if (icon != null)
                    {
                        // el propio icono, enorme y difuminado, de textura
                        using (var blur = Shrink(icon, 40, 40))
                        {
                            G.Blur(blur, 3);
                            using (var ia = Alpha(0.55f))
                                g.DrawImage(blur, new Rectangle((int)(-w * 0.35f), (int)(h * 0.02f), (int)(w * 1.7f), (int)(w * 1.7f)), 0, 0, 40, 40, GraphicsUnit.Pixel, ia);
                        }
                    }
                    using (var lg = new LinearGradientBrush(new RectangleF(0, h * 0.45f - 2, w, h * 0.55f + 4), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(200, 0, 0, 0), 90f))
                        g.FillRectangle(lg, 0, h * 0.45f, w, h * 0.55f);
                    float s = w * 0.5f, cy = h * 0.36f;
                    if (icon != null)
                    {
                        using (var sh = G.ShadowOf(icon, 10, 160)) g.DrawImage(sh, (w - s) / 2, cy - s / 2 + Theme.P(6), s, s);
                        g.DrawImage(icon, (w - s) / 2, cy - s / 2, s, s);
                    }
                    else
                    {
                        float bs = w * 0.46f;
                        Brand.Draw(g, new RectangleF((w - bs) / 2, cy - bs / 2, bs, bs), false);
                    }
                    // nombre
                    var f = Theme.F("Segoe UI Variable Display", 19, FontStyle.Bold);
                    var lines = Wrap(g, game.Name, f, w - Theme.P(28), 3);
                    float lh = G.Measure(g, "Ag", f).Height * 0.98f, ty = h - Theme.P(40) - lh * lines.Count;
                    foreach (var l in lines) { G.Text(g, l, f, Color.FromArgb(250, 246, 238), Theme.P(14), ty); ty += lh; }
                    G.Spaced(g, game.Platform, Theme.Mono(9.5f, true), Color.FromArgb(200, Mix(tint, Color.White, 0.35f)), Theme.P(15), h - Theme.P(30), Theme.P(1.6f));
                    g.Clip = old;
                }
            }
            if (icon != null) icon.Dispose();
            var light = Shrink(b, 32, 48);
            lock (artLock) { Bitmap old; if (coverLight.TryGetValue(game, out old)) old.Dispose(); coverLight[game] = light; }
            return b;
        }

        static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        static ImageAttributes Alpha(float a)
        {
            var ia = new ImageAttributes();
            var cm = new ColorMatrix(); cm.Matrix33 = a;
            ia.SetColorMatrix(cm);
            return ia;
        }

        // ¿Es un icono grande de verdad? Si el exe solo trae uno pequeño, Windows lo devuelve enano
        // dentro de un marco: casi todo transparente. Medimos cuanta parte del recuadro esta pintada.
        static bool IsRealIcon(Bitmap b)
        {
            int minX = b.Width, maxX = -1, minY = b.Height, maxY = -1, solid = 0;
            for (int y = 0; y < b.Height; y += 4)
                for (int x = 0; x < b.Width; x += 4)
                    if (b.GetPixel(x, y).A > 60) { solid++; if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
            if (maxX < 0) return false;
            float area = (maxX - minX + 4) * (maxY - minY + 4) / 16f;
            return (maxX - minX) > b.Width * 0.45f && solid / Math.Max(1f, area) > 0.3f;
        }

        static List<string> Wrap(Graphics g, string text, Font f, float maxW, int maxLines)
        {
            var lines = new List<string>(); string cur = "";
            foreach (var word in text.Split(' '))
            {
                string t = cur.Length == 0 ? word : cur + " " + word;
                if (G.Measure(g, t, f).Width <= maxW || cur.Length == 0) cur = t; else { lines.Add(cur); cur = word; }
            }
            if (cur.Length > 0) lines.Add(cur);
            if (lines.Count > maxLines) { lines = lines.Take(maxLines).ToList(); lines[maxLines - 1] = G.Fit(g, lines[maxLines - 1] + "…", f, maxW); }
            return lines;
        }

        // ------------------------------------------------------------------ fondo
        Bitmap Compose(Game game)
        {
            var b = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
            Art a = game != null ? ArtOf(game) : null;
            if (a != null && !a.Ready) a = null;
            using (var g = Graphics.FromImage(b))
            {
                g.Clear(Color.FromArgb(10, 11, 10));
                if (a != null && a.Ambient != null)
                    using (var ia = new ImageAttributes())
                    {
                        ia.SetWrapMode(WrapMode.TileFlipXY);
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.DrawImage(a.Ambient, new Rectangle(0, 0, W, H), 0, 0, a.Ambient.Width, a.Ambient.Height, GraphicsUnit.Pixel, ia);
                        g.FillRectangle(Theme.B(Color.FromArgb(a.Banner != null ? 150 : 120, 6, 7, 6)), 0, 0, W, H);
                    }
                if (a != null && a.Banner != null)
                {
                    g.CompositingMode = CompositingMode.SourceOver;
                    g.DrawImageUnscaled(a.Banner, 0, 0);
                    // el arte a pantalla completa suele ser muy vivo: lo bajamos para que mande la interfaz
                    if (a.Full) g.FillRectangle(Theme.B(Color.FromArgb(120, 4, 5, 4)), 0, 0, W, H);
                }
                if (veil == null) veil = MakeVeil();
                g.DrawImageUnscaled(veil, 0, 0);
            }
            return b;
        }

        Bitmap veil;
        // velos para que se lea todo (izquierda: texto, abajo: estanteria, arriba: cabecera) + grano.
        // Igual para todos los juegos: se pinta una vez y luego es una sola copia.
        Bitmap MakeVeil()
        {
            var b = new Bitmap(W, H, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(b))
            {
                using (var lg = new LinearGradientBrush(new RectangleF(-1, 0, W * 0.72f + 2, H), Color.FromArgb(215, 5, 6, 5), Color.FromArgb(0, 5, 6, 5), 0f))
                {
                    var bl = new Blend(); bl.Positions = new[] { 0f, 0.4f, 1f }; bl.Factors = new[] { 0f, 0.55f, 1f }; lg.Blend = bl;
                    g.FillRectangle(lg, 0, 0, W * 0.72f, H);
                }
                float by = H * 0.42f;
                using (var lg = new LinearGradientBrush(new RectangleF(0, by - 1, W, H - by + 2), Color.FromArgb(0, 4, 5, 4), Color.FromArgb(242, 4, 5, 4), 90f))
                {
                    var bl = new Blend(); bl.Positions = new[] { 0f, 0.5f, 1f }; bl.Factors = new[] { 0f, 0.62f, 1f }; lg.Blend = bl;
                    g.FillRectangle(lg, 0, by, W, H - by);
                }
                using (var lg = new LinearGradientBrush(new RectangleF(0, -1, W, Theme.P(240) + 2), Color.FromArgb(190, 4, 5, 4), Color.FromArgb(0, 4, 5, 4), 90f))
                {
                    var bl = new Blend(); bl.Positions = new[] { 0f, 0.5f, 1f }; bl.Factors = new[] { 0f, 0.6f, 1f }; lg.Blend = bl;
                    g.FillRectangle(lg, 0, 0, W, Theme.P(240));
                }
                using (var tb = new TextureBrush(grain, WrapMode.Tile)) g.FillRectangle(tb, 0, 0, W, H);
            }
            return b;
        }

        void SetBg(Game g, bool force = false)
        {
            if (g == bgGame && !force) return;
            var nb = Compose(g);
            if (bgCur == null || headless) { if (bgCur != null) bgCur.Dispose(); bgCur = nb; bgGame = g; return; }
            float t = Since(tBg, 320);
            if (bgPrev != null && t < 1)
            {
                Mix(bgPrev, bgCur, frame, Ease(t));
                var tmp = bgPrev; bgPrev = frame; frame = tmp;
                bgCur.Dispose();
            }
            else { if (bgPrev != null) bgPrev.Dispose(); bgPrev = bgCur; }
            bgCur = nb; bgGame = g; tBg = Now;
            Animate();
        }

        // fundido entre dos fondos (mismo tamaño, PArgb) directamente sobre la memoria
        static unsafe void Mix(Bitmap a, Bitmap b, Bitmap dst, float t)
        {
            var r = new Rectangle(0, 0, a.Width, a.Height);
            var da = a.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            var db = b.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            var dd = dst.LockBits(r, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            uint k = (uint)(t * 256), ik = 256 - k;
            int n = a.Width * a.Height;
            uint* pa = (uint*)da.Scan0, pb = (uint*)db.Scan0, pd = (uint*)dd.Scan0;
            for (int i = 0; i < n; i++)
            {
                uint x = pa[i], y = pb[i];
                uint rb = (((x & 0xFF00FF) * ik + (y & 0xFF00FF) * k) >> 8) & 0xFF00FF;
                uint ag = (((x >> 8) & 0xFF00FF) * ik + ((y >> 8) & 0xFF00FF) * k) & 0xFF00FF00;
                pd[i] = rb | ag;
            }
            a.UnlockBits(da); b.UnlockBits(db); dst.UnlockBits(dd);
        }

        // ------------------------------------------------------------------ filtro, busqueda, foco
        int Count(string f) { return all.Count(g => Match(g, f)); }
        static bool Match(Game g, string f)
        {
            if (f == "TODOS") return true;
            if (f == "OTROS") return g.Platform != "STEAM" && g.Platform != "EPIC";
            return g.Platform == f;
        }

        void Refilter(bool anim = true)
        {
            string q = query.Trim().ToLowerInvariant();
            view = all.Where(g => Match(g, filter) && (q.Length == 0 || g.Name.ToLowerInvariant().Contains(q) || g.Platform.ToLowerInvariant().Contains(q))).ToList();
            if (q.Length > 0)   // lo que empieza por lo escrito, primero
                view = view.OrderBy(g => g.Name.ToLowerInvariant().StartsWith(q) ? 0 : 1).ToList();
            scale = new float[view.Count];
            for (int i = 0; i < scale.Length; i++) scale[i] = 1f;
            focus = 0; hoverCard = -1; scroll = scrollTarget = 0;
            if (anim && view.Count > 0) { scale[0] = 1f; SetFocus(0, true, true); }
            else if (anim) Animate();
        }

        Game Focused { get { return view.Count > 0 && focus < view.Count ? view[focus] : null; } }

        void SetFocus(int i, bool keyboard, bool force = false)
        {
            if (view.Count == 0) return;
            i = Math.Max(0, Math.Min(view.Count - 1, i));
            if (i == focus && !force) return;
            focus = i;
            if (keyboard) EnsureVisible(i, false);
            tHero = Now;
            SetBg(view[i]);
            Animate();
        }

        float Step { get { return cw + gap; } }

        void EnsureVisible(int i, bool instant)
        {
            float x = i * Step, left = scrollTarget, right = scrollTarget + (W - 2 * M) - cw;
            if (x < left + Step * 0.5f) scrollTarget = Math.Max(0, x - Step);
            else if (x > right - Step * 0.5f) scrollTarget = x - (W - 2 * M) + cw + Step;
            scrollTarget = Math.Max(0, Math.Min(MaxScroll, scrollTarget));
            if (instant) scroll = scrollTarget;
        }

        float MaxScroll { get { return Math.Max(0, view.Count * Step - gap - (W - 2 * M)); } }

        // ------------------------------------------------------------------ bucle de animacion
        void Animate()
        {
            if (headless || Hwnd == IntPtr.Zero) return;
            if (!animating) { animating = true; N.SetTimer(Hwnd, new IntPtr(T_ANIM), 15, IntPtr.Zero); }
        }

        void Tick()
        {
            bool busy = false;
            float ds = scrollTarget - scroll;
            if (Math.Abs(ds) > 0.5f) { scroll += ds * 0.2f; busy = true; } else scroll = scrollTarget;
            for (int i = 0; i < scale.Length; i++)
            {
                float target = i == focus ? 1.08f : (i == hoverCard ? 1.03f : 1f);
                float d = target - scale[i];
                if (Math.Abs(d) > 0.001f) { scale[i] += d * 0.25f; busy = true; } else scale[i] = target;
            }
            if (Since(tOpen, 420) < 1 || Since(tBg, 320) < 1 || Since(tHero, 360) < 1) busy = true;
            if (launching != null) busy = true;
            if (bgPrev != null && Since(tBg, 320) >= 1) { bgPrev.Dispose(); bgPrev = null; }
            Paint((byte)(255 * Ease(Since(tOpen, 180))));
            if (!busy) { animating = false; N.KillTimer(Hwnd, new IntPtr(T_ANIM)); }
        }

        // ------------------------------------------------------------------ dibujo
        void Paint(byte alpha = 255)
        {
            using (var g = BeginDraw())
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                float tb = Since(tBg, 320);
                if (bgPrev != null && tb < 1) { Mix(bgPrev, bgCur, frame, Ease(tb)); g.DrawImageUnscaled(frame, 0, 0); }
                else if (bgCur != null) g.DrawImageUnscaled(bgCur, 0, 0);
                else g.Clear(Color.FromArgb(10, 11, 10));
                g.CompositingMode = CompositingMode.SourceOver;
                hits.Clear();
                float rise = Theme.P(36) * (1 - Ease(Since(tOpen, 420)));
                DrawTopBar(g);
                DrawHero(g, rise);
                DrawShelf(g, rise);
                DrawFooter(g);
                if (launching != null) DrawLaunching(g);
            }
            Present(alpha);
        }

        Color Accent { get { var f = Focused; if (f == null) return Theme.Amber; var a = ArtOf(f); return a.Ready ? a.Accent : Theme.Amber; } }

        void DrawTopBar(Graphics g)
        {
            float y = Theme.P(46), bh = Theme.P(44);
            // marca
            float bs = Theme.P(40);
            Brand.Draw(g, new RectangleF(M, y + (bh - bs) / 2, bs, bs));
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            var fb = Theme.F("Segoe UI Variable Display", 21, FontStyle.Bold);
            G.Text(g, "Portal", fb, Theme.Cream, M + bs + Theme.P(14), y + (bh - G.Measure(g, "Portal", fb).Height) / 2 - Theme.P(1));
            float x = M + bs + Theme.P(14) + G.Measure(g, "Portal", fb).Width + Theme.P(26);
            g.FillRectangle(Theme.B(Theme.A(Theme.Cream, 40)), x, y + Theme.P(12), Math.Max(1, Theme.P(1)), bh - Theme.P(24));
            x += Theme.P(28);

            // pestañas por plataforma
            var ft = Theme.F("Segoe UI Variable Text Semibold", 15.5f);
            var fc = Theme.F("Segoe UI Variable Text", 12);
            for (int i = 0; i < Filters.Length; i++)
            {
                int n = Count(Filters[i]);
                if (n == 0 && Filters[i] != "TODOS") continue;
                bool on = filter == Filters[i], h = hot == "tab:" + Filters[i];
                var sz = G.Measure(g, FilterNames[i], ft); var cz = G.Measure(g, n.ToString(), fc);
                float tw = sz.Width + Theme.P(6) + cz.Width;
                var r = new RectangleF(x - Theme.P(12), y, tw + Theme.P(24), bh);
                hits["tab:" + Filters[i]] = r;
                if (h && !on) using (var p = G.Round(r, bh / 2)) g.FillPath(Theme.B(Theme.A(Theme.Cream, 18)), p);
                float ty = y + (bh - sz.Height) / 2 - Theme.P(1);
                G.Text(g, FilterNames[i], ft, Theme.A(Theme.Cream, on ? 255 : h ? 220 : 150), x, ty);
                G.Text(g, n.ToString(), fc, Theme.A(on ? Accent : Theme.Cream, on ? 255 : 110), x + sz.Width + Theme.P(6), ty + Theme.P(2));
                if (on)
                    using (var p = G.Round(new RectangleF(x, y + bh - Theme.P(5), sz.Width, Theme.P(3)), Theme.P(1.5f))) g.FillPath(Theme.B(Accent), p);
                x += tw + Theme.P(34);
            }

            // derecha: cerrar, reloj, buscador
            float cs = bh;
            var closeR = new RectangleF(W - M - cs, y, cs, cs);
            hits["close"] = closeR;
            g.FillEllipse(Theme.B(Theme.A(Theme.Cream, hot == "close" ? 46 : 16)), closeR);
            using (var pen = new Pen(Theme.A(Theme.Cream, 36), 1f)) g.DrawEllipse(pen, closeR);
            var fi = Theme.Icons(14); var xs = G.Measure(g, "", fi);
            G.Text(g, "", fi, Theme.Cream, closeR.X + (cs - xs.Width) / 2, closeR.Y + (cs - xs.Height) / 2);

            var fk = Theme.F("Segoe UI Variable Display", 20, FontStyle.Bold);
            string clock = DateTime.Now.ToString("HH:mm");
            var ks = G.Measure(g, clock, fk);
            float kx = closeR.X - Theme.P(26) - ks.Width;
            G.Text(g, clock, fk, Theme.Cream, kx, y + (bh - ks.Height) / 2 - Theme.P(1));

            float sw = Theme.P(query.Length > 0 ? 360 : 300);
            var sr = new RectangleF(kx - Theme.P(26) - sw, y, sw, bh);
            hits["search"] = sr;
            using (var p = G.Round(sr, bh / 2))
            {
                g.FillPath(Theme.B(Color.FromArgb(query.Length > 0 ? 150 : 95, 8, 9, 8)), p);
                using (var pen = new Pen(query.Length > 0 ? Theme.A(Accent, 230) : Theme.A(Theme.Cream, hot == "search" ? 80 : 38), query.Length > 0 ? Theme.P(1.5f) : 1f)) g.DrawPath(pen, p);
            }
            var fsi = Theme.Icons(15); var ms = G.Measure(g, "", fsi);
            G.Text(g, "", fsi, Theme.A(Theme.Cream, query.Length > 0 ? 230 : 150), sr.X + Theme.P(18), sr.Y + (bh - ms.Height) / 2);
            var fq = Theme.F("Segoe UI Variable Text", 15);
            string shown = query.Length > 0 ? query : "Escribe para buscar";
            var qs = G.Measure(g, "Ag", fq);
            float qx = sr.X + Theme.P(46);
            G.Text(g, G.Fit(g, shown, fq, sr.Width - Theme.P(80)), fq, Theme.A(Theme.Cream, query.Length > 0 ? 250 : 120), qx, sr.Y + (bh - qs.Height) / 2);
            if (query.Length > 0 && (Now / 530) % 2 == 0)
            {
                float cx = qx + G.Measure(g, G.Fit(g, shown, fq, sr.Width - Theme.P(80)), fq).Width + Theme.P(2);
                g.FillRectangle(Theme.B(Accent), cx, sr.Y + Theme.P(12), Math.Max(1, Theme.P(1.6f)), bh - Theme.P(24));
            }
            if (query.Length == 0)
            {
                // tecla de ejemplo para que se entienda que basta con teclear
                var fkc = Theme.F("Segoe UI Variable Text Semibold", 11);
                KeyCap(g, "A–Z", fkc, sr.Right - Theme.P(14), sr.Y + bh / 2, true);
            }
        }

        // tecla dibujada; devuelve el ancho. Si alignRight, 'x' es el borde derecho
        float KeyCap(Graphics g, string t, Font f, float x, float cy, bool alignRight = false)
        {
            var s = G.Measure(g, t, f);
            float w = Math.Max(Theme.P(26), s.Width + Theme.P(14)), h = Theme.P(24);
            if (alignRight) x -= w;
            var r = new RectangleF(x, cy - h / 2, w, h);
            using (var p = G.Round(r, Theme.P(6)))
            {
                g.FillPath(Theme.B(Theme.A(Theme.Cream, 20)), p);
                using (var pen = new Pen(Theme.A(Theme.Cream, 55), 1f)) g.DrawPath(pen, p);
            }
            g.FillRectangle(Theme.B(Theme.A(Theme.Cream, 30)), r.X + Theme.P(5), r.Bottom - Theme.P(2), r.Width - Theme.P(10), Math.Max(1, Theme.P(1)));
            G.Text(g, t, f, Theme.A(Theme.Cream, 215), r.X + (w - s.Width) / 2, r.Y + (h - s.Height) / 2 - Theme.P(1));
            return w;
        }

        void DrawHero(Graphics g, float rise)
        {
            var game = Focused;
            if (game == null)
            {
                string msg = all.Count == 0 ? "No he encontrado juegos instalados" : "Ningún juego coincide con «" + query + "»";
                var fm = Theme.F("Segoe UI Variable Display", 40, FontStyle.Bold);
                G.Text(g, msg, fm, Theme.Cream, M, shelfTop - Theme.P(200));
                G.Text(g, "Borra con Retroceso o pulsa Esc", Theme.F("Segoe UI Variable Text", 17), Theme.A(Theme.Cream, 150), M, shelfTop - Theme.P(138));
                return;
            }
            var art = ArtOf(game);
            float k = Ease(Since(tHero, 360));
            float dx = Theme.P(22) * (1 - k);
            int al = (int)(255 * k);
            float x = M + dx;
            float bottom = shelfTop - Theme.P(58) + rise;

            // botones
            float bh = Theme.P(56);
            float by = bottom - bh;
            var playR = new RectangleF(x, by, Theme.P(196), bh);
            hits["play"] = playR;
            bool hp = hot == "play";
            if (hp || true)
            {
                // halo del color del juego detras del boton
                for (int i = 1; i <= 4; i++)
                    using (var p = G.Round(RectangleF.Inflate(playR, i * Theme.P(3), i * Theme.P(3)), bh / 2 + i * Theme.P(3)))
                        g.FillPath(Theme.B(Color.FromArgb((hp ? 26 : 12) * al / 255, Accent)), p);
            }
            using (var p = G.Round(playR, bh / 2))
            {
                var c1 = hp ? Color.FromArgb(al, 255, 255, 255) : Color.FromArgb(al, 246, 240, 228);
                g.FillPath(Theme.B(c1), p);
            }
            var fpI = Theme.Icons(17); var fpT = Theme.F("Segoe UI Variable Display", 19, FontStyle.Bold);
            var ps = G.Measure(g, "Jugar", fpT); var pis = G.Measure(g, "", fpI);
            float pw = pis.Width + Theme.P(12) + ps.Width, px = playR.X + (playR.Width - pw) / 2;
            var ink = Color.FromArgb(al, 16, 15, 13);
            G.Text(g, "", fpI, ink, px, playR.Y + (bh - pis.Height) / 2 + Theme.P(1));
            G.Text(g, "Jugar", fpT, ink, px + pis.Width + Theme.P(12), playR.Y + (bh - ps.Height) / 2 - Theme.P(1));

            float bx = playR.Right + Theme.P(14);
            if (!string.IsNullOrEmpty(game.Folder) && Directory.Exists(game.Folder)) { RoundButton(g, "folder", "", bx, by, bh, al); bx += bh + Theme.P(12); }
            RoundButton(g, "more", "", bx, by, bh, al);

            // datos: plataforma · tiempo · ultima vez · tamaño
            float my = by - Theme.P(48);
            var fmeta = Theme.F("Segoe UI Variable Text Semibold", 15.5f);
            float mx = x;
            var fbd = Theme.F("Segoe UI Variable Text Semibold", 11.5f);
            string plat = game.Platform;
            float pbw = G.SpacedWidth(g, plat, fbd, Theme.P(1.4f)) + Theme.P(20), pbh = Theme.P(26);
            var pbr = new RectangleF(mx, my + Theme.P(10) - pbh / 2, pbw, pbh);
            using (var p = G.Round(pbr, Theme.P(6)))
            {
                g.FillPath(Theme.B(Color.FromArgb(40 * al / 255, Accent)), p);
                using (var pen = new Pen(Color.FromArgb(150 * al / 255, Accent), 1f)) g.DrawPath(pen, p);
            }
            G.Spaced(g, plat, fbd, Color.FromArgb(al, Mix(Accent, Color.White, 0.55f)), pbr.X + Theme.P(10), pbr.Y + (pbh - G.Measure(g, "A", fbd).Height) / 2 - Theme.P(1), Theme.P(1.4f));
            mx = pbr.Right + Theme.P(16);
            var parts = new List<KeyValuePair<string, string>>();
            if (game.Minutes > 0) parts.Add(new KeyValuePair<string, string>("", Hours(game.Minutes)));
            parts.Add(new KeyValuePair<string, string>("", Ago(game.LastPlayed)));
            if (game.Bytes > 0) parts.Add(new KeyValuePair<string, string>("", Size(game.Bytes)));
            var fmi = Theme.Icons(14);
            foreach (var kv in parts)
            {
                var isz = G.Measure(g, kv.Key, fmi);
                G.Text(g, kv.Key, fmi, Color.FromArgb(170 * al / 255, Theme.Cream), mx, my + Theme.P(10) - isz.Height / 2 + Theme.P(1));
                mx += isz.Width + Theme.P(8);
                var ts = G.Measure(g, kv.Value, fmeta);
                G.Text(g, kv.Value, fmeta, Color.FromArgb(225 * al / 255, Theme.Cream), mx, my + Theme.P(10) - ts.Height / 2 - Theme.P(1));
                mx += ts.Width + Theme.P(26);
            }

            // logo o nombre
            float ly = my - Theme.P(26);
            if (art.Ready && art.Logo != null)
            {
                float lx = x, top = ly - art.Logo.Height;
                int pad = Theme.P(28);
                using (var ia = Alpha(k))
                {
                    g.DrawImage(art.LogoShadow, new Rectangle((int)lx - pad, (int)top - pad + Theme.P(4), art.LogoShadow.Width, art.LogoShadow.Height), 0, 0, art.LogoShadow.Width, art.LogoShadow.Height, GraphicsUnit.Pixel, ia);
                    g.DrawImage(art.Logo, new Rectangle((int)lx, (int)top, art.Logo.Width, art.Logo.Height), 0, 0, art.Logo.Width, art.Logo.Height, GraphicsUnit.Pixel, ia);
                }
                ly = top;
            }
            else
            {
                // si el arte ya trae el logo dentro (pasa con Epic), el titulo va mas discreto
                var ftitle = Theme.F("Segoe UI Variable Display", art.Ready && art.Full ? 46 : 74, FontStyle.Bold);
                var lines = Wrap(g, game.Name, ftitle, W * 0.5f, 2);
                float lh = G.Measure(g, "Ag", ftitle).Height * 0.92f;
                float ty = ly - lh * lines.Count;
                foreach (var l in lines)
                {
                    G.Text(g, l, ftitle, Color.FromArgb(60 * al / 255, 0, 0, 0), x + Theme.P(2), ty + Theme.P(4));
                    G.Text(g, l, ftitle, Color.FromArgb(al, 250, 246, 238), x, ty);
                    ty += lh;
                }
                ly = ly - lh * lines.Count;
            }
            if (game == mostRecent && game.LastPlayed > DateTime.MinValue)
                G.Spaced(g, "SEGUIR JUGANDO", Theme.F("Segoe UI Variable Text Semibold", 12.5f), Color.FromArgb(al, Accent), x, ly - Theme.P(30), Theme.P(3f));
        }

        void RoundButton(Graphics g, string id, string glyph, float x, float y, float s, int al)
        {
            var r = new RectangleF(x, y, s, s);
            hits[id] = r;
            bool h = hot == id;
            g.FillEllipse(Theme.B(Color.FromArgb((h ? 70 : 34) * al / 255, Theme.Cream)), r);
            using (var pen = new Pen(Color.FromArgb((h ? 120 : 60) * al / 255, Theme.Cream), 1f)) g.DrawEllipse(pen, r);
            var f = Theme.Icons(18); var sz = G.Measure(g, glyph, f);
            G.Text(g, glyph, f, Color.FromArgb(al, Theme.Cream), r.X + (s - sz.Width) / 2, r.Y + (s - sz.Height) / 2);
        }

        void DrawShelf(Graphics g, float rise)
        {
            cardR.Clear();
            float y = shelfTop + rise;
            var acc = Accent;
            int focusIdx = -1;
            for (int i = 0; i < view.Count; i++)
            {
                float x = M + i * Step - scroll;
                var r = new RectangleF(x, y, cw, ch);
                cardR.Add(r);
                if (i == focus) { focusIdx = i; continue; }               // la enfocada se pinta la ultima, encima
                if (r.Right < -Theme.P(40) || r.X > W + Theme.P(40)) continue;
                DrawCard(g, view[i], r, i < scale.Length ? scale[i] : 1f, false, acc);
            }
            if (focusIdx >= 0)
            {
                var r = cardR[focusIdx];
                DrawCard(g, view[focusIdx], r, focusIdx < scale.Length ? scale[focusIdx] : 1.08f, true, acc);
            }
            // flechas de "hay mas" en los bordes
            if (scroll > 1) Chevron(g, "", Theme.P(30), y + ch / 2);
            if (scroll < MaxScroll - 1) Chevron(g, "", W - Theme.P(66), y + ch / 2);
        }

        void Chevron(Graphics g, string glyph, float x, float cy)
        {
            var f = Theme.Icons(20); var s = G.Measure(g, glyph, f);
            G.Text(g, glyph, f, Theme.A(Theme.Cream, 140), x + (Theme.P(36) - s.Width) / 2, cy - s.Height / 2);
        }

        void DrawCard(Graphics g, Game game, RectangleF r, float s, bool focused, Color acc)
        {
            float w = r.Width * s, h = r.Height * s;
            var rr = new RectangleF(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2 - (s - 1) * r.Height * 0.25f, w, h);
            float rad = Theme.P(10) * s;
            float k = Math.Max(0, Math.Min(1, (s - 1) / 0.08f));      // 0..1 cuanto de "enfocada" tiene
            // sombra
            using (var p = G.Round(new RectangleF(rr.X + Theme.P(3), rr.Y + Theme.P(10), rr.Width - Theme.P(6), rr.Height), rad))
                g.FillPath(Theme.B(Color.FromArgb(90, 0, 0, 0)), p);
            if (focused)
            {
                // resplandor del color del juego
                for (int i = 1; i <= 6; i++)
                    using (var p = G.Round(RectangleF.Inflate(rr, i * Theme.P(4), i * Theme.P(4)), rad + i * Theme.P(4)))
                        g.FillPath(Theme.B(Color.FromArgb((int)(12 * k), acc)), p);
            }
            Bitmap cov;
            if (covers.TryGetValue(game, out cov) && cov != null)
            {
                if (Math.Abs(s - 1) < 0.002f) g.DrawImageUnscaled(cov, (int)Math.Round(rr.X), (int)Math.Round(rr.Y));
                else
                {
                    var old = g.InterpolationMode; g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(cov, rr);
                    g.InterpolationMode = old;
                }
            }
            if (!focused)
            {
                // las que no estan enfocadas, un poco apagadas; las que se salen por los bordes, mas
                int dim = game == (hoverCard >= 0 && hoverCard < view.Count ? view[hoverCard] : null) ? 20 : 92;
                float edge = Math.Min(Math.Max(0, (W - M * 0.5f - rr.X) / Step), Math.Max(0, (rr.Right - M * 0.5f) / Step));
                if (edge < 1) dim = (int)(dim + (238 - dim) * (1 - edge));
                using (var p = G.Round(rr, rad)) g.FillPath(Theme.B(Color.FromArgb(dim, 4, 5, 4)), p);
            }
            else
            {
                // aro claro separado de la caratula
                float o = Theme.P(5);
                using (var p = G.Round(RectangleF.Inflate(rr, o, o), rad + o))
                using (var pen = new Pen(Color.FromArgb((int)(255 * k), 250, 246, 238), Theme.P(3))) g.DrawPath(pen, p);
                // nombre debajo
                var fn = Theme.F("Segoe UI Variable Text Semibold", 14.5f);
                string name = G.Fit(g, game.Name, fn, Theme.P(260));
                var ns = G.Measure(g, name, fn);
                float nx = rr.X + rr.Width / 2 - ns.Width / 2;
                nx = Math.Max(Theme.P(16), Math.Min(W - Theme.P(16) - ns.Width, nx));
                G.Text(g, name, fn, Color.FromArgb((int)(240 * k), Theme.Cream), nx, rr.Bottom + Theme.P(14));
            }
        }

        void DrawFooter(Graphics g)
        {
            float cy = H - Theme.P(34);
            var fk = Theme.F("Segoe UI Variable Text Semibold", 11);
            var fl = Theme.F("Segoe UI Variable Text", 13);
            string[][] keys = { new[] { "← →", "Elegir" }, new[] { "Enter", "Jugar" }, new[] { "Tab", "Plataforma" }, new[] { "Esc", "Salir" } };
            float x = W - M;
            for (int i = keys.Length - 1; i >= 0; i--)
            {
                var ls = G.Measure(g, keys[i][1], fl);
                x -= ls.Width;
                G.Text(g, keys[i][1], fl, Theme.A(Theme.Cream, 150), x, cy - ls.Height / 2 - Theme.P(1));
                x -= Theme.P(8);
                var s = G.Measure(g, keys[i][0], fk);
                float kw = Math.Max(Theme.P(26), s.Width + Theme.P(14));
                KeyCap(g, keys[i][0], fk, x - kw, cy);
                x -= kw + Theme.P(26);
            }
            int total = all.Sum(gm => Math.Max(0, gm.Minutes));
            string left = all.Count + (all.Count == 1 ? " juego" : " juegos") + (total > 60 ? "   ·   " + (total / 60).ToString("N0", Es) + " h jugadas en Steam" : "");
            var ls2 = G.Measure(g, left, fl);
            G.Text(g, left, fl, Theme.A(Theme.Cream, 120), M, cy - ls2.Height / 2 - Theme.P(1));
        }

        void DrawLaunching(Graphics g)
        {
            float t = Since(tLaunch, 500);
            int al = (int)(235 * Ease(t));
            g.FillRectangle(Theme.B(Color.FromArgb(al, 5, 6, 5)), 0, 0, W, H);
            var acc = Accent;
            Bitmap cov;
            float s = 1.3f + 0.06f * Ease(t);
            float w = cw * s, h = ch * s, cx = W / 2f, top = H / 2f - h / 2 - Theme.P(70);
            var rr = new RectangleF(cx - w / 2, top, w, h);
            for (int i = 1; i <= 8; i++)
                using (var p = G.Round(RectangleF.Inflate(rr, i * Theme.P(6), i * Theme.P(6)), Theme.P(14) + i * Theme.P(6)))
                    g.FillPath(Theme.B(Color.FromArgb((int)(10 * Ease(t)), acc)), p);
            if (covers.TryGetValue(launching, out cov) && cov != null)
                using (var ia = Alpha(Ease(t))) g.DrawImage(cov, Rectangle.Round(rr), 0, 0, cov.Width, cov.Height, GraphicsUnit.Pixel, ia);
            var f1 = Theme.F("Segoe UI Variable Text Semibold", 13);
            string t1 = "INICIANDO";
            float w1 = G.SpacedWidth(g, t1, f1, Theme.P(5));
            G.Spaced(g, t1, f1, Color.FromArgb((int)(255 * Ease(t)), acc), cx - w1 / 2, rr.Bottom + Theme.P(44), Theme.P(5));
            var f2 = Theme.F("Segoe UI Variable Display", 40, FontStyle.Bold);
            var s2 = G.Measure(g, launching.Name, f2);
            G.Text(g, launching.Name, f2, Color.FromArgb((int)(255 * Ease(t)), Theme.Cream), cx - s2.Width / 2, rr.Bottom + Theme.P(72));
            // barra de progreso indeterminada
            float bw = Theme.P(220), by = rr.Bottom + Theme.P(146);
            using (var p = G.Round(new RectangleF(cx - bw / 2, by, bw, Theme.P(3)), Theme.P(1.5f))) g.FillPath(Theme.B(Theme.A(Theme.Cream, 30)), p);
            float ph = ((Now - tLaunch) % 1100) / 1100f, segW = bw * 0.32f;
            float sx = cx - bw / 2 + (bw + segW) * Ease(ph) - segW;
            var seg = RectangleF.Intersect(new RectangleF(sx, by, segW, Theme.P(3)), new RectangleF(cx - bw / 2, by, bw, Theme.P(3)));
            if (seg.Width > 0) using (var p = G.Round(seg, Theme.P(1.5f))) g.FillPath(Theme.B(acc), p);
        }

        static string Hours(int min)
        {
            if (min < 60) return min + " min jugados";
            double h = min / 60.0;
            return (h < 10 ? h.ToString("0.#", Es) : h.ToString("N0", Es)) + " h jugadas";
        }

        static string Ago(DateTime t)
        {
            if (t == DateTime.MinValue) return "Sin estrenar";
            var d = (DateTime.Now.Date - t.Date).Days;
            if (d <= 0) return "Jugado hoy";
            if (d == 1) return "Jugado ayer";
            if (d < 30) return "Hace " + d + " días";
            if (t.Year == DateTime.Now.Year) return "El " + t.ToString("d 'de' MMMM", Es);
            return t.ToString("MMMM 'de' yyyy", Es).Substring(0, 1).ToUpper() + t.ToString("MMMM 'de' yyyy", Es).Substring(1);
        }

        static string Size(long b)
        {
            double gb = b / 1073741824.0;
            return gb >= 10 ? gb.ToString("N0", Es) + " GB" : gb >= 1 ? gb.ToString("0.0", Es) + " GB" : (b / 1048576.0).ToString("N0", Es) + " MB";
        }

        // ------------------------------------------------------------------ acciones
        void Launch(Game game)
        {
            if (game == null || launching != null) return;
            launching = game; tLaunch = Now;
            try { Library.MarkPlayed(dir, game); } catch { }
            Library.Start(game);
            Animate();
            N.SetTimer(Hwnd, new IntPtr(2), 1500, IntPtr.Zero);                 // un momento con "INICIANDO" y se cierra
        }

        void Close() { N.PostQuitMessage(0); }

        void CardMenu(Game game)
        {
            if (game == null) return;
            IntPtr menu = N.CreatePopupMenu();
            N.AppendMenu(menu, 0, new IntPtr(1), "Jugar a " + game.Name.Replace("&", "&&"));
            if (!string.IsNullOrEmpty(game.Folder) && Directory.Exists(game.Folder)) N.AppendMenu(menu, 0, new IntPtr(2), "Abrir la carpeta del juego");
            N.AppendMenu(menu, 0x800, IntPtr.Zero, null);
            N.AppendMenu(menu, 0, new IntPtr(3), "Ocultar del portal");
            N.AppendMenu(menu, 0, new IntPtr(4), "Añadir juegos a mano (juegos-extra.txt)");
            N.POINT p; N.GetCursorPos(out p);
            int cmd = N.TrackPopupMenu(menu, 0x0100, p.X, p.Y, 0, Hwnd, IntPtr.Zero);
            N.DestroyMenu(menu);
            if (cmd == 1) Launch(game);
            else if (cmd == 2) OpenFolder(game);
            else if (cmd == 3) { Library.Hide(dir, game); all.Remove(game); Refilter(); Paint(); }
            else if (cmd == 4) N.ShellExecute(IntPtr.Zero, "open", "notepad.exe", "\"" + Path.Combine(dir, "juegos-extra.txt") + "\"", null, 1);
        }

        void OpenFolder(Game game)
        {
            if (!string.IsNullOrEmpty(game.Folder) && Directory.Exists(game.Folder)) N.ShellExecute(IntPtr.Zero, "open", game.Folder, null, null, 1);
        }

        int CardAt(int x, int y)
        {
            // la enfocada esta encima y algo mas grande
            if (focus < cardR.Count && RectangleF.Inflate(cardR[focus], cw * 0.04f, ch * 0.06f).Contains(x, y)) return focus;
            for (int i = 0; i < cardR.Count; i++) if (cardR[i].Contains(x, y)) return i;
            return -1;
        }

        protected override IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
        {
            int lx = (short)(l.ToInt64() & 0xFFFF), ly = (short)((l.ToInt64() >> 16) & 0xFFFF);
            switch (m)
            {
                case N.WM_TIMER:
                    {
                        long id = w.ToInt64();
                        if (id == T_ANIM) Tick();
                        else if (id == 2) Close();
                        else if (id == T_HOVER)
                        {
                            N.KillTimer(Hwnd, new IntPtr(T_HOVER));
                            if (pendingFocus >= 0 && pendingFocus == hoverCard) SetFocus(pendingFocus, false);
                            pendingFocus = -1;
                        }
                        else if (id == T_CLOCK)
                        {
                            if (DateTime.Now.Minute != lastMinute || query.Length > 0) { lastMinute = DateTime.Now.Minute; if (!animating) Paint(); }
                        }
                        return IntPtr.Zero;
                    }
                case WM_ART:
                    {
                        int i = w.ToInt32();
                        if (i >= 0 && i < all.Count && all[i] == bgGame) { SetBg(bgGame, true); }
                        else if (!animating) Paint();
                        return IntPtr.Zero;
                    }
                case WM_COVER:
                    {
                        int i = w.ToInt32();
                        if (i >= 0 && i < all.Count) { var old = covers[all[i]]; covers[all[i]] = MakeCover(all[i]); if (old != null) old.Dispose(); }
                        if (!animating) Paint();
                        return IntPtr.Zero;
                    }
                case 0x001C:   // WM_ACTIVATEAPP: si te vas a otra cosa (tecla Windows, Alt+Tab), el portal se cierra
                    if (w != IntPtr.Zero) wasActive = true;
                    else if (wasActive && launching == null) Close();
                    return IntPtr.Zero;
                case N.WM_MOUSEMOVE:
                    {
                        string nh = null;
                        foreach (var kv in hits) if (kv.Value.Contains(lx, ly)) nh = kv.Key;
                        int card = nh == null ? CardAt(lx, ly) : -1;
                        bool changed = nh != hot || card != hoverCard;
                        hot = nh;
                        if (card != hoverCard)
                        {
                            hoverCard = card;
                            if (card >= 0 && card != focus)
                            {
                                pendingFocus = card;
                                N.SetTimer(Hwnd, new IntPtr(T_HOVER), 70, IntPtr.Zero);
                            }
                        }
                        if (changed) { Animate(); }
                        return IntPtr.Zero;
                    }
                case N.WM_LBUTTONUP:
                    {
                        if (launching != null) return IntPtr.Zero;
                        string at = null;
                        foreach (var kv in hits) if (kv.Value.Contains(lx, ly)) at = kv.Key;
                        if (at == "close") { Close(); return IntPtr.Zero; }
                        if (at == "play") { Launch(Focused); return IntPtr.Zero; }
                        if (at == "folder") { if (Focused != null) OpenFolder(Focused); return IntPtr.Zero; }
                        if (at == "more") { CardMenu(Focused); return IntPtr.Zero; }
                        if (at == "search") { if (query.Length > 0) { query = ""; Refilter(); } return IntPtr.Zero; }
                        if (at != null && at.StartsWith("tab:")) { filter = at.Substring(4); Refilter(); return IntPtr.Zero; }
                        int c = CardAt(lx, ly);
                        if (c >= 0) { SetFocus(c, false); Launch(view[c]); }
                        return IntPtr.Zero;
                    }
                case N.WM_RBUTTONUP:
                    {
                        int c = CardAt(lx, ly);
                        if (c >= 0) { SetFocus(c, false); CardMenu(view[c]); }
                        return IntPtr.Zero;
                    }
                case N.WM_MOUSEWHEEL:
                    {
                        int delta = (short)((w.ToInt64() >> 16) & 0xFFFF);
                        scrollTarget = Math.Max(0, Math.Min(MaxScroll, scrollTarget - delta / 120f * Step * 2));
                        Animate();
                        return IntPtr.Zero;
                    }
                case 0x0100:   // WM_KEYDOWN
                    {
                        if (launching != null) return IntPtr.Zero;
                        int vk = (int)w.ToInt64();
                        bool shift = (N.GetKeyState(0x10) & 0x8000) != 0;
                        if (vk == 0x1B) { if (query.Length > 0) { query = ""; Refilter(); } else Close(); }
                        else if (vk == 0x0D) Launch(Focused);
                        else if (vk == 0x25 || vk == 0x26) SetFocus(focus - 1, true);
                        else if (vk == 0x27 || vk == 0x28) SetFocus(focus + 1, true);
                        else if (vk == 0x24) SetFocus(0, true);
                        else if (vk == 0x23) SetFocus(view.Count - 1, true);
                        else if (vk == 0x21) SetFocus(focus - 5, true);
                        else if (vk == 0x22) SetFocus(focus + 5, true);
                        else if (vk == 0x5D) CardMenu(Focused);
                        else if (vk == 0x09)
                        {
                            var avail = Filters.Where(f => f == "TODOS" || Count(f) > 0).ToList();
                            int idx = avail.IndexOf(filter);
                            filter = avail[(idx + (shift ? avail.Count - 1 : 1)) % avail.Count];
                            Refilter();
                        }
                        return IntPtr.Zero;
                    }
                case 0x0102:   // WM_CHAR: escribir = buscar
                    {
                        if (launching != null) return IntPtr.Zero;
                        char c = (char)w.ToInt64();
                        if (c == '\b') { if (query.Length == 0) return IntPtr.Zero; query = query.Substring(0, query.Length - 1); }
                        else if (c >= 32 && c != 127 && query.Length < 40) { if (c == ' ' && query.Length == 0) return IntPtr.Zero; query += c; }
                        else return IntPtr.Zero;
                        Refilter();
                        return IntPtr.Zero;
                    }
            }
            return base.WndProc(h, m, w, l);
        }
    }

    static class PortalProgram
    {
        [STAThread]
        static void Main(string[] args)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                Theme.S = N.GetDpiForSystem() / 96f;
                // utilidades para compilar y probar:
                //   --icono <ico> [png]               genera el icono (y una hoja de muestra)
                //   --captura <png> [foco] [texto] [filtro]   dibuja el portal en un PNG sin abrir ventana
                //   --lanzar <clave|numero>           lanza un juego igual que el boton Jugar
                //   --lista <txt>                     escribe las claves de los juegos
                if (args.Length >= 2 && args[0] == "--icono") { Brand.WriteIco(args[1], args.Length > 2 ? args[2] : null); return; }
                if (args.Length >= 2 && args[0] == "--captura")
                {
                    var pw = new PortalWindow(dir);
                    pw.Open(true, args.Length > 2 ? int.Parse(args[2]) : 0, args.Length > 3 ? args[3] : "", args.Length > 4 ? args[4] : "TODOS");
                    pw.SaveFrame(args[1]);
                    return;
                }
                if (args.Length >= 1 && args[0] == "--medir")
                {
                    var pw = new PortalWindow(dir);
                    pw.Open(true);
                    Log.W("medir: " + pw.Bench());
                    return;
                }
                if (args.Length >= 2 && args[0] == "--lista")
                {
                    var games = Library.Scan(dir);
                    File.WriteAllLines(args[1], games.Select((g, i) => i + " | " + g.Key + " | " + g.Name + " | " + g.Launch + " | " + g.Folder));
                    return;
                }
                if (args.Length >= 2 && args[0] == "--lanzar")
                {
                    var games = Library.Scan(dir);
                    int n;
                    var game = int.TryParse(args[1], out n) && n >= 0 && n < games.Count ? games[n] : games.FirstOrDefault(g => g.Key == args[1]);
                    Log.W("prueba lanzar " + (game == null ? args[1] + " (no existe)" : game.Key + " -> " + Library.Start(game)));
                    return;
                }
            }
            catch (Exception e) { Log.W("portal: " + e); return; }

            // si ya esta abierto, solo traerlo delante
            IntPtr existing = N.FindWindow("HudSurface", "Portal de juegos");
            if (existing != IntPtr.Zero) { N.SetForegroundWindow(existing); return; }
            try
            {
                var win = new PortalWindow(dir);
                win.Open();
                N.MSG msg;
                while (N.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) { N.TranslateMessage(ref msg); N.DispatchMessage(ref msg); }
                win.Destroy();
            }
            catch (Exception e) { Log.W("portal: " + e); }
        }
    }
}
