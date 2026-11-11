using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Hud
{
    class Game
    {
        public string Name = "", Platform = "", Launch = "", Args = "", Cover = "", Hero = "", Logo = "", Icon = "", Folder = "", Key = "";
        public DateTime LastPlayed = DateTime.MinValue;
        public int Minutes = -1;          // tiempo jugado (solo Steam lo guarda en el PC)
        public long Bytes;                // tamaño en disco
        public string EpicItem = "";      // para bajar la caratula oficial de Epic
    }

    // Busca los juegos instalados cada vez que se abre el portal: Steam y Epic solos,
    // el resto desde juegos-extra.txt. Los ocultos se guardan en juegos-ocultos.txt.
    static class Library
    {
        static readonly HashSet<string> notGames = new HashSet<string> { "228980", "250820", "1366800", "1070560", "1391110", "1628350", "1493710", "2180100" };

        public static List<Game> Scan(string dir)
        {
            var list = new List<Game>();
            try { Steam(list); } catch (Exception e) { Log.W("portal steam: " + e.Message); }
            try { Epic(list, dir); } catch (Exception e) { Log.W("portal epic: " + e.Message); }
            try { Extras(list, Path.Combine(dir, "juegos-extra.txt")); } catch (Exception e) { Log.W("portal extra: " + e.Message); }
            var hidden = ReadSet(Path.Combine(dir, "juegos-ocultos.txt"));
            list.RemoveAll(g => hidden.Contains(g.Key));
            // ultimas partidas lanzadas desde el portal (para los que no son de Steam)
            foreach (var kv in ReadRecent(Path.Combine(dir, "juegos-recientes.txt")))
            {
                var g = list.FirstOrDefault(x => x.Key == kv.Key);
                if (g != null && kv.Value > g.LastPlayed) g.LastPlayed = kv.Value;
            }
            // primero lo ultimo que has jugado; lo que nunca has abierto, por nombre al final
            return list.OrderByDescending(g => g.LastPlayed).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        // ------------------------------------------------------------------ lanzar
        // Los enlaces (steam://, com.epicgames.launcher://, uplay://) y los accesos directos se los pasamos
        // a explorer.exe: Windows tarda un par de segundos en atender el enlace cuando el launcher esta
        // cerrado y, si el programa que lo pidio ya se ha cerrado, la orden se pierde (eso pasaba con Epic).
        // explorer se queda vivo lo que haga falta. Los .exe se arrancan directamente en su carpeta.
        public static bool Start(Game g)
        {
            string t = g.Launch;
            try
            {
                bool exe = !t.Contains("://") && t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                if (exe) return Spawn("\"" + t + "\"" + (g.Args.Length > 0 ? " " + g.Args : ""), Path.GetDirectoryName(t));
                string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                return Spawn("\"" + explorer + "\" \"" + t + "\"", null);
            }
            catch (Exception e) { Log.W("lanzar " + g.Key + ": " + e.Message); return false; }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SI { public int cb; public string r, desk, title; public int x, y, w, h, cx, cy, fill, flags; public short show, r2; public IntPtr r3, i, o, e; }
        [StructLayout(LayoutKind.Sequential)] struct PI { public IntPtr hp, ht; public int pid, tid; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref SI si, out PI pi);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        // Si el portal se abrio desde algo que corre dentro de un "job" de Windows (por ejemplo, una tarea
        // programada), intenta sacar el juego de ahi: asi, si ese programa se cierra, el juego no se cierra con el.
        static bool Spawn(string cmd, string wd)
        {
            var si = new SI(); si.cb = Marshal.SizeOf(typeof(SI));
            PI pi;
            foreach (uint flags in new uint[] { 0x01000000 /*BREAKAWAY_FROM_JOB*/, 0 })
                if (CreateProcess(null, new StringBuilder(cmd), IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, wd, ref si, out pi))
                {
                    CloseHandle(pi.hp); CloseHandle(pi.ht);
                    return true;
                }
            Log.W("no se pudo lanzar: " + cmd + " (error " + Marshal.GetLastWin32Error() + ")");
            return false;
        }

        // ------------------------------------------------------------------ Steam
        static void Steam(List<Game> list)
        {
            string root = null;
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                if (k != null) root = Convert.ToString(k.GetValue("SteamPath", "")).Replace('/', '\\');
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) root = @"C:\Program Files (x86)\Steam";
            if (!Directory.Exists(root)) return;
            var libs = new List<string> { root };
            string vdf = Path.Combine(root, @"steamapps\libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                {
                    string p = m.Groups[1].Value.Replace("\\\\", "\\");
                    if (!libs.Any(l => string.Equals(l.TrimEnd('\\'), p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) libs.Add(p);
                }
            // tiempo jugado: lo guarda cada cuenta en userdata\<id>\config\localconfig.vdf
            var configs = new List<string>();
            try
            {
                string ud = Path.Combine(root, "userdata");
                if (Directory.Exists(ud))
                    foreach (var u in Directory.GetDirectories(ud))
                    {
                        string lc = Path.Combine(u, @"config\localconfig.vdf");
                        if (File.Exists(lc)) configs.Add(File.ReadAllText(lc));
                    }
            }
            catch { }

            string cache = Path.Combine(root, @"appcache\librarycache");
            foreach (var lib in libs)
            {
                string apps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(apps)) continue;
                foreach (var f in Directory.GetFiles(apps, "appmanifest_*.acf"))
                {
                    string t = File.ReadAllText(f);
                    string id = Field(t, "appid"), name = Field(t, "name"), inst = Field(t, "installdir"), lp = Field(t, "LastPlayed");
                    if (id == "" || notGames.Contains(id)) continue;
                    if (Regex.IsMatch(name, "Redistributable|Proton|Steam Linux Runtime|SteamVR|Steamworks", RegexOptions.IgnoreCase)) continue;
                    var g = new Game { Name = name, Platform = "STEAM", Launch = "steam://rungameid/" + id, Key = "steam:" + id, Folder = Path.Combine(apps, "common", inst) };
                    long secs, size;
                    if (long.TryParse(lp, out secs) && secs > 0) g.LastPlayed = DateTimeOffset.FromUnixTimeSeconds(secs).LocalDateTime;
                    if (long.TryParse(Field(t, "SizeOnDisk"), out size)) g.Bytes = size;
                    foreach (var c in configs) ReadPlaytime(c, id, g);
                    g.Cover = FirstFile(Path.Combine(cache, id), "library_600x900.jpg", "library_capsule.jpg") ?? FirstFile(cache, id + "_library_600x900.jpg");
                    g.Hero = FirstFile(Path.Combine(cache, id), "library_hero.jpg") ?? FirstFile(cache, id + "_library_hero.jpg");
                    g.Logo = FirstFile(Path.Combine(cache, id), "logo.png") ?? FirstFile(cache, id + "_logo.png");
                    if (g.Cover == null) g.Cover = FirstFile(Path.Combine(cache, id), "library_header.jpg", "header.jpg") ?? "";
                    if (g.Hero == null) g.Hero = "";
                    if (g.Logo == null) g.Logo = "";
                    list.Add(g);
                }
            }
        }

        // busca el bloque  "<appid>" { ... "Playtime" "123" ... }  de localconfig.vdf
        static void ReadPlaytime(string vdf, string id, Game g)
        {
            foreach (Match m in Regex.Matches(vdf, "\"" + id + "\"\\s*\\{"))
            {
                int start = m.Index + m.Length, depth = 1, i = start;
                for (; i < vdf.Length && depth > 0; i++) { if (vdf[i] == '{') depth++; else if (vdf[i] == '}') depth--; }
                string block = vdf.Substring(start, Math.Max(0, i - start));
                var pt = Regex.Match(block, "\"Playtime\"\\s+\"(\\d+)\"");
                int min;
                if (pt.Success && int.TryParse(pt.Groups[1].Value, out min) && min > g.Minutes) g.Minutes = min;
                var lp = Regex.Match(block, "\"LastPlayed\"\\s+\"(\\d+)\"");
                long secs;
                if (lp.Success && long.TryParse(lp.Groups[1].Value, out secs) && secs > 0)
                {
                    var d = DateTimeOffset.FromUnixTimeSeconds(secs).LocalDateTime;
                    if (d > g.LastPlayed) g.LastPlayed = d;
                }
            }
        }

        static string Field(string vdf, string key)
        {
            var m = Regex.Match(vdf, "\"" + key + "\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : "";
        }

        static string FirstFile(string folder, params string[] names)
        {
            if (!Directory.Exists(folder)) return null;
            foreach (var n in names)
            {
                string p = Path.Combine(folder, n);
                if (File.Exists(p)) return p;
                // a veces van en una subcarpeta con hash
                try { var hit = Directory.GetFiles(folder, n, SearchOption.AllDirectories).FirstOrDefault(); if (hit != null) return hit; } catch { }
            }
            return null;
        }

        // ------------------------------------------------------------------ Epic
        static void Epic(List<Game> list, string dir)
        {
            string man = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
            if (!Directory.Exists(man)) return;
            foreach (var f in Directory.GetFiles(man, "*.item"))
            {
                string t = File.ReadAllText(f);
                string cats = Json(t, "AppCategories", true);
                if (!cats.Contains("games") || cats.Contains("hidden")) continue;
                string name = Json(t, "DisplayName"), app = Json(t, "AppName"), ns = Json(t, "CatalogNamespace"), item = Json(t, "CatalogItemId");
                string loc = Json(t, "InstallLocation").Replace('/', '\\'), exe = Json(t, "LaunchExecutable").Replace('/', '\\');
                if (name == "" || app == "") continue;
                name = name.Replace("\u00AE", "").Replace("\u2122", "").Trim();
                var g = new Game
                {
                    Name = name, Platform = "EPIC", Key = "epic:" + app, Folder = loc, EpicItem = item,
                    Launch = "com.epicgames.launcher://apps/" + ns + "%3A" + item + "%3A" + app + "?action=launch&silent=true",
                    Icon = loc.Length > 0 && exe.Length > 0 ? Path.Combine(loc, exe) : ""
                };
                var size = Regex.Match(t, "\"InstallSize\"\\s*:\\s*(\\d+)");
                if (size.Success) long.TryParse(size.Groups[1].Value, out g.Bytes);
                // caratula y fondo oficiales (se bajan una vez a cache\); si aun no estan, la pantalla de carga del juego
                string tall = Path.Combine(dir, @"cache\epic-" + item + "-tall.jpg"), wide = Path.Combine(dir, @"cache\epic-" + item + "-wide.jpg");
                if (File.Exists(tall)) g.Cover = tall;
                if (File.Exists(wide)) g.Hero = wide;
                if (g.Cover == "" && Directory.Exists(loc))
                {
                    try
                    {
                        var splash = Directory.GetFiles(loc, "Splash.bmp", SearchOption.AllDirectories).FirstOrDefault()
                                     ?? Directory.GetFiles(loc, "SplashScreen.png", SearchOption.AllDirectories).FirstOrDefault();
                        if (splash != null) g.Cover = splash;
                    }
                    catch { }
                }
                list.Add(g);
            }
        }

        // El launcher de Epic guarda su catalogo (con los enlaces a las imagenes oficiales) en catcache.bin.
        // Se bajan una sola vez, reducidas, a cache\. Devuelve true si ha bajado algo nuevo.
        public static bool FetchEpicArt(string dir, Game g)
        {
            if (g.EpicItem == "") return false;
            string cdir = Path.Combine(dir, "cache");
            string tall = Path.Combine(cdir, "epic-" + g.EpicItem + "-tall.jpg"), wide = Path.Combine(cdir, "epic-" + g.EpicItem + "-wide.jpg");
            if (File.Exists(tall) && File.Exists(wide)) return false;
            string cat = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Catalog\catcache.bin";
            if (!File.Exists(cat)) return false;
            string tallUrl = null, wideUrl = null;
            try
            {
                string json = Encoding.UTF8.GetString(Convert.FromBase64String(File.ReadAllText(cat).Trim()));
                var js = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };
                var items = js.DeserializeObject(json) as object[];
                if (items == null) return false;
                foreach (var o in items)
                {
                    var it = o as Dictionary<string, object>;
                    if (it == null || !(it.ContainsKey("id") && Convert.ToString(it["id"]) == g.EpicItem) || !it.ContainsKey("keyImages")) continue;
                    var imgs = it["keyImages"] as object[];
                    if (imgs == null) continue;
                    foreach (var io in imgs)
                    {
                        var ki = io as Dictionary<string, object>;
                        if (ki == null || !ki.ContainsKey("type") || !ki.ContainsKey("url")) continue;
                        string type = Convert.ToString(ki["type"]);
                        if (type == "DieselGameBoxTall") tallUrl = Convert.ToString(ki["url"]);
                        else if (type == "DieselGameBox") wideUrl = Convert.ToString(ki["url"]);
                    }
                }
            }
            catch (Exception e) { Log.W("catalogo epic: " + e.Message); return false; }
            bool any = false;
            Directory.CreateDirectory(cdir);
            if (!File.Exists(tall) && tallUrl != null) any |= Download(tallUrl, tall, 600, 900);
            if (!File.Exists(wide) && wideUrl != null) any |= Download(wideUrl, wide, 1920, 1080);
            if (File.Exists(tall)) g.Cover = tall;
            if (File.Exists(wide)) g.Hero = wide;
            return any;
        }

        static bool Download(string url, string file, int maxW, int maxH)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                byte[] data;
                using (var wc = new WebClient()) { wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0"; data = wc.DownloadData(url); }
                using (var ms = new MemoryStream(data))
                using (var img = Image.FromStream(ms))
                {
                    float sc = Math.Min(1f, Math.Max(maxW / (float)img.Width, maxH / (float)img.Height));
                    int w = Math.Max(1, (int)(img.Width * sc)), h = Math.Max(1, (int)(img.Height * sc));
                    using (var b = new Bitmap(w, h, PixelFormat.Format24bppRgb))
                    {
                        using (var gr = Graphics.FromImage(b))
                        {
                            gr.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            gr.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            gr.DrawImage(img, 0, 0, w, h);
                        }
                        var enc = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
                        var ep = new EncoderParameters(1); ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
                        string tmp = file + ".tmp";
                        b.Save(tmp, enc, ep);
                        if (File.Exists(file)) File.Delete(file);
                        File.Move(tmp, file);
                    }
                }
                return true;
            }
            catch (Exception e) { Log.W("caratula epic: " + e.Message); return false; }
        }

        static string Json(string json, string key, bool raw = false)
        {
            if (raw)
            {
                var a = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\\[([^\\]]*)\\]");
                return a.Success ? a.Groups[1].Value : "";
            }
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return "";
            try { return Regex.Unescape(m.Groups[1].Value); } catch { return m.Groups[1].Value; }
        }

        // ------------------------------------------------------------------ resto (a mano)
        // Formato: Nombre | Plataforma | que lanzar (exe, acceso directo o enlace) | portada (opcional) | icono (opcional) | fondo (opcional)
        static void Extras(List<Game> list, string file)
        {
            if (!File.Exists(file)) return;
            foreach (var raw in File.ReadAllLines(file))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var p = line.Split('|').Select(s => Environment.ExpandEnvironmentVariables(s.Trim())).ToArray();
                if (p.Length < 3) continue;
                var g = new Game { Name = p[0], Platform = p[1].ToUpperInvariant(), Launch = p[2], Key = "extra:" + p[0].ToLowerInvariant() };
                if (p.Length > 3) g.Cover = p[3];
                g.Icon = p.Length > 4 && p[4].Length > 0 ? p[4] : (p[2].Contains("://") ? "" : p[2]);
                if (p.Length > 5) g.Hero = p[5];
                if (!p[2].Contains("://") && !File.Exists(p[2])) continue;          // ya no esta instalado
                try
                {
                    string exe = g.Icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? g.Icon : p[2];
                    if (File.Exists(exe) && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) g.Folder = Path.GetDirectoryName(exe);
                }
                catch { }
                list.Add(g);
            }
        }

        // ------------------------------------------------------------------ ocultos y recientes
        public static HashSet<string> ReadSet(string file)
        {
            var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(file)) foreach (var l in File.ReadAllLines(file)) if (l.Trim().Length > 0 && !l.StartsWith("#")) s.Add(l.Trim());
            return s;
        }
        public static void Hide(string dir, Game g)
        {
            File.AppendAllText(Path.Combine(dir, "juegos-ocultos.txt"), g.Key + Environment.NewLine);
        }
        static Dictionary<string, DateTime> ReadRecent(string file)
        {
            var d = new Dictionary<string, DateTime>();
            if (!File.Exists(file)) return d;
            foreach (var l in File.ReadAllLines(file))
            {
                var p = l.Split('|');
                long t;
                if (p.Length == 2 && long.TryParse(p[1], out t)) d[p[0]] = new DateTime(t);
            }
            return d;
        }
        public static void MarkPlayed(string dir, Game g)
        {
            string file = Path.Combine(dir, "juegos-recientes.txt");
            var d = ReadRecent(file);
            d[g.Key] = DateTime.Now;
            File.WriteAllLines(file, d.Select(kv => kv.Key + "|" + kv.Value.Ticks));
        }
    }
}
