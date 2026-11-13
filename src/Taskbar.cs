// Taskbar integration: settings, Windows Search visibility, pin detection, and the AllFind taskbar target
// (an instant-click layer over the pinned AllFind icon, or an AllFind search bar next to Start).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AllFind
{
    static class Cfg
    {
        static string FilePath { get { return Path.Combine(Engine.Dir, "settings.txt"); } }
        static Dictionary<string, string> d;
        static void Load()
        {
            if (d != null) return;
            d = new Dictionary<string, string>();
            try
            {
                foreach (var l in File.ReadAllLines(FilePath))
                {
                    int i = l.IndexOf('='); if (i > 0) d[l.Substring(0, i)] = l.Substring(i + 1);
                }
            }
            catch { }
        }
        public static string Get(string k, string def) { Load(); string v; return d.TryGetValue(k, out v) ? v : def; }
        public static void Set(string k, string v)
        {
            Load(); d[k] = v;
            try { Directory.CreateDirectory(Engine.Dir); File.WriteAllLines(FilePath, d.Select(kv => kv.Key + "=" + kv.Value)); } catch { }
        }
    }

    static class Taskbar
    {
        [DllImport("user32", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string n);
        [DllImport("user32", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, string l, int flags, int timeout, out IntPtr result);
        const string SearchKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Search";

        // 0 hidden, 1 icon, 2 search box, 3 icon + label
        public static int SearchMode
        {
            get { try { var v = Registry.GetValue(SearchKey, "SearchboxTaskbarMode", 1); return v is int ? (int)v : 1; } catch { return 1; } }
        }
        public static void SetSearchMode(int v)
        {
            if (Cfg.Get("origSearchMode", null) == null) Cfg.Set("origSearchMode", SearchMode.ToString());
            if (SearchMode == v) return;
            Registry.SetValue(SearchKey, "SearchboxTaskbarMode", v, RegistryValueKind.DWord);
            IntPtr r;
            SendMessageTimeout((IntPtr)0xFFFF, 0x1A, IntPtr.Zero, "TraySettings", 2, 400, out r);
        }
        public static int OriginalMode
        {
            get { int m; return int.TryParse(Cfg.Get("origSearchMode", "1"), out m) ? m : 1; }
        }
        public static bool LeftAligned
        {
            get
            {
                try { var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAl", 1); return v is int && (int)v == 0; }
                catch { return false; }
            }
        }

        // Explorer only re-reads the search box setting when it starts, so restart it (Windows relaunches it)
        public static void RestartExplorer()
        {
            int sid = Process.GetCurrentProcess().SessionId;
            foreach (var p in Process.GetProcessesByName("explorer"))
                try { if (p.SessionId == sid) p.Kill(); } catch { }
            for (int i = 0; i < 30; i++)
            {
                Thread.Sleep(200);
                if (FindWindow("Shell_TrayWnd", null) != IntPtr.Zero) return;
            }
            try { Process.Start("explorer.exe"); } catch { }
        }

        public static AutomationElement FindPinned(AutomationElement root)
        {
            try
            {
                foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                {
                    string id = e.Current.AutomationId ?? "";
                    if (id.StartsWith("Appid:") && id.IndexOf("AllFind.exe", StringComparison.OrdinalIgnoreCase) >= 0) return e;
                }
            }
            catch { }
            return null;
        }
        public static bool IsPinned()
        {
            try { return FindPinned(AutomationElement.FromHandle(FindWindow("Shell_TrayWnd", null))) != null; } catch { return false; }
        }

        // where the AllFind bar goes: right after the last app button, if there's room before the tray
        public static Rect? FindAfterApps(AutomationElement root, double k)
        {
            try
            {
                double right = -1, trayLeft = double.MaxValue, top = 0, height = 0;
                foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                {
                    var c = e.Current; var r = c.BoundingRectangle; if (r.IsEmpty) continue;
                    string id = c.AutomationId ?? "";
                    if (id.StartsWith("Appid:") || id == "StartButton" || id == "TaskViewButton" || id == "WidgetsButton") { if (r.Right > right) { right = r.Right; top = r.Top; height = r.Height; } }
                    else if (id == "SystemTrayIcon" && r.Left < trayLeft) trayLeft = r.Left;
                }
                if (right < 0) return null;
                double w = 230 * k, x = right + 8 * k;
                if (x + w > trayLeft - 8 * k) return null; // not enough room: stay out of the way
                return new Rect(x, top, w, height);
            }
            catch { return null; }
        }

        // Windows' search box (only present when SearchboxTaskbarMode = 2), right after Start
        public static AutomationElement FindSearchBox(AutomationElement root)
        {
            try
            {
                var start = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "StartButton"));
                if (start == null) return null;
                double sx = start.Current.BoundingRectangle.Right;
                foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition))
                {
                    var c = e.Current; var r = c.BoundingRectangle;
                    string id = c.AutomationId ?? "", nm = c.Name ?? "";
                    if (r.IsEmpty || id == "StartButton" || id.StartsWith("Appid:") || nm == "Running applications") continue;
                    if (r.Width < 80 || r.Width > 600 || r.Height < 24) continue;     // a box, not an icon or the whole bar
                    if (Math.Abs(r.Left - sx) > 24) continue;                         // sits right after Start
                    bool named = id.IndexOf("search", StringComparison.OrdinalIgnoreCase) >= 0 || nm.IndexOf("search", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (named || c.ControlType == ControlType.Button || c.ControlType == ControlType.Edit) return e;
                }
            }
            catch { }
            return null;
        }
    }

    // A small window owned by the taskbar and kept on top of it, so clicks reach AllFind instantly
    // without launching a process. Bar mode draws the AllFind search bar; icon mode is an invisible layer
    // over the pinned AllFind icon that mimics the taskbar hover effect.
    class TaskbarPill
    {
        [DllImport("user32")] static extern bool ScreenToClient(IntPtr h, ref POINT p);
        [DllImport("user32")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
        [DllImport("user32")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32")] static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32")] static extern void mouse_event(uint f, int dx, int dy, uint data, UIntPtr extra);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        const uint NOACT = 0x0010, SHOW = 0x0040, HIDE = 0x0080, NOSIZE = 0x0001, NOMOVE = 0x0002;

        readonly MainWin win;
        readonly bool dark;
        HwndSource src; IntPtr tray; Border pill; Grid root; bool srcBar;
        volatile bool on, bar; Thread loop;
        DateTime suppressUntil = DateTime.MinValue;
        public bool Active { get { return on; } }
        public bool Bar { get { return bar; } }
        public volatile bool SlotFound;

        public TaskbarPill(MainWin w, bool dark) { win = w; this.dark = dark; }

        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
        [DllImport("user32")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        static WinEventProc hookProc; static IntPtr hook;
        public static volatile int Dirty = 1;
        // call once on the UI thread: create/destroy/show/hide of whole windows marks the taskbar layout dirty
        public static void InstallHook()
        {
            if (hook != IntPtr.Zero) return;
            hookProc = (h, ev, hwnd, obj, child, th, time) => { if (obj == 0 && child == 0) Dirty++; };
            hook = SetWinEventHook(0x8000, 0x8003, IntPtr.Zero, hookProc, 0, 0, 0x0002 /*out-of-context, skip own process*/);
            // Explorer restarts recreate the taskbar
            Dirty++;
        }

        public void Enable(bool barMode)
        {
            bar = barMode; on = true; SlotFound = false;
            win.Dispatcher.BeginInvoke(new Action(Destroy));
            if (loop != null) return;
            loop = new Thread(Loop) { IsBackground = true, Name = "taskbar", Priority = ThreadPriority.BelowNormal };
            loop.SetApartmentState(ApartmentState.MTA);
            loop.Start();
        }
        public void Disable() { on = false; win.Dispatcher.BeginInvoke(new Action(Destroy)); }
        void Destroy() { expanded = false; if (dwell != null) dwell.Stop(); if (src != null) { try { src.Dispose(); } catch { } src = null; } }

        // cheap polling: re-read one cached element's rectangle every second; only search the taskbar tree
        // again when the element disappears (Explorer restart, unpin, layout change)
        void Loop()
        {
            AutomationElement el = null; bool elBar = false; IntPtr elTray = IntPtr.Zero; DateTime nextFind = DateTime.MinValue;
            Rect? afterApps = null; sentRect = null; sentTray = IntPtr.Zero; int seenDirty = -1; DateTime nextIdle = DateTime.MinValue;
            while (true)
            {
                Thread.Sleep(400);
                if (!on) { el = null; sentRect = null; sentTray = IntPtr.Zero; continue; }
                // idle: nothing changed on screen and we already know where we are -> no work at all
                int dirty = Dirty;
                if (dirty == seenDirty && sentRect != null && (DateTime.Now - lastSent).TotalSeconds < 60) continue;
                if (dirty == seenDirty && sentRect == null && DateTime.Now < nextIdle) continue;
                seenDirty = dirty; nextIdle = DateTime.Now.AddSeconds(5);
                try
                {
                    IntPtr t = Taskbar.FindWindow("Shell_TrayWnd", null);
                    if (t != elTray || elBar != bar) { el = null; afterApps = null; }
                    Rect? r = null;
                    if (el != null)
                    {
                        try { var b = el.Current.BoundingRectangle; if (!b.IsEmpty && b.Width > 0) r = b; else el = null; }
                        catch { el = null; }
                    }
                    if (el == null && bar && t != IntPtr.Zero)
                    {
                        // free-space bar: only re-measure when the set of app windows changes (cheap check)
                        {
                            var rootEl = AutomationElement.FromHandle(t);
                            var box = Taskbar.FindSearchBox(rootEl);
                            if (box != null) { el = box; elTray = t; elBar = true; r = box.Current.BoundingRectangle; }
                            else afterApps = Taskbar.FindAfterApps(rootEl, GetDpiForWindow(t) / 96.0);
                        }
                        if (el == null) r = afterApps;
                    }
                    else if (el == null && DateTime.Now >= nextFind && t != IntPtr.Zero)
                    {
                        el = Taskbar.FindPinned(AutomationElement.FromHandle(t));
                        elTray = t; elBar = bar;
                        if (el != null) r = el.Current.BoundingRectangle; else nextFind = DateTime.Now.AddSeconds(30);
                    }
                    SlotFound = r != null;
                    // only wake the UI thread when something actually changed (plus a slow keep-on-top refresh)
                    bool changed = t != sentTray || !Nullable.Equals(r, sentRect);
                    if (changed || (DateTime.Now - lastSent).TotalSeconds > 15)
                    {
                        Log("tray=" + t + " bar=" + bar + " slot=" + (r.HasValue ? r.Value.ToString() : "none"));
                        sentTray = t; sentRect = r; lastSent = DateTime.Now;
                        var tt = t; var rr = r;
                        win.Dispatcher.BeginInvoke(new Action(() => Place(tt, rr)));
                    }
                }
                catch (Exception ex) { el = null; Log("loop: " + ex); }
            }
        }
        public static void Log(string s)
        {
            if (Environment.GetEnvironmentVariable("ALLFIND_DEBUG") != "1") return;
            try { File.AppendAllText(Path.Combine(Engine.Dir, "debug.txt"), DateTime.Now.ToString("HH:mm:ss.fff ") + s + Environment.NewLine); } catch { }
        }

        [DllImport("user32")] static extern bool EnumWindows(EnumProc f, IntPtr l);
        [DllImport("user32")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32")] static extern int GetWindowLong(IntPtr h, int i);
        delegate bool EnumProc(IntPtr h, IntPtr l);
        // cheap fingerprint of the windows that get taskbar buttons (visible, unowned, not tool windows)
        static int WindowSignature()
        {
            int sig = 17;
            EnumWindows((h, l) =>
            {
                uint pid;
                if (IsWindowVisible(h) && GetWindow(h, 4) == IntPtr.Zero && (GetWindowLong(h, -20) & 0x80) == 0
                    && GetWindowThreadProcessId(h, out pid) != 0 && pid != MyPid)
                    sig = unchecked(sig * 31 + h.ToInt32());
                return true;
            }, IntPtr.Zero);
            return sig;
        }

        Rect lastRect; IntPtr lastTray; Rect? sentRect; IntPtr sentTray; DateTime lastSent;
        static readonly uint MyPid = (uint)Process.GetCurrentProcess().Id;
        [DllImport("user32")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        void Place(IntPtr t, Rect? slot)
        {
            if (!on) { Destroy(); return; }
            if (src != null && (tray != t || !IsWindow(t) || srcBar != bar)) Destroy();
            if (slot == null || t == IntPtr.Zero || DateTime.Now < suppressUntil)
            {
                if (src != null) SetWindowPos(src.Handle, IntPtr.Zero, 0, 0, 0, 0, HIDE | NOACT | NOSIZE | NOMOVE);
                return;
            }
            var r = slot.Value;
            double k = GetDpiForWindow(t) / 96.0; if (k <= 0) k = 1;
            int h, w; POINT p;
            if (bar)
            {
                h = (int)Math.Min(r.Height, 40 * k) + 4; w = (int)r.Width + 4;
                p = new POINT { X = (int)r.Left - 2, Y = (int)(r.Top + (r.Height - h) / 2) };
            }
            else
            {
                // icon mode: the window covers the pinned icon; expanded, it widens into a type-here search pill
                h = (int)r.Height; w = expanded ? (int)(ExpW * k) : (int)r.Width; p = new POINT { X = (int)r.Left, Y = (int)r.Top };
                this.slot = r; slotTray = t; slotK = k;
            }
            if (src == null)
            {
                tray = t; srcBar = bar;
                var prm = new HwndSourceParameters("AllFindTaskbar")
                {
                    // a separate popup OWNED by the taskbar (not a child of it): Windows keeps owned windows above
                    // their owner, and the taskbar's own window tree / accessibility stays untouched
                    ParentWindow = t, WindowStyle = unchecked((int)0x80000000), // WS_POPUP
                    ExtendedWindowStyle = 0x80 | 0x08000000,                    // toolwindow | noactivate
                    UsesPerPixelTransparency = true, PositionX = p.X, PositionY = p.Y, Width = w, Height = h
                };
                try { src = new HwndSource(prm); } catch (Exception ex) { Log("hwndsource: " + ex); throw; }
                Log("created child " + src.Handle + " at " + p.X + "," + p.Y + " " + w + "x" + h);
                src.AddHook(ChildProc);
                src.RootVisual = bar ? BuildBar() : BuildIcon();
                lastRect = Rect.Empty;
            }
            var nr = new Rect(p.X, p.Y, w, h);
            if (nr == lastRect && lastTray == t) { SetWindowPos(src.Handle, IntPtr.Zero, 0, 0, 0, 0, NOACT | SHOW | NOSIZE | NOMOVE); return; }
            lastRect = nr; lastTray = t;
            placedAt = DateTime.Now;   // the window just moved under a possibly still cursor: that's not a hover
            root.Width = w / k; root.Height = h / k;
            if (bar) pill.CornerRadius = new CornerRadius(h / k / 2);
            else { hl.Width = Math.Max(10, r.Width / k - 6); xPill.Height = Math.Min(36, r.Height / k - 10); xPill.CornerRadius = new CornerRadius(xPill.Height / 2); }
            SetWindowPos(src.Handle, IntPtr.Zero /*HWND_TOP*/, p.X, p.Y, w, h, NOACT | SHOW);
        }

        // Handle clicks at the Win32 level: answer WM_MOUSEACTIVATE ourselves (otherwise the taskbar, as our
        // parent, can swallow the click) and act on button-up directly.
        IntPtr ChildProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            switch (msg)
            {
                case 0x0021: handled = true; return (IntPtr)3;            // WM_MOUSEACTIVATE -> MA_NOACTIVATE
                case 0x0202: Log("overlay click"); Click(); break;         // WM_LBUTTONUP
                case 0x0205: handled = true; if (!expanded) PassRightClick(); break; // WM_RBUTTONUP
            }
            return IntPtr.Zero;
        }

        // right-click goes to the real taskbar (jump list, unpin, taskbar settings)
        void PassRightClick()
        {
            suppressUntil = DateTime.Now.AddSeconds(6);
            if (src != null) SetWindowPos(src.Handle, IntPtr.Zero, 0, 0, 0, 0, HIDE | NOACT | NOSIZE | NOMOVE);
            mouse_event(0x0008, 0, 0, 0, UIntPtr.Zero); mouse_event(0x0010, 0, 0, 0, UIntPtr.Zero);
        }

        // ---------------- icon mode: hover the pinned icon and it grows into a search box you can type into
        const double ExpW = 300;
        bool expanded, armed = true; DateTime expandedAt;
        DispatcherTimer dwell; Rect? slot; IntPtr slotTray; double slotK = 1;
        Border hl, xPill, xCaret; TextBlock xText, xPh; SolidColorBrush hover; Color hc;
        public bool Expanded { get { return expanded; } }

        void Click()
        {
            if (bar) { win.Toggle(); return; }
            if (dwell != null) dwell.Stop();
            // double-clicks and fast repeat clicks: one action per 350 ms, so it never opens/closes twice
            if ((DateTime.Now - lastClick).TotalMilliseconds < 350) return;
            lastClick = DateTime.Now;
            if (!expanded || !win.IsVisible) { armed = false; if (expanded) win.ShowFromTaskbar(); else Expand(false); return; }
            // it opened by hover and you clicked to "open" it: keep it open instead of closing it on you
            bool justHovered = byHover && (DateTime.Now - expandedAt).TotalMilliseconds < 1500;
            byHover = false;
            if (justHovered) win.ShowFromTaskbar();
            else win.HidePopup();
        }
        DateTime lastClick = DateTime.MinValue;

        bool byHover; DateTime placedAt;
        void Expand(bool hovered)
        {
            if (expanded || src == null || slot == null) return;
            expanded = true; expandedAt = DateTime.Now; armed = false; byHover = hovered;
            hover.Color = Colors.Transparent;
            lastRect = Rect.Empty; Place(slotTray, slot);
            root.Background = Brushes.Transparent;    // empty corners stay click-through, the pill itself is clickable
            SetText("");
            xPill.Visibility = Visibility.Visible;
            xPill.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(Math.Max(20, slot.Value.Width / slotK - 6), ExpW - 6, TimeSpan.FromMilliseconds(190)) { EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut } });
            xPill.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
            win.ShowFromTaskbar();
        }

        public void Collapse()
        {
            if (!expanded) return;
            expanded = false;
            if (xPill == null || src == null) return;
            var a = new DoubleAnimation(Math.Max(20, slot.HasValue ? slot.Value.Width / slotK - 6 : 40), TimeSpan.FromMilliseconds(140)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            a.Completed += (s, e) =>
            {
                if (expanded || src == null) return;
                xPill.Visibility = Visibility.Collapsed;
                root.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
                lastRect = Rect.Empty; Place(slotTray, slot);
            };
            xPill.BeginAnimation(FrameworkElement.WidthProperty, a);
            xPill.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(140)));
        }

        // mirror what's typed into the panel, so it feels like typing straight into the taskbar
        public void SetText(string t)
        {
            if (xText == null) return;
            t = t ?? "";
            xPh.Visibility = t.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            xText.Text = t.Length > 34 ? "…" + t.Substring(t.Length - 33) : t;
        }

        UIElement BuildIcon()
        {
            hover = new SolidColorBrush(Colors.Transparent);
            root = new Grid { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) }; // invisible but clickable
            hl = new Border { CornerRadius = new CornerRadius(5), Margin = new Thickness(3, 5, 3, 5), Background = hover, HorizontalAlignment = HorizontalAlignment.Left };
            root.Children.Add(hl);
            hc = dark ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(14, 0, 0, 0);
            Color pc = dark ? Color.FromArgb(12, 255, 255, 255) : Color.FromArgb(8, 0, 0, 0);

            var fg = new SolidColorBrush(dark ? Color.FromRgb(0xF2, 0xF2, 0xF6) : Color.FromRgb(0x18, 0x18, 0x1E));
            var sub = new SolidColorBrush(dark ? Color.FromRgb(0xA8, 0xA8, 0xB4) : Color.FromRgb(0x60, 0x60, 0x6A));
            xPill = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0),
                Background = new SolidColorBrush(dark ? Color.FromRgb(0x24, 0x24, 0x2B) : Color.FromRgb(0xFB, 0xFB, 0xFD)),
                BorderBrush = new LinearGradientBrush(Color.FromArgb(200, 0x4F, 0xB0, 0xFF), Color.FromArgb(200, 0xA4, 0x4B, 0xF5), 0),
                BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed, ClipToBounds = true, Width = 40, Height = 36, SnapsToDevicePixels = true
            };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new Image { Source = Logo.Image(64, true), Width = 18, Height = 18, Margin = new Thickness(11, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center });
            var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var font = new FontFamily("Segoe UI Variable Text, Segoe UI");
            xText = new TextBlock { FontSize = 13.5, Foreground = fg, FontFamily = font, VerticalAlignment = VerticalAlignment.Center };
            xCaret = new Border { Width = 1.5, Height = 17, Margin = new Thickness(1, 0, 0, 0), Background = new SolidColorBrush(dark ? Color.FromRgb(0x9C, 0xA4, 0xFF) : Color.FromRgb(0x5B, 0x5F, 0xF6)), VerticalAlignment = VerticalAlignment.Center };
            var blink = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1060), RepeatBehavior = RepeatBehavior.Forever };
            blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(530))));
            xCaret.BeginAnimation(UIElement.OpacityProperty, blink);
            xPh = new TextBlock { Text = "Type to search", FontSize = 13.5, Foreground = sub, FontFamily = font, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) };
            line.Children.Add(xText); line.Children.Add(xCaret); line.Children.Add(xPh);
            Grid.SetColumn(line, 1); g.Children.Add(line);
            xPill.Child = g;
            root.Children.Add(xPill);

            dwell = new DispatcherTimer(DispatcherPriority.Input, win.Dispatcher) { Interval = TimeSpan.FromMilliseconds(260) };
            dwell.Tick += (s, e) => { dwell.Stop(); if (root.IsMouseOver && !expanded && Mouse.LeftButton != MouseButtonState.Pressed) Expand(true); };
            root.MouseEnter += (s, e) =>
            {
                if (expanded) return;
                hover.Color = hc;
                if (armed && Mouse.LeftButton != MouseButtonState.Pressed && (DateTime.Now - placedAt).TotalMilliseconds > 500) { dwell.Stop(); dwell.Start(); }
            };
            root.MouseLeave += (s, e) => { if (dwell != null) dwell.Stop(); armed = true; if (!expanded) hover.Color = Colors.Transparent; };
            root.MouseLeftButtonDown += (s, e) => { if (!expanded) hover.Color = pc; };
            root.MouseLeftButtonUp += (s, e) => { if (!expanded) hover.Color = hc; };
            return root;
        }

        UIElement BuildBar()
        {
            var bg = dark ? Color.FromRgb(0x2A, 0x2A, 0x31) : Color.FromRgb(0xF7, 0xF7, 0xFA);
            var bgHover = dark ? Color.FromRgb(0x34, 0x34, 0x3D) : Color.FromRgb(0xFF, 0xFF, 0xFF);
            var edge = new LinearGradientBrush(Color.FromArgb(150, 0x4F, 0xB0, 0xFF), Color.FromArgb(150, 0xA4, 0x4B, 0xF5), 0);
            var fill = new SolidColorBrush(bg);
            root = new Grid { Background = Brushes.Transparent };
            pill = new Border { Background = fill, BorderBrush = edge, BorderThickness = new Thickness(1), SnapsToDevicePixels = true };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
            sp.Children.Add(new Image { Source = Logo.Image(64, true), Width = 20, Height = 20, Margin = new Thickness(0, 0, 9, 0) });
            sp.Children.Add(new TextBlock
            {
                Text = "Search", FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
                Foreground = new SolidColorBrush(dark ? Color.FromRgb(0xC8, 0xC8, 0xD2) : Color.FromRgb(0x50, 0x50, 0x5A))
            });
            pill.Child = sp;
            root.Children.Add(pill);
            root.MouseEnter += (s, e) => fill.Color = bgHover;
            root.MouseLeave += (s, e) => fill.Color = bg;

            return root;
        }
    }
}
