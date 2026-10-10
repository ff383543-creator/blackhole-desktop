using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace BlackHole
{
    // Swallows the taskbar: a snapshot of it is cut into tiles that spiral into the hole while the
    // real taskbar is hidden. The real taskbar comes back on Stop() (key P, quit, logoff, next start).
    public sealed class TaskbarFx
    {
        class Tile
        {
            public Bitmap Bmp; public float X, Y; public int W, H;
            public double Delay, Ang0, Rad0; public bool Done;
        }

        public static readonly string FlagPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlackHole", "taskbar_hidden.flag");

        OverlayForm form;
        IntPtr screenDC, memDC, dib, oldBmp, bits, tray;
        Bitmap surface; Graphics g;
        int sw, sh, hx, hy;
        float shadow;
        List<Tile> tiles = new List<Tile>();
        long startMs = -1, lastMs;
        bool overlayAlive, trayHidden;
        public bool Finished;

        const double Dur = 3.4;   // seconds each tile takes to fall in
        const double Stagger = 0.07; // seconds between tiles

        public static void ShowTaskbar()
        {
            try
            {
                IntPtr t = Native.FindWindow("Shell_TrayWnd", null);
                if (t != IntPtr.Zero) Native.ShowWindow(t, 5);
                IntPtr s = IntPtr.Zero;
                while ((s = Native.FindWindowEx(IntPtr.Zero, s, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) Native.ShowWindow(s, 5);
                if (File.Exists(FlagPath)) File.Delete(FlagPath);
            }
            catch (Exception) { }
        }

        public static void RestoreIfLeftHidden()
        {
            if (File.Exists(FlagPath)) ShowTaskbar();
        }

        public bool Start(int holeX, int holeY, float shadowPx)
        {
            tray = Native.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return false;
            RECT r;
            if (!Native.GetWindowRect(tray, out r)) return false;
            int tx = r.L, ty = r.T, tw = r.R - r.L, th = r.B - r.T;
            if (tw < 100 || th < 20) return false;
            hx = holeX; hy = holeY; shadow = shadowPx;
            sw = Native.GetSystemMetrics(0); sh = Native.GetSystemMetrics(1);

            Bitmap snap = new Bitmap(tw, th, PixelFormat.Format32bppPArgb);
            using (Graphics sg = Graphics.FromImage(snap)) sg.CopyFromScreen(tx, ty, 0, 0, new Size(tw, th));

            int cell = (int)(48 * (th / 72.0)); if (cell < 24) cell = 24;
            for (int x = 0; x < tw; x += cell)
            {
                int w = Math.Min(cell, tw - x);
                Tile t = new Tile();
                t.W = w; t.H = th; t.X = tx + x + w / 2f; t.Y = ty + th / 2f;
                t.Bmp = snap.Clone(new Rectangle(x, 0, w, th), PixelFormat.Format32bppPArgb);
                double dx = t.X - hx, dy = t.Y - hy;
                t.Rad0 = Math.Sqrt(dx * dx + dy * dy); t.Ang0 = Math.Atan2(dy, dx);
                tiles.Add(t);
            }
            snap.Dispose();
            tiles.Sort(delegate(Tile a, Tile b) { return a.Rad0.CompareTo(b.Rad0); });
            for (int i = 0; i < tiles.Count; i++) tiles[i].Delay = i * Stagger;

            form = new OverlayForm(true);
            form.Show();
            if (Environment.GetEnvironmentVariable("BH_NOAFFINITY") != "1") Native.SetWindowDisplayAffinity(form.Handle, 0x11);
            Native.SetWindowPos(form.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // TOPMOST

            screenDC = Native.GetDC(IntPtr.Zero);
            memDC = Native.CreateCompatibleDC(screenDC);
            BITMAPINFOHEADER bi = new BITMAPINFOHEADER();
            bi.biSize = 40; bi.biWidth = sw; bi.biHeight = -sh; bi.biPlanes = 1; bi.biBitCount = 32;
            dib = Native.CreateDIBSection(screenDC, ref bi, 0, out bits, IntPtr.Zero, 0);
            oldBmp = Native.SelectObject(memDC, dib);
            surface = new Bitmap(sw, sh, sw * 4, PixelFormat.Format32bppPArgb, bits);
            g = Graphics.FromImage(surface);
            g.InterpolationMode = InterpolationMode.Bilinear;
            overlayAlive = true;

            DrawFrame(0);   // identical to the real taskbar, so hiding it does not flicker
            Present();
            try { Directory.CreateDirectory(Path.GetDirectoryName(FlagPath)); File.WriteAllText(FlagPath, "hidden"); } catch (Exception) { }
            Native.ShowWindow(tray, 0);
            IntPtr s2 = IntPtr.Zero;
            while ((s2 = Native.FindWindowEx(IntPtr.Zero, s2, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) Native.ShowWindow(s2, 0);
            trayHidden = true;
            return true;
        }

        // returns the number of tiles swallowed during this tick
        public int Tick(long nowMs)
        {
            if (!overlayAlive) return 0;
            if (startMs < 0) startMs = nowMs;
            if (nowMs - lastMs < 33) return 0;
            lastMs = nowMs;
            int swallowed = DrawFrame((nowMs - startMs) / 1000.0);
            Present();
            bool all = true;
            foreach (Tile t in tiles) if (!t.Done) { all = false; break; }
            if (all) { Finished = true; DisposeOverlay(); }
            return swallowed;
        }

        int DrawFrame(double t)
        {
            int swallowed = 0;
            g.Clear(Color.Transparent);
            foreach (Tile tile in tiles)
            {
                if (tile.Done) continue;
                double p = (t - tile.Delay) / Dur;
                if (p <= 0)
                {
                    g.DrawImage(tile.Bmp, tile.X - tile.W / 2f, tile.Y - tile.H / 2f, tile.W, tile.H);
                    continue;
                }
                if (p > 1) p = 1;
                double e = p * p;
                double rad = tile.Rad0 * (1 - e);
                if (rad < shadow * 1.0) { tile.Done = true; swallowed++; continue; }
                double ang = tile.Ang0 + 1.9 * Math.PI * e;
                float px = (float)(hx + rad * Math.Cos(ang)), py = (float)(hy + rad * Math.Sin(ang));
                // spaghettification: align with the pull direction, stretch along it, squeeze across it
                double radial = ang + Math.PI;
                double rot = radial * Math.Min(1.0, e * 4);
                while (rot > Math.PI) rot -= 2 * Math.PI;
                while (rot < -Math.PI) rot += 2 * Math.PI;
                float stretch = (float)(1 + 3.2 * e * e);
                float squeeze = (float)Math.Max(0.06, Math.Pow(1 - e, 0.9));
                GraphicsState st = g.Save();
                g.TranslateTransform(px, py);
                g.RotateTransform((float)(rot * 180 / Math.PI));
                g.ScaleTransform(stretch, squeeze);
                g.DrawImage(tile.Bmp, -tile.W / 2f, -tile.H / 2f, tile.W, tile.H);
                g.Restore(st);
            }
            return swallowed;
        }

        void Present()
        {
            PT dst = new PT(); SZ size = new SZ(); size.cx = sw; size.cy = sh;
            PT src = new PT();
            BLENDFUNCTION bf = new BLENDFUNCTION(); bf.SourceConstantAlpha = 255; bf.AlphaFormat = 1;
            Native.UpdateLayeredWindow(form.Handle, screenDC, ref dst, ref size, memDC, ref src, 0, ref bf, 2);
        }

        void DisposeOverlay()
        {
            if (!overlayAlive) return;
            overlayAlive = false;
            try { g.Dispose(); surface.Dispose(); } catch (Exception) { }
            Native.SelectObject(memDC, oldBmp); Native.DeleteObject(dib); Native.DeleteDC(memDC);
            Native.ReleaseDC(IntPtr.Zero, screenDC);
            try { form.Close(); } catch (Exception) { }
            foreach (Tile t in tiles) t.Bmp.Dispose();
        }

        public void Stop()
        {
            DisposeOverlay();
            if (trayHidden) { ShowTaskbar(); trayHidden = false; }
        }
    }
}
