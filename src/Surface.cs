using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace Hud
{
    static class Log
    {
        static readonly string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hud.log");
        public static void W(string m)
        {
            try
            {
                var fi = new FileInfo(file);
                if (fi.Exists && fi.Length > 256 * 1024) fi.Delete();
                File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + m + Environment.NewLine);
            }
            catch { }
        }
        public static void E(Exception e) { W(e.GetType().Name + ": " + e.Message + " @ " + (e.StackTrace ?? "").Split('\n')[0].Trim()); }
    }

    // El escritorio de Windows (la vista de iconos). Los widgets se cuelgan de ahi:
    // se ven solo cuando ves el escritorio y cualquier ventana los tapa.
    static class Desktop
    {
        public static IntPtr DefView()
        {
            IntPtr progman = N.FindWindow("Progman", null);
            IntPtr dv = N.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (dv != IntPtr.Zero) return dv;
            IntPtr found = IntPtr.Zero;
            N.EnumWindows((h, l) =>
            {
                IntPtr d = N.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (d != IntPtr.Zero) { found = d; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        public static bool Attach(IntPtr h)
        {
            IntPtr dv = DefView();
            if (dv == IntPtr.Zero) return false;
            long st = N.GetWindowLongPtr(h, N.GWL_STYLE).ToInt64();
            st = (st & ~0x80000000L) | 0x40000000L | N.WS_CLIPSIBLINGS;
            N.SetWindowLongPtr(h, N.GWL_STYLE, new IntPtr(st));
            N.SetParent(h, dv);
            return true;
        }

        // Ocultar/mostrar iconos del escritorio igual que "Ver > Mostrar iconos del escritorio".
        public static void SetIconsHidden(bool hidden)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
                    k.SetValue("HideIcons", hidden ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
                IntPtr dv = DefView();
                IntPtr lv = N.FindWindowEx(dv, IntPtr.Zero, "SysListView32", null);
                if (dv != IntPtr.Zero && lv != IntPtr.Zero && N.IsWindowVisible(lv) == hidden)
                    N.SendMessage(dv, N.WM_COMMAND, new IntPtr(0x7402), IntPtr.Zero);
            }
            catch (Exception e) { Log.E(e); }
        }
    }

    // Ventana con transparencia por pixel (UpdateLayeredWindow) dibujada con GDI+
    // directamente sobre memoria DIB: sin copias, sin GPU, sin frameworks.
    abstract class Surface
    {
        const string CLS = "HudSurface";
        static readonly Dictionary<IntPtr, Surface> map = new Dictionary<IntPtr, Surface>();
        static N.WndProc procRef;
        public static readonly IntPtr Inst = N.GetModuleHandle(IntPtr.Zero);

        public IntPtr Hwnd;
        public int X, Y, W, H;            // coordenadas de pantalla, pixeles fisicos
        public bool DesktopChild, Topmost, ClickThrough, Activatable;
        public string Name = "hud";
        protected Bitmap Canvas;
        IntPtr memDC, hBmp, oldBmp;
        int cw, ch;
        bool shown;

        static void Register()
        {
            if (procRef != null) return;
            procRef = StaticProc;
            var wc = new N.WNDCLASSEX();
            wc.cbSize = Marshal.SizeOf(typeof(N.WNDCLASSEX));
            wc.lpfnWndProc = procRef;
            wc.hInstance = Inst;
            wc.lpszClassName = CLS;
            wc.hCursor = N.LoadCursor(IntPtr.Zero, new IntPtr(32512));
            N.RegisterClassEx(ref wc);
        }

        static IntPtr StaticProc(IntPtr h, uint m, IntPtr w, IntPtr l)
        {
            Surface s;
            if (map.TryGetValue(h, out s))
            {
                try { return s.WndProc(h, m, w, l); }
                catch (Exception e) { Log.E(e); }
            }
            return N.DefWindowProc(h, m, w, l);
        }

        protected virtual IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
        {
            if (m == N.WM_MOUSEACTIVATE && !Activatable) return new IntPtr(3);   // MA_NOACTIVATE
            if (m == N.WM_DESTROY)
            {
                map.Remove(h);
                if (h == Hwnd) { Hwnd = IntPtr.Zero; shown = false; }
            }
            return N.DefWindowProc(h, m, w, l);
        }

        public bool Alive { get { return Hwnd != IntPtr.Zero && N.IsWindow(Hwnd); } }

        public void Create()
        {
            Register();
            int ex = N.WS_EX_LAYERED | (Activatable ? 0 : N.WS_EX_TOOLWINDOW | N.WS_EX_NOACTIVATE) | (Topmost ? N.WS_EX_TOPMOST : 0) | (ClickThrough ? 0x20 : 0);
            Hwnd = N.CreateWindowEx(ex, CLS, Name, N.WS_POPUP, X, Y, Math.Max(1, W), Math.Max(1, H), IntPtr.Zero, IntPtr.Zero, Inst, IntPtr.Zero);
            map[Hwnd] = this;
            shown = false;
            if (DesktopChild && !Desktop.Attach(Hwnd)) Log.W("no se encontro el escritorio para " + Name);
            Place();
        }

        public void Destroy()
        {
            if (Hwnd != IntPtr.Zero) { var h = Hwnd; Hwnd = IntPtr.Zero; map.Remove(h); N.DestroyWindow(h); }
        }

        public void SetBounds(int x, int y, int w, int h)
        {
            X = x; Y = y; W = w; H = h;
            Place();
        }

        void Place()
        {
            if (Hwnd == IntPtr.Zero) return;
            var p = new N.POINT(X, Y);
            if (DesktopChild) { var par = N.GetParent(Hwnd); if (par != IntPtr.Zero) N.ScreenToClient(par, ref p); }
            N.SetWindowPos(Hwnd, Topmost ? N.HWND_TOPMOST : N.HWND_TOP, p.X, p.Y, W, H, N.SWP_NOACTIVATE);
        }

        protected Graphics BeginDraw()
        {
            if (Canvas == null || cw != W || ch != H) AllocCanvas();
            var g = Graphics.FromImage(Canvas);
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            return g;
        }

        void AllocCanvas()
        {
            FreeCanvas();
            cw = W; ch = H;
            var bi = new N.BITMAPINFOHEADER();
            bi.biSize = Marshal.SizeOf(typeof(N.BITMAPINFOHEADER));
            bi.biWidth = cw; bi.biHeight = -ch; bi.biPlanes = 1; bi.biBitCount = 32;
            IntPtr screen = N.GetDC(IntPtr.Zero);
            IntPtr bits;
            hBmp = N.CreateDIBSection(screen, ref bi, 0, out bits, IntPtr.Zero, 0);
            memDC = N.CreateCompatibleDC(screen);
            N.ReleaseDC(IntPtr.Zero, screen);
            oldBmp = N.SelectObject(memDC, hBmp);
            Canvas = new Bitmap(cw, ch, cw * 4, PixelFormat.Format32bppPArgb, bits);
        }

        void FreeCanvas()
        {
            if (Canvas != null) { Canvas.Dispose(); Canvas = null; }
            if (memDC != IntPtr.Zero) { N.SelectObject(memDC, oldBmp); N.DeleteDC(memDC); memDC = IntPtr.Zero; }
            if (hBmp != IntPtr.Zero) { N.DeleteObject(hBmp); hBmp = IntPtr.Zero; }
        }

        protected void Present(byte alpha = 255)
        {
            if (Hwnd == IntPtr.Zero || memDC == IntPtr.Zero) return;
            var size = new N.SIZE(cw, ch);
            var src = new N.POINT(0, 0);
            var bf = new N.BLENDFUNCTION { Op = 0, Flags = 0, Alpha = alpha, Format = 1 };
            N.UpdateLayeredWindow(Hwnd, IntPtr.Zero, IntPtr.Zero, ref size, memDC, ref src, 0, ref bf, 2);
            if (!shown) { N.ShowWindow(Hwnd, N.SW_SHOWNA); shown = true; }
        }

        // ¿Se ve algo de esta ventana ahora mismo? (si una app la tapa, no merece la pena animarla)
        public bool IsExposed()
        {
            if (Hwnd == IntPtr.Zero) return false;
            var p = new N.POINT(X + W / 2, Y + H / 2);
            IntPtr at = N.WindowFromPoint(p);
            if (at == Hwnd) return true;
            // WindowFromPoint ignora los pixeles transparentes; probamos otro punto opaco
            p = new N.POINT(X + 20, Y + H - 20);
            at = N.WindowFromPoint(p);
            return at == Hwnd;
        }
    }
}
