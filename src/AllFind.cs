// AllFind - a fast, polished search launcher for Windows (WPF, .NET Framework 4.8, C# 5)
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using WF = System.Windows.Forms;

// declare 4.8 so WPF enables per-monitor DPI and modern rendering behaviour
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace AllFind
{
    // ================================================================ index
    // Names are packed into one big string ("name\nname\n...") so searching is a
    // single linear scan over contiguous memory instead of millions of string objects.
    class Idx
    {
        public const byte DIR = 1, NOISE = 2, USER = 4, PROG = 8, ROOT = 16, KNOWN = 32;
        public string Orig = "";
        public int[] S = new int[] { 0 }, P = new int[0];
        public byte[] F = new byte[0], D = new byte[0];
        public int Count;

        public int Len(int i) { return S[i + 1] - S[i] - 1; }
        public string Name(int i) { return Orig.Substring(S[i], Len(i)); }
        public int EntryAt(int pos)
        {
            int k = Array.BinarySearch(S, 0, Count, pos);
            return k >= 0 ? k : ~k - 1;
        }
        public string PathOf(int i)
        {
            var parts = new List<string>();
            while (i >= 0) { parts.Add(Name(i)); i = P[i]; }
            parts.Reverse();
            var sb = new StringBuilder(parts[0]);
            for (int k = 1; k < parts.Count; k++) { if (sb[sb.Length - 1] != '\\') sb.Append('\\'); sb.Append(parts[k]); }
            return sb.ToString();
        }

        public static string Lower(string s)
        {
            var a = s.ToCharArray();
            for (int i = 0; i < a.Length; i++) { char c = a[i]; a[i] = c < 128 ? (c >= 'A' && c <= 'Z' ? (char)(c + 32) : c) : char.ToLowerInvariant(c); }
            return new string(a);
        }

        public void Save(BinaryWriter w)
        {
            w.Write(Count); w.Write(Orig.Length);
            var buf = new char[1 << 16]; var bb = new byte[buf.Length * 2];
            for (int o = 0; o < Orig.Length; o += buf.Length)
            {
                int n = Math.Min(buf.Length, Orig.Length - o);
                Orig.CopyTo(o, buf, 0, n); Buffer.BlockCopy(buf, 0, bb, 0, n * 2); w.Write(bb, 0, n * 2);
            }
            WriteInts(w, S, Count + 1); WriteInts(w, P, Count); w.Write(F, 0, Count); w.Write(D, 0, Count);
        }
        static void WriteInts(BinaryWriter w, int[] a, int n) { var b = new byte[n * 4]; Buffer.BlockCopy(a, 0, b, 0, b.Length); w.Write(b); }
        static int[] ReadInts(BinaryReader r, int n) { var b = r.ReadBytes(n * 4); var a = new int[n]; Buffer.BlockCopy(b, 0, a, 0, b.Length); return a; }
        public static Idx Load(BinaryReader r)
        {
            var x = new Idx();
            x.Count = r.ReadInt32(); int len = r.ReadInt32();
            // fill the string in place, 128 KB at a time, instead of byte[] -> char[] -> string copies
            x.Orig = new string('\0', len);
            unsafe
            {
                fixed (char* dst = x.Orig)
                {
                    var bb = new byte[1 << 17]; long done = 0, total = (long)len * 2;
                    while (done < total)
                    {
                        int n = r.Read(bb, 0, (int)Math.Min(bb.Length, total - done));
                        if (n <= 0) throw new EndOfStreamException();
                        Marshal.Copy(bb, 0, (IntPtr)((byte*)dst + done), n);
                        done += n;
                    }
                }
            }
            x.S = ReadInts(r, x.Count + 1); x.P = ReadInts(r, x.Count);
            x.F = r.ReadBytes(x.Count); x.D = r.ReadBytes(x.Count);
            return x;
        }
    }

    class IdxBuilder
    {
        public List<string> N = new List<string>(1 << 16);
        public List<int> P = new List<int>(1 << 16);
        public List<byte> F = new List<byte>(1 << 16), D = new List<byte>(1 << 16);
        public int Count { get { return N.Count; } }
        public int Add(string n, int p, byte f, byte d) { N.Add(n); P.Add(p); F.Add(f); D.Add(d); return N.Count - 1; }
        public Idx Freeze()
        {
            var x = new Idx { Count = N.Count, P = P.ToArray(), F = F.ToArray(), D = D.ToArray(), S = new int[N.Count + 1] };
            long total = 0; foreach (var n in N) total += n.Length + 1;
            var sb = new StringBuilder((int)total);
            for (int i = 0; i < N.Count; i++) { x.S[i] = sb.Length; sb.Append(N[i]).Append('\n'); }
            x.S[N.Count] = sb.Length;
            x.Orig = sb.ToString();
            return x;
        }
    }

    static class Scanner
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct FIND_DATA
        {
            public uint attr; public uint c1, c2, a1, a2, w1, w2; public uint sizeHi, sizeLo, r0, r1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string alt;
        }
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindFirstFileExW(string p, int level, out FIND_DATA d, int op, IntPtr f, int flags);
        [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern bool FindNextFileW(IntPtr h, out FIND_DATA d);
        [DllImport("kernel32")] static extern bool FindClose(IntPtr h);

        static readonly HashSet<string> Skip = new HashSet<string> { "node_modules", ".git", "winsxs", "$recycle.bin",
            "system volume information", "$windows.~bt", "$windows.~ws", "$winreagent", "msocache", "config.msi" };
        static readonly HashSet<string> NoiseAny = new HashSet<string> { "appdata", "temp", "tmp", "cache", "caches", "__pycache__",
            "site-packages", "obj", "windowsapps", "packages", "inetcache", "logs" };
        static readonly HashSet<string> Known = new HashSet<string> { "desktop", "documents", "downloads", "pictures", "videos", "music" };
        // developer toolchain folders inside the user profile: searchable via Whole PC, hidden from top results
        static readonly HashSet<string> DevNoise = new HashSet<string> { "sdk", "ndk", ".gradle", "venv", ".venv", "dist", "build", "target", "vendor", "bower_components", "debug", "release" };
        static readonly HashSet<string> NoiseRoot = new HashSet<string> { "windows", "programdata", "perflogs", "recovery", "intel", "amd", "nvidia" };
        static readonly HashSet<string> JunkExt = new HashSet<string> { ".dll", ".sys", ".mui", ".pdb", ".cat", ".manifest", ".pyc", ".pyd",
            ".obj", ".o", ".tmp", ".etl", ".lock", ".ini", ".dat", ".bin", ".idx", ".pak", ".cache", ".tlog", ".pf", ".nls", ".lib", ".h", ".map", ".blf", ".regtrans-ms" };

        public static bool IsJunk(string name)
        {
            int d = name.LastIndexOf('.');
            return d > 0 && JunkExt.Contains(name.Substring(d).ToLowerInvariant());
        }
        public static bool IsNoisePath(string p)
        {
            string l = p.ToLowerInvariant();
            return l.Contains("\\appdata\\") || l.Contains("\\.") || l.Contains("\\node_modules\\") || l.Contains("\\$") || IsJunk(p) || Path.GetFileName(p).StartsWith("~$");
        }

        public static void Scan(IdxBuilder idx, string root, byte baseFlags, Func<string, string, bool> allow, Action<int> progress)
        {
            int r = idx.Add(root, -1, (byte)(baseFlags | Idx.DIR | Idx.ROOT), 0);
            var stack = new Stack<KeyValuePair<int, string>>();
            stack.Push(new KeyValuePair<int, string>(r, root));
            while (stack.Count > 0)
            {
                var top = stack.Pop(); int di = top.Key; string dp = top.Value;
                byte pf = (byte)(idx.F[di] & (Idx.NOISE | Idx.USER | Idx.PROG | Idx.KNOWN));
                int depth = idx.D[di] + 1; if (depth > 255) depth = 255;
                FIND_DATA fd;
                IntPtr h = FindFirstFileExW(dp.TrimEnd('\\') + "\\*", 1, out fd, 0, IntPtr.Zero, 2);
                if (h == new IntPtr(-1)) continue;
                do
                {
                    string n = fd.name;
                    if (n == "." || n == "..") continue;
                    uint a = fd.attr;
                    bool hidden = (a & 2) != 0, sys = (a & 4) != 0;
                    if ((a & 0x10) != 0)
                    {
                        if ((a & 0x400) != 0) continue;
                        string nl = n.ToLowerInvariant();
                        if (Skip.Contains(nl) || nl.StartsWith("$")) continue;
                        if (allow != null && !allow(dp, n)) continue;
                        byte f = (byte)(pf | Idx.DIR);
                        if (hidden || sys || nl.StartsWith(".") || NoiseAny.Contains(nl) || (depth == 1 && NoiseRoot.Contains(nl))) f |= Idx.NOISE;
                        if (depth == 1 && (nl == "program files" || nl == "program files (x86)")) f |= Idx.PROG;
                        if (depth == 1 && (pf & Idx.USER) != 0 && (Known.Contains(nl) || nl.StartsWith("onedrive"))) f |= Idx.KNOWN;
                        if ((pf & Idx.USER) != 0 && DevNoise.Contains(nl)) f |= Idx.NOISE;
                        int ci = idx.Add(n, di, f, (byte)depth);
                        stack.Push(new KeyValuePair<int, string>(ci, dp.TrimEnd('\\') + "\\" + n));
                    }
                    else
                    {
                        if (sys && hidden) continue;
                        byte f = pf;
                        if (hidden || IsJunk(n) || n.StartsWith("~$") || n.IndexOf('.') <= 0) f |= Idx.NOISE;
                        idx.Add(n, di, f, (byte)depth);
                    }
                    if ((idx.Count & 0x7FFF) == 0 && progress != null) progress(idx.Count);
                } while (FindNextFileW(h, out fd));
                FindClose(h);
            }
        }
    }

    // ================================================================ shell helpers
    static class Shell
    {
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm); }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public short bmPlanes, bmBitsPixel; public IntPtr bmBits; }
        // BITMAP + BITMAPINFOHEADER: the header's height sign tells bottom-up (icons) from top-down (thumbnails)
        [StructLayout(LayoutKind.Sequential)] struct DIBSECTION
        {
            public BITMAP dsBm;
            public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPels, biYPels, biClrUsed, biClrImportant;
            public int bf0, bf1, bf2; public IntPtr dshSection; public int dsOffset;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEINFO { public IntPtr hIcon; public int iIcon; public uint attr; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string disp; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string type; }

        [DllImport("shell32", CharSet = CharSet.Unicode)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);
        [DllImport("shell32", CharSet = CharSet.Unicode)] static extern IntPtr SHGetFileInfoW(string p, uint attr, ref SHFILEINFO psfi, int cb, uint flags);
        [DllImport("shell32")] static extern int SHGetKnownFolderPath(ref Guid id, int flags, IntPtr token, out IntPtr path);
        [DllImport("gdi32")] static extern int GetObject(IntPtr h, int size, ref DIBSECTION ds);
        [DllImport("gdi32")] static extern bool DeleteObject(IntPtr h);

        static readonly Dictionary<string, ImageSource> cache = new Dictionary<string, ImageSource>();

        public static bool TryCached(string key, out ImageSource img)
        {
            lock (cache) return cache.TryGetValue(key, out img);
        }
        public static ImageSource Image(string parse, int size, bool thumb, string cacheKey)
        {
            ImageSource img;
            if (cacheKey != null && TryCached(cacheKey, out img)) return img;
            img = null;
            try
            {
                Guid g = typeof(IShellItemImageFactory).GUID;
                IShellItemImageFactory f;
                if (SHCreateItemFromParsingName(parse, IntPtr.Zero, ref g, out f) == 0 && f != null)
                {
                    IntPtr hbm = IntPtr.Zero;
                    var sz = new SIZE { cx = size, cy = size };
                    // real thumbnails (THUMBNAILONLY) arrive top-down even though their header says bottom-up;
                    // icons (ICONONLY) really are bottom-up. Without a thumbnail, fall back to the icon.
                    bool gotThumb = thumb && f.GetImage(sz, 0x8, out hbm) == 0 && hbm != IntPtr.Zero;
                    if (gotThumb || (f.GetImage(sz, 4, out hbm) == 0 && hbm != IntPtr.Zero))
                    {
                        img = FromHBitmap(hbm, gotThumb);
                        DeleteObject(hbm);
                    }
                    Marshal.ReleaseComObject(f);
                }
            }
            catch { }
            if (cacheKey != null) lock (cache) cache[cacheKey] = img;
            return img;
        }

        static BitmapSource FromHBitmap(IntPtr hbm, bool topDownData)
        {
            var ds = new DIBSECTION();
            GetObject(hbm, Marshal.SizeOf(typeof(DIBSECTION)), ref ds);
            var bm = ds.dsBm;
            bool topDown = topDownData || ds.biHeight < 0;
            if (bm.bmBitsPixel != 32 || bm.bmBits == IntPtr.Zero)
            {
                var s = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                s.Freeze(); return s;
            }
            int w = bm.bmWidth, h = Math.Abs(bm.bmHeight), stride = bm.bmWidthBytes;
            var buf = new byte[stride * h];
            Marshal.Copy(bm.bmBits, buf, 0, buf.Length);
            var flipped = new byte[buf.Length];
            if (topDown) flipped = buf;
            else for (int y = 0; y < h; y++) Buffer.BlockCopy(buf, y * stride, flipped, (h - 1 - y) * stride, stride);
            // detect whether the shell gave us premultiplied or straight alpha; treating straight alpha
            // as premultiplied is what produced bright jagged fringes around icons
            bool anyAlpha = false, straight = false;
            for (int i = 0; i + 3 < flipped.Length; i += 4)
            {
                byte a = flipped[i + 3];
                if (a != 0) anyAlpha = true;
                if (flipped[i] > a || flipped[i + 1] > a || flipped[i + 2] > a) straight = true;
            }
            var fmt = !anyAlpha ? PixelFormats.Bgr32 : straight ? PixelFormats.Bgra32 : PixelFormats.Pbgra32;
            var bmp = BitmapSource.Create(w, h, 96, 96, fmt, null, flipped, stride);
            bmp.Freeze();
            return bmp;
        }

        public static string TypeName(string path, bool dir)
        {
            var i = new SHFILEINFO();
            SHGetFileInfoW(path, dir ? 0x10u : 0x80u, ref i, Marshal.SizeOf(i), 0x400 | 0x10);
            return string.IsNullOrEmpty(i.type) ? (dir ? "File folder" : "File") : i.type;
        }

        public static string ResolveAppTarget(string id)
        {
            try
            {
                if (id.StartsWith("{"))
                {
                    int e = id.IndexOf("}\\");
                    if (e < 0) return null;
                    Guid g = new Guid(id.Substring(0, e + 1)); IntPtr p;
                    if (SHGetKnownFolderPath(ref g, 0, IntPtr.Zero, out p) != 0) return null;
                    string root = Marshal.PtrToStringUni(p); Marshal.FreeCoTaskMem(p);
                    string full = Path.Combine(root, id.Substring(e + 2));
                    return File.Exists(full) ? full : null;
                }
                if (id.Length > 2 && id[1] == ':' && File.Exists(id)) return id;
            }
            catch { }
            return null;
        }
    }

    // ================================================================ right-click menus
    // Windows' own context menu for a file, folder or app (the "Show more options" menu), plus helpers to
    // pull single entries like "Pin to Start" / "Uninstall" out of it.
    static class ShellMenu
    {
        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem { [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv); }
        [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IContextMenu
        {
            [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint index, uint first, uint last, uint flags);
            [PreserveSig] int InvokeCommand(ref INVOKE ici);
            [PreserveSig] int GetCommandString(UIntPtr cmd, uint type, IntPtr res, IntPtr name, int cch);
        }
        [ComImport, Guid("000214f4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IContextMenu2
        {
            [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint index, uint first, uint last, uint flags);
            [PreserveSig] int InvokeCommand(ref INVOKE ici);
            [PreserveSig] int GetCommandString(UIntPtr cmd, uint type, IntPtr res, IntPtr name, int cch);
            [PreserveSig] int HandleMenuMsg(uint msg, IntPtr w, IntPtr l);
        }
        [StructLayout(LayoutKind.Sequential)]
        struct INVOKE { public int cbSize, fMask; public IntPtr hwnd, lpVerb, lpParameters, lpDirectory; public int nShow, dwHotKey; public IntPtr hIcon, lpTitle, lpVerbW, lpParametersW, lpDirectoryW, lpTitleW; public int x, y; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SEI { public int cbSize; public uint fMask; public IntPtr hwnd; public string lpVerb, lpFile, lpParameters, lpDirectory; public int nShow; public IntPtr hInstApp, lpIDList; public string lpClass; public IntPtr hkeyClass; public uint dwHotKey; public IntPtr hIcon, hProcess; }

        [DllImport("shell32", CharSet = CharSet.Unicode)] static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItem ppv);
        [DllImport("shell32", CharSet = CharSet.Unicode)] static extern bool ShellExecuteEx(ref SEI e);
        [DllImport("user32")] static extern IntPtr CreatePopupMenu();
        [DllImport("user32")] static extern bool DestroyMenu(IntPtr m);
        [DllImport("user32")] static extern int TrackPopupMenuEx(IntPtr m, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
        [DllImport("user32")] static extern int GetMenuItemCount(IntPtr m);
        [DllImport("user32")] static extern uint GetMenuItemID(IntPtr m, int pos);
        [DllImport("user32", CharSet = CharSet.Unicode)] static extern int GetMenuString(IntPtr m, uint id, StringBuilder s, int max, uint flags);

        static IContextMenu2 active;
        // owner-drawn/submenu items (Send to, Open with...) need these messages while the menu is open
        public static bool HandleMsg(int msg, IntPtr w, IntPtr l)
        {
            var a = active; if (a == null) return false;
            if (msg == 0x117 || msg == 0x2B || msg == 0x2C) { try { a.HandleMenuMsg((uint)msg, w, l); } catch { } return true; }
            return false;
        }

        static IContextMenu Get(string parse, out object com)
        {
            com = null;
            try
            {
                IShellItem si; Guid iid = typeof(IShellItem).GUID;
                if (SHCreateItemFromParsingName(parse, IntPtr.Zero, ref iid, out si) != 0 || si == null) return null;
                Guid bh = new Guid("3981e225-f559-11d3-8e3a-00c04f6837d5"), ic = typeof(IContextMenu).GUID; IntPtr p;
                int hr = si.BindToHandler(IntPtr.Zero, ref bh, ref ic, out p);
                Marshal.ReleaseComObject(si);
                if (hr != 0 || p == IntPtr.Zero) return null;
                com = Marshal.GetObjectForIUnknown(p); Marshal.Release(p);
                return com as IContextMenu;
            }
            catch { return null; }
        }
        static void Release(object com) { try { if (com != null) Marshal.ReleaseComObject(com); } catch { } }

        static void Invoke(IContextMenu cm, IntPtr hwnd, int id, int x, int y, bool pt)
        {
            var ici = new INVOKE { fMask = 0x4000 | (pt ? 0x20000000 : 0), hwnd = hwnd, lpVerb = (IntPtr)id, lpVerbW = (IntPtr)id, nShow = 1, x = x, y = y };
            ici.cbSize = Marshal.SizeOf(ici);
            cm.InvokeCommand(ref ici);
        }

        // the full classic Windows menu; returns true when a command was run
        public static bool Show(string parse, IntPtr hwnd, int x, int y, bool extended)
        {
            object com; var cm = Get(parse, out com); if (cm == null) return false;
            IntPtr menu = CreatePopupMenu();
            try
            {
                cm.QueryContextMenu(menu, 0, 1, 0x7FFF, extended ? 0x100u : 0u);
                active = com as IContextMenu2;
                int cmd = TrackPopupMenuEx(menu, 0x0100 | 0x0002, x, y, hwnd, IntPtr.Zero); // TPM_RETURNCMD | TPM_RIGHTBUTTON
                active = null;
                if (cmd <= 0) return false;
                Invoke(cm, hwnd, cmd - 1, x, y, true);
                return true;
            }
            catch { return false; }
            finally { active = null; DestroyMenu(menu); Release(com); }
        }

        // entries Windows offers for this item whose text passes the filter (e.g. "Pin to Start", "Uninstall")
        public static List<string> Items(string parse, Func<string, bool> want)
        {
            var r = new List<string>(); object com; var cm = Get(parse, out com); if (cm == null) return r;
            IntPtr menu = CreatePopupMenu();
            try
            {
                cm.QueryContextMenu(menu, 0, 1, 0x7FFF, 0);
                int n = GetMenuItemCount(menu); var sb = new StringBuilder(256);
                for (int i = 0; i < n; i++)
                {
                    sb.Clear(); if (GetMenuString(menu, (uint)i, sb, 256, 0x400) <= 0) continue;
                    string t = sb.ToString().Replace("&", "");
                    if (want(t) && !r.Contains(t)) r.Add(t);
                }
            }
            catch { }
            finally { DestroyMenu(menu); Release(com); }
            return r;
        }
        public static bool InvokeText(string parse, string text, IntPtr hwnd)
        {
            object com; var cm = Get(parse, out com); if (cm == null) return false;
            IntPtr menu = CreatePopupMenu();
            try
            {
                cm.QueryContextMenu(menu, 0, 1, 0x7FFF, 0);
                int n = GetMenuItemCount(menu); var sb = new StringBuilder(256);
                for (int i = 0; i < n; i++)
                {
                    sb.Clear(); if (GetMenuString(menu, (uint)i, sb, 256, 0x400) <= 0) continue;
                    if (sb.ToString().Replace("&", "") != text) continue;
                    uint id = GetMenuItemID(menu, i); if (id == 0xFFFFFFFF || id == 0) return false;
                    Invoke(cm, hwnd, (int)id - 1, 0, 0, false); return true;
                }
            }
            catch { }
            finally { DestroyMenu(menu); Release(com); }
            return false;
        }

        public static void Properties(string path)
        {
            var e = new SEI { lpVerb = "properties", lpFile = path, nShow = 1, fMask = 0xC }; // SEE_MASK_INVOKEIDLIST
            e.cbSize = Marshal.SizeOf(e);
            ShellExecuteEx(ref e);
        }
    }

    // ================================================================ data
    class App { public string Name, Id, Target; }
    class Setting { public string Name, Uri, Keys, Glyph; public Setting(string n, string u, string k, string g) { Name = n; Uri = u; Keys = k; Glyph = g; } }

    class Hit : INotifyPropertyChanged
    {
        public const int APP = 0, SETTING = 1, FILE = 2, FOLDER = 3, CALC = 4, HEADER = 5, MORE = 6;
        public event PropertyChangedEventHandler PropertyChanged;
        public int Type { get; set; }
        public string Title { get; set; }
        public string Sub { get; set; }
        public string Badge { get; set; }
        public string Glyph { get; set; }
        public string Pre { get; set; }
        public string Mid { get; set; }
        public string Post { get; set; }
        public string Path, Key, Cat, AppId, Target;
        public double Score;
        public int Gen;
        public bool Big { get; set; }
        public bool IsHeader { get { return Type == HEADER; } }
        public Visibility HeaderVis { get { return IsHeader ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility RowVis { get { return IsHeader ? Visibility.Collapsed : Visibility.Visible; } }
        public Visibility SubVis { get { return string.IsNullOrEmpty(Sub) ? Visibility.Collapsed : Visibility.Visible; } }
        public double IconSize { get { return Big ? 40 : 24; } }
        public double GlyphSize { get { return Big ? 26 : 16; } }
        public double TitleSize { get { return Big ? 16 : 13.5; } }
        public FontWeight TitleWeight { get { return Big ? FontWeights.SemiBold : FontWeights.Normal; } }

        public void Highlight(string q)
        {
            string t = Title ?? "";
            int i = string.IsNullOrEmpty(q) ? -1 : t.IndexOf(q, StringComparison.OrdinalIgnoreCase);
            if (i < 0 && q != null) { var w = q.Split(' ').Where(x => x.Length > 0).OrderByDescending(x => x.Length).FirstOrDefault(); if (w != null) { q = w; i = t.IndexOf(w, StringComparison.OrdinalIgnoreCase); } }
            if (i < 0) { Pre = t; Mid = ""; Post = ""; return; }
            Pre = t.Substring(0, i); Mid = t.Substring(i, q.Length); Post = t.Substring(i + q.Length);
        }

        // icons load on a background thread; the row shows a placeholder until ready
        int iconState; ImageSource icon;
        public int IconPx { get { return Icons.Px(Big ? 40 : 24); } }
        public ImageSource Icon
        {
            get
            {
                if (iconState == 0)
                {
                    string key = Icons.KeyFor(this, IconPx);
                    ImageSource c;
                    if (key == null) iconState = 2;
                    else if (Shell.TryCached(key, out c)) { icon = c; iconState = 2; }
                    else { iconState = 1; IconLoader.Request(this); }
                }
                return icon;
            }
        }
        public void SetIcon(ImageSource img)
        {
            icon = img; iconState = 2;
            var h = PropertyChanged;
            if (h != null) { h(this, new PropertyChangedEventArgs("Icon")); h(this, new PropertyChangedEventArgs("GlyphVis")); }
        }
        public Visibility GlyphVis { get { var _ = Icon; return iconState == 2 && icon == null ? Visibility.Visible : Visibility.Collapsed; } }
    }

    static class Icons
    {
        static readonly HashSet<string> PerFile = new HashSet<string> { ".exe", ".lnk", ".ico", ".url", ".appref-ms", ".msc", ".cpl", ".scr" };
        // pictures and videos show their own thumbnail instead of the file-type icon
        public static readonly HashSet<string> Thumbs = new HashSet<string> { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".tif", ".tiff", ".jfif", ".avif",
            ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp" };
        static bool IsThumb(Hit h) { return h.Type == Hit.FILE && Thumbs.Contains(System.IO.Path.GetExtension(h.Path).ToLowerInvariant()); }
        public static double Scale = 1.0; // monitor DPI scale
        static readonly int[] Steps = { 32, 48, 64, 96, 128, 256 };
        // request a native icon size at least as big as the on-screen pixels, then let WPF downscale smoothly
        public static int Px(double dip)
        {
            double need = dip * Scale * 1.25;
            foreach (int s in Steps) if (s >= need) return s;
            return 256;
        }
        public static string KeyFor(Hit h, int size)
        {
            if (h.Type == Hit.APP) return "app:" + h.AppId + "|" + size;
            if (h.Type == Hit.FOLDER) return "dir|" + size;
            if (h.Type == Hit.FILE)
            {
                string ext = System.IO.Path.GetExtension(h.Path).ToLowerInvariant();
                return PerFile.Contains(ext) || Thumbs.Contains(ext) ? h.Path.ToLowerInvariant() + "|" + size : "ext" + ext + "|" + size;
            }
            return null;
        }
        public static ImageSource Load(Hit h, int size)
        {
            string key = KeyFor(h, size);
            if (key == null) return null;
            string parse = h.Type == Hit.APP ? "shell:AppsFolder\\" + h.AppId : h.Path;
            return Shell.Image(parse, size, IsThumb(h), key);
        }
    }

    // single STA worker: newest requests first, stale ones dropped
    static class IconLoader
    {
        static readonly List<Hit> queue = new List<Hit>();
        static readonly AutoResetEvent ev = new AutoResetEvent(false);
        static Dispatcher ui;
        public static volatile int Gen;
        static Func<object> previewJob; static Action<object> previewDone;

        public static void Start(Dispatcher d)
        {
            ui = d;
            var t = new Thread(Loop) { IsBackground = true, Name = "icons" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }
        public static void Request(Hit h) { lock (queue) queue.Add(h); ev.Set(); }
        public static void Preview(Func<object> job, Action<object> done) { lock (queue) { previewJob = job; previewDone = done; } ev.Set(); }

        static void Loop()
        {
            while (true)
            {
                Hit h = null; Func<object> pj = null; Action<object> pd = null;
                lock (queue)
                {
                    if (previewJob != null) { pj = previewJob; pd = previewDone; previewJob = null; }
                    else
                    {
                        // visible rows request icons top-down; serve in that order but skip stale generations
                        while (queue.Count > 0 && h == null)
                        {
                            var x = queue[0]; queue.RemoveAt(0);
                            if (x.Gen == Gen || x.Gen < 0) h = x;
                        }
                    }
                }
                if (pj != null)
                {
                    object r = null; try { r = pj(); } catch { }
                    var done = pd; ui.BeginInvoke(new Action(() => done(r)));
                    continue;
                }
                if (h == null) { ev.WaitOne(); continue; }
                ImageSource img = null;
                try { img = Icons.Load(h, h.IconPx); } catch { }
                var hh = h; ui.BeginInvoke(DispatcherPriority.Background, new Action(() => hh.SetIcon(img)));
            }
        }
    }

    // ================================================================ engine
    class Engine
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AllFind");
        static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        public volatile Idx Quick = new Idx(), Full = new Idx();
        public volatile List<App> Apps = new List<App>();
        public volatile List<KeyValuePair<string, DateTime>> Recent = new List<KeyValuePair<string, DateTime>>();
        public Dictionary<string, DateTime> RecentSet = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Usage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> live = new List<string>();
        public string Status = "";
        public volatile bool Busy, Loaded;
        public event Action Changed;
        FileSystemWatcher watcher;

        public static readonly Setting[] Settings = {
            new Setting("Bluetooth & devices", "ms-settings:bluetooth", "bluetooth devices headphones pair airpods", ""),
            new Setting("Display", "ms-settings:display", "screen resolution brightness scale monitor hdr refresh", ""),
            new Setting("Sound", "ms-settings:sound", "audio volume speakers microphone output input", ""),
            new Setting("Wi-Fi", "ms-settings:network-wifi", "wifi wireless internet network", ""),
            new Setting("Network & internet", "ms-settings:network", "network internet ethernet ip proxy", ""),
            new Setting("VPN", "ms-settings:network-vpn", "vpn", ""),
            new Setting("Windows Update", "ms-settings:windowsupdate", "update upgrade patch", ""),
            new Setting("Installed apps", "ms-settings:appsfeatures", "uninstall remove programs apps features", ""),
            new Setting("Default apps", "ms-settings:defaultapps", "default browser open with", ""),
            new Setting("Startup apps", "ms-settings:startupapps", "startup boot autostart", ""),
            new Setting("Personalization", "ms-settings:personalization", "wallpaper background colors theme personalize", ""),
            new Setting("Colors & dark mode", "ms-settings:colors", "dark light mode accent color", ""),
            new Setting("Taskbar", "ms-settings:taskbar", "taskbar", ""),
            new Setting("Night light", "ms-settings:nightlight", "night light blue light", ""),
            new Setting("Mouse", "ms-settings:mousetouchpad", "mouse pointer cursor speed", ""),
            new Setting("Keyboard & typing", "ms-settings:typing", "keyboard typing autocorrect", ""),
            new Setting("Language & region", "ms-settings:regionlanguage", "language region keyboard layout input", ""),
            new Setting("Date & time", "ms-settings:dateandtime", "date time clock timezone", ""),
            new Setting("Printers & scanners", "ms-settings:printers", "printer scanner print", ""),
            new Setting("Storage", "ms-settings:storagesense", "storage disk space cleanup", ""),
            new Setting("Power & battery", "ms-settings:powersleep", "power battery sleep", ""),
            new Setting("Notifications", "ms-settings:notifications", "notifications focus do not disturb", ""),
            new Setting("Privacy & security", "ms-settings:privacy", "privacy security permissions camera microphone", ""),
            new Setting("Windows Security", "windowsdefender:", "antivirus defender virus firewall security", ""),
            new Setting("Accounts", "ms-settings:yourinfo", "account user sign in password", ""),
            new Setting("About this PC", "ms-settings:about", "about pc specs rename computer name", ""),
            new Setting("Gaming", "ms-settings:gaming-gamebar", "game bar xbox gaming", ""),
            new Setting("Control Panel", "control.exe", "control panel", ""),
            new Setting("Device Manager", "devmgmt.msc", "device manager drivers hardware", ""),
            new Setting("Task Manager", "taskmgr.exe", "task manager processes performance", ""),
            new Setting("Environment variables", "rundll32.exe sysdm.cpl,EditEnvironmentVariables", "environment variables path", ""),
            new Setting("Disk Management", "diskmgmt.msc", "disk partition drive format", ""),
        };

        public void Init()
        {
            Directory.CreateDirectory(Dir);
            LoadUsage();
            new Thread(() =>
            {
                try
                {
                    string f = Path.Combine(Dir, "index.bin");
                    if (File.Exists(f))
                        using (var r = new BinaryReader(new BufferedStream(File.OpenRead(f), 1 << 20)))
                        {
                            if (r.ReadInt32() == 0x41460003) { var q = Idx.Load(r); var fu = Idx.Load(r); Quick = q; Full = fu; }
                        }
                }
                catch { }
                Loaded = true; Fire(); Trim(true);
                Rebuild(Full.Count == 0);
            }) { IsBackground = true }.Start();
            RunSta(LoadApps);
            RunSta(LoadRecent);
            StartWatcher();
        }

        static void RunSta(Action a)
        {
            var t = new Thread(() => { try { a(); } catch { } }); t.SetApartmentState(ApartmentState.STA); t.IsBackground = true; t.Start();
        }
        void Fire() { var c = Changed; if (c != null) c(); }

        void StartWatcher()
        {
            try
            {
                watcher = new FileSystemWatcher(Home) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName, InternalBufferSize = 64 * 1024 };
                FileSystemEventHandler add = (s, e) => AddLive(e.FullPath);
                watcher.Created += add;
                watcher.Renamed += (s, e) => AddLive(e.FullPath);
                watcher.EnableRaisingEvents = true;
            }
            catch { }
        }
        void AddLive(string p)
        {
            if (Scanner.IsNoisePath(p)) return;
            lock (live) { live.Add(p); if (live.Count > 5000) live.RemoveRange(0, 1000); }
        }

        void LoadApps()
        {
            var list = new List<App>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            dynamic ns = sh.NameSpace("shell:AppsFolder");
            foreach (dynamic it in ns.Items())
            {
                string name = it.Name, id = it.Path;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(id)) continue;
                string nl = name.ToLowerInvariant(), il = id.ToLowerInvariant();
                if (nl.Contains("uninstall") || il.StartsWith("http")) continue;
                string ext = Path.GetExtension(il);
                if (ext == ".txt" || ext == ".chm" || ext == ".htm" || ext == ".html" || ext == ".pdf" || ext == ".url" || ext == ".rtf" || ext == ".ini" || ext == ".md") continue;
                if (!seen.Add(name)) continue;
                list.Add(new App { Name = name, Id = id, Target = Shell.ResolveAppTarget(id) });
            }
            Apps = list;
            Fire();
        }

        void LoadRecent()
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            var files = new DirectoryInfo(dir).GetFiles("*.lnk").OrderByDescending(f => f.LastWriteTime).Take(200).ToList();
            dynamic ws = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            var d = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            var list = new List<KeyValuePair<string, DateTime>>();
            foreach (var f in files)
            {
                try
                {
                    string t = ws.CreateShortcut(f.FullName).TargetPath;
                    if (string.IsNullOrEmpty(t) || d.ContainsKey(t)) continue;
                    d[t] = f.LastWriteTime;
                    string tl = t.ToLowerInvariant();
                    bool sysPath = tl.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant()) || Scanner.IsNoisePath(t) || tl.Contains("\\programdata\\");
                    if (list.Count < 10 && !sysPath && (File.Exists(t) || Directory.Exists(t))) list.Add(new KeyValuePair<string, DateTime>(t, f.LastWriteTime));
                }
                catch { }
            }
            RecentSet = d; Recent = list;
            Fire();
        }

        void LoadUsage()
        {
            try
            {
                string f = Path.Combine(Dir, "usage.txt");
                string old = Path.Combine(Path.GetDirectoryName(Dir), "QuickFind", "usage.txt");
                if (!File.Exists(f) && File.Exists(old)) File.Copy(old, f);
                foreach (var l in File.ReadAllLines(f))
                {
                    int t = l.IndexOf('\t'); int c;
                    if (t > 0 && int.TryParse(l.Substring(0, t), out c)) Usage[l.Substring(t + 1)] = c;
                }
            }
            catch { }
        }
        public void Used(string key)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                lock (Usage)
                {
                    int c; Usage.TryGetValue(key, out c); Usage[key] = c + 1;
                    try { File.WriteAllLines(Path.Combine(Dir, "usage.txt"), Usage.Select(kv => kv.Value + "\t" + kv.Key)); } catch { }
                }
            });
        }
        public int UsageOf(string key) { lock (Usage) { int u; return Usage.TryGetValue(key, out u) ? u : 0; } }
        public List<KeyValuePair<string, int>> TopUsage() { lock (Usage) return Usage.OrderByDescending(kv => kv.Value).ToList(); }

        static IEnumerable<string> FixedDrives()
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                bool ok = false;
                try { ok = d.DriveType == DriveType.Fixed && d.IsReady; } catch { }
                if (ok) yield return d.RootDirectory.FullName;
            }
        }

        int rebuilding;
        public DateTime LastQuick = DateTime.MinValue;
        public void Rebuild(bool full)
        {
            if (Interlocked.Exchange(ref rebuilding, 1) == 1) return;
            var t = new Thread(() =>
            {
                try
                {
                    Busy = true;
                    string homeL = Home.TrimEnd('\\');
                    var qb = new IdxBuilder();
                    Scanner.Scan(qb, Home, Idx.USER, (p, n) => !(n.Equals("AppData", StringComparison.OrdinalIgnoreCase) && p.TrimEnd('\\').Equals(homeL, StringComparison.OrdinalIgnoreCase)), null);
                    Quick = qb.Freeze(); LastQuick = DateTime.Now;
                    lock (live) live.Clear();
                    Fire();
                    if (full || Full.Count == 0)
                    {
                        var fb = new IdxBuilder();
                        foreach (var d in FixedDrives())
                            Scanner.Scan(fb, d, 0, (p, n) =>
                                !(p.TrimEnd('\\').Equals(homeL, StringComparison.OrdinalIgnoreCase) && !n.Equals("AppData", StringComparison.OrdinalIgnoreCase)),
                                c => { Status = "Indexing your PC… " + c.ToString("N0") + " items"; Fire(); });
                        Full = fb.Freeze();
                        fb = null;
                    }
                    Busy = false; Status = ""; Fire();
                    string tmp = Path.Combine(Dir, "index.tmp");
                    using (var w = new BinaryWriter(new BufferedStream(File.Create(tmp), 1 << 20))) { w.Write(0x41460003); Quick.Save(w); Full.Save(w); }
                    string dst = Path.Combine(Dir, "index.bin");
                    if (File.Exists(dst)) File.Delete(dst);
                    File.Move(tmp, dst);
                    Trim(true);
                }
                catch { }
                finally { Busy = false; Interlocked.Exchange(ref rebuilding, 0); }
            });
            t.IsBackground = true; t.Priority = ThreadPriority.BelowNormal; t.Start();
        }

        public int ItemCount { get { return Quick.Count + Full.Count; } }

        [DllImport("kernel32")] static extern bool SetProcessWorkingSetSize(IntPtr p, IntPtr min, IntPtr max);
        // give memory back to Windows: compact the heap after big index work
        public static void Trim(bool compact)
        {
            try
            {
                if (compact)
                {
                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                }
                // the working set is deliberately NOT emptied any more: paging the window and icon cache back in
                // is what made opening feel slow and icons appear late
            }
            catch { }
        }

        // ---------------- matching
        static double MatchAt(string low, string orig, int st, int len, string q, string[] words)
        {
            int i = len >= q.Length ? orig.IndexOf(q, st, len, StringComparison.OrdinalIgnoreCase) : -1;
            if (i < 0)
            {
                if (words.Length < 2) return -1;
                foreach (var w in words) if (len < w.Length || orig.IndexOf(w, st, len, StringComparison.OrdinalIgnoreCase) < 0) return -1;
                return 22;
            }
            i -= st;
            int dot = orig.LastIndexOf('.', st + len - 1, len) - st;
            int baseLen = dot > 0 ? dot : len;
            if (i == 0 && q.Length == baseLen) return 100;
            if (i == 0) return 72;
            char c = orig[st + i - 1];
            if (!char.IsLetterOrDigit(c) || (char.IsUpper(orig[st + i]) && char.IsLower(c))) return 52;
            return 26;
        }
        static double Match(string name, string q, string[] words)
        {
            string low = Idx.Lower(name);
            return MatchAt(low, name, 0, name.Length, q, words);
        }
        static bool Initials(string name, string q)
        {
            if (q.Length < 2) return false;
            var sb = new StringBuilder();
            foreach (var w in Regex.Split(name, @"[\s\-_.]+")) if (w.Length > 0) sb.Append(char.ToLowerInvariant(w[0]));
            return sb.ToString().StartsWith(q, StringComparison.Ordinal);
        }

        static readonly HashSet<string> Docs = new HashSet<string> { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md", ".rtf", ".csv", ".odt", ".ods", ".epub", ".json", ".xml", ".html", ".log" };
        static readonly HashSet<string> Office = new HashSet<string> { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".rtf", ".epub", ".csv", ".txt" };
        static readonly HashSet<string> Media = new HashSet<string> { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".heic", ".svg", ".psd", ".tif", ".tiff", ".raw", ".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".mp3", ".wav", ".flac", ".m4a", ".ogg", ".aac" };
        static readonly HashSet<string> Progs = new HashSet<string> { ".exe", ".lnk", ".appref-ms", ".url", ".msi", ".bat", ".cmd" };
        public const string C_APPS = "Apps", C_SET = "Settings", C_DOCS = "Documents", C_DIRS = "Folders", C_MEDIA = "Photos & media", C_FILES = "Files";
        static string ExtAt(string low, int st, int len)
        {
            int dot = low.LastIndexOf('.', st + len - 1, len);
            return dot > st ? low.Substring(dot, st + len - dot).ToLowerInvariant() : "";
        }
        static string CatOf(string ext, bool dir)
        {
            if (dir) return C_DIRS;
            if (Progs.Contains(ext)) return C_APPS;
            if (Docs.Contains(ext)) return C_DOCS;
            if (Media.Contains(ext)) return C_MEDIA;
            return C_FILES;
        }
        static double TypeBonus(string ext, bool dir)
        {
            if (dir) return 6;
            if (ext == ".exe" || ext == ".lnk" || ext == ".appref-ms") return 30;
            if (Office.Contains(ext)) return 16;
            if (Docs.Contains(ext)) return 4;
            if (Media.Contains(ext)) return 5;
            return 0;
        }
        static readonly string[] BadExe = { "uninst", "unins0", "setup", "update", "crash", "helper", "installer", "elevat", "notif", "report", "service", "vc_redist", "dotnet-" };
        const string Freq = "etaoinsrhldcumfpgwybvkxjqz";

        struct Cand { public double S; public int Src; public int I; }

        class TopK
        {
            readonly Cand[] h; int n;
            public TopK(int k) { h = new Cand[k]; }
            public int Total;
            public void Add(Cand c)
            {
                Total++;
                if (n < h.Length) { h[n] = c; int i = n++; while (i > 0) { int p = (i - 1) / 2; if (h[p].S <= h[i].S) break; var t = h[p]; h[p] = h[i]; h[i] = t; i = p; } return; }
                if (c.S <= h[0].S) return;
                h[0] = c; int j = 0;
                while (true)
                {
                    int l = 2 * j + 1, r = l + 1, m = j;
                    if (l < n && h[l].S < h[m].S) m = l;
                    if (r < n && h[r].S < h[m].S) m = r;
                    if (m == j) break;
                    var t = h[m]; h[m] = h[j]; h[j] = t; j = m;
                }
            }
            public IEnumerable<Cand> Items { get { for (int i = 0; i < n; i++) yield return h[i]; } }
        }

        // narrowing cache: when the query grows ("ble" -> "blen") only re-check the previous matches
        class Narrow { public Idx X; public string Pre; public int[] Hits; }
        readonly Narrow[] narrow = new Narrow[2];

        // mode: 0 top, 1 apps, 2 docs, 3 folders, 4 media, 5 whole pc
        public List<Hit> Search(string q, int mode, Func<bool> stale, out int total)
        {
            q = Idx.Lower(q);
            var words = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string pre = words.OrderByDescending(w => w.Length).First();
            var top = new TopK(500);
            var apps = Apps;
            if (mode == 0 || mode == 1 || mode == 5)
                for (int i = 0; i < apps.Count; i++)
                {
                    double m = Match(apps[i].Name, q, words);
                    if (m < 0 && Initials(apps[i].Name, q)) m = 48;
                    if (m < 0) continue;
                    top.Add(new Cand { S = m + 80 - apps[i].Name.Length * 0.1, Src = 0, I = i });
                }
            if (mode == 0 || mode == 5)
                for (int i = 0; i < Settings.Length; i++)
                {
                    double m = Match(Settings[i].Name, q, words);
                    if (m < 0 && q.Length >= 3) foreach (var k in Settings[i].Keys.Split(' ')) if (k.StartsWith(q, StringComparison.Ordinal)) { m = 45; break; }
                    if (m < 0) continue;
                    top.Add(new Cand { S = m + 50, Src = 1, I = i });
                }
            string[] livePaths; lock (live) livePaths = live.ToArray();
            for (int i = 0; i < livePaths.Length; i++)
            {
                string name = Path.GetFileName(livePaths[i]);
                double m = Match(name, q, words);
                if (m < 0) continue;
                bool dir = Directory.Exists(livePaths[i]);
                string ext = dir ? "" : Path.GetExtension(name).ToLowerInvariant();
                if (!ModeOk(mode, CatOf(ext, dir))) continue;
                top.Add(new Cand { S = m + TypeBonus(ext, dir) + 25, Src = 4, I = i });
            }

            var idxs = new[] { Quick, Full };
            for (int s = 0; s < 2; s++) ScanIdx(idxs[s], s, q, pre, words, mode, top, stale);
            total = top.Total;
            if (stale()) return null;

            var cands = top.Items.ToList();
            cands.Sort((a, b) => b.S.CompareTo(a.S));
            var hits = new List<Hit>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var recent = RecentSet;
            foreach (var c in cands)
            {
                Hit h;
                if (c.Src == 0)
                {
                    var a = apps[c.I];
                    h = new Hit { Type = Hit.APP, Title = a.Name, AppId = a.Id, Target = a.Target, Path = a.Target, Key = "app:" + a.Id, Cat = C_APPS, Glyph = "" };
                }
                else if (c.Src == 1)
                {
                    var st = Settings[c.I];
                    h = new Hit { Type = Hit.SETTING, Title = st.Name, Path = st.Uri, Key = "set:" + st.Uri, Cat = C_SET, Glyph = st.Glyph, Sub = "System settings" };
                }
                else
                {
                    string p; string name; bool dir;
                    if (c.Src == 4) { p = livePaths[c.I]; name = Path.GetFileName(p); dir = Directory.Exists(p); }
                    else { var x = idxs[c.Src - 2]; p = x.PathOf(c.I); dir = (x.F[c.I] & Idx.DIR) != 0; name = x.Name(c.I); }
                    string title = name;
                    if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) title = name.Substring(0, name.Length - 4);
                    h = new Hit { Type = dir ? Hit.FOLDER : Hit.FILE, Title = title, Path = p, Key = p, Cat = CatOf(dir ? "" : Path.GetExtension(name).ToLowerInvariant(), dir), Glyph = dir ? "" : "", Sub = Pretty(Path.GetDirectoryName(p) ?? p) };
                }
                h.Score = c.S;
                if (h.Key != null)
                {
                    int u = UsageOf(h.Key); if (u > 0) h.Score += Math.Min(45, 14 * Math.Log(1 + u, 2));
                    DateTime dt; if (h.Path != null && recent.TryGetValue(h.Path, out dt)) h.Score += Math.Max(8, 30 - (DateTime.Now - dt).TotalDays);
                }
                if (!seen.Add(h.Key ?? h.Title)) continue;
                hits.Add(h);
            }
            hits.Sort((a, b) => b.Score.CompareTo(a.Score));
            return hits;
        }

        static bool ModeOk(int mode, string cat)
        {
            switch (mode)
            {
                case 1: return cat == C_APPS;
                case 2: return cat == C_DOCS;
                case 3: return cat == C_DIRS;
                case 4: return cat == C_MEDIA;
            }
            return true;
        }

        void ScanIdx(Idx x, int s, string q, string pre, string[] words, int mode, TopK top, Func<bool> stale)
        {
            if (x.Count == 0) return;
            string orig = x.Orig, low = orig; /* single case-preserving copy; matching is case-insensitive */ int[] S = x.S; byte[] F = x.F, D = x.D;
            var nc = narrow[s];
            bool useNarrow = nc != null && nc.X == x && pre.Contains(nc.Pre);
            int workers = Math.Max(1, Environment.ProcessorCount);
            var tops = new TopK[workers]; var found = new List<int>[workers];

            Action<int, int> consider = null; // set per worker below
            Parallel.For(0, workers, w =>
            {
                var tk = new TopK(500); var fl = new List<int>();
                tops[w] = tk; found[w] = fl;
                Action<int> eval = i =>
                {
                    byte f = F[i];
                    if ((f & Idx.ROOT) != 0) return;
                    bool dir = (f & Idx.DIR) != 0, noise = (f & Idx.NOISE) != 0;
                    int st = S[i], len = S[i + 1] - st - 1;
                    string ext = dir ? "" : ExtAt(low, st, len);
                    if (mode != 5)
                    {
                        if (noise) return;
                        if ((f & Idx.PROG) != 0 && ext != ".exe") return;
                    }
                    string cat = CatOf(ext, dir);
                    if (!ModeOk(mode, cat)) return;
                    double m = MatchAt(low, orig, st, len, q, words);
                    if (m < 0) return;
                    double sc = m + TypeBonus(ext, dir) - D[i] * 1.5 - len * 0.15;
                    if ((f & Idx.USER) != 0) sc += 15; else sc -= 8;
                    if ((f & Idx.KNOWN) != 0) sc += 22;
                    if (noise) sc -= 40;
                    if (cat == C_APPS && ext == ".exe")
                        foreach (var b in BadExe) if (orig.IndexOf(b, st, len, StringComparison.OrdinalIgnoreCase) >= 0) { sc -= 45; break; }
                    tk.Add(new Cand { S = sc, Src = 2 + s, I = i });
                };

                if (useNarrow)
                {
                    var arr = nc.Hits; int a = (int)((long)arr.Length * w / workers), b = (int)((long)arr.Length * (w + 1) / workers);
                    for (int k = a; k < b; k++)
                    {
                        int i = arr[k]; int st = S[i], len = S[i + 1] - st - 1;
                        if (len < pre.Length || orig.IndexOf(pre, st, len, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        fl.Add(i); eval(i);
                    }
                    return;
                }
                // anchor on the rarest character of the query for fewer false starts
                int ai = 0, best = -1;
                for (int k = 0; k < pre.Length; k++)
                {
                    int r = Freq.IndexOf(pre[k]); if (r < 0) r = 100;
                    if (r > best) { best = r; ai = k; }
                }
                char anchor = pre[ai]; int L = pre.Length; var anchors = new[] { anchor, char.ToUpperInvariant(anchor) };
                int e0 = (int)((long)x.Count * w / workers), e1 = (int)((long)x.Count * (w + 1) / workers);
                int from = S[e0] + ai, to = S[e1];
                int pos = from, iter = 0;
                while (pos < to)
                {
                    pos = anchors[0] == anchors[1] ? orig.IndexOf(anchor, pos, to - pos) : orig.IndexOfAny(anchors, pos, to - pos);
                    if (pos < 0) break;
                    int start = pos - ai;
                    if (start >= 0 && start + L <= orig.Length && string.Compare(orig, start, pre, 0, L, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        int i = x.EntryAt(start);
                        if (start + L <= S[i + 1] - 1)
                        {
                            fl.Add(i); eval(i);
                            pos = S[i + 1] + ai; // jump to next name
                            if ((++iter & 1023) == 0 && stale()) return;
                            continue;
                        }
                    }
                    pos++;
                }
            });
            consider = null;
            if (stale()) return;
            int cnt = 0; foreach (var l in found) cnt += l.Count;
            var all = new int[cnt]; int o = 0;
            foreach (var l in found) { l.CopyTo(all, o); o += l.Count; }
            narrow[s] = new Narrow { X = x, Pre = pre, Hits = all };
            foreach (var tk in tops) { foreach (var c in tk.Items) top.Add(c); top.Total += tk.Total - tk.Items.Count(); }
        }

        public static string Pretty(string p)
        {
            if (p.StartsWith(Home, StringComparison.OrdinalIgnoreCase)) return "Home" + p.Substring(Home.Length);
            return p;
        }
    }

    // ================================================================ window
    class MainWin : Window
    {
        [DllImport("dwmapi")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        [DllImport("user32")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32")] static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32")] static extern IntPtr GetForegroundWindow();
        [DllImport("uxtheme", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
        [DllImport("uxtheme", EntryPoint = "#136")] static extern void FlushMenuThemes();
        [DllImport("user32")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
        [DllImport("kernel32")] static extern uint GetCurrentThreadId();
        [DllImport("user32")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("user32")] static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

        static readonly string[] ChipNames = { "Top results", "Apps", "Documents", "Folders", "Photos & media", "Whole PC" };
        static readonly string[] ChipGlyphs = { "", "", "", "", "", "" };
        public Engine E = new Engine();
        public string HotkeyLabel = "";
        TextBox Box; TextBlock Ph, Status, Count, PTitle, PKind, PPath, PMeta, PGlyph, TilesHead;
        ListBox List; StackPanel Chips, PActions, Keys; ItemsControl Tiles;
        Image PImg, LogoImg; Border Root, PreviewPane, SearchRow; Grid Body; FrameworkElement HomeTop;
        const double UiScale = 0.88;
        bool tbMode; // opened from the taskbar pill: typing shows in the pill, the panel's own box row is hidden
        int mode; int version; DispatcherTimer debounce;
        bool dark, hiding; Color accent;
        Grid SetupPane; Button StyleIconBtn, StyleBarBtn, BtnRefresh; CheckBox TglHide, TglPin, TglStart; TextBlock HideSub, PinSub;
        public TaskbarPill Pill, IconPill;
        bool pinned, pinHint;

        public void ShowSetup()
        {
            pinHint = false;
            SetupPane.Visibility = Visibility.Visible;
            SetupPane.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            RefreshSetup();
            ThreadPool.QueueUserWorkItem(_ => { bool p = Taskbar.IsPinned(); Dispatcher.BeginInvoke(new Action(() => { pinned = p; RefreshSetup(); })); });
        }
        void CloseSetup()
        {
            Cfg.Set("setup", "1");
            var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
            a.Completed += (s, e) => { SetupPane.Visibility = Visibility.Collapsed; };
            SetupPane.BeginAnimation(OpacityProperty, a);
            Box.Focus();
        }
        void RefreshSetup()
        {
            bool bar = Cfg.Get("style", "icon") == "bar";
            StyleIconBtn.Tag = bar ? null : "sel"; StyleBarBtn.Tag = bar ? "sel" : null;
            if (bar)
            {
                bool on = Cfg.Get("bar", "0") == "1";
                TglPin.IsEnabled = true; TglPin.IsChecked = on;
                PinSub.Text = !on ? "Adds an AllFind search bar to your taskbar."
                    : Pill.SlotFound ? "Done ✓  The bar sits right after your taskbar apps. (Your Windows edition has no built-in search box spot next to Start.)"
                    : "On — it appears as soon as there's free space after your taskbar apps.";
                BtnRefresh.Visibility = Visibility.Collapsed;
                TglHide.IsEnabled = true; TglHide.IsChecked = Taskbar.SearchMode == 0;
                HideSub.Text = "Hides the Windows search icon/box so AllFind is your only search.";
            }
            else
            {
                TglPin.IsChecked = pinned; TglPin.IsEnabled = true;
                PinSub.Text = pinHint ? "In the menu that opens, choose " + (pinned ? "Unpin from taskbar." : "Pin to taskbar, then drag the icon next to Start.")
                    : pinned ? "Pinned ✓  Clicking it opens AllFind. Flip off to unpin."
                    : "Not pinned yet — flip this on to pin it.";
                TglHide.IsEnabled = true; TglHide.IsChecked = Taskbar.SearchMode == 0;
                BtnRefresh.Visibility = Visibility.Collapsed;
                HideSub.Text = "Hides the Windows search icon/box so AllFind is your only search.";
            }
            TglStart.IsChecked = Program.IsStartup();
        }
        void ApplyTaskbar()
        {
            bool bar = Cfg.Get("style", "icon") == "bar", barOn = bar && Cfg.Get("bar", "0") == "1";
            try
            {
                if (barOn)
                {
                    Pill.Enable(true);
                    int o2 = Taskbar.OriginalMode;
                    Taskbar.SetSearchMode(Cfg.Get("hideWin", "1") == "1" ? 0 : (o2 == 0 ? 1 : o2));
                    if (!Program.IsStartup()) Program.SetStartup(true);
                    // if Explorer didn't make room for the box, offer to refresh it
                    var chk = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
                    chk.Tick += (s, e) => { chk.Stop(); RefreshSetup(); };
                    chk.Start();
                }
                else
                {
                    Pill.Disable();
                    int orig = Taskbar.OriginalMode;
                    Taskbar.SetSearchMode(Cfg.Get("hideWin", "1") == "1" ? 0 : (orig == 0 ? 1 : orig));
                }
            }
            catch (Exception ex) { Count.Text = "Couldn't change the taskbar: " + ex.Message; }
        }

        public MainWin()
        {
            dark = ReadDark();
            accent = dark ? Color.FromRgb(0x8E, 0x96, 0xFF) : Color.FromRgb(0x5B, 0x5F, 0xF6); // AllFind brand blue-violet
            var tok = new Dictionary<string, string>();
            // glass palette: mostly-transparent layers over a live blur of the desktop
            tok["@BG"] = dark ? "#160C0C14" : "#20FFFFFF";
            tok["@PANEL"] = dark ? "#13FFFFFF" : "#5FFFFFFF";
            tok["@PREV"] = dark ? "#0DFFFFFF" : "#48FFFFFF";
            tok["@BORDER"] = dark ? "#1CFFFFFF" : "#16000000";
            tok["@FG"] = dark ? "#FFFFFFFF" : "#FF15151A";
            tok["@SUB"] = dark ? "#FFB8B8C6" : "#FF52525E";
            tok["@SEL"] = dark ? "#1EFFFFFF" : "#14000000";
            tok["@HOVER"] = dark ? "#10FFFFFF" : "#0A000000";
            tok["@KEY"] = dark ? "#16FFFFFF" : "#90FFFFFF";
            tok["@EDGE1"] = dark ? "#55FFFFFF" : "#F0FFFFFF";
            tok["@EDGE2"] = dark ? "#0AFFFFFF" : "#40FFFFFF";
            tok["@SHEEN"] = dark ? "#12FFFFFF" : "#55FFFFFF";
            tok["@GLOWA"] = dark ? "#663D9BFF" : "#553D9BFF";
            tok["@GLOWB"] = dark ? "#668E44F0" : "#558E44F0";
            tok["@SELA"] = dark ? "#5C4F8BFF" : "#404F8BFF";
            tok["@SELB"] = dark ? "#3A9A4BF5" : "#309A4BF5";
            tok["@SCRIM"] = dark ? "#B0101016" : "#A0F4F4F8";
            tok["@CARD"] = dark ? "#F01E1E26" : "#F8FFFFFF";
            tok["@MOCKAPP"] = dark ? "#33FFFFFF" : "#22000000";
            tok["@MOCK"] = dark ? "#FF16161B" : "#FFE6E6EC";
            tok["@ACCSOFT"] = Color.FromArgb(dark ? (byte)0x38 : (byte)0x26, accent.R, accent.G, accent.B).ToString();
            tok["@ACC"] = accent.ToString();
            string xaml = Xaml;
            foreach (var kv in tok.OrderByDescending(k => k.Key.Length)) xaml = xaml.Replace(kv.Key, kv.Value);
            var content = (FrameworkElement)XamlReader.Parse(xaml);

            Title = "AllFind"; Width = 900 * UiScale; Height = 740; Topmost = false; ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize; Background = Brushes.Transparent;
            WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, GlassFrameThickness = new Thickness(-1), ResizeBorderThickness = new Thickness(0), UseAeroCaptionButtons = false, CornerRadius = new CornerRadius(0) });
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            UseLayoutRounding = true;
            content.LayoutTransform = new ScaleTransform(UiScale, UiScale); // the original layout, just smaller
            Content = content;

            Func<string, object> F = n => content.FindName(n);
            Root = (Border)content; Body = (Grid)F("Body");
            Box = (TextBox)F("Box"); Ph = (TextBlock)F("Ph"); LogoImg = (Image)F("LogoImg");
            Status = (TextBlock)F("Status"); Count = (TextBlock)F("Count"); Keys = (StackPanel)F("Keys");
            List = (ListBox)F("List"); Chips = (StackPanel)F("Chips");
            Tiles = (ItemsControl)F("Tiles"); HomeTop = (FrameworkElement)F("HomeTop"); TilesHead = (TextBlock)F("TilesHead");
            PreviewPane = (Border)F("PreviewPane"); SearchRow = (Border)F("SearchRow");
            PImg = (Image)F("PImg"); PGlyph = (TextBlock)F("PGlyph");
            PTitle = (TextBlock)F("PTitle"); PKind = (TextBlock)F("PKind");
            PPath = (TextBlock)F("PPath"); PMeta = (TextBlock)F("PMeta");
            PActions = (StackPanel)F("PActions");
            LogoImg.Source = Logo.Image(64, true);

            for (int i = 0; i < ChipNames.Length; i++)
            {
                int k = i;
                var b = new Button { Style = (Style)Root.Resources["Chip"], Focusable = false };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock { Text = ChipGlyphs[i], FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(new TextBlock { Text = ChipNames[i], VerticalAlignment = VerticalAlignment.Center });
                b.Content = sp;
                b.Click += (s, e) => SetMode(k);
                Chips.Children.Add(b);
            }
            SetMode(0, false);
            BuildKeys();

            // ---- taskbar setup
            SetupPane = (Grid)F("Setup");
            ((Image)F("SetupLogo")).Source = Logo.Image(64, true);
            ((Image)F("MockLogoA")).Source = Logo.Image(64, false);
            ((Image)F("MockLogoB")).Source = Logo.Image(64, false);
            StyleIconBtn = (Button)F("StyleIcon"); StyleBarBtn = (Button)F("StyleBar");
            TglHide = (CheckBox)F("TglHide"); TglPin = (CheckBox)F("TglPin"); TglStart = (CheckBox)F("TglStart");
            HideSub = (TextBlock)F("HideSub"); PinSub = (TextBlock)F("PinSub");
            StyleIconBtn.Click += (s, e) => { Cfg.Set("style", "icon"); Cfg.Set("bar", "0"); Cfg.Set("hideWin", "1"); ApplyTaskbar(); RefreshSetup(); };
            
            TglHide.Click += (s, e) => { Cfg.Set("hideWin", TglHide.IsChecked == true ? "1" : "0"); ApplyTaskbar(); RefreshSetup(); };
            TglPin.Click += (s, e) =>
            {
                if (Cfg.Get("style", "icon") == "bar") { Cfg.Set("bar", TglPin.IsChecked == true ? "1" : "0"); ApplyTaskbar(); RefreshSetup(); return; }
                // Windows only lets Explorer pin: open Explorer's own menu on AllFind.exe, then watch for the change
                pinHint = true; bool want = TglPin.IsChecked == true;
                RefreshSetup(); TglPin.IsChecked = want;
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                HidePopup();
                PinViaExplorer(exe);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    for (int i = 0; i < 60; i++)   // up to ~30 s for the user to pick Pin/Unpin in Explorer's menu
                    {
                        Thread.Sleep(500);
                        bool p = Taskbar.IsPinned();
                        if (p == want) { Dispatcher.BeginInvoke(new Action(() => { pinned = p; pinHint = false; RefreshSetup(); })); return; }
                    }
                    Dispatcher.BeginInvoke(new Action(() => { pinned = Taskbar.IsPinned(); RefreshSetup(); }));
                });
            };
            TglStart.Click += (s, e) => { Program.SetStartup(TglStart.IsChecked == true); RefreshSetup(); };
            ((Button)F("BtnDone")).Click += (s, e) => CloseSetup();
            BtnRefresh = (Button)F("BtnRefresh");
            BtnRefresh.Click += (s, e) =>
            {
                BtnRefresh.IsEnabled = false; PinSub.Text = "Refreshing the taskbar…";
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Taskbar.RestartExplorer();
                    Thread.Sleep(3500); // let the taskbar finish laying out, then the bar snaps in
                    Dispatcher.BeginInvoke(new Action(() => { BtnRefresh.IsEnabled = true; ShowPopup(); RefreshSetup(); }));
                });
            };
            ((Button)F("Gear")).Click += (s, e) => ShowSetup();
            Pill = new TaskbarPill(this, dark);
            TaskbarPill.InstallHook();
            IconPill = new TaskbarPill(this, dark); IconPill.Enable(false);  // instant click on the pinned icon
            // the old "Search bar on the taskbar" style is gone: move anyone still on it back to the pinned icon
            if (Cfg.Get("style", "icon") != "icon") { Cfg.Set("style", "icon"); Cfg.Set("bar", "0"); }
            if (Cfg.Get("setup", "0") == "1") ApplyTaskbar();

            debounce = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(28) };
            debounce.Tick += (s, e) => { debounce.Stop(); Run(); };
            Box.TextChanged += (s, e) =>
            {
                Ph.Visibility = Box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; debounce.Stop(); debounce.Start();
                if (tbMode && Pill != null) Pill.SetText(Box.Text);
            };
            PreviewKeyDown += OnKey;
            List.SelectionChanged += (s, e) => UpdatePreview();
            List.PreviewMouseLeftButtonUp += (s, e) =>
            {
                var item = ItemsControl.ContainerFromElement(List, e.OriginalSource as DependencyObject) as ListBoxItem;
                if (item != null) { var h = item.DataContext as Hit; if (h != null && !h.IsHeader) OpenHit(h, 0); }
                Box.Focus();
            };
            List.PreviewMouseRightButtonUp += (s, e) =>
            {
                var item = ItemsControl.ContainerFromElement(List, e.OriginalSource as DependencyObject) as ListBoxItem;
                var h = item == null ? null : item.DataContext as Hit;
                if (h != null && !h.IsHeader) { List.SelectedItem = h; ShowMenu(h, null); e.Handled = true; }
            };
            Tiles.PreviewMouseRightButtonUp += (s, e) =>
            {
                var fe = e.OriginalSource as FrameworkElement; var h = fe == null ? null : fe.DataContext as Hit;
                if (h != null) { ShowMenu(h, null); e.Handled = true; }
            };
            Tiles.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                var fe = e.OriginalSource as FrameworkElement; var h = fe == null ? null : fe.DataContext as Hit;
                if (h != null) OpenHit(h, 0);
            }));
            Deactivated += (s, e) => { lastDeactHide = DateTime.Now; HidePopup(); };
            SourceInitialized += OnSource;
            E.Changed += () => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (!prewarmed && E.Apps.Count > 0 && IsLoaded0) { prewarmed = true; Prewarm(); }
                UpdateStatus();
                if (IsVisible && (!E.Busy || Box.Text.Trim().Length == 0)) { if (Box.Text.Trim().Length == 0) { if (List.SelectedIndex < 0) Run(); } else Run(); }
            }));
            new WindowInteropHelper(this).EnsureHandle();
            IconLoader.Start(Dispatcher);
            E.Init();
            UpdateStatus();
        }

        bool prewarmed; bool IsLoaded0 { get { return Icons.Scale > 0 && new WindowInteropHelper(this).Handle != IntPtr.Zero; } }
        // load every app icon (both row sizes) and the recent files' icons into the cache right after start,
        // so results show their icons instantly instead of popping in one by one
        void Prewarm()
        {
            var apps = E.Apps; var recent = E.Recent;
            int small = Icons.Px(24), big = Icons.Px(40);
            var t = new Thread(() =>
            {
                foreach (var a in apps)
                    foreach (int px in new[] { big, small })
                        Shell.Image("shell:AppsFolder\\" + a.Id, px, false, "app:" + a.Id + "|" + px);
                foreach (var kv in recent)
                {
                    var h = new Hit { Type = Directory.Exists(kv.Key) ? Hit.FOLDER : Hit.FILE, Path = kv.Key };
                    try { Icons.Load(h, small); } catch { }
                }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "prewarm" };
            t.SetApartmentState(ApartmentState.STA); t.Start();
        }

        void UpdateStatus()
        {
            if (!E.Loaded) Status.Text = "Loading…";
            else if (E.Busy && E.Status != "") Status.Text = E.Status;
            else Status.Text = E.ItemCount.ToString("N0") + " items indexed" + (HotkeyLabel != "" ? "   ·   " + HotkeyLabel + " to open" : "");
        }

        void BuildKeys()
        {
            Keys.Children.Clear();
            string[][] ks = { new[] { "↵", "Open" }, new[] { "Ctrl ↵", "File location" }, new[] { "Ctrl ⇧ ↵", "Admin" }, new[] { "Tab", "Filter" }, new[] { "Esc", "Close" } };
            foreach (var k in ks)
            {
                var cap = new Border { Style = (Style)Root.Resources["KeyCap"], Child = new TextBlock { Text = k[0], FontSize = 10.5, Foreground = (Brush)new BrushConverter().ConvertFromString(dark ? "#FFD0D0D6" : "#FF404048") } };
                Keys.Children.Add(cap);
                Keys.Children.Add(new TextBlock { Text = k[1], FontSize = 11.5, Foreground = Status.Foreground, Margin = new Thickness(6, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center });
            }
        }

        static bool ReadDark()
        {
            string force = Environment.GetEnvironmentVariable("ALLFIND_THEME");
            if (force == "light") return false; if (force == "dark") return true;
            try { var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0); return v is int && (int)v == 0; }
            catch { return true; }
        }
        static Color ReadAccent(bool dark)
        {
            Color c = Color.FromRgb(0, 120, 212);
            try
            {
                var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null);
                if (v is int) { uint u = unchecked((uint)(int)v); c = Color.FromRgb((byte)(u & 0xFF), (byte)((u >> 8) & 0xFF), (byte)((u >> 16) & 0xFF)); }
            }
            catch { }
            if (dark) c = Color.FromRgb((byte)(c.R + (255 - c.R) * 0.4), (byte)(c.G + (255 - c.G) * 0.4), (byte)(c.B + (255 - c.B) * 0.4));
            return c;
        }

        void OnSource(object s, EventArgs e)
        {
            IntPtr h = new WindowInteropHelper(this).Handle;
            var src = HwndSource.FromHwnd(h);
            src.CompositionTarget.BackgroundColor = Colors.Transparent;
            SetWindowLong(h, -16, GetWindowLong(h, -16) & ~0x00080000);
            int v = dark ? 1 : 0; DwmSetWindowAttribute(h, 20, ref v, 4);
            v = 2; DwmSetWindowAttribute(h, 33, ref v, 4);
            // Windows 11's own acrylic (the Start menu / flyout material): blurs what's behind the panel.
            // Older builds without it fall back to the composition-attribute acrylic, then to a solid colour.
            v = 3; int hr = DwmSetWindowAttribute(h, 38, ref v, 4);
            if (hr != 0 && !AcrylicBlur(h, dark ? 0x9A1C1A1Cu : 0xA6F6F3F3u))
                Root.Background = new SolidColorBrush(dark ? Color.FromRgb(30, 30, 34) : Color.FromRgb(243, 243, 246));
            Icons.Scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            DpiChanged += (s2, e2) => { Icons.Scale = e2.NewDpi.DpiScaleX; };
            src.AddHook(WndProc);
            // let Windows draw its own context menus ("Show more options") in dark mode when the system is dark
            try { SetPreferredAppMode(1); FlushMenuThemes(); } catch { }
            if (RegisterHotKey(h, 1, 0x1 | 0x4000, 0x20)) HotkeyLabel = "Alt+Space";
            else if (RegisterHotKey(h, 1, 0x1 | 0x2 | 0x4000, 0x20)) HotkeyLabel = "Ctrl+Alt+Space";
        }
        [StructLayout(LayoutKind.Sequential)] struct ACCENT { public int State, Flags, Color, Anim; }
        [StructLayout(LayoutKind.Sequential)] struct WCAD { public int Attr; public IntPtr Data; public int Size; }
        [DllImport("user32")] static extern int SetWindowCompositionAttribute(IntPtr h, ref WCAD d);
        [DllImport("user32")] static extern uint GetDpiForWindow(IntPtr h);
        static bool AcrylicBlur(IntPtr h, uint abgr)
        {
            try
            {
                var acc = new ACCENT { State = 4, Flags = 2, Color = unchecked((int)abgr) };
                int size = Marshal.SizeOf(acc);
                IntPtr p = Marshal.AllocHGlobal(size);
                Marshal.StructureToPtr(acc, p, false);
                var d = new WCAD { Attr = 19, Data = p, Size = size };
                int ok = SetWindowCompositionAttribute(h, ref d);
                Marshal.FreeHGlobal(p);
                return ok != 0;
            }
            catch { return false; }
        }

        IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == 0x0312) { Toggle(); handled = true; }
            else if (ShellMenu.HandleMsg(msg, w, l)) handled = true;
            return IntPtr.Zero;
        }

        // behaves like the taskbar search button: click opens, click again closes. Clicking the taskbar first
        // deactivates (hides) us, so a request right after a deactivation-hide means "close" and is ignored.
        DateTime lastDeactHide = DateTime.MinValue;
        public void Toggle()
        {
            if (IsVisible && !hiding) { if (IsActive) HidePopup(); else ShowPopup(); return; }
            if ((DateTime.Now - lastDeactHide).TotalMilliseconds < 350) return;
            ShowPopup();
        }
        public void ShowFromTaskbar() { ShowPopup(true); }
        public void ToggleFromTaskbar()
        {
            if (IsVisible && !hiding) { if (IsActive) HidePopup(); else ShowPopup(true); return; }
            ShowPopup(true);
        }

        // Windows only lets the foreground app hand out focus; when we're opened by hover there was no click,
        // so borrow the foreground thread's input state for a moment (and as a last resort tap Alt).
        void ForceForeground()
        {
            IntPtr h = new WindowInteropHelper(this).Handle;
            SetForegroundWindow(h);
            if (GetForegroundWindow() == h) return;
            IntPtr fg = GetForegroundWindow();
            uint ft = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, IntPtr.Zero), me = GetCurrentThreadId();
            bool att = ft != 0 && ft != me && AttachThreadInput(me, ft, true);
            BringWindowToTop(h); SetForegroundWindow(h);
            if (att) AttachThreadInput(me, ft, false);
            if (GetForegroundWindow() != h) { keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero); SetForegroundWindow(h); }
        }

        // close on any click outside AllFind (its own panel, menus and taskbar icon count as inside), even when
        // the click doesn't move focus anywhere
        [DllImport("user32")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32")] static extern bool GetCursorPos(out WPOINT p);
        [DllImport("user32")] static extern IntPtr WindowFromPoint(WPOINT p);
        [DllImport("user32", EntryPoint = "GetWindowThreadProcessId")] static extern uint GetWindowPid(IntPtr h, out uint pid);
        [StructLayout(LayoutKind.Sequential)] struct WPOINT { public int X, Y; }
        static readonly uint MyPid = (uint)Process.GetCurrentProcess().Id;
        DispatcherTimer outside; DateTime watchFrom; bool wasDown;
        void WatchOutside()
        {
            watchFrom = DateTime.Now; wasDown = true;
            if (outside == null)
            {
                outside = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(60) };
                outside.Tick += (s, e) =>
                {
                    if (!IsVisible || hiding) { outside.Stop(); return; }
                    if ((DateTime.Now - watchFrom).TotalMilliseconds < 350) return;   // the click that opened us
                    bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0 || (GetAsyncKeyState(0x04) & 0x8000) != 0;
                    bool fresh = down && !wasDown; wasDown = down;
                    // only judge where a press STARTS: dragging out of the panel (selecting text) must not close it
                    if (!fresh) return;
                    WPOINT p; GetCursorPos(out p);
                    uint pid; GetWindowPid(WindowFromPoint(p), out pid);
                    if (pid != MyPid) HidePopup();
                };
            }
            outside.Start();
        }

        [DllImport("user32", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        // Start, Windows Search, notifications and quick settings sit in a layer above every app window, so
        // Windows closes them when you click the taskbar. Our click layer doesn't, so close them ourselves.
        void CloseShellFlyout()
        {
            var sb = new StringBuilder(64);
            if (GetClassName(GetForegroundWindow(), sb, 64) == 0) return;
            string c = sb.ToString();
            if (c != "Windows.UI.Core.CoreWindow" && c != "XamlExplorerHostIslandWindow" && c != "ControlCenterWindow") return;
            keybd_event(0x1B, 0, 0, UIntPtr.Zero); keybd_event(0x1B, 0, 2, UIntPtr.Zero);   // Esc
            for (int i = 0; i < 15 && GetForegroundWindow() != IntPtr.Zero; i++)
            {
                sb.Clear(); GetClassName(GetForegroundWindow(), sb, 64);
                if (sb.ToString() != c) break;
                Thread.Sleep(10);
            }
        }

        void SetTbMode(bool tb)
        {
            tbMode = tb;   // the panel keeps its own search box at the top; the taskbar pill mirrors it
        }

        static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        static readonly IEasingFunction Ease5 = new QuinticEase { EasingMode = EasingMode.EaseOut };
        public void ShowPopup() { ShowPopup(false); }
        public void ShowPopup(bool fromTaskbar)
        {
            if (IsVisible && !hiding)
            {
                // already open: just make sure it has the keyboard (no second slide, no flicker)
                if (fromTaskbar != tbMode) { SetTbMode(fromTaskbar); if (fromTaskbar) Pill.SetText(Box.Text); }
                ForceForeground(); Activate(); Box.Focus();
                return;
            }
            hiding = false;
            CloseShellFlyout();
            SetTbMode(fromTaskbar);
            if (fromTaskbar) Box.Clear();
            // monitor under the mouse, in physical pixels -> convert to WPF units
            var scr = WF.Screen.FromPoint(WF.Cursor.Position);
            uint dpi = 96;
            try { dpi = GetDpiForWindow(new WindowInteropHelper(this).Handle); } catch { }
            double k = dpi > 0 ? dpi / 96.0 : 1;
            Icons.Scale = k;
            var wa = new Rect(scr.WorkingArea.Left / k, scr.WorkingArea.Top / k, scr.WorkingArea.Width / k, scr.WorkingArea.Height / k);
            var sb = new Rect(scr.Bounds.Left / k, scr.Bounds.Top / k, scr.Bounds.Width / k, scr.Bounds.Height / k);
            Height = Math.Min(740, wa.Height - 24);
            // anchor to the same side as the Start menu: left for a left-aligned taskbar, otherwise centred
            Left = Taskbar.LeftAligned ? wa.Left + 12 : wa.Left + (wa.Width - Width) / 2;
            // final spot just above the taskbar; start fully below it so the panel rises out from behind the
            // taskbar exactly like Start (the taskbar is always-on-top, we are not, so it masks us mid-slide)
            double final, from;
            if (wa.Bottom < sb.Bottom - 1) { final = wa.Bottom - Height - 12; from = sb.Bottom; }
            else if (wa.Top > sb.Top + 1) { final = wa.Top + 12; from = sb.Top - Height; }
            else { final = wa.Bottom - Height - 60; from = sb.Bottom; }
            slideFrom = from; slideTo = final;
            Body.BeginAnimation(OpacityProperty, null); Body.Opacity = 1;
            ((TranslateTransform)Body.RenderTransform).BeginAnimation(TranslateTransform.YProperty, null);
            Top = from;
            Show(); ForceForeground(); Activate();
            Box.Focus(); Box.SelectAll();
            // Windows sometimes hands focus back to the previous window a moment later (e.g. while Start closes):
            // take it back so typing always lands in the search box
            WatchOutside();
            foreach (int ms in new[] { 120, 300, 600 })
            {
                var ft = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(ms) };
                ft.Tick += (s2, e2) => { ft.Stop(); if (IsVisible && !hiding && !IsActive) { ForceForeground(); Activate(); Box.Focus(); } };
                ft.Start();
            }
            Slide(from, final, 170, true, () => { if (IsVisible && !hiding) Topmost = true; });
            if (Cfg.Get("setup", "0") != "1") ShowSetup();
            if ((DateTime.Now - E.LastQuick).TotalMinutes > 20 && E.Loaded) E.Rebuild(false);
            Run();
        }
        double slideFrom, slideTo;
        DispatcherTimer trimTimer;
        void TrimSoon()
        {
            if (trimTimer == null)
            {
                trimTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(4) };
                trimTimer.Tick += (s, e) => { trimTimer.Stop(); if (!IsVisible) Engine.Trim(false); };
            }
            trimTimer.Stop(); trimTimer.Start();
        }
        EventHandler slideTick;
        void Slide(double from, double to, int ms, bool easeOut, Action done)
        {
            if (slideTick != null) CompositionTarget.Rendering -= slideTick;
            var sw = Stopwatch.StartNew();
            slideTick = (s, e) =>
            {
                double t = Math.Min(1, sw.Elapsed.TotalMilliseconds / ms);
                double v = easeOut ? 1 - Math.Pow(1 - t, 4) : t * t * t;   // quartic out / cubic in
                Top = from + (to - from) * v;
                if (t >= 1)
                {
                    CompositionTarget.Rendering -= slideTick; slideTick = null;
                    if (done != null) done();
                }
            };
            CompositionTarget.Rendering += slideTick;
        }
        public void HidePopup()
        {
            if (!IsVisible || hiding) return;
            hiding = true;
            Topmost = false;
            if (IconPill != null) IconPill.Collapse();
            if (Pill != null) Pill.SetText("");
            if (Pill != null && Pill.Expanded) Pill.Collapse();
            Slide(Top, slideFrom, 110, false, () => { if (hiding) { Hide(); hiding = false; } });
        }
        public void SetQuery(string q) { Box.Text = q; Box.CaretIndex = q.Length; }

        void SetMode(int m, bool run = true)
        {
            mode = m;
            for (int i = 0; i < Chips.Children.Count; i++) ((Button)Chips.Children[i]).Tag = i == m ? "sel" : null;
            if (run) Run();
            Box.Focus();
        }

        void Run()
        {
            string q = Box.Text.Trim();
            int ver = ++version;
            IconLoader.Gen = ver;
            int m = mode;
            if (q.Length == 0) { ShowHome(ver); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var sw = Stopwatch.StartNew();
                int total = 0;
                List<Hit> hits;
                try { hits = E.Search(q, m, () => ver != version, out total); } catch { hits = new List<Hit>(); }
                if (hits == null || ver != version) return;
                var rows = Arrange(q, m, hits, ver);
                long ms = sw.ElapsedMilliseconds;
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { if (ver == version) ShowRows(rows, total, ms); }));
            });
        }

        List<Hit> Arrange(string q, int m, List<Hit> hits, int ver)
        {
            var rows = new List<Hit>();
            Hit calc = Calc(q);
            foreach (var h in hits) { h.Gen = ver; h.Highlight(q); }
            if (m != 0)
            {
                if (calc != null) rows.Add(calc);
                foreach (var h in hits.Take(400)) { h.Badge = Kind(h); rows.Add(h); }
                if (calc != null) { calc.Gen = ver; calc.Highlight(null); }
                return rows;
            }
            if (calc != null) { rows.Add(Header("Best match")); calc.Big = true; calc.Highlight(null); rows.Add(calc); }
            else if (hits.Count > 0) { rows.Add(Header("Best match")); hits[0].Big = true; hits[0].Sub = Kind(hits[0]) + (hits[0].Type >= Hit.FILE ? "   ·   " + hits[0].Sub : ""); rows.Add(hits[0]); }
            var groups = hits.Skip(calc != null ? 0 : 1).GroupBy(h => h.Cat).OrderByDescending(g => g.First().Score);
            foreach (var g in groups)
            {
                rows.Add(Header(g.Key));
                foreach (var h in g.Take(g.Key == Engine.C_APPS ? 5 : 4)) rows.Add(h);
            }
            var more = new Hit { Type = Hit.MORE, Title = "Search the whole PC for “" + q + "”", Sub = "Includes system, program and hidden files", Glyph = "" };
            more.Highlight(null);
            rows.Add(more);
            return rows;
        }

        static Hit Header(string t) { return new Hit { Type = Hit.HEADER, Title = t }; }

        static string Kind(Hit h)
        {
            switch (h.Type)
            {
                case Hit.APP: return "App";
                case Hit.SETTING: return "Settings";
                case Hit.FOLDER: return "Folder";
                case Hit.FILE:
                    string e = Path.GetExtension(h.Path).ToUpperInvariant().TrimStart('.');
                    if (e == "EXE" || e == "LNK" || e == "APPREF-MS") return "Program";
                    return e.Length > 0 ? e + " file" : "File";
            }
            return "";
        }

        static Hit Calc(string q)
        {
            if (!Regex.IsMatch(q, @"^[\d\s\.\+\-\*/\(\)%]+$") || !Regex.IsMatch(q, @"\d\s*[\+\-\*/%]\s*[\d\(]")) return null;
            try
            {
                var r = new System.Data.DataTable().Compute(q.Replace("%", "/100*1"), null);
                double d = Convert.ToDouble(r);
                string s = Math.Round(d, 10).ToString("G12");
                return new Hit { Type = Hit.CALC, Title = "= " + s, Sub = "Calculator  ·  Enter to copy", Path = s, Glyph = "", Cat = "Calculator" };
            }
            catch { return null; }
        }

        static readonly string[] Favorites = { "File Explorer", "Google Chrome", "Microsoft Edge", "Settings", "Visual Studio Code", "Terminal", "Discord", "Spotify", "Steam", "Notepad", "Calculator", "Photos", "Firefox" };
        void ShowHome(int ver)
        {
            var apps = E.Apps;
            var byId = new Dictionary<string, App>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in apps) byId[a.Id] = a;
            var picks = new List<App>();
            foreach (var kv in E.TopUsage())
            {
                App a;
                if (kv.Key.StartsWith("app:") && byId.TryGetValue(kv.Key.Substring(4), out a)) picks.Add(a);
                if (picks.Count == 6) break;
            }
            foreach (var n in Favorites)
            {
                if (picks.Count == 6) break;
                var a = apps.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (a != null && !picks.Contains(a)) picks.Add(a);
            }
            var tiles = picks.Select(a => new Hit { Type = Hit.APP, Title = a.Name, AppId = a.Id, Target = a.Target, Path = a.Target, Key = "app:" + a.Id, Glyph = "", Big = true, Gen = -1 }).ToList();
            Tiles.ItemsSource = tiles;
            HomeTop.Visibility = tiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            var rows = new List<Hit>();
            var rec = E.Recent;
            if (rec.Count > 0)
            {
                rows.Add(Header("Recent"));
                foreach (var kv in rec.Take(8))
                {
                    bool dir = Directory.Exists(kv.Key);
                    var h = new Hit { Type = dir ? Hit.FOLDER : Hit.FILE, Title = Path.GetFileName(kv.Key.TrimEnd('\\')), Path = kv.Key, Key = kv.Key, Sub = Engine.Pretty(Path.GetDirectoryName(kv.Key) ?? ""), Badge = When(kv.Value), Glyph = dir ? "" : "", Gen = ver };
                    h.Highlight(null); rows.Add(h);
                }
            }
            ShowRows(rows, 0, 0);
        }
        static string When(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalMinutes < 60) return Math.Max(1, (int)d.TotalMinutes) + " min ago";
            if (d.TotalHours < 24) return (int)d.TotalHours + " h ago";
            if (d.TotalDays < 7) return (int)d.TotalDays + " d ago";
            return t.ToString("MMM d");
        }

        void ShowRows(List<Hit> rows, int total, long ms)
        {
            bool home = Box.Text.Trim().Length == 0;
            if (!home) HomeTop.Visibility = Visibility.Collapsed;
            if (rows.Count == 0 && !home && mode != 5)
            {
                var h = new Hit { Type = Hit.MORE, Title = "No matches here — search the whole PC for “" + Box.Text.Trim() + "”", Glyph = "" };
                h.Highlight(null); rows.Add(h);
            }
            List.ItemsSource = rows;
            int first = rows.FindIndex(h => !h.IsHeader);
            List.SelectedIndex = home ? -1 : first;
            if (rows.Count > 0) List.ScrollIntoView(rows[0]);
            Count.Text = home ? "" : (total == 0 ? "No results" : total.ToString("N0") + (total == 1 ? " result" : " results") + "  ·  " + ms + " ms");
            UpdatePreview();
        }

        Hit Sel { get { return List.SelectedItem as Hit; } }

        class PreviewData { public ImageSource Img; public string Kind, Meta; }

        void UpdatePreview()
        {
            var h = Sel;
            PActions.Children.Clear();
            bool none = h == null || h.IsHeader;
            // home view with nothing selected: give the list the full width instead of an empty side panel
            if (none) { PreviewPane.Visibility = Visibility.Hidden; return; }
            PreviewPane.Visibility = Visibility.Visible;
            PTitle.Text = h.Title;
            bool isFs = h.Type == Hit.FILE || h.Type == Hit.FOLDER;
            PKind.Text = isFs ? (h.Type == Hit.FOLDER ? "Folder" : Kind(h)) : Kind(h);
            PPath.Text = isFs ? h.Path : (h.Type == Hit.APP && h.Target != null ? h.Target : "");
            PMeta.Text = "";
            // show the small cached icon right away, upgrade to the large one / thumbnail asynchronously
            PImg.Source = h.Icon; PImg.Opacity = 1;
            bool thumb = h.Type == Hit.FILE && Icons.Thumbs.Contains(Path.GetExtension(h.Path).ToLowerInvariant());
            PImg.MaxHeight = thumb ? 150 : 112;
            PGlyph.Text = h.Glyph ?? "";
            PGlyph.Visibility = h.Icon == null && (h.Type == Hit.SETTING || h.Type == Hit.CALC || h.Type == Hit.MORE) ? Visibility.Visible : Visibility.Collapsed;
            if (h.Type == Hit.APP || isFs)
            {
                IconLoader.Preview(() =>
                {
                    var d = new PreviewData();
                    int px = Icons.Px(104);
                    if (h.Type == Hit.APP) d.Img = Shell.Image("shell:AppsFolder\\" + h.AppId, px, false, "app:" + h.AppId + "|" + px);
                    else
                    {
                        d.Img = Shell.Image(h.Path, thumb ? 320 : 256, h.Type == Hit.FILE, null);
                        d.Kind = Shell.TypeName(h.Path, h.Type == Hit.FOLDER);
                        try
                        {
                            if (h.Type == Hit.FILE) { var fi = new FileInfo(h.Path); d.Meta = Size(fi.Length) + "   ·   Modified " + fi.LastWriteTime.ToString("MMM d, yyyy  h:mm tt"); }
                            else d.Meta = "Modified " + Directory.GetLastWriteTime(h.Path).ToString("MMM d, yyyy");
                        }
                        catch { }
                    }
                    return d;
                }, r =>
                {
                    var d = r as PreviewData;
                    if (d == null || Sel != h) return;
                    if (d.Img != null)
                    {
                        PImg.Source = d.Img; PGlyph.Visibility = Visibility.Collapsed;
                        PImg.BeginAnimation(OpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = Ease });
                    }
                    else if (PImg.Source == null) PGlyph.Visibility = Visibility.Visible;
                    if (d.Kind != null) PKind.Text = d.Kind;
                    if (d.Meta != null) PMeta.Text = d.Meta;
                });
            }

            switch (h.Type)
            {
                case Hit.APP:
                    Act("", "Open", h, 0, true);
                    if (h.Target != null) { Act("", "Run as administrator", h, 2); Act("", "Open file location", h, 1); }
                    break;
                case Hit.SETTING: Act("", "Open", h, 0, true); break;
                case Hit.FILE:
                    Act("", "Open", h, 0, true);
                    if (h.Cat == Engine.C_APPS) Act("", "Run as administrator", h, 2);
                    Act("", "Open file location", h, 1);
                    Act("", "Copy path", h, 3);
                    break;
                case Hit.FOLDER:
                    Act("", "Open", h, 0, true);
                    Act("", "Open file location", h, 1);
                    Act("", "Open in Terminal", h, 4);
                    Act("", "Copy path", h, 3);
                    break;
                case Hit.CALC: Act("", "Copy result", h, 0, true); break;
                case Hit.MORE: Act("", "Search whole PC", h, 0, true); break;
            }
        }

        void Act(string glyph, string text, Hit h, int how, bool primary = false)
        {
            var b = new Button { Style = (Style)Root.Resources[primary ? "ActPrimary" : "Act"], Focusable = false };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            b.Content = sp; b.Click += (s, e) => OpenHit(h, how);
            PActions.Children.Add(b);
        }

        static string Size(long b)
        {
            string[] u = { "bytes", "KB", "MB", "GB", "TB" }; double d = b; int i = 0;
            while (d >= 1024 && i < u.Length - 1) { d /= 1024; i++; }
            return (i == 0 ? d.ToString("0") : d.ToString("0.#")) + " " + u[i];
        }

        void Copy(string s) { try { Clipboard.SetText(s); Count.Text = "Copied ✓"; } catch { } }

        // how: 0 open, 1 location, 2 admin, 3 copy path, 4 terminal
        void OpenHit(Hit h, int how)
        {
            if (h == null || h.IsHeader) return;
            if (h.Type == Hit.CALC) { Copy(h.Path); return; }
            if (h.Type == Hit.MORE) { SetMode(5); return; }
            if (how == 3) { Copy(h.Path); return; }
            try
            {
                string target = h.Type == Hit.APP ? h.Target : h.Path;
                if ((how == 1 || how == 2) && target == null) { Count.Text = "Not available for this app"; return; }
                if (how == 4)
                {
                    try { Process.Start(new ProcessStartInfo("wt.exe", "-d \"" + h.Path + "\"") { UseShellExecute = true }); }
                    catch { Process.Start(new ProcessStartInfo("powershell.exe") { WorkingDirectory = h.Path, UseShellExecute = true }); }
                }
                else if (how == 1 && target != null) Process.Start("explorer.exe", "/select,\"" + target + "\"");
                else if (how == 2 && target != null) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, Verb = "runas" });
                else if (h.Type == Hit.APP) Process.Start("explorer.exe", "shell:AppsFolder\\" + h.AppId);
                else if (h.Type == Hit.SETTING && h.Path.Contains(" "))
                {
                    int sp = h.Path.IndexOf(' ');
                    Process.Start(h.Path.Substring(0, sp), h.Path.Substring(sp + 1));
                }
                else Process.Start(new ProcessStartInfo(h.Path) { UseShellExecute = true });
                if (h.Key != null) E.Used(h.Key);
                HidePopup();
            }
            catch (Exception ex) { if (!(ex is Win32Exception && how == 2)) Count.Text = "Couldn't open: " + ex.Message; }
        }

        // ---------------- right-click menu (like Windows Search: open, admin, location, pin, uninstall, more)
        static string ParseName(Hit h) { return h.Type == Hit.APP ? "shell:AppsFolder\\" + h.AppId : h.Path; }

        void ShowMenu(Hit h, UIElement anchor)
        {
            if (h == null || h.IsHeader) return;
            var cm = new ContextMenu { Style = (Style)Root.Resources["Menu"] };
            Func<string, string, Action, MenuItem> make = (g, t, a) =>
            {
                var mi = new MenuItem { Header = t, Tag = g, Style = (Style)Root.Resources["MI"] };
                mi.Click += (s, e) => { try { a(); } catch (Exception ex) { Count.Text = "Couldn't do that: " + ex.Message; } };
                return mi;
            };
            Action<string, string, Action> add = (g, t, a) => cm.Items.Add(make(g, t, a));
            Action sep = () => cm.Items.Add(new Separator { Style = (Style)Root.Resources["Sep"] });
            string target = h.Type == Hit.APP ? h.Target : h.Path;
            bool fs = h.Type == Hit.FILE || h.Type == Hit.FOLDER;
            bool prog = h.Type == Hit.APP || (h.Type == Hit.FILE && h.Cat == Engine.C_APPS);
            IntPtr hwnd = new WindowInteropHelper(this).Handle;

            if (h.Type == Hit.CALC) add("", "Copy result", () => OpenHit(h, 0));
            else if (h.Type == Hit.MORE) add("", "Search whole PC", () => OpenHit(h, 0));
            else if (h.Type == Hit.SETTING) add("", "Open", () => OpenHit(h, 0));
            else
            {
                add("", "Open", () => OpenHit(h, 0));
                if (prog && target != null) add("", "Run as administrator", () => OpenHit(h, 2));
                if (h.Type == Hit.FILE && !prog) add("", "Open with…", () => { Process.Start("rundll32.exe", "shell32.dll,OpenAs_RunDLL " + h.Path); HidePopup(); });
                if (target != null) add("", "Open file location", () => OpenHit(h, 1));
                if (h.Type == Hit.FOLDER) add("", "Open in Terminal", () => OpenHit(h, 4));
                if (prog)
                {
                    // Windows only lets File Explorer itself pin things, so open Explorer's own menu on the program
                    if (target != null) add("\uE718", "Pin to taskbar", () => { HidePopup(); PinViaExplorer(target); });
                    add("\uE74D", "Uninstall", () => { HidePopup(); Uninstall(target); });
                    add("\uE8E5", "Send to desktop (create shortcut)", () => SendToDesktop(h));
                }
                sep();
                if (target != null) add("", "Copy path", () => OpenHit(h, 3));
                if (fs) add("", "Copy", () =>
                {
                    var files = new System.Collections.Specialized.StringCollection(); files.Add(h.Path);
                    try { Clipboard.SetFileDropList(files); Count.Text = "Copied ✓"; } catch { }
                });
                if (target != null) add("", "Properties", () => { HidePopup(); ShellMenu.Properties(target); });
            }
            if (anchor != null) { cm.PlacementTarget = anchor; cm.Placement = PlacementMode.Bottom; cm.HorizontalOffset = 40; }
            else cm.Placement = PlacementMode.MousePoint;
            cm.Closed += (s, e) => { if (IsVisible && !hiding) Box.Focus(); };
            cm.IsOpen = true;
        }

        // opens File Explorer with the program selected and pops up Explorer's own menu, where "Pin to taskbar"
        // works (Windows refuses it from any other app). Keys are only sent once that Explorer window is in front.
        [DllImport("user32", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32")] static extern bool AllowSetForegroundWindow(int pid);
        void PinViaExplorer(string file)
        {
            AllowSetForegroundWindow(-1);   // let Explorer come to the front (we're giving it our focus)
            Process.Start("explorer.exe", "/select,\"" + file + "\"");
            string folder = Path.GetFileName(Path.GetDirectoryName(file)) ?? "";
            new Thread(() =>
            {
                var sb = new StringBuilder(256);
                for (int i = 0; i < 40; i++)
                {
                    Thread.Sleep(100);
                    IntPtr fg = GetForegroundWindow();
                    sb.Clear(); GetClassName(fg, sb, 256); if (sb.ToString() != "CabinetWClass") continue;
                    sb.Clear(); GetWindowText(fg, sb, 256); if (sb.ToString().IndexOf(folder, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Thread.Sleep(500);   // let Explorer finish selecting the file
                    if (GetForegroundWindow() != fg) return;
                    keybd_event(0x10, 0, 0, UIntPtr.Zero); keybd_event(0x79, 0, 0, UIntPtr.Zero);       // Shift+F10
                    keybd_event(0x79, 0, 2, UIntPtr.Zero); keybd_event(0x10, 0, 2, UIntPtr.Zero);
                    return;
                }
            }) { IsBackground = true }.Start();
        }

        // runs the program's own uninstaller (the one Settings > Installed apps uses); otherwise opens that page
        void Uninstall(string exe)
        {
            string cmd = exe == null ? null : FindUninstaller(exe);
            if (cmd != null)
            {
                try
                {
                    string file, args;
                    if (cmd.StartsWith("\"")) { int q = cmd.IndexOf('"', 1); file = cmd.Substring(1, q - 1); args = cmd.Substring(q + 1).Trim(); }
                    else { int e2 = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); file = e2 > 0 ? cmd.Substring(0, e2 + 4) : cmd; args = e2 > 0 ? cmd.Substring(e2 + 4).Trim() : ""; }
                    Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
                    return;
                }
                catch (Win32Exception) { return; }   // the admin prompt was cancelled
                catch { }
            }
            Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
        }
        static string FindUninstaller(string exe)
        {
            string dir = Path.GetDirectoryName(exe).TrimEnd('\\') + "\\";
            var roots = new[] {
                Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
                Registry.LocalMachine.OpenSubKey(@"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall") };
            string best = null; int bestLen = 0;
            foreach (var root in roots)
            {
                if (root == null) continue;
                using (root)
                    foreach (var name in root.GetSubKeyNames())
                        using (var k = root.OpenSubKey(name))
                        {
                            if (k == null) continue;
                            string un = k.GetValue("UninstallString") as string;
                            object sc = k.GetValue("SystemComponent");
                            if (string.IsNullOrEmpty(un) || (sc is int && (int)sc == 1)) continue;
                            string icon = ((k.GetValue("DisplayIcon") as string) ?? "").Trim('"');
                            int comma = icon.LastIndexOf(','); if (comma > 2) icon = icon.Substring(0, comma).Trim('"');
                            if (string.Equals(icon, exe, StringComparison.OrdinalIgnoreCase)) return un;
                            string loc = ((k.GetValue("InstallLocation") as string) ?? "").Trim('"').TrimEnd('\\');
                            if (loc.Length > 3 && dir.StartsWith(loc + "\\", StringComparison.OrdinalIgnoreCase) && loc.Length > bestLen) { best = un; bestLen = loc.Length; }
                        }
            }
            return best;
        }

        // a shortcut on the desktop: a normal .lnk for programs; Store apps get one that opens them through Explorer
        void SendToDesktop(Hit h)
        {
            string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string target = h.Type == Hit.APP ? h.Target : h.Path;
            string name = string.Join("_", (h.Title ?? Path.GetFileNameWithoutExtension(target ?? "App")).Split(Path.GetInvalidFileNameChars()));
            string dest = Path.Combine(desk, name + ".lnk");
            if (target != null && target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) File.Copy(target, dest, true);
            else
            {
                dynamic ws = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                dynamic lnk = ws.CreateShortcut(dest);
                if (target != null) { lnk.TargetPath = target; lnk.WorkingDirectory = Path.GetDirectoryName(target); }
                else { lnk.TargetPath = "explorer.exe"; lnk.Arguments = "shell:AppsFolder\\" + h.AppId; }   // Store app
                lnk.Save();
            }
            Count.Text = "Shortcut added to your desktop ✓";
        }

        void Move(int d)
        {
            var rows = List.ItemsSource as List<Hit>; if (rows == null || rows.Count == 0) return;
            int i = List.SelectedIndex;
            if (i < 0) i = d > 0 ? -1 : rows.Count;
            int target = -1;
            for (int k = i + d; k >= 0 && k < rows.Count; k += Math.Sign(d))
            {
                if (!rows[k].IsHeader) { target = k; if (Math.Abs(k - i) >= Math.Abs(d)) break; }
            }
            if (target < 0) return;
            List.SelectedIndex = target;
            List.ScrollIntoView(rows[target]);
            if (target > 0 && rows[target - 1].IsHeader) List.ScrollIntoView(rows[target - 1]);
        }

        void OnKey(object s, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (SetupPane.Visibility == Visibility.Visible)
            {
                if (e.Key == Key.Escape || e.Key == Key.Enter) { CloseSetup(); e.Handled = true; }
                return;
            }
            switch (e.Key)
            {
                case Key.Down: Move(1); e.Handled = true; break;
                case Key.Up: Move(-1); e.Handled = true; break;
                case Key.PageDown: Move(8); e.Handled = true; break;
                case Key.PageUp: Move(-8); e.Handled = true; break;
                case Key.Enter:
                    {
                        var h = Sel;
                        if (h == null && Box.Text.Length == 0) { var t = Tiles.ItemsSource as List<Hit>; if (t != null && t.Count > 0) h = t[0]; }
                        OpenHit(h, ctrl && shift ? 2 : ctrl ? 1 : 0); e.Handled = true; break;
                    }
                case Key.Tab: SetMode((mode + (shift ? ChipNames.Length - 1 : 1)) % ChipNames.Length); e.Handled = true; break;
                case Key.Escape:
                    if (Box.Text.Length > 0) Box.Clear(); else HidePopup();
                    e.Handled = true; break;
                case Key.Apps:
                case Key.F10:
                    if (e.Key == Key.F10 && !shift) break;
                    if (Sel != null) { var c = List.ItemContainerGenerator.ContainerFromItem(Sel) as UIElement; ShowMenu(Sel, c); }
                    e.Handled = true; break;
                case Key.C:
                    if (ctrl && Box.SelectionLength == 0 && Sel != null && Sel.Path != null) { Copy(Sel.Path); e.Handled = true; }
                    break;
            }
        }

        const string Xaml = @"
<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        x:Name='Root' Background='@BG'>
  <Border.Resources>
    <LinearGradientBrush x:Key='Edge' StartPoint='0,0' EndPoint='0,1'>
      <GradientStop Color='@EDGE1' Offset='0'/><GradientStop Color='@EDGE2' Offset='1'/>
    </LinearGradientBrush>
    <LinearGradientBrush x:Key='SelBrush' StartPoint='0,0' EndPoint='1,1'>
      <GradientStop Color='@SELA' Offset='0'/><GradientStop Color='@SELB' Offset='1'/>
    </LinearGradientBrush>
    <LinearGradientBrush x:Key='BrandV' StartPoint='0,0' EndPoint='0,1'>
      <GradientStop Color='#FF4FB0FF' Offset='0'/><GradientStop Color='#FFA44BF5' Offset='1'/>
    </LinearGradientBrush>
    <LinearGradientBrush x:Key='BrandH' StartPoint='0,0' EndPoint='1,0'>
      <GradientStop Color='#FF4FB0FF' Offset='0'/><GradientStop Color='#FFA44BF5' Offset='1'/>
    </LinearGradientBrush>
    <Style x:Key='Switch' TargetType='CheckBox'>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Focusable' Value='False'/>
      <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='CheckBox'>
        <Grid Background='Transparent'>
          <Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>
          <ContentPresenter VerticalAlignment='Center'/>
          <Border x:Name='Track' Grid.Column='1' Width='42' Height='22' CornerRadius='11' Background='@HOVER' BorderBrush='@SUB' BorderThickness='1' VerticalAlignment='Center' Margin='18,0,0,0'>
            <Ellipse x:Name='Knob' Width='12' Height='12' Fill='@SUB' HorizontalAlignment='Left' Margin='4,0,0,0'/>
          </Border>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property='IsChecked' Value='True'>
            <Setter TargetName='Track' Property='Background' Value='{StaticResource BrandH}'/>
            <Setter TargetName='Track' Property='BorderThickness' Value='0'/>
            <Setter TargetName='Knob' Property='Fill' Value='White'/>
            <Setter TargetName='Knob' Property='HorizontalAlignment' Value='Right'/>
            <Setter TargetName='Knob' Property='Width' Value='14'/><Setter TargetName='Knob' Property='Height' Value='14'/>
          </Trigger>
          <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.55'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style x:Key='Choice' TargetType='Button'>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Focusable' Value='False'/>
      <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
      <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
        <Border x:Name='B' CornerRadius='12' Padding='12' Background='@HOVER' BorderBrush='@BORDER' BorderThickness='1.5'>
          <ContentPresenter/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='B' Property='Background' Value='@SEL'/></Trigger>
          <Trigger Property='Tag' Value='sel'><Setter TargetName='B' Property='BorderBrush' Value='{StaticResource BrandH}'/><Setter TargetName='B' Property='Background' Value='{StaticResource SelBrush}'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style TargetType='ScrollBar'>
      <Setter Property='Width' Value='10'/>
      <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ScrollBar'>
        <Track x:Name='PART_Track' IsDirectionReversed='True'>
          <Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='Thumb'>
            <Border CornerRadius='3' Background='@SUB' Opacity='0.3' Margin='3,2'/>
          </ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
        </Track>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style x:Key='KeyCap' TargetType='Border'>
      <Setter Property='Background' Value='@KEY'/>
      <Setter Property='BorderBrush' Value='@BORDER'/>
      <Setter Property='BorderThickness' Value='1,1,1,2'/>
      <Setter Property='CornerRadius' Value='4'/>
      <Setter Property='Padding' Value='6,1'/>
      <Setter Property='VerticalAlignment' Value='Center'/>
    </Style>
    <Style x:Key='Chip' TargetType='Button'>
      <Setter Property='Foreground' Value='@FG'/>
      <Setter Property='FontSize' Value='12.5'/>
      <Setter Property='Margin' Value='0,0,6,0'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
        <Border x:Name='B' CornerRadius='15' Padding='13,6' Background='@HOVER' BorderBrush='{StaticResource Edge}' BorderThickness='1'>
          <ContentPresenter/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='B' Property='Background' Value='@SEL'/></Trigger>
          <Trigger Property='Tag' Value='sel'><Setter TargetName='B' Property='Background' Value='{StaticResource SelBrush}'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style x:Key='Act' TargetType='Button'>
      <Setter Property='HorizontalContentAlignment' Value='Left'/>
      <Setter Property='Foreground' Value='@FG'/>
      <Setter Property='FontSize' Value='13'/>
      <Setter Property='Margin' Value='0,0,0,3'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
        <Border x:Name='B' CornerRadius='6' Padding='12,8' Background='Transparent'>
          <ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='B' Property='Background' Value='@SEL'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style x:Key='ActPrimary' TargetType='Button' BasedOn='{StaticResource Act}'>
      <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
        <Border x:Name='B' CornerRadius='8' Padding='12,8' Background='{StaticResource SelBrush}' BorderBrush='{StaticResource Edge}' BorderThickness='1'>
          <ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='B' Property='Background' Value='@SEL'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style x:Key='Tile' TargetType='Button'>
      <Setter Property='Foreground' Value='@FG'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Focusable' Value='False'/>
      <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
        <Border x:Name='B' CornerRadius='8' Padding='6,12,6,8' Margin='2' Background='Transparent'>
          <ContentPresenter HorizontalAlignment='Center'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='B' Property='Background' Value='@SEL'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style x:Key='Menu' TargetType='ContextMenu'>
      <Setter Property='OverridesDefaultStyle' Value='True'/>
      <Setter Property='HasDropShadow' Value='False'/>
      <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ContextMenu'>
        <Border Background='@CARD' BorderBrush='@BORDER' BorderThickness='1' CornerRadius='8' Padding='4' Margin='4,4,12,12' MinWidth='200'>
          <Border.Effect><DropShadowEffect BlurRadius='16' ShadowDepth='4' Opacity='0.32'/></Border.Effect>
          <StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Cycle'/>
        </Border>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style x:Key='MI' TargetType='MenuItem'>
      <Setter Property='Foreground' Value='@FG'/>
      <Setter Property='FontSize' Value='12.5'/>
      <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='MenuItem'>
        <Border x:Name='B' CornerRadius='5' Padding='10,6,22,6' Background='Transparent'>
          <StackPanel Orientation='Horizontal'>
            <TextBlock Text='{Binding Tag, RelativeSource={RelativeSource TemplatedParent}}' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='13' Width='26' VerticalAlignment='Center' Foreground='@SUB'/>
            <ContentPresenter ContentSource='Header' VerticalAlignment='Center'/>
          </StackPanel>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='B' Property='Background' Value='@SEL'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style x:Key='Sep' TargetType='Separator'>
      <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Separator'>
        <Border Height='1' Background='@BORDER' Margin='8,4'/>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style x:Key='Row' TargetType='ListBoxItem'>
      <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
      <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
      <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ListBoxItem'>
        <Border x:Name='Bd' CornerRadius='8' Background='Transparent' BorderThickness='1' BorderBrush='Transparent' Margin='0,1'>
          <Grid>
            <Border x:Name='Bar' Width='3' Height='20' CornerRadius='1.5' HorizontalAlignment='Left' Background='{StaticResource BrandV}' Visibility='Collapsed'/>
            <ContentPresenter/>
          </Grid>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Background' Value='@HOVER'/></Trigger>
          <Trigger Property='IsSelected' Value='True'><Setter TargetName='Bd' Property='Background' Value='{StaticResource SelBrush}'/><Setter TargetName='Bd' Property='BorderBrush' Value='{StaticResource Edge}'/><Setter TargetName='Bar' Property='Visibility' Value='Visible'/></Trigger>
          <DataTrigger Binding='{Binding IsHeader}' Value='True'><Setter TargetName='Bd' Property='Background' Value='Transparent'/></DataTrigger>
        </ControlTemplate.Triggers>
      </ControlTemplate></Setter.Value></Setter>
      <Setter Property='Cursor' Value='Hand'/>
      <Style.Triggers>
        <DataTrigger Binding='{Binding IsHeader}' Value='True'>
          <Setter Property='IsHitTestVisible' Value='False'/><Setter Property='Focusable' Value='False'/>
        </DataTrigger>
      </Style.Triggers>
    </Style>
  </Border.Resources>

  <Grid ClipToBounds='True'>
  <!-- ambient brand glows shining through the glass -->
  <Ellipse Width='620' Height='480' HorizontalAlignment='Left' VerticalAlignment='Top' Margin='-260,-260,0,0' IsHitTestVisible='False'>
    <Ellipse.Fill><RadialGradientBrush><GradientStop Color='@GLOWA' Offset='0'/><GradientStop Color='#003D9BFF' Offset='1'/></RadialGradientBrush></Ellipse.Fill>
  </Ellipse>
  <Ellipse Width='680' Height='520' HorizontalAlignment='Right' VerticalAlignment='Bottom' Margin='0,0,-280,-300' IsHitTestVisible='False'>
    <Ellipse.Fill><RadialGradientBrush><GradientStop Color='@GLOWB' Offset='0'/><GradientStop Color='#008E44F0' Offset='1'/></RadialGradientBrush></Ellipse.Fill>
  </Ellipse>
  <!-- top sheen + light-catching edge -->
  <Border IsHitTestVisible='False'>
    <Border.Background><LinearGradientBrush StartPoint='0,0' EndPoint='0,1'><GradientStop Color='@SHEEN' Offset='0'/><GradientStop Color='#00FFFFFF' Offset='0.4'/></LinearGradientBrush></Border.Background>
  </Border>
  <Border IsHitTestVisible='False' BorderThickness='1' CornerRadius='8' BorderBrush='{StaticResource Edge}'/>

  <Grid x:Name='Body'>
    <Grid.RenderTransform><TranslateTransform/></Grid.RenderTransform>
    <Grid.RowDefinitions><RowDefinition Height='Auto'/><RowDefinition Height='Auto'/><RowDefinition Height='*'/><RowDefinition Height='Auto'/></Grid.RowDefinitions>

    <Border x:Name='SearchRow' Margin='16,16,16,12' CornerRadius='12' Background='@PANEL' BorderBrush='{StaticResource Edge}' BorderThickness='1'>
      <Grid>
        <Border CornerRadius='0,0,10,10' Height='2' VerticalAlignment='Bottom' Margin='10,0'>
          <Border.Background><LinearGradientBrush StartPoint='0,0' EndPoint='1,0'>
            <GradientStop Color='#003D9BFF' Offset='0'/><GradientStop Color='#FF3D9BFF' Offset='0.25'/><GradientStop Color='#FF8E44F0' Offset='0.75'/><GradientStop Color='#008E44F0' Offset='1'/>
          </LinearGradientBrush></Border.Background>
        </Border>
        <Grid Margin='12,0,16,0'>
          <Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>
          <Image x:Name='LogoImg' Width='28' Height='28' Margin='0,0,12,0' VerticalAlignment='Center' RenderOptions.BitmapScalingMode='HighQuality'/>
          <TextBlock x:Name='Ph' Grid.Column='1' Text='Search apps, files, settings &#x2014; anything' FontSize='18' Foreground='@SUB' VerticalAlignment='Center' IsHitTestVisible='False' Margin='2,0,0,0' FontFamily='Segoe UI Variable Display, Segoe UI'/>
          <TextBox x:Name='Box' Grid.Column='1' FontSize='18' Foreground='@FG' CaretBrush='@ACC' Background='Transparent' BorderThickness='0' Padding='0,14' VerticalContentAlignment='Center' SelectionBrush='@ACC' FontFamily='Segoe UI Variable Display, Segoe UI'/>
          <TextBlock x:Name='Count' Grid.Column='2' FontSize='12' Foreground='@SUB' VerticalAlignment='Center' Margin='10,0,0,0'/>
        </Grid>
      </Grid>
    </Border>

    <StackPanel x:Name='Chips' Grid.Row='1' Orientation='Horizontal' Margin='16,0,16,10'/>

    <Grid Grid.Row='2' Margin='10,0,16,0'>
      <Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition x:Name='PrevCol' Width='290'/></Grid.ColumnDefinitions>
      <DockPanel>
        <StackPanel x:Name='HomeTop' DockPanel.Dock='Top' Margin='0,0,0,6'>
          <TextBlock x:Name='TilesHead' Text='Top apps' FontWeight='SemiBold' FontSize='12.5' Foreground='@FG' Opacity='0.85' Margin='10,4,0,6'/>
          <ItemsControl x:Name='Tiles'>
            <ItemsControl.ItemsPanel><ItemsPanelTemplate><UniformGrid Columns='6' Rows='1'/></ItemsPanelTemplate></ItemsControl.ItemsPanel>
            <ItemsControl.ItemTemplate><DataTemplate>
              <Button Style='{StaticResource Tile}'>
                <StackPanel Width='76'>
                  <Grid Width='36' Height='36' HorizontalAlignment='Center'>
                    <Image Source='{Binding Icon}' RenderOptions.BitmapScalingMode='HighQuality'/>
                    <TextBlock Text='{Binding Glyph}' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='22' Foreground='@ACC' HorizontalAlignment='Center' VerticalAlignment='Center' Visibility='{Binding GlyphVis}'/>
                  </Grid>
                  <TextBlock Text='{Binding Title}' FontSize='11.5' Foreground='@FG' TextAlignment='Center' TextWrapping='Wrap' TextTrimming='CharacterEllipsis' MaxHeight='32' Margin='0,8,0,0'/>
                </StackPanel>
              </Button>
            </DataTemplate></ItemsControl.ItemTemplate>
          </ItemsControl>
        </StackPanel>
        <ListBox x:Name='List' Background='Transparent' BorderThickness='0' ItemContainerStyle='{StaticResource Row}'
                 ScrollViewer.HorizontalScrollBarVisibility='Disabled' VirtualizingPanel.IsVirtualizing='True' VirtualizingPanel.VirtualizationMode='Recycling' VirtualizingPanel.ScrollUnit='Pixel'>
          <ListBox.ItemTemplate><DataTemplate>
            <Grid>
              <TextBlock Text='{Binding Title}' FontWeight='SemiBold' FontSize='12.5' Foreground='@FG' Opacity='0.85' Margin='10,10,0,4' Visibility='{Binding HeaderVis}'/>
              <Grid Visibility='{Binding RowVis}' Margin='10,6,8,6'>
                <Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>
                <Grid Width='{Binding IconSize}' Height='{Binding IconSize}' Margin='0,0,12,0' VerticalAlignment='Center'>
                  <Image Source='{Binding Icon}' RenderOptions.BitmapScalingMode='HighQuality'/>
                  <TextBlock Text='{Binding Glyph}' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='{Binding GlyphSize}' Foreground='@ACC' HorizontalAlignment='Center' VerticalAlignment='Center' Visibility='{Binding GlyphVis}'/>
                </Grid>
                <StackPanel Grid.Column='1' VerticalAlignment='Center'>
                  <TextBlock FontSize='{Binding TitleSize}' FontWeight='{Binding TitleWeight}' Foreground='@FG' TextTrimming='CharacterEllipsis'>
                    <Run Text='{Binding Pre, Mode=OneWay}'/><Run Text='{Binding Mid, Mode=OneWay}' FontWeight='Bold'/><Run Text='{Binding Post, Mode=OneWay}'/>
                  </TextBlock>
                  <TextBlock Text='{Binding Sub}' FontSize='11.5' Foreground='@SUB' TextTrimming='CharacterEllipsis' Visibility='{Binding SubVis}' Margin='0,1,0,0'/>
                </StackPanel>
                <TextBlock Grid.Column='2' Text='{Binding Badge}' FontSize='11.5' Foreground='@SUB' VerticalAlignment='Center' Margin='10,0,0,0'/>
              </Grid>
            </Grid>
          </DataTemplate></ListBox.ItemTemplate>
        </ListBox>
      </DockPanel>

      <Border x:Name='PreviewPane' Grid.Column='1' Margin='10,0,0,0' CornerRadius='12' Background='@PREV' BorderBrush='{StaticResource Edge}' BorderThickness='1' Padding='18'>
        <StackPanel>
          <Grid Height='150' Margin='0,4,0,12'>
            <Image x:Name='PImg' MaxWidth='240' MaxHeight='150' RenderOptions.BitmapScalingMode='HighQuality' HorizontalAlignment='Center'>
              <Image.Effect><DropShadowEffect BlurRadius='18' ShadowDepth='3' Opacity='0.28'/></Image.Effect>
            </Image>
            <TextBlock x:Name='PGlyph' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='56' Foreground='@ACC' HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Grid>
          <TextBlock x:Name='PTitle' FontSize='17' FontWeight='SemiBold' Foreground='@FG' TextAlignment='Center' TextWrapping='Wrap' MaxHeight='72' TextTrimming='CharacterEllipsis' FontFamily='Segoe UI Variable Display, Segoe UI'/>
          <TextBlock x:Name='PKind' FontSize='12.5' Foreground='@SUB' TextAlignment='Center' Margin='0,3,0,0'/>
          <Border Height='1' Background='@BORDER' Margin='0,16,0,12'/>
          <StackPanel x:Name='PActions'/>
          <TextBlock x:Name='PPath' FontSize='11.5' Foreground='@SUB' TextWrapping='Wrap' Margin='2,12,0,0' MaxHeight='64' TextTrimming='CharacterEllipsis'/>
          <TextBlock x:Name='PMeta' FontSize='11.5' Foreground='@SUB' TextWrapping='Wrap' Margin='2,6,0,0'/>
        </StackPanel>
      </Border>
    </Grid>

    <Grid Grid.Row='3' Margin='18,10,18,12'>
      <StackPanel x:Name='Keys' Orientation='Horizontal'/>
      <StackPanel Orientation='Horizontal' HorizontalAlignment='Right' VerticalAlignment='Center'>
        <TextBlock x:Name='Status' FontSize='11.5' Foreground='@SUB' VerticalAlignment='Center'/>
        <Button x:Name='Gear' Style='{StaticResource Act}' Margin='10,0,0,0' Padding='0' ToolTip='Taskbar setup' Focusable='False'>
          <TextBlock Text='&#xE713;' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='13' Foreground='@SUB'/>
        </Button>
      </StackPanel>
    </Grid>
  </Grid>

  <!-- first-run setup -->
  <Grid x:Name='Setup' Visibility='Collapsed'>
    <Border Background='@SCRIM'/>
    <Border Width='600' HorizontalAlignment='Center' VerticalAlignment='Center' CornerRadius='16' Background='@CARD' BorderBrush='{StaticResource Edge}' BorderThickness='1' Padding='30,26'>
      <StackPanel>
        <Image x:Name='SetupLogo' Width='54' Height='54' HorizontalAlignment='Center'/>
        <TextBlock Text='Make AllFind your taskbar search' FontSize='22' FontWeight='SemiBold' Foreground='@FG' HorizontalAlignment='Center' Margin='0,12,0,4' FontFamily='Segoe UI Variable Display, Segoe UI'/>
        <TextBlock Text='A few quick options. You can change these anytime with the gear button below.' FontSize='12.5' Foreground='@SUB' HorizontalAlignment='Center'/>

        <UniformGrid Columns='2' Margin='0,20,0,18' Visibility='Collapsed'>
          <Button x:Name='StyleIcon' Style='{StaticResource Choice}' Margin='0,0,6,0'>
            <StackPanel>
              <Border Height='40' CornerRadius='6' Background='@MOCK' Padding='10,0'>
                <StackPanel Orientation='Horizontal' VerticalAlignment='Center'>
                  <UniformGrid Rows='2' Columns='2' Width='15' Height='15' VerticalAlignment='Center' Margin='0,0,14,0'><Border Background='#FF4CC2FF' Margin='0,0,1,1'/><Border Background='#FF4CC2FF' Margin='1,0,0,1'/><Border Background='#FF4CC2FF' Margin='0,1,1,0'/><Border Background='#FF4CC2FF' Margin='1,1,0,0'/></UniformGrid>
                  <Image x:Name='MockLogoA' Width='20' Height='20' Margin='0,0,14,0'/>
                  <Border Width='18' Height='18' CornerRadius='4' Background='@MOCKAPP' Margin='0,0,10,0'/>
                  <Border Width='18' Height='18' CornerRadius='4' Background='@MOCKAPP' Margin='0,0,10,0'/>
                  <Border Width='18' Height='18' CornerRadius='4' Background='@MOCKAPP'/>
                </StackPanel>
              </Border>
              <TextBlock Text='Icon only' FontWeight='SemiBold' Foreground='@FG' Margin='2,10,0,0'/>
              <TextBlock Text='Compact, uses your pinned AllFind icon' FontSize='11.5' Foreground='@SUB' Margin='2,2,0,0'/>
            </StackPanel>
          </Button>
          <Button x:Name='StyleBar' Style='{StaticResource Choice}' Margin='6,0,0,0'>
            <StackPanel>
              <Border Height='40' CornerRadius='6' Background='@MOCK' Padding='10,0'>
                <StackPanel Orientation='Horizontal' VerticalAlignment='Center'>
                  <UniformGrid Rows='2' Columns='2' Width='15' Height='15' VerticalAlignment='Center' Margin='0,0,10,0'><Border Background='#FF4CC2FF' Margin='0,0,1,1'/><Border Background='#FF4CC2FF' Margin='1,0,0,1'/><Border Background='#FF4CC2FF' Margin='0,1,1,0'/><Border Background='#FF4CC2FF' Margin='1,1,0,0'/></UniformGrid>
                  <Border Height='26' Width='120' CornerRadius='13' Background='@MOCKAPP' BorderBrush='{StaticResource BrandH}' BorderThickness='1' Margin='0,0,10,0'>
                    <StackPanel Orientation='Horizontal' VerticalAlignment='Center' Margin='9,0,0,0'>
                      <Image x:Name='MockLogoB' Width='14' Height='14' Margin='0,0,6,0'/>
                      <TextBlock Text='Search' FontSize='11' Foreground='@SUB' VerticalAlignment='Center'/>
                    </StackPanel>
                  </Border>
                  <Border Width='18' Height='18' CornerRadius='4' Background='@MOCKAPP'/>
                </StackPanel>
              </Border>
              <TextBlock Text='Search bar' FontWeight='SemiBold' Foreground='@FG' Margin='2,10,0,0'/>
              <TextBlock Text='A search box on your taskbar, opens instantly' FontSize='11.5' Foreground='@SUB' Margin='2,2,0,0'/>
            </StackPanel>
          </Button>
        </UniformGrid>

        <CheckBox x:Name='TglHide' Style='{StaticResource Switch}'>
          <StackPanel><TextBlock Text='Remove the old Windows Search from the taskbar' Foreground='@FG' FontSize='13.5'/><TextBlock x:Name='HideSub' Foreground='@SUB' FontSize='11.5' Margin='0,2,0,0' TextWrapping='Wrap'/></StackPanel>
        </CheckBox>
        <CheckBox x:Name='TglPin' Style='{StaticResource Switch}' Margin='0,14,0,0'>
          <StackPanel><TextBlock Text='Put AllFind first on the taskbar, next to Start' Foreground='@FG' FontSize='13.5'/><TextBlock x:Name='PinSub' Foreground='@SUB' FontSize='11.5' Margin='0,2,0,0' TextWrapping='Wrap'/></StackPanel>
        </CheckBox>
        <CheckBox x:Name='TglStart' Style='{StaticResource Switch}' Margin='0,14,0,0'>
          <StackPanel><TextBlock Text='Start with Windows' Foreground='@FG' FontSize='13.5'/><TextBlock Text='So AllFind is ready the moment you click it' Foreground='@SUB' FontSize='11.5' Margin='0,2,0,0'/></StackPanel>
        </CheckBox>

        <Button x:Name='BtnRefresh' Style='{StaticResource Act}' Margin='0,14,0,0' HorizontalContentAlignment='Center' Visibility='Collapsed' BorderThickness='1'>
          <StackPanel Orientation='Horizontal'>
            <TextBlock Text='&#xE72C;' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='13' Margin='0,1,8,0' VerticalAlignment='Center'/>
            <TextBlock Text='Refresh taskbar now  (2 seconds, closes open File Explorer windows)' VerticalAlignment='Center'/>
          </StackPanel>
        </Button>
        <Button x:Name='BtnDone' Style='{StaticResource ActPrimary}' Margin='0,22,0,0' HorizontalContentAlignment='Center'>
          <TextBlock Text='Done' FontWeight='SemiBold' HorizontalAlignment='Center'/>
        </Button>
      </StackPanel>
    </Border>
  </Grid>
  </Grid>
</Border>";
    }
}
