#if SMOKE
// Developer smoke test for the real overlay + libmpv path, without cameras: plays generated test patterns
// (mpv's lavfi input) in the grid and single views, captures the overlay pixels, checks click-through and
// teardown. Built by `.\build.ps1 smoke`; needs libmpv-2.dll next to build\smoke\smoke.exe.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CctvOverlay.Dev
{
    internal static class Smoke
    {
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Native.POINT p);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);

        sealed class Cb : IVideoCallbacks
        {
            public int Frames, Errors;
            public void FirstFrame(int slot, uint session) { Frames++; Overlay.SetState(slot, AppState.Live, null); Console.WriteLine("  slot {0}: first frame", slot); }
            public void Error(int slot, uint session, string msg) { Errors++; Overlay.SetState(slot, AppState.Error, msg); Console.WriteLine("  slot {0}: error {1}", slot, msg); }
            public void Ended(int slot, uint session) { Error(slot, session, "ended"); }
        }

        static int fails;
        static void Check(bool ok, string what) { Console.WriteLine((ok ? "ok   - " : "FAIL - ") + what); if (!ok) fails++; }

        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);

        /* BitBlt with CAPTUREBLT, so layered windows are included. */
        static Bitmap Capture(Rectangle r)
        {
            var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                IntPtr screen = Native.GetDC(IntPtr.Zero);
                IntPtr hdc = g.GetHdc();
                BitBlt(hdc, 0, 0, r.Width, r.Height, screen, r.Left, r.Top, 0x00CC0020 | 0x40000000);
                g.ReleaseHdc(hdc);
                Native.ReleaseDC(IntPtr.Zero, screen);
            }
            return bmp;
        }

        /* Fraction of sampled pixels that are clearly not black, and the number of distinct coarse colours. */
        static void Stats(Bitmap b, Rectangle area, out double lit, out int colours)
        {
            int n = 0, l = 0;
            var seen = new System.Collections.Generic.HashSet<int>();
            for (int y = area.Top; y < area.Bottom; y += 4)
                for (int x = area.Left; x < area.Right; x += 4)
                {
                    Color c = b.GetPixel(x, y);
                    n++;
                    if (c.R + c.G + c.B > 60) l++;
                    seen.Add((c.R >> 5) << 6 | (c.G >> 5) << 3 | (c.B >> 5));
                }
            lit = n > 0 ? (double)l / n : 0;
            colours = seen.Count;
        }

        static void Pump(int ms) { Loop.Pump(ms); }

        [STAThread]
        static int Main(string[] args)
        {
            string outDir = args.Length > 0 ? args[0] : ".";
            Log.Level = LogLevel.Debug;
            Loop.Init();
            string err;
            Check(Overlay.Init(out err), "overlay init (libmpv loads): " + (err ?? "ok"));
            if (err != null) return 1;

            string dir = Path.Combine(Path.GetTempPath(), "cctv-smoke");
            Directory.CreateDirectory(dir);
            string cfgPath = Path.Combine(dir, "config.ini");
            File.WriteAllText(cfgPath, "[general]\nmonitor = primary\n[overlay]\nwidth = 720\nheight = 405\nanchor = top-right\n" +
                                       "[camera.a]\nname = Front Door\nurl = rtsp://127.0.0.1:1/a\n[camera.b]\nname = Driveway\nurl = rtsp://127.0.0.1:1/b\n" +
                                       "[camera.c]\nname = Backyard\nurl = rtsp://127.0.0.1:1/c\n[camera.d]\nname = Garage\nurl = rtsp://127.0.0.1:1/d\n");
            Config cfg = Config.Load(cfgPath);
            string[] pats = { "testsrc2=size=1280x720:rate=25", "smptebars=size=1280x720:rate=25", "mandelbrot=size=640x360:rate=25",
                              "rgbtestsrc=size=1280x720:rate=25" };
            var cb = new Cb();

            /* ---- grid ---- */
            Overlay.Show(cfg, 4, new[] { "Front Door", "Driveway", "Backyard", "Garage" });
            Overlay.SetAudio(true, true);
            Rectangle r = Overlay.ShownBounds;
            Console.WriteLine("overlay at {0}", r);
            for (int i = 0; i < 4; i++)
            {
                string u = "av://lavfi:" + pats[i];
                uint s = Video.Start(i, cfg, false, slot => Encoding.UTF8.GetBytes(u), cb, out err);
                Check(s != 0, "start slot " + i + (err != null ? ": " + err : ""));
            }
            long end = Util.MonotonicMs() + 8000;
            while (cb.Frames < 4 && Util.MonotonicMs() < end) Pump(20);
            Check(cb.Frames == 4, "all 4 tiles report a first frame (" + cb.Frames + ")");
            Pump(1500);

            /* click-through: the window under the overlay centre must belong to another process */
            var centre = new Native.POINT(r.Left + r.Width / 2, r.Top + r.Height / 2);
            IntPtr under = GetAncestor(WindowFromPoint(centre), 2 /* GA_ROOT */);
            uint pid;
            GetWindowThreadProcessId(under, out pid);
            Check(pid != (uint)System.Diagnostics.Process.GetCurrentProcess().Id, "click-through: WindowFromPoint skips the overlay (pid " + pid + ")");
            Check(Native.GetForegroundWindow() == IntPtr.Zero || GetWindowThreadProcessIdOf(Native.GetForegroundWindow()) != (uint)System.Diagnostics.Process.GetCurrentProcess().Id,
                  "no focus: the foreground window is not ours");

            using (Bitmap b = Capture(r))
            {
                b.Save(Path.Combine(outDir, "smoke-grid.png"), ImageFormat.Png);
                double lit; int colours;
                for (int i = 0; i < 4; i++)
                {
                    int col = i % 2, row = i / 2;
                    var cell = new Rectangle(col * r.Width / 2 + 20, row * r.Height / 2 + 40, r.Width / 2 - 40, r.Height / 2 - 60);
                    Stats(b, cell, out lit, out colours);
                    Check(lit > 0.2 && colours > 4, string.Format("tile {0} shows video (lit {1:P0}, {2} colours)", i, lit, colours));
                }
            }

            /* ---- single view, audio icon ---- */
            Video.StopAll();
            Overlay.Show(cfg, 1, new[] { "Front Door" });
            Overlay.SetAudio(true, false);
            cb.Frames = 0;
            uint s1 = Video.Start(0, cfg, true, slot => Encoding.UTF8.GetBytes("av://lavfi:testsrc2=size=1920x1080:rate=30"), cb, out err);
            Check(s1 != 0, "single view start");
            end = Util.MonotonicMs() + 8000;
            while (cb.Frames < 1 && Util.MonotonicMs() < end) Pump(20);
            Check(cb.Frames == 1, "single view first frame");
            Pump(1500);
            using (Bitmap b = Capture(Overlay.ShownBounds))
            {
                b.Save(Path.Combine(outDir, "smoke-single.png"), ImageFormat.Png);
                double lit; int colours;
                Stats(b, new Rectangle(40, 60, b.Width - 80, b.Height - 120), out lit, out colours);
                Check(lit > 0.2 && colours > 4, string.Format("single view shows video (lit {0:P0}, {1} colours)", lit, colours));
            }

            /* ---- error path: unreachable RTSP ---- */
            Video.StopAll();
            cb.Errors = 0;
            uint s2 = Video.Start(0, cfg, false, slot => Encoding.UTF8.GetBytes("rtsp://127.0.0.1:1/none"), cb, out err);
            end = Util.MonotonicMs() + 10000;
            while (cb.Errors < 1 && Util.MonotonicMs() < end) Pump(20);
            Check(s2 != 0 && cb.Errors == 1, "unreachable stream reports an error");
            Pump(1000);

            /* ---- hide: everything released ---- */
            long t0 = Util.MonotonicMs();
            Overlay.Hide();
            Video.WaitForTeardown(5000);
            Check(Util.MonotonicMs() - t0 < 5000, "players shut down after hide (" + (Util.MonotonicMs() - t0) + " ms)");
            Check(Overlay.ShownBounds.IsEmpty, "overlay gone");
            Pump(500);
            Console.WriteLine();
            Console.WriteLine(fails == 0 ? "smoke passed" : fails + " smoke checks failed");
            return fails == 0 ? 0 : 1;
        }

        static uint GetWindowThreadProcessIdOf(IntPtr h)
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            return pid;
        }
    }
}
#endif
