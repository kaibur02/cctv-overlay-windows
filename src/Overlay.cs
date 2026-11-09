#if !STUB
// The overlay: two borderless, always-on-top, click-through tool windows that never take focus.
//  - the video window holds the tile grid; each tile hosts one mpv video window;
//  - the HUD window (owned by it, so always directly above) is a per-pixel-alpha layered window with the camera
//    name chips, status dots, status lines and the speaker icon, drawn with GDI+ (no icon theme needed).
// Both have WS_EX_LAYERED | WS_EX_TRANSPARENT (mouse input passes to the window underneath),
// WS_EX_NOACTIVATE (no focus, keyboard stays where it is) and WS_EX_TOOLWINDOW (no taskbar/Alt+Tab entry).
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace CctvOverlay
{
    internal static class Overlay
    {
        const int MaxTiles = 4;

        sealed class OTile
        {
            public Panel Box;
            public Rectangle Rect;
            public string Name = "";
            public AppState State = AppState.Connecting;
            public string Status = "";
            public bool AudioTrack = true;
        }

        static bool inited;
        static VideoWindow win;
        static HudWindow hud;
        static bool showLabel, showStatus;
        static int ntiles;
        static readonly OTile[] T = { new OTile(), new OTile(), new OTile(), new OTile() };
        static bool audio, audioDimmed;
        static LoopTimer trimSrc, zorderSrc;
        static float scale = 1f;
        static bool destroying;

        /// <summary>Screen rectangle of the visible overlay (physical pixels); empty while hidden.</summary>
        public static Rectangle ShownBounds { get { return win != null && !win.IsDisposed ? win.Bounds : Rectangle.Empty; } }

        /// <summary>Raised when Windows asks a window of ours to close (taskkill, session end): the daemon quits.</summary>
        public static Action CloseRequested;

        /* ---------------- init ---------------- */

        public static bool Init(out string error)
        {
            error = null;
            if (inited)
                return true;
            if (!Environment.UserInteractive)
            {
                error = "no interactive desktop (the overlay must run in your Windows session, not as a service)";
                return false;
            }
            if (!Mpv.Available(out error))
                return false;
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (Exception) { } /* PMv2; also in the manifest */
            inited = true;
            Log.Debug("overlay: ready (Windows {0})", Environment.OSVersion.Version);
            return true;
        }

        /* ---------------- helpers ---------------- */

        public static Control TileHost(int slot)
        {
            return slot >= 0 && slot < ntiles && win != null && T[slot].Box != null ? T[slot].Box : null;
        }

        static void ForgetWidgets()
        {
            win = null;
            hud = null;
            for (int i = 0; i < MaxTiles; i++)
                T[i].Box = null;
            ntiles = 0;
            if (zorderSrc != null)
                zorderSrc.Remove();
            zorderSrc = null;
        }

        /// <summary>A window was destroyed behind our back (not by Hide()).</summary>
        static void OnLost()
        {
            if (destroying || win == null)
                return;
            Log.Warn("overlay: window destroyed by Windows or another program");
            HudWindow h = hud;
            ForgetWidgets();
            Video.SurfaceLost();
            if (h != null && !h.IsDisposed)
            {
                destroying = true;
                h.Dispose();
                destroying = false;
            }
        }

        static Screen FindScreen(string spec)
        {
            if (spec == "primary")
                return Screen.PrimaryScreen;
            if (spec != "focused")
            {
                string want = spec.StartsWith("\\\\.\\", StringComparison.Ordinal) ? spec.Substring(4) : spec;
                int n;
                if (int.TryParse(want, out n))
                    want = "DISPLAY" + n;
                foreach (Screen s in Screen.AllScreens)
                {
                    string dn = s.DeviceName.StartsWith("\\\\.\\", StringComparison.Ordinal) ? s.DeviceName.Substring(4) : s.DeviceName;
                    if (string.Equals(dn, want, StringComparison.OrdinalIgnoreCase))
                        return s;
                }
                Log.Warn("overlay: monitor '{0}' not found; using the focused monitor", spec);
            }
            /* "focused": the monitor of the window you are working in (else the one under the mouse). */
            IntPtr fg = Native.GetForegroundWindow();
            return fg != IntPtr.Zero ? Screen.FromHandle(fg) : Screen.FromPoint(Cursor.Position);
        }

        static int Px(float css)
        {
            return (int)Math.Round(css * scale);
        }

        /* ---------------- tiles ---------------- */

        /* (Re)build the tile grid inside the frame. The caller has stopped every video slot. */
        static void BuildTiles(int n, string[] labels)
        {
            win.SuspendLayout();
            var old = new System.Collections.Generic.List<Control>();
            foreach (Control c in win.Controls)
                old.Add(c);
            foreach (Control c in old)
                c.Dispose(); /* old tiles and padding cells; players have already parked their windows */
            for (int i = 0; i < MaxTiles; i++)
                T[i].Box = null;
            Size cs = win.ClientSize;
            int gap = n > 1 ? Px(2) : 0;
            int cw = n > 1 ? (cs.Width - gap) / 2 : cs.Width;
            int ch = n > 1 ? (cs.Height - gap) / 2 : cs.Height;
            win.BackColor = n > 1 ? Color.FromArgb(0x11, 0x11, 0x11) : Color.Black;
            for (int i = 0; i < n; i++)
            {
                /* 1 tile fills the frame; 2..4 tiles use a 2x2 layout (empty cells stay black) */
                int col = n > 1 ? i % 2 : 0, row = n > 1 ? i / 2 : 0;
                int x = col * (cw + gap), y = row * (ch + gap);
                int w = n > 1 && col == 1 ? cs.Width - x : cw;
                int h = n > 1 && row == 1 ? cs.Height - y : ch;
                var box = new Panel();
                box.BackColor = Color.Black;
                box.Enabled = false; /* nothing in the overlay takes input */
                box.Bounds = new Rectangle(x, y, w, h);
                win.Controls.Add(box);
                T[i].Box = box;
                T[i].Rect = box.Bounds;
                T[i].Name = labels[i] ?? "";
                T[i].State = AppState.Connecting;
                T[i].Status = "Connecting…";
                T[i].AudioTrack = true; /* until the engine says otherwise */
            }
            if (n == 2 || n == 3)
            { /* keep the 2x2 cell size: fill the remaining cells with black */
                for (int i = n; i < 4; i++)
                {
                    int col = i % 2, row = i / 2;
                    var pad = new Panel();
                    pad.BackColor = Color.Black;
                    pad.Enabled = false;
                    pad.Bounds = new Rectangle(col * (cw + gap), row * (ch + gap), col == 1 ? cs.Width - col * (cw + gap) : cw,
                                               row == 1 ? cs.Height - row * (ch + gap) : ch);
                    win.Controls.Add(pad);
                }
            }
            ntiles = n;
            win.ResumeLayout();
            foreach (Control c in win.Controls)
                c.CreateControl();
        }

        /* ---------------- interface ---------------- */

        public static void Show(Config cfg, int n, string[] labels)
        {
            n = Math.Max(1, Math.Min(n, MaxTiles));
            if (trimSrc != null)
            { /* cancel a pending trim from a recent hide */
                trimSrc.Remove();
                trimSrc = null;
            }
            if (win != null && !win.IsDisposed)
            {
                if (n != ntiles)
                    BuildTiles(n, labels);
                else
                {
                    for (int i = 0; i < n; i++)
                        T[i].Name = labels[i] ?? "";
                }
                Redraw();
                return;
            }
            if (!inited)
            {
                Log.Error("overlay: Show() before Init()");
                return;
            }

            showLabel = cfg.ShowLabel;
            showStatus = cfg.ShowStatus;

            Screen scr = FindScreen(cfg.Monitor);
            Rectangle wa = scr.WorkingArea;
            scale = Native.DpiAt(wa.Left + wa.Width / 2, wa.Top + wa.Height / 2) / 96f;
            int w = Px(cfg.Width), h = Px(cfg.Height);
            bool top = cfg.Anchor.StartsWith("top", StringComparison.Ordinal);
            bool left = cfg.Anchor.EndsWith("left", StringComparison.Ordinal);
            int x = left ? wa.Left + Px(cfg.MarginX) : wa.Right - Px(cfg.MarginX) - w;
            int y = top ? wa.Top + Px(cfg.MarginY) : wa.Bottom - Px(cfg.MarginY) - h;
            var bounds = new Rectangle(x, y, w, h);

            win = new VideoWindow(bounds);
            win.Lost += OnLost;
            BuildTiles(n, labels);
            IntPtr rgn = Native.CreateRoundRectRgn(0, 0, w + 1, h + 1, Px(16), Px(16)); /* 8 px radius */
            Native.SetWindowRgn(win.Handle, rgn, false); /* the window owns the region now */

            hud = new HudWindow(bounds);
            hud.Owner = win; /* owned by the video window: always directly above it */
            hud.Lost += OnLost;

            win.Show(); /* ShowWithoutActivation: no focus change */
            Native.SetWindowPos(win.Handle, Native.HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                                Native.SWP_NOACTIVATE);
            Redraw();
            hud.Show();
            Native.SetWindowPos(hud.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

            /* Layer "overlay": stay above other always-on-top windows (taskbar, other overlays) while visible. */
            if (cfg.Layer == "overlay")
            {
                zorderSrc = Loop.Timeout(1000, delegate
                {
                    if (win == null || win.IsDisposed)
                        return false;
                    Native.SetWindowPos(win.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                                        Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                    if (hud != null && !hud.IsDisposed)
                        Native.SetWindowPos(hud.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                                            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                    return true;
                });
            }
            Log.Debug("overlay: shown {0}x{1} at {2},{3} (scale {4}) tiles={5} anchor={6} monitor={7} layer={8}",
                      w, h, x, y, scale, n, cfg.Anchor, scr.DeviceName, cfg.Layer);
        }

        public static void Hide()
        {
            if (win == null)
                return;
            Video.StopAll(); /* players let go of their tile windows first */
            VideoWindow w = win;
            HudWindow hw = hud;
            ForgetWidgets();
            destroying = true;
            if (hw != null && !hw.IsDisposed)
                hw.Dispose();
            if (!w.IsDisposed)
                w.Dispose();
            destroying = false;
            if (trimSrc != null)
                trimSrc.Remove();
            trimSrc = Loop.Timeout(300, delegate
            {
                trimSrc = null;
                /* hand freed memory (mpv, GPU driver, GDI) back while the overlay is hidden */
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
                return false;
            });
            Log.Debug("overlay: hidden");
        }

        public static void Shutdown()
        {
            Hide();
        }

        public static void SetState(int tile, AppState state, string detail)
        {
            if (win == null || state == AppState.Hidden || tile >= ntiles)
                return;
            OTile t = T[tile];
            t.State = state;
            switch (state)
            {
                case AppState.Connecting: t.Status = "Connecting…"; break;
                case AppState.Reconnecting: t.Status = "Reconnecting…" + (detail != null ? " " + detail : ""); break;
                case AppState.Error: t.Status = "Error" + (detail != null ? ": " + detail : ""); break;
                default: t.Status = ""; break; /* live: no text */
            }
            Redraw();
        }

        public static void SetAudio(bool enabled, bool mutedView)
        {
            audio = enabled;
            audioDimmed = mutedView;
            Redraw();
        }

        public static void SetAudioTrack(int slot, bool present)
        {
            if (slot >= 0 && slot < MaxTiles && T[slot].AudioTrack != present)
            {
                T[slot].AudioTrack = present;
                Redraw();
            }
        }

        /* ---------------- HUD drawing ---------------- */

        static readonly Color DotGrey = Color.FromArgb(0x88, 0x88, 0x88);
        static readonly Color DotAmber = Color.FromArgb(0xf0, 0xa5, 0x00);
        static readonly Color DotGreen = Color.FromArgb(0x2e, 0xcc, 0x71);
        static readonly Color DotRed = Color.FromArgb(0xe7, 0x4c, 0x3c);

        static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static string Ellipsize(Graphics g, string s, Font f, float maxW, int maxChars)
        {
            if (s.Length > maxChars)
                s = s.Substring(0, maxChars - 1) + "…";
            if (g.MeasureString(s, f, PointF.Empty, StringFormat.GenericTypographic).Width <= maxW)
                return s;
            while (s.Length > 1)
            {
                s = s.Substring(0, s.Length - 2) + "…";
                if (g.MeasureString(s, f, PointF.Empty, StringFormat.GenericTypographic).Width <= maxW)
                    break;
            }
            return s;
        }

        static void DrawTile(Graphics g, OTile t, bool tiled)
        {
            Rectangle r = t.Rect;
            /* chip: status dot + camera name, top left */
            if (showLabel || showStatus)
            {
                float margin = Px(tiled ? 4 : 6), padX = Px(tiled ? 6 : 8), padY = Px(tiled ? 1 : 2);
                float dot = Px(tiled ? 7 : 9), gapW = Px(6);
                using (var f = new Font("Segoe UI Semibold", Px(tiled ? 10 : 12), FontStyle.Regular, GraphicsUnit.Pixel))
                {
                    float maxText = r.Width - 2 * margin - 2 * padX - (showStatus ? dot + gapW : 0);
                    string name = showLabel ? Ellipsize(g, t.Name, f, Math.Max(10, maxText), 32) : "";
                    SizeF ts = showLabel ? g.MeasureString(name, f, PointF.Empty, StringFormat.GenericTypographic) : SizeF.Empty;
                    float lineH = Math.Max(f.GetHeight(g), dot);
                    float w = padX * 2 + (showStatus ? dot : 0) + (showStatus && showLabel ? gapW : 0) + ts.Width;
                    var chip = new RectangleF(r.X + margin, r.Y + margin, w, lineH + padY * 2);
                    using (GraphicsPath p = RoundRect(chip, Px(6)))
                    using (var b = new SolidBrush(Color.FromArgb(140, 0, 0, 0)))
                        g.FillPath(b, p);
                    float cx = chip.X + padX;
                    if (showStatus)
                    {
                        Color dc = t.State == AppState.Live ? DotGreen :
                                   t.State == AppState.Error ? DotRed :
                                   t.State == AppState.Connecting || t.State == AppState.Reconnecting ? DotAmber : DotGrey;
                        using (var b = new SolidBrush(dc))
                            g.FillEllipse(b, cx, chip.Y + (chip.Height - dot) / 2, dot, dot);
                        cx += dot + gapW;
                    }
                    if (showLabel)
                        g.DrawString(name, f, Brushes.White, cx, chip.Y + (chip.Height - ts.Height) / 2, StringFormat.GenericTypographic);
                }
            }
            /* status line, bottom centre, while not live */
            if (showStatus && !string.IsNullOrEmpty(t.Status))
            {
                float margin = Px(tiled ? 4 : 8), padX = Px(tiled ? 6 : 8), padY = Px(tiled ? 1 : 2);
                using (var f = new Font("Segoe UI", Px(tiled ? 9 : 11), FontStyle.Regular, GraphicsUnit.Pixel))
                {
                    string s = Ellipsize(g, t.Status, f, Math.Max(10, r.Width - 2 * margin - 2 * padX), 48);
                    SizeF ts = g.MeasureString(s, f, PointF.Empty, StringFormat.GenericTypographic);
                    float hgt = f.GetHeight(g) + padY * 2;
                    var box = new RectangleF(r.X + (r.Width - ts.Width - 2 * padX) / 2, r.Bottom - margin - hgt, ts.Width + 2 * padX, hgt);
                    using (GraphicsPath p = RoundRect(box, Px(6)))
                    using (var b = new SolidBrush(Color.FromArgb(153, 0, 0, 0)))
                        g.FillPath(b, p);
                    g.DrawString(s, f, Brushes.White, box.X + padX, box.Y + (box.Height - ts.Height) / 2, StringFormat.GenericTypographic);
                }
            }
        }

        /* The speaker icon, drawn from paths (16x16 design units). */
        static void DrawSpeaker(Graphics g, Size frame)
        {
            float margin = Px(6), pad = Px(3), icon = Px(16);
            var box = new RectangleF(frame.Width - margin - icon - 2 * pad, margin, icon + 2 * pad, icon + 2 * pad);
            using (GraphicsPath p = RoundRect(box, Px(6)))
            using (var b = new SolidBrush(Color.FromArgb(140, 0, 0, 0)))
                g.FillPath(b, p);
            bool crossed = ntiles == 1 && !T[0].AudioTrack;
            int alpha = audioDimmed ? 115 : 255;
            GraphicsState st = g.Save();
            g.TranslateTransform(box.X + pad, box.Y + pad);
            g.ScaleTransform(icon / 16f, icon / 16f);
            using (var b = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255)))
            using (var pen = new Pen(Color.FromArgb(alpha, 255, 255, 255), 1.4f))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                /* body + cone */
                g.FillPolygon(b, new[] { new PointF(1.5f, 5.5f), new PointF(4.5f, 5.5f), new PointF(8.5f, 2f),
                                         new PointF(8.5f, 14f), new PointF(4.5f, 10.5f), new PointF(1.5f, 10.5f) });
                if (crossed || audioDimmed)
                { /* no audio track / grid view: an "x" instead of sound waves */
                    g.DrawLine(pen, 10.5f, 5.5f, 14.5f, 10.5f);
                    g.DrawLine(pen, 14.5f, 5.5f, 10.5f, 10.5f);
                }
                else
                {
                    g.DrawArc(pen, 8.5f - 3.2f, 8f - 3.2f, 6.4f, 6.4f, -45f, 90f);
                    g.DrawArc(pen, 8.5f - 6f, 8f - 6f, 12f, 12f, -56f, 112f);
                }
            }
            g.Restore(st);
        }

        static void Redraw()
        {
            if (hud == null || hud.IsDisposed || win == null)
                return;
            Size sz = hud.Size;
            using (var bmp = new Bitmap(Math.Max(1, sz.Width), Math.Max(1, sz.Height), PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    using (Region clip = new Region(RoundRect(new RectangleF(0, 0, sz.Width, sz.Height), Px(8))))
                    {
                        g.Clip = clip;
                        for (int i = 0; i < ntiles; i++)
                            DrawTile(g, T[i], ntiles > 1);
                        if (audio)
                            DrawSpeaker(g, sz);
                    }
                }
                hud.SetBitmap(bmp);
            }
        }

        /* ---------------- windows ---------------- */

        abstract class OverlayForm : Form
        {
            public event Action Lost;
            bool closingByUs;

            protected OverlayForm(Rectangle bounds)
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                ControlBox = false;
                Text = "cctv-overlay";
                Bounds = bounds;
                AutoScaleMode = AutoScaleMode.None;
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW |
                                  Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                    cp.Style |= Native.WS_CLIPCHILDREN;
                    return cp;
                }
            }

            protected override void WndProc(ref Message m)
            {
                switch (m.Msg)
                {
                    case Native.WM_NCHITTEST:
                        m.Result = new IntPtr(Native.HTTRANSPARENT);
                        return;
                    case Native.WM_MOUSEACTIVATE:
                        m.Result = new IntPtr(Native.MA_NOACTIVATE);
                        return;
                    case Native.WM_DPICHANGED:
                        m.Result = IntPtr.Zero; /* keep our own pixel geometry */
                        return;
                    case Native.WM_CLOSE:
                        if (!closingByUs)
                        {
                            if (CloseRequested != null)
                                CloseRequested();
                            return;
                        }
                        break;
                }
                base.WndProc(ref m);
            }

            protected override void Dispose(bool disposing)
            {
                closingByUs = true;
                base.Dispose(disposing);
            }

            protected override void OnHandleDestroyed(EventArgs e)
            {
                base.OnHandleDestroyed(e);
                if (!destroying && Lost != null)
                    Lost();
            }
        }

        sealed class VideoWindow : OverlayForm
        {
            public VideoWindow(Rectangle bounds) : base(bounds)
            {
                BackColor = Color.Black;
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                Native.SetLayeredWindowAttributes(Handle, 0, 255, Native.LWA_ALPHA);
            }
        }

        sealed class HudWindow : OverlayForm
        {
            public HudWindow(Rectangle bounds) : base(bounds)
            {
            }

            public void SetBitmap(Bitmap bmp)
            {
                IntPtr screen = Native.GetDC(IntPtr.Zero);
                IntPtr mem = Native.CreateCompatibleDC(screen);
                IntPtr hbmp = IntPtr.Zero, old = IntPtr.Zero;
                try
                {
                    hbmp = bmp.GetHbitmap(Color.FromArgb(0));
                    old = Native.SelectObject(mem, hbmp);
                    var size = new Native.SIZE(bmp.Width, bmp.Height);
                    var src = new Native.POINT(0, 0);
                    var dst = new Native.POINT(Left, Top);
                    var blend = new Native.BLENDFUNCTION
                    {
                        BlendOp = Native.AC_SRC_OVER, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = Native.AC_SRC_ALPHA
                    };
                    Native.UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, Native.ULW_ALPHA);
                }
                finally
                {
                    Native.ReleaseDC(IntPtr.Zero, screen);
                    if (hbmp != IntPtr.Zero)
                    {
                        Native.SelectObject(mem, old);
                        Native.DeleteObject(hbmp);
                    }
                    Native.DeleteDC(mem);
                }
            }
        }
    }
}
#endif
