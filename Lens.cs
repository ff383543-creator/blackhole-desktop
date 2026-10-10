using System;
using System.Threading.Tasks;

namespace BlackHole
{
    // Precomputed Schwarzschild ray tracing (M = 1). Camera is static, so every pixel's
    // deflection, disk crossings and photon-ring glow are computed once.
    public sealed class LensTable
    {
        public int N, R;
        public float Shadow;
        public const float RIn = 4.3f, ROut = 13.5f;

        public float[] dx, dy;          // lens displacement (already weighted)
        public float[] rpx, rpy, rph;   // ripple amplitude vector + radial phase
        public float[] alpha;           // window alpha
        public float[] ring;            // photon ring glow
        public byte[] fell;
        public byte[] nh;
        public float[] hitR, hitPsi, hitG; // 3 per pixel

        static float Smooth(float a, float b, float x)
        {
            float t = (x - a) / (b - a);
            if (t < 0) t = 0; if (t > 1) t = 1;
            return t * t * (3 - 2 * t);
        }

        public static LensTable Build(int R, float shadowPx)
        {
            LensTable L = new LensTable();
            int N = 2 * R;
            L.N = N; L.R = R; L.Shadow = shadowPx;
            int n = N * N;
            L.dx = new float[n]; L.dy = new float[n];
            L.rpx = new float[n]; L.rpy = new float[n]; L.rph = new float[n];
            L.alpha = new float[n]; L.ring = new float[n];
            L.fell = new byte[n]; L.nh = new byte[n];
            L.hitR = new float[n * 3]; L.hitPsi = new float[n * 3]; L.hitG = new float[n * 3];

            const double r0 = 22.0;
            double el = 10.0 * Math.PI / 180.0;
            double bc = 3.0 * Math.Sqrt(3.0);
            double sinAc = bc / r0 * Math.Sqrt(1.0 - 2.0 / r0);
            double tanAc = sinAc / Math.Sqrt(1 - sinAc * sinAc);
            double f = shadowPx / tanAc;
            double ce = Math.Cos(el), se = Math.Sin(el);
            double[] e1 = new double[] { ce, 0, se };
            double[] fwd = new double[] { -ce, 0, -se };
            double[] up = new double[] { -se, 0, ce };
            double u0 = 1.0 / r0;
            double sq0 = Math.Sqrt(1 - 2 * u0);
            const double h = 0.02;
            double phiMax = 5.0 * Math.PI;
            double uEsc = 1.0 / 400.0;
            double rin = RIn, rout = ROut;
            double uMin = 1.0 / rout, uMax = 1.0 / rin;
            double crossBase = Math.Atan2(-e1[2], 0.0); // overwritten per ray (e2z varies)
            float shadow = shadowPx;
            float ampRipple = 2.4f * shadowPx / 93f;

            Parallel.For(0, N, delegate(int y)
            {
                for (int x = 0; x < N; x++)
                {
                    int i = y * N + x;
                    double px = x + 0.5 - R, py = y + 0.5 - R;
                    float rho = (float)(Math.Sqrt(px * px + py * py) / R);
                    L.alpha[i] = 1f - Smooth(0.86f, 1.0f, rho);
                    float wd = 1f - Smooth(0.40f, 0.88f, rho);
                    float bump = Smooth(0.27f, 0.45f, rho) * (1f - Smooth(0.72f, 0.95f, rho));
                    if (rho > 1e-4f)
                    {
                        L.rpx[i] = (float)(px / (rho * R)) * ampRipple * bump;
                        L.rpy[i] = (float)(py / (rho * R)) * ampRipple * bump;
                    }
                    L.rph[i] = rho * 15f;
                    if (L.alpha[i] <= 0f) continue;

                    double tx = px / f, ty = -py / f;
                    double dxv = fwd[0] + tx * 0 + ty * up[0];
                    double dyv = tx;
                    double dzv = fwd[2] + ty * up[2];
                    double dn = Math.Sqrt(dxv * dxv + dyv * dyv + dzv * dzv);
                    dxv /= dn; dyv /= dn; dzv /= dn;
                    double cosA = dxv * fwd[0] + dzv * fwd[2];
                    double sinA = Math.Sqrt(Math.Max(0, 1 - cosA * cosA));
                    if (sinA < 1e-7) { L.fell[i] = 1; continue; }
                    // e2 = (d + cosA * e1) / sinA
                    double e2x = (dxv + cosA * e1[0]) / sinA;
                    double e2y = (dyv) / sinA;
                    double e2z = (dzv + cosA * e1[2]) / sinA;
                    double b = r0 * sinA / sq0;

                    double u = u0;
                    double v = Math.Sqrt(Math.Max(0, 1.0 / (b * b) - u0 * u0 * (1 - 2 * u0)));
                    double phi = 0;
                    double cross = Math.Atan2(-e1[2], e2z);
                    while (cross <= 1e-6) cross += Math.PI;
                    while (cross > Math.PI) cross -= Math.PI;
                    int nhit = 0;
                    bool escaped = false, fellIn = false;

                    while (phi < phiMax)
                    {
                        double k1u = v, k1v = -u + 3 * u * u;
                        double u2 = u + 0.5 * h * k1u, v2 = v + 0.5 * h * k1v;
                        double k2u = v2, k2v = -u2 + 3 * u2 * u2;
                        double u3 = u + 0.5 * h * k2u, v3 = v + 0.5 * h * k2v;
                        double k3u = v3, k3v = -u3 + 3 * u3 * u3;
                        double u4 = u + h * k3u, v4 = v + h * k3v;
                        double k4u = v4, k4v = -u4 + 3 * u4 * u4;
                        double un = u + h / 6 * (k1u + 2 * k2u + 2 * k3u + k4u);
                        double vn = v + h / 6 * (k1v + 2 * k2v + 2 * k3v + k4v);

                        if (un >= 0.5) { fellIn = true; break; }

                        while (nhit < 3 && phi + h >= cross)
                        {
                            double s = (cross - phi) / h;
                            double s2 = s * s, s3 = s2 * s;
                            double uc = (2 * s3 - 3 * s2 + 1) * u + (s3 - 2 * s2 + s) * h * v
                                      + (-2 * s3 + 3 * s2) * un + (s3 - s2) * h * vn;
                            double vc = v + (vn - v) * s;
                            if (uc >= uMin && uc <= uMax)
                            {
                                double pc = cross;
                                double cp = Math.Cos(pc), sp = Math.Sin(pc);
                                double Px = cp * e1[0] + sp * e2x, Py = sp * e2y, Pz = cp * e1[2] + sp * e2z;
                                double perx = -sp * e1[0] + cp * e2x, pery = cp * e2y, perz = -sp * e1[2] + cp * e2z;
                                double psi = Math.Atan2(Py, Px);
                                double sq = Math.Sqrt(Math.Max(1e-6, 1 - 2 * uc));
                                double trad = -vc / sq, ttan = uc;
                                double tn = Math.Sqrt(trad * trad + ttan * ttan);
                                // photon direction toward the observer = -traced tangent
                                double kx = (-trad * Px - ttan * perx) / tn;
                                double ky = (-trad * Py - ttan * pery) / tn;
                                double kz = (-trad * Pz - ttan * perz) / tn;
                                double rr = 1.0 / uc;
                                double vel = 1.0 / Math.Sqrt(Math.Max(0.5, rr - 2.0));
                                double plen = Math.Sqrt(Px * Px + Py * Py);
                                double vx = -Py / plen * vel, vy = Px / plen * vel;
                                double vk = vx * kx + vy * ky;
                                double gam = 1.0 / Math.Sqrt(1 - vel * vel);
                                double dop = 1.0 / (gam * (1 - vk));
                                double g = dop * sq / sq0;
                                double g3 = Math.Pow(g, 1.8);
                                if (g3 < 0.2) g3 = 0.2; if (g3 > 3.5) g3 = 3.5;
                                int k = i * 3 + nhit;
                                L.hitR[k] = (float)((rr - rin) / (rout - rin));
                                L.hitPsi[k] = (float)psi;
                                L.hitG[k] = (float)g3;
                                nhit++;
                            }
                            cross += Math.PI;
                        }

                        phi += h; u = un; v = vn;
                        if (v < 0 && u < uEsc) { escaped = true; break; }
                    }
                    L.nh[i] = (byte)nhit;
                    if (!escaped)
                    {
                        L.fell[i] = 1;
                        // trapped near photon sphere (phiMax reached) still counts as black
                        if (!fellIn && phi >= phiMax) L.ring[i] = 1f;
                        continue;
                    }

                    double th = phi + Math.Asin(Math.Min(1.0, b * u));
                    double ct = Math.Cos(th), st = Math.Sin(th);
                    double ox = ct * e1[0] + st * e2x, oy = st * e2y, oz = ct * e1[2] + st * e2z;
                    double dz = ox * fwd[0] + oz * fwd[2];
                    if (dz < 0.12) dz = 0.12;
                    double qx = oy / dz;
                    double qy = (ox * up[0] + oz * up[2]) / dz;
                    double sxp = qx * f + R;
                    double syp = -qy * f + R;
                    L.dx[i] = (float)((sxp - (x + 0.5)) * wd);
                    L.dy[i] = (float)((syp - (y + 0.5)) * wd);

                    float rg = (float)((phi - 1.5 * Math.PI) / (2.4 * Math.PI));
                    if (rg < 0) rg = 0; if (rg > 1) rg = 1;
                    L.ring[i] = rg * rg;
                }
            });
            return L;
        }
    }
}
