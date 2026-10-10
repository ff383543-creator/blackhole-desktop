using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BlackHole
{
    public sealed class OverlayForm : Form
    {
        bool topmost;
        public OverlayForm() : this(false) { }
        public OverlayForm(bool topmost)
        {
            this.topmost = topmost;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Size = new Size(1, 1);
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00080000 | 0x00000020 | 0x00000080 | 0x08000000; // LAYERED|TRANSPARENT|TOOLWINDOW|NOACTIVATE
                if (topmost) cp.ExStyle |= 0x00000008;
                return cp;
            }
        }
    }

    public class Falling
    {
        public string Path; public IntPtr Pidl;
        public double Ang0, Rad0; public long T0;
        public int OrigX, OrigY;
        public double Spin;
    }

    public sealed class HoleApp : ApplicationContext
    {
        const string RecycleGuid = "{645FF040-5081-101B-9F08-00AA002F954E}";
        static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlackHole");

        OverlayForm form;
        LensTable lens; Renderer rend;
        System.Windows.Forms.Timer timer;
        NotifyIcon tray;
        DesktopView dv;
        Stopwatch clock = Stopwatch.StartNew();
        long lastFrameMs, lastZMs, lastOccMs;
        bool covered;

        int cx, cy; float scale = 0.7f; float dpi = 1f;
        float shadow;
        bool building;
        bool frozen; // debug: capture once

        // hook / drag state
        HookProc hookProc; IntPtr hook;
        bool leftDown, dragging, needSnapshot, upPending; long upAt;
        PT downPt, curPt; long lastDownTick; PT lastDownPt;
        Dictionary<string, PT> snap;
        List<Falling> falling = new List<Falling>();
        long lastTick;

        TaskbarFx fx; bool taskbarFx = true;
        bool drift, snapOff, snapWasOn, snapKnown, restorePending; long lastDriftMs;
        Dictionary<string, double[]> drifted = new Dictionary<string, double[]>();
        Dictionary<string, PT> origins = new Dictionary<string, PT>();
        HookProc kbProc; IntPtr kbHook;
        MenuItem miAuto;
        MenuItem[] miSize;

        public HoleApp()
        {
            Directory.CreateDirectory(DataDir);
            IntPtr dc = Native.GetDC(IntPtr.Zero);
            dpi = Native.GetDeviceCaps(dc, 88) / 96f;
            Native.ReleaseDC(IntPtr.Zero, dc);
            frozen = Environment.GetEnvironmentVariable("BH_FREEZE") == "1";

            form = new OverlayForm();
            form.Show();
            if (Environment.GetEnvironmentVariable("BH_NOAFFINITY") != "1")
                Log.Write("display affinity (exclude from capture): " + Native.SetWindowDisplayAffinity(form.Handle, 0x11));
            Native.SetWindowPos(form.Handle, Native.HWND_BOTTOM, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);

            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
            Native.RestoreSystemCursors();
            arrow = Native.CopyIcon(Native.LoadCursor(IntPtr.Zero, 32512));
            ICONINFO aii; if (Native.GetIconInfo(arrow, out aii)) arrowHot = aii.xHot;
            SetRecycleHidden(true);
            dv = DesktopView.Open();
            Log.Write("desktop view (icons via COM): " + (dv != null ? "ok" : "FAILED"));
            LoadSettings();
            Log.Write("dpi=" + dpi + " screen=" + Native.GetSystemMetrics(0) + "x" + Native.GetSystemMetrics(1) + " hole center=" + cx + "," + cy + " scale=" + scale);
            BuildTray();

            hookProc = new HookProc(MouseProc);
            hook = Native.SetWindowsHookEx(14, hookProc, Native.GetModuleHandle(null), 0);
            kbProc = new HookProc(KeyProc);
            kbHook = Native.SetWindowsHookEx(13, kbProc, Native.GetModuleHandle(null), 0);
            TaskbarFx.RestoreIfLeftHidden();
            AppDomain.CurrentDomain.ProcessExit += delegate { if (fx != null) TaskbarFx.ShowTaskbar(); };
            LoadOrigins();
            System.Windows.Forms.Timer keyTimer = new System.Windows.Forms.Timer();
            keyTimer.Interval = 40; keyTimer.Tick += delegate { PollKeys(); }; keyTimer.Start();
            restorePending = origins.Count > 0; Log.Write("leftover icon origins: " + origins.Count); // icons left displaced by a previous session (e.g. reboot)
            SystemEvents.SessionEnding += delegate { try { form.Invoke(new MethodInvoker(StopDrift)); } catch (Exception) { } };
            Log.Write("mouse hook: " + (hook != IntPtr.Zero ? "ok" : "FAILED"));

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 10;
            timer.Tick += OnTick;
            timer.Start();

            bool createdNew;
            EventWaitHandle quit = new EventWaitHandle(false, EventResetMode.AutoReset, "BlackHoleQuitEvent", out createdNew);
            ThreadPool.RegisterWaitForSingleObject(quit, delegate(object s, bool to)
            {
                try { form.BeginInvoke(new MethodInvoker(Quit)); } catch (Exception) { }
            }, null, -1, true);

            EventWaitHandle evSnap = new EventWaitHandle(false, EventResetMode.AutoReset, "BlackHoleSnap");
            EventWaitHandle evDrop = new EventWaitHandle(false, EventResetMode.AutoReset, "BlackHoleDrop");
            ThreadPool.RegisterWaitForSingleObject(evSnap, delegate(object st, bool to)
            {
                try { form.BeginInvoke(new MethodInvoker(delegate { needSnapshot = true; })); } catch (Exception) { }
            }, null, -1, false);
            ThreadPool.RegisterWaitForSingleObject(evDrop, delegate(object st, bool to)
            {
                try { form.BeginInvoke(new MethodInvoker(delegate { upPending = true; upAt = clock.ElapsedMilliseconds - 1000; })); } catch (Exception) { }
            }, null, -1, false);

            EventWaitHandle evSwallow = new EventWaitHandle(false, EventResetMode.AutoReset, "BlackHoleSwallow");
            EventWaitHandle evStop = new EventWaitHandle(false, EventResetMode.AutoReset, "BlackHoleStop");
            ThreadPool.RegisterWaitForSingleObject(evSwallow, delegate(object st, bool to)
            {
                try { form.BeginInvoke(new MethodInvoker(StartDrift)); } catch (Exception) { }
            }, null, -1, false);
            ThreadPool.RegisterWaitForSingleObject(evStop, delegate(object st, bool to)
            {
                try { form.BeginInvoke(new MethodInvoker(StopDrift)); } catch (Exception) { }
            }, null, -1, false);

            Rebuild();
        }

        // ---------- settings ----------
        string SettingsPath { get { return Path.Combine(DataDir, "settings.ini"); } }

        void LoadSettings()
        {
            int sw = Native.GetSystemMetrics(0), sh = Native.GetSystemMetrics(1);
            bool have = false;
            try
            {
                foreach (string line in File.ReadAllLines(SettingsPath))
                {
                    string[] kv = line.Split('=');
                    if (kv.Length != 2) continue;
                    if (kv[0] == "cx") cx = int.Parse(kv[1]);
                    else if (kv[0] == "cy") cy = int.Parse(kv[1]);
                    else if (kv[0] == "scale") scale = float.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture);
                }
                have = cx != 0 || cy != 0;
            }
            catch (Exception) { }
            if (!have)
            {
                // default: where the Recycle Bin icon used to be
                cx = sw - (int)(230 * dpi); cy = (int)(190 * dpi);
                try
                {
                    if (dv != null)
                        foreach (DesktopItem it in dv.Items())
                            if (it.Path == "::" + RecycleGuid) { cx = it.X + dv.Spacing.X / 2; cy = it.Y + dv.Spacing.Y / 2; }
                }
                catch (Exception) { }
                float m = 62f * dpi * 3.4f; // keep the whole lens window on screen
                cx = Math.Max((int)m, Math.Min(sw - (int)m, cx));
                cy = Math.Max((int)m, Math.Min(sh - (int)m, cy));
                SaveSettings();
            }
        }

        void SaveSettings()
        {
            try
            {
                File.WriteAllText(SettingsPath, "cx=" + cx + "\r\ncy=" + cy + "\r\nscale=" + scale.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n");
            }
            catch (Exception) { }
        }

        // ---------- recycle bin icon ----------
        public static void SetRecycleHidden(bool hidden)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel"))
                    k.SetValue(RecycleGuid, hidden ? 1 : 0, RegistryValueKind.DWord);
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\ClassicStartMenu"))
                    k.SetValue(RecycleGuid, hidden ? 1 : 0, RegistryValueKind.DWord);
            }
            catch (Exception) { }
            DesktopView d = DesktopView.Open();
            if (d != null) d.Refresh();
        }

        // ---------- lens (re)build ----------
        void Rebuild()
        {
            if (building) return;
            building = true;
            shadow = 62f * dpi * scale;
            int R = (int)(3.8f * shadow);
            float sh = shadow;
            ThreadPool.QueueUserWorkItem(delegate(object o)
            {
                Stopwatch lsw = Stopwatch.StartNew();
                LensTable t = LensTable.Build(R, sh);
                Log.Write("lens built: " + lsw.ElapsedMilliseconds + " ms, size " + t.N);
                try
                {
                    form.BeginInvoke(new MethodInvoker(delegate
                    {
                        Renderer old = rend;
                        rend = new Renderer(t);
                        lens = t;
                        if (old != null) old.Dispose();
                        building = false;
                        needCapture = true;
                    }));
                }
                catch (Exception) { }
            });
        }
        bool needCapture = true;
        IntPtr arrow; int arrowHot; bool cursorHidden;
        void SetCursorHidden(bool h)
        {
            if (h == cursorHidden) return;
            cursorHidden = h;
            if (h) Native.HideSystemCursors(); else Native.RestoreSystemCursors();
        }

        // ---------- tray ----------
        void BuildTray()
        {
            tray = new NotifyIcon();
            tray.Icon = MakeIcon();
            tray.Text = "Чёрная дыра";
            ContextMenu m = new ContextMenu();
            m.MenuItems.Add("Открыть корзину", delegate { OpenBin(); });
            m.MenuItems.Add("Очистить корзину...", delegate { Native.SHEmptyRecycleBin(IntPtr.Zero, null, 0); });
            m.MenuItems.Add("-");
            MenuItem size = new MenuItem("Размер");
            string[] names = new string[] { "Крошечная", "Маленькая", "Обычная" };
            float[] vals = new float[] { 0.5f, 0.7f, 1f };
            miSize = new MenuItem[3];
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                miSize[i] = new MenuItem(names[i], delegate
                {
                    scale = vals[idx]; SaveSettings(); UpdateSizeChecks(); Rebuild();
                });
                size.MenuItems.Add(miSize[i]);
            }
            m.MenuItems.Add(size);
            m.MenuItems.Add("Переместить в позицию курсора (через 3 сек)", delegate { MoveHere(); });
            miAuto = new MenuItem("Запускать вместе с Windows", delegate { ToggleAutostart(); });
            m.MenuItems.Add(miAuto);
            m.MenuItems.Add("Засосать весь рабочий стол (клавиша O)", delegate { StartDrift(); });
            m.MenuItems.Add("Остановить и вернуть всё на места (клавиша P)", delegate { StopDrift(); });
            m.MenuItems.Add("-");
            m.MenuItems.Add("Открыть журнал (для диагностики)", delegate { try { Process.Start("notepad.exe", Log.Path); } catch (Exception) { } });
            m.MenuItems.Add("-");
            m.MenuItems.Add("Выход (вернуть значок корзины)", delegate { Quit(); });
            m.Popup += delegate { miAuto.Checked = IsAutostart(); UpdateSizeChecks(); };
            tray.ContextMenu = m;
            tray.DoubleClick += delegate { OpenBin(); };
            tray.Visible = true;
            tray.ShowBalloonTip(4000, "Чёрная дыра запущена",
                "Она живёт на рабочем столе. Пока открыто любое окно, дыра замирает. Меню: значок в трее.", ToolTipIcon.Info);
            UpdateSizeChecks();
        }

        void UpdateSizeChecks()
        {
            if (miSize == null) return;
            miSize[0].Checked = Math.Abs(scale - 0.5f) < 0.01f;
            miSize[1].Checked = Math.Abs(scale - 0.7f) < 0.01f;
            miSize[2].Checked = Math.Abs(scale - 1f) < 0.01f;
        }

        static Icon MakeIcon()
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush glow = new SolidBrush(Color.FromArgb(255, 255, 150, 40)))
                    g.FillEllipse(glow, 1, 1, 30, 30);
                using (SolidBrush mid = new SolidBrush(Color.FromArgb(255, 200, 70, 10)))
                    g.FillEllipse(mid, 4, 4, 24, 24);
                using (SolidBrush core = new SolidBrush(Color.Black))
                    g.FillEllipse(core, 8, 8, 16, 16);
            }
            IntPtr h = bmp.GetHicon();
            return Icon.FromHandle(h);
        }

        static void OpenBin()
        {
            try { Process.Start("explorer.exe", "shell:RecycleBinFolder"); } catch (Exception) { }
        }

        void MoveHere()
        {
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 3000;
            t.Tick += delegate
            {
                t.Stop(); t.Dispose();
                PT p; Native.GetCursorPos(out p);
                cx = p.X; cy = p.Y; SaveSettings(); needCapture = true;
            };
            t.Start();
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static bool IsAutostart()
        {
            try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("BlackHole") != null; }
            catch (Exception) { return false; }
        }
        static void ToggleAutostart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k.GetValue("BlackHole") != null) k.DeleteValue("BlackHole");
                    else k.SetValue("BlackHole", "\"" + Application.ExecutablePath + "\" --autostart");
                }
            }
            catch (Exception) { }
        }

        void Quit()
        {
            try { timer.Stop(); } catch (Exception) { }
            // put back anything still falling
            foreach (Falling f in falling) { try { if (dv != null) dv.SetPosition(f.Pidl, f.OrigX, f.OrigY); } catch (Exception) { } }
            try { StopDrift(); } catch (Exception) { }
            SetCursorHidden(false);
            if (kbHook != IntPtr.Zero) Native.UnhookWindowsHookEx(kbHook);
            if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
            tray.Visible = false; tray.Dispose();
            SetRecycleHidden(false);
            form.Close();
            ExitThread();
        }

        // ---------- mouse ----------
        IntPtr MouseProc(int code, IntPtr w, IntPtr l)
        {
            if (code >= 0)
            {
                int msg = (int)w;
                MSLLHOOKSTRUCT ms = (MSLLHOOKSTRUCT)System.Runtime.InteropServices.Marshal.PtrToStructure(l, typeof(MSLLHOOKSTRUCT));
                curPt = ms.pt;
                if (msg == 0x0201) // LBUTTONDOWN
                {
                    leftDown = true; dragging = false; downPt = ms.pt; snap = null; needSnapshot = false;
                    long now = Environment.TickCount;
                    double dxc = ms.pt.X - cx, dyc = ms.pt.Y - cy;
                    bool inCore = Math.Sqrt(dxc * dxc + dyc * dyc) < shadow * 1.4f;
                    if (inCore && now - lastDownTick <= Native.GetDoubleClickTime() &&
                        Math.Abs(ms.pt.X - lastDownPt.X) < 8 && Math.Abs(ms.pt.Y - lastDownPt.Y) < 8)
                    {
                        lastDownTick = 0;
                        try { form.BeginInvoke(new MethodInvoker(OpenBin)); } catch (Exception) { }
                    }
                    else { lastDownTick = now; lastDownPt = ms.pt; }
                }
                else if (msg == 0x0200) // MOVE
                {
                    if (leftDown && !dragging)
                    {
                        int ddx = ms.pt.X - downPt.X, ddy = ms.pt.Y - downPt.Y;
                        if (ddx * ddx + ddy * ddy > 144) { dragging = true; needSnapshot = true; }
                    }
                }
                else if (msg == 0x0202) // LBUTTONUP
                {
                    if (leftDown && dragging) { upPending = true; upAt = clock.ElapsedMilliseconds; }
                    leftDown = false; dragging = false;
                }
            }
            return Native.CallNextHookEx(hook, code, w, l);
        }

        Dictionary<string, PT> TakeSnapshot()
        {
            Dictionary<string, PT> d = new Dictionary<string, PT>();
            if (dv == null) return d;
            List<DesktopItem> items = dv.Items();
            foreach (DesktopItem it in items) { PT p = new PT(); p.X = it.X; p.Y = it.Y; d[it.Path] = p; }
            DesktopView.Free(items);
            return d;
        }

        void ProcessDrop()
        {
            if (dv == null) { dv = DesktopView.Open(); if (dv == null) return; }
            List<DesktopItem> items = dv.Items();
            bool auto = false;
            try { auto = dv.AutoArrange; } catch (Exception) { }
            double rPull = shadow * 2.4;
            List<DesktopItem> keep = new List<DesktopItem>();
            foreach (DesktopItem it in items)
            {
                bool take = false;
                if (!it.Path.StartsWith("::") && it.Path.Length > 3)
                {
                    PT old;
                    bool isNew = snap == null || !snap.TryGetValue(it.Path, out old);
                    bool moved = false;
                    if (!isNew) { old = snap[it.Path]; moved = Math.Abs(old.X - it.X) > 6 || Math.Abs(old.Y - it.Y) > 6; }
                    if (isNew || moved)
                    {
                        double ccx = it.X + dv.Spacing.X / 2.0, ccy = it.Y + dv.Spacing.Y / 2.0;
                        double d = Math.Sqrt((ccx - cx) * (ccx - cx) + (ccy - cy) * (ccy - cy));
                        if (d < rPull && !IsFalling(it.Path)) take = true;
                    }
                }
                if (take)
                {
                    Falling f = new Falling();
                    f.Path = it.Path; f.Pidl = it.Pidl; f.OrigX = it.X; f.OrigY = it.Y;
                    double sx = it.X + dv.Spacing.X / 2.0 - cx, sy = it.Y + dv.Spacing.Y / 2.0 - cy;
                    f.Ang0 = Math.Atan2(sy, sx); f.Rad0 = Math.Sqrt(sx * sx + sy * sy);
                    f.T0 = clock.ElapsedMilliseconds; f.Spin = 2.6 * Math.PI;
                    if (auto) { f.T0 -= 5000; } // can't animate: finish at once
                    falling.Add(f);
                    keep.Add(it);
                }
                else if (it.Pidl != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeCoTaskMem(it.Pidl);
            }
        }

        bool IsFalling(string p)
        {
            foreach (Falling f in falling) if (f.Path == p) return true;
            return false;
        }

        const double FallMs = 1250;

        void UpdateFalling()
        {
            long now = clock.ElapsedMilliseconds;
            for (int i = falling.Count - 1; i >= 0; i--)
            {
                Falling f = falling[i];
                double p = (now - f.T0) / FallMs;
                if (p < 1)
                {
                    if (p < 0) p = 0;
                    double e = p * p * (0.35 + 0.65 * p);
                    double rad = f.Rad0 * (1 - e);
                    double ang = f.Ang0 + f.Spin * e;
                    double ccx = cx + rad * Math.Cos(ang), ccy = cy + rad * Math.Sin(ang);
                    try { dv.SetPosition(f.Pidl, (int)(ccx - dv.Spacing.X / 2.0), (int)(ccy - dv.Spacing.Y / 2.0)); } catch (Exception) { }
                }
                else
                {
                    bool ok = Native.RecycleFile(f.Path);
                    if (ok) { if (rend != null) rend.Flare = 1f; }
                    else { try { dv.SetPosition(f.Pidl, f.OrigX, f.OrigY); } catch (Exception) { } }
                    System.Runtime.InteropServices.Marshal.FreeCoTaskMem(f.Pidl);
                    falling.RemoveAt(i);
                }
            }
        }

        // ---------- swallow the whole desktop (icons drift in) ----------
        string OriginsPath { get { return Path.Combine(DataDir, "origins.txt"); } }

        void LoadOrigins()
        {
            try
            {
                foreach (string line in File.ReadAllLines(OriginsPath, Encoding.UTF8))
                {
                    if (line.StartsWith("#snap=")) { snapWasOn = line.EndsWith("1"); snapKnown = true; continue; }
                    string[] q = line.Split('|');
                    if (q.Length != 3) continue;
                    PT pt = new PT(); pt.X = int.Parse(q[1]); pt.Y = int.Parse(q[2]);
                    origins[q[0]] = pt;
                }
            }
            catch (Exception) { }
        }

        void SaveOrigins()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("#snap=" + (snapWasOn ? "1" : "0") + "\r\n");
                foreach (KeyValuePair<string, PT> kv in origins) sb.Append(kv.Key + "|" + kv.Value.X + "|" + kv.Value.Y + "\r\n");
                File.WriteAllText(OriginsPath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception) { }
        }

        void SetSnap(bool off)
        {
            IntPtr lv = Native.FindDesktopListView();
            if (lv == IntPtr.Zero) return;
            if (off && !snapOff)
            {
                if (!snapKnown)
                {
                    long st = (long)Native.SendMessage(lv, 0x1037, IntPtr.Zero, IntPtr.Zero);
                    snapWasOn = (st & 0x00080000) != 0; snapKnown = true;
                }
                Native.SendMessage(lv, 0x1036, new IntPtr(0x00080000), IntPtr.Zero);
                snapOff = true;
            }
            else if (!off && snapOff)
            {
                if (snapWasOn) Native.SendMessage(lv, 0x1036, new IntPtr(0x00080000), new IntPtr(0x00080000));
                snapOff = false;
            }
            if (!off) snapKnown = false;
        }

        void StartDrift()
        {
            if (drift) return;
            drift = true; lastDriftMs = 0;
            if (taskbarFx && fx == null)
            {
                try { TaskbarFx f = new TaskbarFx(); if (f.Start(cx, cy, shadow)) { fx = f; Log.Write("taskbar swallow started"); } }
                catch (Exception ex) { Log.Write("taskbar fx failed: " + ex); TaskbarFx.ShowTaskbar(); }
            }
            Log.Write("swallow started");
        }

        void StopDrift()
        {
            if (!drift && origins.Count == 0 && fx == null) return;
            drift = false;
            if (driftCache != null) { DesktopView.Free(driftCache); driftCache = null; }
            if (fx != null) { try { fx.Stop(); } catch (Exception) { TaskbarFx.ShowTaskbar(); } fx = null; Log.Write("taskbar restored"); }
            try { RestoreOrigins(); } catch (Exception) { dv = null; }
            if (rend != null) rend.Flare = 0.7f;
            Log.Write("swallow stopped, icons restored");
        }

        // returns false when the desktop is not ready yet (no icons listed) so the caller can retry
        bool RestoreOrigins()
        {
            if (dv == null) dv = DesktopView.Open();
            if (dv == null) return false;
            List<DesktopItem> items = dv.Items();
            if (items.Count == 0 && origins.Count > 0) return false;
            SetSnap(true);
            List<IntPtr> ps = new List<IntPtr>(); List<PT> pts = new List<PT>();
            foreach (DesktopItem it in items)
            {
                PT o;
                if (origins.TryGetValue(it.Path, out o)) { ps.Add(it.Pidl); pts.Add(o); }
            }
            int rc = dv.SetPositions(ps, pts);
            Log.Write("restore: " + ps.Count + " of " + origins.Count + " icons put back, hr=0x" + rc.ToString("X"));
            DesktopView.Free(items);
            SetSnap(false);
            origins.Clear(); drifted.Clear();
            try { File.Delete(OriginsPath); } catch (Exception) { }
            return true;
        }

        int restoreTries; long lastRestoreMs;

        void DriftStep(long now)
        {
            if (!drift || dv == null || falling.Count > 0 || leftDown || upPending || rend == null) return;
            if (now - lastDriftMs < 40) return; // ~25 moves per second: smooth
            double dts = (now - lastDriftMs) / 1000.0; lastDriftMs = now;
            if (dts > 0.25) dts = 0.04;
            if (dv.AutoArrange) return;
            SetSnap(true);
            double speed = 30.0 * dpi, rmin = shadow * 1.9;

            // listing the desktop is the slow part: refresh it every 2 s, in between move the cached items
            if (driftCache == null || now - driftCacheMs > 2000)
            {
                if (driftCache != null) DesktopView.Free(driftCache);
                driftCache = dv.Items(); driftCacheMs = now;
                bool newOrigin = false;
                foreach (DesktopItem it in driftCache)
                {
                    if (it.Path.StartsWith("::") || it.Path.Length <= 3) continue;
                    double[] last;
                    if (drifted.TryGetValue(it.Path, out last) && Math.Abs(last[0] - it.X) <= 6 && Math.Abs(last[1] - it.Y) <= 6) continue; // still ours
                    drifted[it.Path] = new double[] { it.X, it.Y }; // new item, or the user moved it
                    if (!origins.ContainsKey(it.Path)) { PT o = new PT(); o.X = it.X; o.Y = it.Y; origins[it.Path] = o; newOrigin = true; }
                }
                if (newOrigin) SaveOrigins(); // saved BEFORE moving so a crash or reboot can always restore
            }
            List<IntPtr> ps = new List<IntPtr>(); List<PT> pts = new List<PT>();
            foreach (DesktopItem it in driftCache)
            {
                double[] cur;
                if (!drifted.TryGetValue(it.Path, out cur)) continue;
                double dx = cx - (cur[0] + dv.Spacing.X / 2.0), dy = cy - (cur[1] + dv.Spacing.Y / 2.0);
                double r = Math.Sqrt(dx * dx + dy * dy);
                if (r <= rmin) continue;
                double step = speed * dts * (1 + Math.Min(2.0, shadow * 4 / r));
                double ux = dx / r, uy = dy / r;
                cur[0] += ux * step - uy * step * 0.35; cur[1] += uy * step + ux * step * 0.35;
                PT np = new PT(); np.X = (int)Math.Round(cur[0]); np.Y = (int)Math.Round(cur[1]);
                ps.Add(it.Pidl); pts.Add(np);
            }
            dv.SetPositions(ps, pts);
        }
        List<DesktopItem> driftCache; long driftCacheMs;

        IntPtr KeyProc(int code, IntPtr w, IntPtr l)
        {
            if (code >= 0 && ((int)w == 0x100 || (int)w == 0x104))
            {
                int vk = System.Runtime.InteropServices.Marshal.ReadInt32(l);
                if ((vk == 0x50 && (drift || origins.Count > 0)) || (vk == 0x4F && !drift))
                {
                    bool ctrl = Native.GetKeyState(0x11) < 0, alt = Native.GetKeyState(0x12) < 0;
                    bool onDesktop = !ctrl && !alt && IsDesktopClass(Native.GetForegroundWindow());
                    if (onDesktop || (ctrl && alt))
                        try { form.BeginInvoke(new MethodInvoker(vk == 0x50 ? (MethodInvoker)StopDrift : (MethodInvoker)StartDrift)); } catch (Exception) { }
                }
            }
            return Native.CallNextHookEx(kbHook, code, w, l);
        }

        // ---------- main loop ----------
        static bool IsDesktopClass(IntPtr h)
        {
            if (h == IntPtr.Zero) return false;
            IntPtr root = Native.GetAncestor(h, 2);
            if (root == IntPtr.Zero) root = h;
            StringBuilder sb = new StringBuilder(64);
            Native.GetClassName(root, sb, 64);
            string c = sb.ToString();
            return c == "Progman" || c == "WorkerW";
        }

        static string ClassOf(IntPtr h)
        {
            if (h == IntPtr.Zero) return "(none)";
            StringBuilder sb = new StringBuilder(64);
            Native.GetClassName(Native.GetAncestor(h, 2) == IntPtr.Zero ? h : Native.GetAncestor(h, 2), sb, 64);
            return sb.ToString();
        }

        static bool IsShellFocus(IntPtr h)
        {
            string c = ClassOf(h);
            return c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" || c == "XamlExplorerHostIslandWindow";
        }

        // keys O / P are polled (more reliable than a keyboard hook, which Windows drops if the app is busy)
        bool oDown, pDown;
        void PollKeys()
        {
            bool ctrl = (Native.GetAsyncKeyState(0x11) & 0x8000) != 0, alt = (Native.GetAsyncKeyState(0x12) & 0x8000) != 0;
            bool o = (Native.GetAsyncKeyState(0x4F) & 0x8000) != 0, pk = (Native.GetAsyncKeyState(0x50) & 0x8000) != 0;
            if ((o && !oDown) || (pk && !pDown))
            {
                IntPtr fg = Native.GetForegroundWindow();
                string fc = ClassOf(fg);
                bool ok = (ctrl && alt) || (!ctrl && !alt && (IsShellFocus(fg) || (!covered && fc != "Windows.UI.Core.CoreWindow" && fc != "ApplicationFrameWindow")));
                Log.Write("key " + (o && !oDown ? "O" : "P") + " foreground=" + ClassOf(fg) + " -> " + (ok ? "accepted" : "ignored"));
                if (ok) { if (o && !oDown) StartDrift(); else StopDrift(); }
            }
            oDown = o; pDown = pk;
        }

        // true when any ordinary app window overlaps the hole: then it is not drawn or animated at all
        bool IsCovered()
        {
            if (rend == null) return false;
            int R = rend.L.R;
            int l = cx - (int)(R * 0.8), t = cy - (int)(R * 0.8), r = cx + (int)(R * 0.8), b = cy + (int)(R * 0.8);
            bool hit = false;
            IntPtr me = form.Handle;
            Native.EnumWindows(delegate(IntPtr h, IntPtr lp)
            {
                if (h == me || !Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
                int ex = Native.GetWindowLong(h, -20);
                if ((ex & (0x80 | 0x08000000 | 0x20)) != 0) return true; // toolwindow / noactivate / transparent
                int cloaked; Native.DwmGetWindowAttribute(h, 14, out cloaked, 4);
                if (cloaked != 0) return true;
                RECT rc; if (!Native.GetWindowRect(h, out rc)) return true;
                if (rc.R - rc.L < 60 || rc.B - rc.T < 60) return true;
                StringBuilder sb = new StringBuilder(64); Native.GetClassName(h, sb, 64);
                string c = sb.ToString();
                if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" || c == "Windows.UI.Core.CoreWindow") return true;
                hit = true; return false; // any open app window anywhere pauses the hole
                return true;
            }, IntPtr.Zero);
            return hit;
        }

        static float Sm(float a, float b, float x)
        {
            float t = (x - a) / (b - a); if (t < 0) t = 0; if (t > 1) t = 1; return t * t * (3 - 2 * t);
        }

        void OnTick(object s, EventArgs e)
        {
            long now = clock.ElapsedMilliseconds;
            float dt = (now - lastTick) / 1000f; lastTick = now;
            if (dt > 0.2f) dt = 0.2f;

            if (needSnapshot)
            {
                needSnapshot = false;
                try { if (dv == null) dv = DesktopView.Open(); snap = TakeSnapshot(); } catch (Exception) { dv = null; }
            }
            if (upPending && now - upAt > 380)
            {
                upPending = false;
                try { ProcessDrop(); } catch (Exception) { dv = null; }
            }
            if (restorePending && now > 3000 && now - lastRestoreMs > 1500)
            {
                // after a reboot the shell may still be filling the desktop: retry until icons are listed
                lastRestoreMs = now;
                try { if (RestoreOrigins() || ++restoreTries >= 40) restorePending = false; }
                catch (Exception ex) { Log.Write("restore failed: " + ex); dv = null; if (++restoreTries >= 40) restorePending = false; }
            }
            if (fx != null && !fx.Finished) { int sw2 = fx.Tick(now); if (sw2 > 0 && rend != null) rend.Flare = Math.Max(rend.Flare, 0.35f); }
            if (falling.Count > 0) UpdateFalling();
            try { DriftStep(now); } catch (Exception) { dv = null; }

            if (now - lastZMs > 700)
            {
                lastZMs = now;
                Native.SetWindowPos(form.Handle, Native.HWND_BOTTOM, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
                if (dv == null) dv = DesktopView.Open();
            }
            if (now - lastOccMs > 300) { lastOccMs = now; bool wasCovered = covered; covered = IsCovered(); if (covered != wasCovered) Log.Write(covered ? "paused: an app window is open" : "running: desktop is clear"); timer.Interval = (covered && !drift && (fx == null || fx.Finished)) ? 250 : 20; }

            if (rend == null) return;

            bool active = falling.Count > 0 || (leftDown && dragging) || rend.Hunger > 0.02f || rend.Flare > 0.02f;
            long interval = active ? 16 : 83; // ~12 fps when idle
            if (now - lastFrameMs < interval) return;
            float fdt = (now - lastFrameMs) / 1000f; lastFrameMs = now;
            if (fdt > 0.2f) fdt = 0.2f;

            float target = 0f;
            if (leftDown && dragging)
            {
                double d = Math.Sqrt((curPt.X - cx) * (double)(curPt.X - cx) + (curPt.Y - cy) * (double)(curPt.Y - cy));
                target = 1f - Sm(shadow * 1.4f, shadow * 4.2f, (float)d);
            }
            if (leftDown && dragging && target > 0.02f && falling.Count == 0)
            {
                double ddx = cx - curPt.X, ddy = cy - curPt.Y, dd = Math.Sqrt(ddx * ddx + ddy * ddy);
                if (dd > shadow * 0.25)
                {
                    double sp = 900.0 * target * target * fdt; // px per frame, grows as the hand gets closer
                    int nx = curPt.X + (int)Math.Round(ddx / dd * sp), ny = curPt.Y + (int)Math.Round(ddy / dd * sp);
                    Native.SetCursorPos(nx, ny); curPt.X = nx; curPt.Y = ny;
                }
            }
            rend.Hunger += (target - rend.Hunger) * Math.Min(1f, fdt * 6f);
            rend.Flare *= (float)Math.Exp(-fdt * 2.6f);
            rend.Time += fdt * (1f + 1.8f * rend.Hunger + 1.2f * rend.Flare);

            PT cpos; Native.GetCursorPos(out cpos);
            double cdd = Math.Sqrt((cpos.X - cx) * (double)(cpos.X - cx) + (cpos.Y - cy) * (double)(cpos.Y - cy));
            SetCursorHidden(!covered && cdd < shadow * 3.0);
            rend.HideCursor = cursorHidden; rend.HoleX = cx; rend.HoleY = cy; rend.HoleShadow = shadow;
            rend.ArrowCursor = arrow; rend.ArrowHot = arrowHot;
            // draw at least one frame even if windows are open; after that a covered hole stays frozen
            if (covered && firstFrameDone && Environment.GetEnvironmentVariable("BH_FORCE") != "1") return;
            if (!firstFrameDone) Log.Write("first frame drawn");
            firstFrameDone = true;

            int x0 = cx - rend.L.R, y0 = cy - rend.L.R;
            if (!frozen || needCapture) { rend.Capture(x0, y0); needCapture = false; }
            rend.Render();
            rend.Present(form.Handle, x0, y0);

            string selfcap = Environment.GetEnvironmentVariable("BH_SELFCAP");
            if (selfcap != null && !selfcapDone && now > 4000)
            {
                selfcapDone = true;
                int n = rend.N;
                Bitmap bmp = new Bitmap(n, n, PixelFormat.Format32bppArgb);
                BitmapData bd = bmp.LockBits(new Rectangle(0, 0, n, n), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                int[] tmp = new int[n * n]; for (int q = 0; q < tmp.Length; q++) tmp[q] = rend.Cap[q] | unchecked((int)0xFF000000);
                System.Runtime.InteropServices.Marshal.Copy(tmp, 0, bd.Scan0, n * n);
                bmp.UnlockBits(bd);
                bmp.Save(selfcap, ImageFormat.Png);
            }
        }
        bool selfcapDone, firstFrameDone;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }
    }

    public static class Log
    {
        public static readonly string Path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlackHole", "log.txt");
        public static void Write(string msg)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                FileInfo fi = new FileInfo(Path);
                if (fi.Exists && fi.Length > 200000) fi.Delete();
                File.AppendAllText(Path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + "\r\n");
            }
            catch (Exception) { }
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
            AppDomain.CurrentDomain.UnhandledException += delegate(object o, UnhandledExceptionEventArgs ea)
            {
                TaskbarFx.ShowTaskbar();
                Log.Write("CRASH: " + ea.ExceptionObject);
                MessageBox.Show("BlackHole crashed. Details: " + Log.Path + "\n\n" + ea.ExceptionObject, "BlackHole");
            };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object o, System.Threading.ThreadExceptionEventArgs ea) { Log.Write("ERROR: " + ea.Exception); };
            Log.Write("start " + Environment.OSVersion + " x64=" + Environment.Is64BitProcess + " args=" + string.Join(" ", args));
            List<string> a = new List<string>(args);
            if (a.Contains("--quit"))
            {
                try { EventWaitHandle.OpenExisting("BlackHoleQuitEvent").Set(); } catch (Exception) { }
                return 0;
            }
            if (a.Contains("--swallow")) { try { EventWaitHandle.OpenExisting("BlackHoleSwallow").Set(); } catch (Exception) { } return 0; }
            if (a.Contains("--stop")) { try { EventWaitHandle.OpenExisting("BlackHoleStop").Set(); } catch (Exception) { } return 0; }
            if (a.Contains("--snap")) { try { EventWaitHandle.OpenExisting("BlackHoleSnap").Set(); } catch (Exception) { } return 0; }
            if (a.Contains("--drop")) { try { EventWaitHandle.OpenExisting("BlackHoleDrop").Set(); } catch (Exception) { } return 0; }
            int pi = a.IndexOf("--place");
            if (pi >= 0)
            {
                DesktopView d = DesktopView.Open();
                foreach (DesktopItem it in d.Items())
                    if (it.Path.EndsWith(a[pi + 1], StringComparison.OrdinalIgnoreCase))
                        d.SetPosition(it.Pidl, int.Parse(a[pi + 2]), int.Parse(a[pi + 3]));
                return 0;
            }
            if (a.Contains("--list"))
            {
                DesktopView d = DesktopView.Open();
                foreach (DesktopItem it in d.Items()) Console.WriteLine(it.X + "," + it.Y + "  " + it.Path);
                return 0;
            }
            if (a.Contains("--restore")) { TaskbarFx.ShowTaskbar(); Native.RestoreSystemCursors(); HoleApp.SetRecycleHidden(false); return 0; }
            if (a.Contains("--hide")) { HoleApp.SetRecycleHidden(true); return 0; }
            int di = a.IndexOf("--dump");
            if (di >= 0) return Dump.Run(a, di);

            bool created;
            if (a.Contains("--autostart"))
            {
                // at logon the shell may not be ready yet: wait for the desktop window
                for (int i = 0; i < 120 && Native.FindWindow("Progman", null) == IntPtr.Zero; i++) Thread.Sleep(500);
                Thread.Sleep(3000);
            }
            Mutex m = new Mutex(true, "BlackHoleTrashSingleton", out created);
            if (!created)
            {
                // another copy is running (maybe an older build): ask it to quit and take over
                try { EventWaitHandle.OpenExisting("BlackHoleQuitEvent").Set(); } catch (Exception) { }
                bool got = false;
                try { got = m.WaitOne(6000); } catch (AbandonedMutexException) { got = true; }
                if (!got) return 0;
            }
            Application.EnableVisualStyles();
            Application.Run(new HoleApp());
            GC.KeepAlive(m);
            return 0;
        }
    }

    static class Dump
    {
        // BlackHole.exe --dump out.png [--grid] [--t 3.0] [--hunger 0.8] [--cx X --cy Y] [--scale 1]
        public static int Run(List<string> a, int di)
        {
            string path = a[di + 1];
            bool grid = a.Contains("--grid");
            float t = 3f, hunger = 0f, scale = 1f;
            int cx = Native.GetSystemMetrics(0) / 2, cy = Native.GetSystemMetrics(1) / 2;
            int i;
            if ((i = a.IndexOf("--t")) >= 0) t = float.Parse(a[i + 1], System.Globalization.CultureInfo.InvariantCulture);
            if ((i = a.IndexOf("--hunger")) >= 0) hunger = float.Parse(a[i + 1], System.Globalization.CultureInfo.InvariantCulture);
            if ((i = a.IndexOf("--scale")) >= 0) scale = float.Parse(a[i + 1], System.Globalization.CultureInfo.InvariantCulture);
            if ((i = a.IndexOf("--cx")) >= 0) cx = int.Parse(a[i + 1]);
            if ((i = a.IndexOf("--cy")) >= 0) cy = int.Parse(a[i + 1]);
            IntPtr dc = Native.GetDC(IntPtr.Zero);
            float dpi = Native.GetDeviceCaps(dc, 88) / 96f;
            Native.ReleaseDC(IntPtr.Zero, dc);
            float shadow = 62f * dpi * scale;
            int R = (int)(3.8f * shadow);
            Stopwatch sw = Stopwatch.StartNew();
            LensTable L = LensTable.Build(R, shadow);
            Console.WriteLine("lens built in " + sw.ElapsedMilliseconds + " ms, N=" + L.N);
            Renderer r = new Renderer(L);
            r.Capture(cx - R, cy - R);
            if (grid)
            {
                int N = L.N;
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                    {
                        int v = 40;
                        if (x % 40 < 2 || y % 40 < 2) v = 200;
                        if (((x / 40) + (y / 40)) % 2 == 0) v += 25;
                        int rr = v, gg = v, bb = v;
                        if (x % 200 < 3) { rr = 255; gg = 80; bb = 80; }
                        if (y % 200 < 3) { rr = 80; gg = 200; bb = 255; }
                        r.Cap[y * N + x] = (255 << 24) | (rr << 16) | (gg << 8) | bb;
                    }
            }
            r.Time = t; r.Hunger = hunger;
            sw.Restart();
            r.Render();
            Console.WriteLine("render " + sw.ElapsedMilliseconds + " ms");
            sw.Restart();
            for (int k = 0; k < 20; k++) r.Render();
            Console.WriteLine("avg render " + (sw.ElapsedMilliseconds / 20.0) + " ms");
            int n = L.N;
            Bitmap bmp = new Bitmap(n, n, PixelFormat.Format32bppArgb);
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, n, n), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            int[] outc = new int[n * n];
            for (int k = 0; k < n * n; k++)
            {
                int o = r.Out[k]; int c = r.Cap[k];
                int al = (o >> 24) & 255;
                int rr = ((o >> 16) & 255) + ((c >> 16) & 255) * (255 - al) / 255;
                int gg = ((o >> 8) & 255) + ((c >> 8) & 255) * (255 - al) / 255;
                int bb = (o & 255) + (c & 255) * (255 - al) / 255;
                outc[k] = (255 << 24) | (Math.Min(255, rr) << 16) | (Math.Min(255, gg) << 8) | Math.Min(255, bb);
            }
            System.Runtime.InteropServices.Marshal.Copy(outc, 0, bd.Scan0, n * n);
            bmp.UnlockBits(bd);
            bmp.Save(path, ImageFormat.Png);
            Console.WriteLine("saved " + path);
            return 0;
        }
    }
}



