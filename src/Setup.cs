// Entry point, tray icon, self-install, uninstall and auto-update from GitHub releases.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using WF = System.Windows.Forms;

namespace AllFind
{
    static class Program
    {
        [DllImport("user32")] static extern bool AllowSetForegroundWindow(int pid);
        public static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AllFind");
        public static readonly string InstallExe = Path.Combine(InstallDir, "AllFind.exe");
        public static string Exe;
        static Mutex mutex;

        // Main only touches mscorlib types, so a second launch (clicking the pinned icon) signals the running
        // copy within a few milliseconds without loading WPF.
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Contains("--uninstall")) { Setup.Uninstall(); return; }
            bool created;
            mutex = new Mutex(true, "AllFind_SingleInstance", out created);
            if (!created && args.Contains("--updated"))
            {
                // the old version is closing to make room for us: wait for it
                try { created = mutex.WaitOne(15000); } catch (AbandonedMutexException) { created = true; }
            }
            var showEvt = new EventWaitHandle(false, EventResetMode.AutoReset, "AllFind_Show");
            if (!created) { AllowSetForegroundWindow(-1); showEvt.Set(); return; } // let the running copy take focus
            Exe = Process.GetCurrentProcess().MainModule.FileName;
            // a downloaded copy installs itself (Start menu, Installed apps) and hands over to the installed one
            if (!args.Contains("--portable") && !string.Equals(Exe, InstallExe, StringComparison.OrdinalIgnoreCase) && Setup.Install())
            {
                mutex.ReleaseMutex(); mutex.Dispose();
                try { Process.Start(InstallExe); } catch { }
                return;
            }
            RunApp(args, showEvt);
            GC.KeepAlive(mutex);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void RunApp(string[] args, EventWaitHandle showEvt)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var win = new MainWin();
            var exe = Exe;
            bool installed = string.Equals(Exe, InstallExe, StringComparison.OrdinalIgnoreCase);
            if (installed) Setup.Register();
            var tray = new WF.NotifyIcon { Text = "AllFind", Visible = true };
            try { tray.Icon = new System.Drawing.Icon(System.Drawing.Icon.ExtractAssociatedIcon(exe), WF.SystemInformation.SmallIconSize); } catch { tray.Icon = System.Drawing.SystemIcons.Application; }
            var menu = new WF.ContextMenuStrip();
            menu.Items.Add(new WF.ToolStripMenuItem("AllFind " + Setup.VersionText) { Enabled = false });
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add("Open AllFind", null, (s, e) => win.ShowPopup());
            menu.Items.Add("Taskbar setup…", null, (s, e) => { win.ShowPopup(); win.ShowSetup(); });
            menu.Items.Add("Rebuild index", null, (s, e) => win.E.Rebuild(true));
            var startup = new WF.ToolStripMenuItem("Start with Windows") { Checked = IsStartup() };
            startup.Click += (s, e) => { SetStartup(!startup.Checked); startup.Checked = IsStartup(); };
            menu.Opening += (s, e) => startup.Checked = IsStartup();
            menu.Items.Add(startup);

            Action quit = () => { tray.Visible = false; app.Shutdown(); };
            Action<bool> check = manual => Updater.Check(win.Dispatcher, manual,
                msg => tray.ShowBalloonTip(4000, "AllFind", msg, WF.ToolTipIcon.Info),
                () => !win.IsVisible, quit);
            if (installed) menu.Items.Add("Check for updates", null, (s, e) => check(true));
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => quit());
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) win.ShowPopup(); };
            tray.Text = "AllFind" + (win.HotkeyLabel != "" ? " (" + win.HotkeyLabel + ")" : "");

            var t = new Thread(() => { while (true) { showEvt.WaitOne(); win.Dispatcher.BeginInvoke(new Action(win.Toggle)); } });
            t.IsBackground = true; t.Start();

            var timer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
            timer.Tick += (s, e) => { win.E.Rebuild(true); if (installed) check(false); };
            timer.Start();
            if (installed)
            {
                var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
                first.Tick += (s, e) => { first.Stop(); check(false); };
                first.Start();
            }
            if (args.Contains("--updated"))
                win.Dispatcher.BeginInvoke(new Action(() => tray.ShowBalloonTip(4000, "AllFind", "Updated to version " + Setup.VersionText + ".", WF.ToolTipIcon.Info)), DispatcherPriority.ApplicationIdle);

            if (!args.Contains("--background") && !args.Contains("--updated")) win.Dispatcher.BeginInvoke(new Action(win.ShowPopup));
            int qi = Array.IndexOf(args, "--query");
            if (qi >= 0 && qi + 1 < args.Length) { string q = args[qi + 1]; win.Dispatcher.BeginInvoke(new Action(() => win.SetQuery(q)), DispatcherPriority.ApplicationIdle); }
            app.Run();
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public static bool IsStartup()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("AllFind") != null;
        }
        public static void SetStartup(bool on)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (on) k.SetValue("AllFind", "\"" + Exe + "\" --background");
                else k.DeleteValue("AllFind", false);
            }
        }
    }

    static class Setup
    {
        const string UninstKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AllFind";
        static string Shortcut { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "AllFind.lnk"); } }
        public static Version Version { get { return typeof(Setup).Assembly.GetName().Version; } }
        public static string VersionText { get { var v = Version; return v.Major + "." + v.Minor + "." + v.Build; } }

        // copy this exe to %LOCALAPPDATA%\Programs\AllFind and add a Start menu entry (no admin rights needed)
        public static bool Install()
        {
            try
            {
                Directory.CreateDirectory(Program.InstallDir);
                File.Copy(Program.Exe, Program.InstallExe, true);
                MakeShortcut();
                return true;
            }
            catch (Exception ex)
            {
                WF.MessageBox.Show("AllFind couldn't install itself, so it will run from here instead.\n\n" + ex.Message, "AllFind");
                return false;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void MakeShortcut()
        {
            dynamic ws = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic lnk = ws.CreateShortcut(Shortcut);
            lnk.TargetPath = Program.InstallExe;
            lnk.WorkingDirectory = Program.InstallDir;
            lnk.Description = "Fast search for apps and files";
            lnk.Save();
        }

        // the entry in Settings > Apps > Installed apps, kept up to date by the installed copy
        public static void Register()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(UninstKey))
                {
                    k.SetValue("DisplayName", "AllFind");
                    k.SetValue("DisplayVersion", VersionText);
                    k.SetValue("DisplayIcon", Program.InstallExe);
                    k.SetValue("Publisher", "AllFind");
                    k.SetValue("InstallLocation", Program.InstallDir);
                    k.SetValue("UninstallString", "\"" + Program.InstallExe + "\" --uninstall");
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    k.SetValue("EstimatedSize", (int)(new FileInfo(Program.InstallExe).Length / 1024), RegistryValueKind.DWord);
                }
                if (!File.Exists(Shortcut)) MakeShortcut();
            }
            catch { }
        }

        public static void Uninstall()
        {
            if (WF.MessageBox.Show("Remove AllFind from this PC?\n\nWindows Search will be put back the way it was.", "Uninstall AllFind",
                WF.MessageBoxButtons.OKCancel, WF.MessageBoxIcon.Question) != WF.DialogResult.OK) return;
            int me = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcessesByName("AllFind"))
                try { if (p.Id != me) { p.Kill(); p.WaitForExit(3000); } } catch { }
            try { Taskbar.SetSearchMode(Taskbar.OriginalMode); } catch { }
            try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)) if (k != null) k.DeleteValue("AllFind", false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstKey, false); } catch { }
            try { File.Delete(Shortcut); } catch { }
            try { Directory.Delete(Engine.Dir, true); } catch { }
            // this exe is still running: delete the program folder a moment after we exit
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", "/c timeout /t 2 /nobreak >nul & rmdir /s /q \"" + Program.InstallDir + "\"")
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch { }
            WF.MessageBox.Show("AllFind has been removed. If you pinned it to the taskbar, right-click the icon and choose Unpin.", "AllFind");
        }
    }

    // Checks the latest GitHub release; when it's newer, downloads AllFind.exe next to the installed copy,
    // swaps the files (a running exe can be renamed, not overwritten) and restarts into the new version.
    static class Updater
    {
        const string Api = "https://api.github.com/repos/Shichibukai-21/AllFind/releases/latest";
        static int busy;

        public static void Check(Dispatcher ui, bool manual, Action<string> notify, Func<bool> idle, Action quit)
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string msg = null; bool restart = false;
                try
                {
                    try { File.Delete(Program.InstallExe + ".old"); } catch { }
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    string json;
                    using (var wc = new WebClient())
                    {
                        wc.Headers[HttpRequestHeader.UserAgent] = "AllFind-Updater";
                        wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                        json = wc.DownloadString(Api);
                    }
                    var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9.]+)\"");
                    var url = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"(https://github\\.com/[^\"]+/AllFind\\.exe)\"");
                    Version latest;
                    if (!tag.Success || !url.Success || !Version.TryParse(tag.Groups[1].Value, out latest)) { if (manual) msg = "Couldn't read the latest release."; }
                    else if (Norm(latest) <= Norm(Setup.Version)) { if (manual) msg = "You have the latest version (" + Setup.VersionText + ")."; }
                    else
                    {
                        string tmp = Program.InstallExe + ".new";
                        using (var wc = new WebClient()) { wc.Headers[HttpRequestHeader.UserAgent] = "AllFind-Updater"; wc.DownloadFile(url.Groups[1].Value, tmp); }
                        var head = new byte[2];
                        using (var f = File.OpenRead(tmp)) { if (f.Length < 100 * 1024 || f.Read(head, 0, 2) != 2 || head[0] != 'M' || head[1] != 'Z') throw new IOException("the download looks broken"); }
                        string old = Program.InstallExe + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(Program.InstallExe, old);
                        try { File.Move(tmp, Program.InstallExe); }
                        catch { File.Move(old, Program.InstallExe); throw; }
                        restart = true;
                    }
                }
                catch (Exception ex) { if (manual) msg = "Update check failed: " + ex.Message; }
                finally { Interlocked.Exchange(ref busy, 0); }
                var m = msg; var r = restart;
                ui.BeginInvoke(new Action(() =>
                {
                    if (m != null) notify(m);
                    if (r) RestartWhenIdle(ui, idle, quit);
                }));
            });
        }
        static Version Norm(Version v) { return new Version(v.Major, v.Minor, Math.Max(0, v.Build)); }

        // never yank the panel away mid-search: wait until it's closed
        static void RestartWhenIdle(Dispatcher ui, Func<bool> idle, Action quit)
        {
            var t = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(2) };
            t.Tick += (s, e) =>
            {
                if (!idle()) return;
                t.Stop();
                try { Process.Start(Program.InstallExe, "--background --updated"); } catch { return; }
                quit();
            };
            t.Start();
        }
    }
}
