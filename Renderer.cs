using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace BlackHole
{
    public sealed class Renderer : IDisposable
    {
        public readonly LensTable L;
        public readonly int N;
        IntPtr screenDC, capDC, capBmp, outDC, outBmp, capBits, outBits, oldCap, oldOut;
        public int[] Cap, Out;

        const int TW = 720, TH = 192;
        const int RAMP = 1024;
        const float RAMPMAX = 3f;
        float[] tex = new float[TW * TH];
        float[] prof = new float[TH];
        float[] diskA = new float[TH];
        float[] shift = new float[TH];
        float[] rampR = new float[RAMP + 1], rampG = new float[RAMP + 1], rampB = new float[RAMP + 1];
        float[] sinLut = new float[1024];

        public float Time, Hunger, Flare;
        public bool HideCursor; public int HoleX, HoleY; public float HoleShadow; public IntPtr ArrowCursor; public int ArrowHot;

        public Renderer(LensTable lens)
        {
            L = lens; N = lens.N;
            screenDC = Native.GetDC(IntPtr.Zero);
            capDC = Native.CreateCompatibleDC(screenDC);
            outDC = Native.CreateCompatibleDC(screenDC);
            BITMAPINFOHEADER bi = new BITMAPINFOHEADER();
            bi.biSize = 40; bi.biWidth = N; bi.biHeight = -N; bi.biPlanes = 1; bi.biBitCount = 32;
            capBmp = Native.CreateDIBSection(screenDC, ref bi, 0, out capBits, IntPtr.Zero, 0);
            outBmp = Native.CreateDIBSection(screenDC, ref bi, 0, out outBits, IntPtr.Zero, 0);
            oldCap = Native.SelectObject(capDC, capBmp);
            oldOut = Native.SelectObject(outDC, outBmp);
            Cap = new int[N * N]; Out = new int[N * N];
            BuildTables();
        }

        public void Dispose()
        {
            Native.SelectObject(capDC, oldCap); Native.SelectObject(outDC, oldOut);
            Native.DeleteObject(capBmp); Native.DeleteObject(outBmp);
            Native.DeleteDC(capDC); Native.DeleteDC(outDC);
            Native.ReleaseDC(IntPtr.Zero, screenDC);
        }

        static float Clamp01(float x) { return x < 0 ? 0 : (x > 1 ? 1 : x); }
        static float Sm(float a, float b, float x) { float t = Clamp01((x - a) / (b - a)); return t * t * (3 - 2 * t); }

        static float[] Lattice(int cw, int ch, Random r)
        {
            float[] a = new float[cw * ch];
            for (int i = 0; i < a.Length; i++) a[i] = (float)r.NextDouble();
            return a;
        }

        static float Noise(float[] lat, int cw, int ch, float x, float y)
        {
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
            int xa = ((x0 % cw) + cw) % cw, xb = (xa + 1) % cw;
            int ya = ((y0 % ch) + ch) % ch, yb = (ya + 1) % ch;
            float a = lat[ya * cw + xa], b = lat[ya * cw + xb], c = lat[yb * cw + xa], d = lat[yb * cw + xb];
            return (a + (b - a) * fx) + ((c + (d - c) * fx) - (a + (b - a) * fx)) * fy;
        }

        void BuildTables()
        {
            Random rng = new Random(11);
            float[] l1 = Lattice(14, 48, rng), l2 = Lattice(28, 96, rng), l3 = Lattice(60, 192, rng);
            for (int v = 0; v < TH; v++)
            {
                float yy = v / (float)TH;
                for (int u = 0; u < TW; u++)
                {
                    float xx = u / (float)TW;
                    float n = 0.50f * Noise(l1, 14, 48, xx * 14, yy * 48)
                            + 0.30f * Noise(l2, 28, 96, xx * 28, yy * 96)
                            + 0.20f * Noise(l3, 60, 192, xx * 60, yy * 192);
                    float ridge = 1f - Math.Abs(2f * n - 1f);
                    float val = 0.55f * n + 0.45f * ridge * ridge;
                    tex[v * TW + u] = Clamp01(val * 1.35f - 0.12f);
                }
            }
            for (int v = 0; v < TH; v++)
            {
                float r = LensTable.RIn + (v / (float)(TH - 1)) * (LensTable.ROut - LensTable.RIn);
                float sIn = Sm(LensTable.RIn, LensTable.RIn + 0.9f, r);
                float sOut = 1f - Sm(LensTable.ROut - 5.5f, LensTable.ROut, r);
                prof[v] = (float)Math.Pow(LensTable.RIn / r, 1.4) * 1.7f * sIn * sOut;
                diskA[v] = 0.93f * sIn * sOut;
                float omega = 16f / (float)Math.Pow(r, 1.5);
                shift[v] = omega * TW / (2f * (float)Math.PI);
            }
            float[,] stops = new float[,] {
                { 0f, 0f, 0f, 0f }, { 0.12f, 0.30f, 0.03f, 0.0f }, { 0.35f, 0.80f, 0.22f, 0.03f },
                { 0.65f, 1.0f, 0.55f, 0.12f }, { 1.0f, 1.0f, 0.82f, 0.45f }, { 1.6f, 1.0f, 0.95f, 0.80f }, { 3.0f, 1f, 1f, 1f } };
            for (int i = 0; i <= RAMP; i++)
            {
                float x = i * RAMPMAX / RAMP;
                int k = 0;
                while (k < stops.GetLength(0) - 2 && x > stops[k + 1, 0]) k++;
                float t = Clamp01((x - stops[k, 0]) / (stops[k + 1, 0] - stops[k, 0]));
                rampR[i] = stops[k, 1] + (stops[k + 1, 1] - stops[k, 1]) * t;
                rampG[i] = stops[k, 2] + (stops[k + 1, 2] - stops[k, 2]) * t;
                rampB[i] = stops[k, 3] + (stops[k + 1, 3] - stops[k, 3]) * t;
            }
            for (int i = 0; i < 1024; i++) sinLut[i] = (float)Math.Sin(i * 2 * Math.PI / 1024);
        }

        public void Capture(int x0, int y0)
        {
            Native.BitBlt(capDC, 0, 0, N, N, screenDC, x0, y0, 0x00CC0020 | 0x40000000);
            // the system cursor is not part of a screen grab: draw it in so the lens bends it too
            PT cp; Native.GetCursorPos(out cp);
            if (HideCursor && ArrowCursor != IntPtr.Zero)
            {
                // our own cursor, being swallowed: swirls inward and shrinks near the hole
                double dxc = cp.X - HoleX, dyc = cp.Y - HoleY, d = Math.Sqrt(dxc * dxc + dyc * dyc);
                double zone = HoleShadow * 3.0;
                double w = Math.Max(0, 1 - d / zone), w2 = w * w;
                double sz = 1 - 1.05 * w2;
                if (sz > 0.08 && d > 1e-3)
                {
                    double nd = d * (1 - 0.5 * w2), ang = Math.Atan2(dyc, dxc) + 1.3 * w2;
                    double px = HoleX + nd * Math.Cos(ang), py = HoleY + nd * Math.Sin(ang);
                    int size = (int)(Native.GetSystemMetrics(13) * sz);
                    Native.DrawIconEx(capDC, (int)(px - x0 - ArrowHot * sz), (int)(py - y0 - ArrowHot * sz), ArrowCursor, size, size, 0, IntPtr.Zero, 3);
                }
            }
            else
            {
                CURSORINFO ci = new CURSORINFO(); ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
                if (Native.GetCursorInfo(ref ci) && (ci.flags & 1) != 0)
                {
                    ICONINFO ii;
                    if (Native.GetIconInfo(ci.hCursor, out ii))
                    {
                        Native.DrawIconEx(capDC, ci.pt.X - x0 - ii.xHot, ci.pt.Y - y0 - ii.yHot, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3);
                        if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
                        if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
                    }
                }
            }
            Marshal.Copy(capBits, Cap, 0, N * N);
        }

        public void Present(IntPtr hwnd, int x0, int y0)
        {
            Marshal.Copy(Out, 0, outBits, N * N);
            PT dst = new PT(); dst.X = x0; dst.Y = y0;
            SZ sz = new SZ(); sz.cx = N; sz.cy = N;
            PT src = new PT();
            BLENDFUNCTION bf = new BLENDFUNCTION(); bf.BlendOp = 0; bf.BlendFlags = 0; bf.SourceConstantAlpha = 255; bf.AlphaFormat = 1;
            Native.UpdateLayeredWindow(hwnd, screenDC, ref dst, ref sz, outDC, ref src, 0, ref bf, 2);
        }

        public void Render()
        {
            float t = Time, hung = Hunger, fl = Flare;
            float gain = 1f + 0.7f * hung + 0.9f * fl;
            float ringGain = 1f + 0.7f * hung + 2.2f * fl;
            float rpT = t * (2.2f + 2.2f * hung);
            float rpA = 1f + 1.6f * hung;
            int n = N;
            LensTable Lc = L;
            int[] cap = Cap, outp = Out;
            float[] texA = tex, profA = prof, dA = diskA, shA = shift, rR = rampR, rG = rampG, rB = rampB, sl = sinLut;
            float texScale = TW / (2f * (float)Math.PI);
            float rampScale = RAMP / RAMPMAX;
            float pulse = 0.9f + 0.1f * (float)Math.Sin(t * 2.7f);
            ParallelOptions po = new ParallelOptions(); po.MaxDegreeOfParallelism = 3;
            Parallel.For(0, n, po, delegate(int y)
            {
                int row = y * n;
                for (int x = 0; x < n; x++)
                {
                    int i = row + x;
                    float wa = Lc.alpha[i];
                    if (wa <= 0f) { outp[i] = 0; continue; }
                    float r = 0, g = 0, b = 0;
                    if (Lc.fell[i] == 0)
                    {
                        float ph = rpT - Lc.rph[i];
                        int pi = ((int)(ph * 162.97f)) & 1023; // 1024/2pi
                        float sv = sl[pi] * rpA;
                        float px = x + Lc.dx[i] + sv * Lc.rpx[i];
                        float py = y + Lc.dy[i] + sv * Lc.rpy[i];
                        if (px < 0) px = 0; else if (px > n - 1.001f) px = n - 1.001f;
                        if (py < 0) py = 0; else if (py > n - 1.001f) py = n - 1.001f;
                        int ix = (int)px, iy = (int)py;
                        float fx = px - ix, fy = py - iy;
                        int o = iy * n + ix;
                        int c00 = cap[o], c10 = cap[o + 1], c01 = cap[o + n], c11 = cap[o + n + 1];
                        float w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy), w01 = (1 - fx) * fy, w11 = fx * fy;
                        r = (((c00 >> 16) & 255) * w00 + ((c10 >> 16) & 255) * w10 + ((c01 >> 16) & 255) * w01 + ((c11 >> 16) & 255) * w11) * (1f / 255f);
                        g = (((c00 >> 8) & 255) * w00 + ((c10 >> 8) & 255) * w10 + ((c01 >> 8) & 255) * w01 + ((c11 >> 8) & 255) * w11) * (1f / 255f);
                        b = ((c00 & 255) * w00 + (c10 & 255) * w10 + (c01 & 255) * w01 + (c11 & 255) * w11) * (1f / 255f);
                    }
                    int nh = Lc.nh[i];
                    for (int k = nh - 1; k >= 0; k--)
                    {
                        int hk = i * 3 + k;
                        float tv = Lc.hitR[hk] * (TH - 1);
                        int v0 = (int)tv; if (v0 > TH - 2) v0 = TH - 2; if (v0 < 0) v0 = 0;
                        float fv = tv - v0;
                        float pu = Lc.hitPsi[hk] * texScale - shA[v0] * t;
                        pu = pu % TW; if (pu < 0) pu += TW;
                        int u0 = (int)pu; float fu = pu - u0; int u1 = u0 + 1; if (u1 >= TW) u1 = 0;
                        float ta = texA[v0 * TW + u0] + (texA[v0 * TW + u1] - texA[v0 * TW + u0]) * fu;
                        float tb = texA[(v0 + 1) * TW + u0] + (texA[(v0 + 1) * TW + u1] - texA[(v0 + 1) * TW + u0]) * fu;
                        float tx = ta + (tb - ta) * fv;
                        float pr = profA[v0] + (profA[v0 + 1] - profA[v0]) * fv;
                        float da = dA[v0] + (dA[v0 + 1] - dA[v0]) * fv;
                        float s = (0.22f + 0.95f * tx) * pr * Lc.hitG[hk] * gain * pulse;
                        int ri = (int)(s * rampScale); if (ri > RAMP) ri = RAMP; if (ri < 0) ri = 0;
                        float a = da * (0.5f + 0.5f * (s > 1f ? 1f : s));
                        r += (rR[ri] - r) * a; g += (rG[ri] - g) * a; b += (rB[ri] - b) * a;
                    }
                    float rg = Lc.ring[i];
                    if (rg > 0f)
                    {
                        float q = rg * ringGain * 1.15f;
                        r += q; g += q * 0.78f; b += q * 0.48f;
                    }
                    if (r > 1f) r = 1f; if (g > 1f) g = 1f; if (b > 1f) b = 1f;
                    int ia = (int)(wa * 255f + 0.5f);
                    int ir = (int)(r * wa * 255f + 0.5f), ig = (int)(g * wa * 255f + 0.5f), ib = (int)(b * wa * 255f + 0.5f);
                    outp[i] = (ia << 24) | (ir << 16) | (ig << 8) | ib;
                }
            });
        }
    }
}
