#if DIRECTOR
// Records the demo media in docs\media (built by `build.ps1 demo`, run by tools\demo\record-media.ps1):
//   director usage   OUTDIR   full-screen backdrop + demo daemon driven through the keys -> frames + stills
//   director install OUTDIR REPO_COPY   the installer in its own console, typed and key-pressed -> frames
//   director encode  FRAMES OUT.mp4|OUT.gif FPS [WIDTH]
// Nothing personal is captured: the backdrop covers the whole screen (it shows this project's own source), the
// installer runs in a throwaway profile folder, and typing is refused unless the installer console is in front.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace CctvOverlay.Demo
{
    internal static class W32
    {
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int rop);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AttachConsole(int pid);
        [DllImport("kernel32.dll")] public static extern bool FreeConsole();
        [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] public static extern bool GetConsoleScreenBufferInfo(IntPtr h, out CSBI info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern bool ReadConsoleOutputCharacter(IntPtr h, StringBuilder buf, uint len, COORD at, out uint read);

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] public struct COORD { public short X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct SMALL_RECT { public short L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] public struct CSBI { public COORD Size, Cursor; public short Attr; public SMALL_RECT Window; public COORD Max; }
        [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort Vk, Scan; public uint Flags, Time; public IntPtr Extra; }
        [StructLayout(LayoutKind.Explicit)] public struct INPUT { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public KEYBDINPUT Ki; [FieldOffset(8)] public long Pad1; [FieldOffset(16)] public long Pad2; [FieldOffset(24)] public long Pad3; [FieldOffset(32)] public long Pad4; } /* 40 bytes, as on x64 */

        public static Bitmap Grab(Rectangle r)
        {
            var b = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppRgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                IntPtr s = GetDC(IntPtr.Zero), d = g.GetHdc();
                BitBlt(d, 0, 0, r.Width, r.Height, s, r.Left, r.Top, 0x00CC0020 | 0x40000000); /* SRCCOPY | CAPTUREBLT */
                g.ReleaseHdc(d);
                ReleaseDC(IntPtr.Zero, s);
            }
            return b;
        }

        /* Windows only lets the foreground process change the foreground window; a synthetic Alt tap is the
         * documented way for automation to get that right. */
        public static bool Front(IntPtr h)
        {
            for (int i = 0; i < 10; i++)
            {
                if (GetForegroundWindow() == h)
                    return true;
                keybd_event(0x12, 0, 0, UIntPtr.Zero);
                keybd_event(0x12, 0, 2, UIntPtr.Zero);
                SetForegroundWindow(h);
                Loop.Pump(100);
            }
            return GetForegroundWindow() == h;
        }
    }

    /* A full-screen, non-topmost "code editor" showing this project's source: the background of the demo. */
    internal sealed class Backdrop : Form
    {
        readonly string[] lines;
        static readonly string[] Keywords = { "using", "namespace", "internal", "sealed", "class", "static", "public", "private",
            "readonly", "const", "void", "bool", "int", "long", "uint", "string", "return", "if", "else", "for", "foreach",
            "while", "new", "null", "true", "false", "var", "switch", "case", "break", "default", "throw", "try", "catch",
            "this", "in", "out", "ref", "enum", "struct", "get", "set", "delegate" };
        static readonly Regex Tok = new Regex("(//.*$|/\\*.*?\\*/|\"(?:[^\"\\\\]|\\\\.)*\"|'(?:[^'\\\\]|\\\\.)'|\\b[A-Za-z_][A-Za-z0-9_]*\\b|\\d+)");

        public Backdrop(string file)
        {
            lines = File.ReadAllLines(file);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = Screen.PrimaryScreen.Bounds;
            BackColor = Color.FromArgb(30, 31, 34);
            DoubleBuffered = true;
            ShowInTaskbar = false;
            Text = "demo backdrop";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            float s = DeviceDpi / 96f;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            int tabH = (int)(34 * s), statusH = (int)(24 * s);
            using (var b = new SolidBrush(Color.FromArgb(24, 25, 28))) g.FillRectangle(b, 0, 0, Width, tabH);
            using (var b = new SolidBrush(Color.FromArgb(30, 31, 34))) g.FillRectangle(b, (int)(8 * s), (int)(6 * s), (int)(150 * s), tabH - (int)(6 * s));
            using (var b = new SolidBrush(Color.FromArgb(86, 156, 214))) g.FillRectangle(b, (int)(8 * s), tabH - (int)(2 * s), (int)(150 * s), (int)(2 * s));
            using (var tf = new Font("Segoe UI", 9.5f, FontStyle.Regular))
            {
                TextRenderer.DrawText(g, "App.cs", tf, new Point((int)(22 * s), (int)(13 * s)), Color.FromArgb(220, 220, 220));
                TextRenderer.DrawText(g, "Config.cs", tf, new Point((int)(180 * s), (int)(13 * s)), Color.FromArgb(140, 140, 140));
                TextRenderer.DrawText(g, "Overlay.cs", tf, new Point((int)(270 * s), (int)(13 * s)), Color.FromArgb(140, 140, 140));
            }
            using (var b = new SolidBrush(Color.FromArgb(0, 92, 160))) g.FillRectangle(b, 0, Height - statusH, Width, statusH);
            using (var sf = new Font("Segoe UI", 9f))
                TextRenderer.DrawText(g, "cctv-overlay  ·  src\\App.cs  ·  C#  ·  UTF-8  ·  Ln 166, Col 9", sf,
                                      new Point((int)(12 * s), Height - statusH + (int)(4 * s)), Color.White);

            using (var mono = new Font("Consolas", 11f))
            {
                int lh = (int)Math.Ceiling(mono.GetHeight(g) * 1.12f);
                int y = tabH + (int)(10 * s), first = 150;
                int gutter = (int)(60 * s);
                for (int i = first; i < lines.Length && y < Height - statusH - lh; i++, y += lh)
                {
                    TextRenderer.DrawText(g, (i + 1).ToString().PadLeft(4), mono, new Point((int)(4 * s), y), Color.FromArgb(110, 112, 118));
                    DrawCode(g, mono, lines[i].Replace("\t", "    "), gutter, y);
                }
            }
        }

        void DrawCode(Graphics g, Font f, string line, int x0, int y)
        {
            int pos = 0;
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            Func<string, int> width = t => TextRenderer.MeasureText(g, t, f, Size.Empty, flags).Width;
            foreach (Match m in Tok.Matches(line))
            {
                if (m.Index > pos)
                    TextRenderer.DrawText(g, line.Substring(pos, m.Index - pos), f, new Point(x0 + width(line.Substring(0, pos)), y), Color.FromArgb(212, 212, 212), flags);
                string t = m.Value;
                Color c = t.StartsWith("//") || t.StartsWith("/*") ? Color.FromArgb(106, 153, 85) :
                          t.StartsWith("\"") || t.StartsWith("'") ? Color.FromArgb(206, 145, 120) :
                          char.IsDigit(t[0]) ? Color.FromArgb(181, 206, 168) :
                          Array.IndexOf(Keywords, t) >= 0 ? Color.FromArgb(86, 156, 214) :
                          char.IsUpper(t[0]) ? Color.FromArgb(78, 201, 176) : Color.FromArgb(156, 220, 254);
                TextRenderer.DrawText(g, t, f, new Point(x0 + width(line.Substring(0, m.Index)), y), c, flags);
                pos = m.Index + m.Length;
            }
            if (pos < line.Length)
                TextRenderer.DrawText(g, line.Substring(pos), f, new Point(x0 + width(line.Substring(0, pos)), y), Color.FromArgb(212, 212, 212), flags);
        }
    }

    /* Captures a screen rectangle at a fixed frame rate to numbered PNGs, drawing the current caption. */
    internal sealed class Recorder
    {
        readonly Rectangle area;
        readonly string dir;
        readonly int fps;
        readonly Size outSize;
        Thread th;
        volatile bool stop;
        public volatile string[] Caption; /* key parts, then a description; null = none */
        public int Frames;

        public Recorder(Rectangle area, string dir, int fps, int outWidth)
        {
            this.area = area;
            this.dir = dir;
            this.fps = fps;
            outSize = new Size(outWidth, (int)Math.Round(area.Height * (double)outWidth / area.Width / 2) * 2);
            Directory.CreateDirectory(dir);
            foreach (string f in Directory.GetFiles(dir, "*.png"))
                File.Delete(f);
        }

        public void Start()
        {
            th = new Thread(Run);
            th.IsBackground = true;
            th.Start();
        }

        public void Stop()
        {
            stop = true;
            th.Join();
            using (var w = new StreamWriter(Path.Combine(dir, "frames.txt")))
                for (int i = 0; i < Frames; i++)
                    w.WriteLine(Path.Combine(dir, i.ToString("D5") + ".png"));
        }

        void Run()
        {
            var sw = Stopwatch.StartNew();
            string last = null;
            while (!stop)
            {
                using (Bitmap raw = W32.Grab(area))
                using (var frame = new Bitmap(outSize.Width, outSize.Height, PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(frame))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(raw, 0, 0, outSize.Width, outSize.Height);
                        string[] cap = Caption;
                        if (cap != null)
                            DrawCaption(g, cap, outSize);
                    }
                    /* one frame per 1/fps of wall time, repeating the last one if capture fell behind */
                    long due = (long)(sw.ElapsedMilliseconds * fps / 1000.0) + 1;
                    string path = Path.Combine(dir, Frames.ToString("D5") + ".png");
                    frame.Save(path, ImageFormat.Png);
                    last = path;
                    Frames++;
                    while (Frames < due)
                    {
                        File.Copy(last, Path.Combine(dir, Frames.ToString("D5") + ".png"));
                        Frames++;
                    }
                }
                long next = (long)(Frames * 1000.0 / fps);
                int wait = (int)(next - sw.ElapsedMilliseconds);
                if (wait > 0)
                    Thread.Sleep(wait);
            }
        }

        /* Bottom-centre pill: [Ctrl] + [Num *]   show / hide */
        static void DrawCaption(Graphics g, string[] cap, Size sz)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float u = sz.Width / 1280f;
            using (var keyFont = new Font("Segoe UI Semibold", 22 * u, GraphicsUnit.Pixel))
            using (var txtFont = new Font("Segoe UI", 22 * u, GraphicsUnit.Pixel))
            {
                var parts = new List<KeyValuePair<string, bool>>(); /* text, isKey */
                for (int i = 0; i < cap.Length - 1; i++)
                {
                    if (i > 0)
                        parts.Add(new KeyValuePair<string, bool>("+", false));
                    parts.Add(new KeyValuePair<string, bool>(cap[i], true));
                }
                float pad = 12 * u, gap = 8 * u, keyPadX = 12 * u, h = 44 * u;
                float total = pad * 2;
                var widths = new List<float>();
                foreach (var p in parts)
                {
                    float w = g.MeasureString(p.Key, p.Value ? keyFont : txtFont).Width + (p.Value ? keyPadX * 2 : 0);
                    widths.Add(w);
                    total += w + gap;
                }
                float descW = g.MeasureString(cap[cap.Length - 1], txtFont).Width;
                total += 10 * u + descW;
                float x = (sz.Width - total) / 2, y = sz.Height - h - 40 * u;
                using (GraphicsPath bg = Round(new RectangleF(x, y - 8 * u, total, h + 16 * u), 14 * u))
                using (var b = new SolidBrush(Color.FromArgb(225, 18, 18, 22)))
                    g.FillPath(b, bg);
                float cx = x + pad;
                for (int i = 0; i < parts.Count; i++)
                {
                    var p = parts[i];
                    if (p.Value)
                    {
                        using (GraphicsPath k = Round(new RectangleF(cx, y, widths[i], h), 8 * u))
                        using (var kb = new SolidBrush(Color.FromArgb(245, 245, 247)))
                        using (var pen = new Pen(Color.FromArgb(160, 160, 168), 2 * u))
                        {
                            g.FillPath(kb, k);
                            g.DrawPath(pen, k);
                        }
                        using (var tb = new SolidBrush(Color.FromArgb(25, 25, 30)))
                            g.DrawString(p.Key, keyFont, tb, cx + keyPadX - 3 * u, y + (h - keyFont.GetHeight(g)) / 2);
                    }
                    else
                    {
                        g.DrawString(p.Key, txtFont, Brushes.White, cx, y + (h - txtFont.GetHeight(g)) / 2);
                    }
                    cx += widths[i] + gap;
                }
                g.DrawString(cap[cap.Length - 1], txtFont, Brushes.White, cx + 10 * u - gap, y + (h - txtFont.GetHeight(g)) / 2);
            }
        }

        static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    internal static class Director
    {
        static StreamWriter log;

        static void Say(string m)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + m;
            if (log != null) { log.WriteLine(line); log.Flush(); }
        }

        const string Pipe = "cctv-overlay-demo";

        static void Cmd(string cmd, string arg = null)
        {
            var o = new JObj().Set("cmd", cmd);
            if (arg != null)
                o.Set(cmd == "audio" ? "mode" : "camera", arg);
            string reply, err;
            IpcResult r = IpcClient.Request(Pipe, Json.Write(o), out reply, out err);
            Say(cmd + " -> " + r + " " + (reply ?? err));
            if (r != IpcResult.Ok)
                throw new Exception("demo daemon: " + cmd + ": " + err);
        }

        static Rectangle OverlayRect()
        {
            /* the demo config: 720x405 logical, top-right, 24 px margins, primary display */
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            float s = Native.DpiAt(wa.Left + 10, wa.Top + 10) / 96f;
            int w = (int)Math.Round(720 * s), h = (int)Math.Round(405 * s), m = (int)Math.Round(24 * s);
            return new Rectangle(wa.Right - m - w, wa.Top + m, w, h);
        }

        static void Still(Rectangle r, string path, int width, int pad)
        {
            r.Inflate(pad, pad);
            r.Intersect(Screen.PrimaryScreen.Bounds);
            using (Bitmap b = W32.Grab(r))
            {
                int h = (int)Math.Round(r.Height * (double)width / r.Width);
                using (var o = new Bitmap(width, h, PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(o))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(b, 0, 0, width, h);
                    }
                    if (path.EndsWith(".jpg"))
                    {
                        ImageCodecInfo jpg = Array.Find(ImageCodecInfo.GetImageEncoders(), c => c.MimeType == "image/jpeg");
                        var ps = new EncoderParameters(1);
                        ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
                        o.Save(path, jpg, ps);
                    }
                    else
                    {
                        o.Save(path, ImageFormat.Png);
                    }
                }
            }
            Say("still " + path);
        }

        /* ---------------- usage: keys driving the overlay over a full-screen editor ---------------- */

        static int Usage(string outDir, string sourceFile)
        {
            var bd = new Backdrop(sourceFile);
            bd.Show();
            Loop.Pump(300);
            if (!W32.Front(bd.Handle))
                throw new Exception("could not bring the backdrop to the front");
            Loop.Pump(1200);
            Rectangle screen = Screen.PrimaryScreen.Bounds;
            Rectangle ov = OverlayRect();

            Cmd("hide");
            Cmd("audio", "off");
            Loop.Pump(500);
            var rec = new Recorder(screen, Path.Combine(outDir, "usage-frames"), 15, 1280);
            rec.Start();
            Action<string[], Action, int> beat = (cap, act, ms) =>
            {
                rec.Caption = cap;
                if (act != null) act();
                Loop.Pump(ms);
            };
            beat(null, null, 1500);
            beat(new[] { "Ctrl", "Num *", "show" }, () => Cmd("toggle"), 3200);
            beat(new[] { "Num *", "next camera" }, () => Cmd("next"), 2200);
            beat(new[] { "Num *", "next camera" }, () => Cmd("next"), 2000);
            beat(new[] { "Num *", "next camera" }, () => Cmd("next"), 1800);
            beat(new[] { "Num *", "next camera" }, () => Cmd("next"), 1800);
            beat(new[] { "Num *", "next camera" }, () => Cmd("next"), 1800);
            beat(new[] { "Num *", "back to the grid" }, () => Cmd("next"), 2200);
            beat(new[] { "Num /", "previous camera" }, () => Cmd("prev"), 2000);
            beat(new[] { "Num /", "previous camera" }, () => Cmd("prev"), 1800);
            beat(new[] { "Ctrl", "Num -", "sound on" }, () => Cmd("audio", "on"), 2800);
            beat(new[] { "Ctrl", "Num *", "hide" }, () => { Cmd("audio", "off"); Cmd("toggle"); }, 1800);
            beat(null, null, 600);
            rec.Stop();
            Say("usage frames: " + rec.Frames);

            /* stills */
            Cmd("toggle");
            Loop.Pump(3500);
            Still(screen, Path.Combine(outDir, "hero.jpg"), 1600, 0);
            Still(ov, Path.Combine(outDir, "grid.png"), 900, 0);
            Cmd("show", "Front Door");
            Cmd("audio", "on");
            Loop.Pump(3000);
            Still(ov, Path.Combine(outDir, "single-audio.png"), 900, 0);
            Cmd("audio", "off");
            Cmd("hide");
            Loop.Pump(500);
            bd.Close();
            return 0;
        }

        /* ---------------- install: the wizard in its own console ---------------- */

        sealed class Console2
        {
            public Process Host;
            public int ShellPid;
            public IntPtr Hwnd;

            public string CursorLine(out int y)
            {
                y = -1;
                if (!W32.AttachConsole(ShellPid))
                    return null;
                try
                {
                    IntPtr h = W32.CreateFile("CONOUT$", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (h == new IntPtr(-1))
                        return null;
                    try
                    {
                        W32.CSBI i;
                        if (!W32.GetConsoleScreenBufferInfo(h, out i))
                            return null;
                        y = i.Cursor.Y;
                        var sb = new StringBuilder(i.Size.X);
                        uint read;
                        W32.ReadConsoleOutputCharacter(h, sb, (uint)i.Size.X, new W32.COORD { X = 0, Y = i.Cursor.Y }, out read);
                        return sb.ToString(0, (int)read).TrimEnd();
                    }
                    finally { W32.CloseHandle(h); }
                }
                finally { W32.FreeConsole(); }
            }

            public string Screen()
            {
                if (!W32.AttachConsole(ShellPid))
                    return "";
                try
                {
                    IntPtr h = W32.CreateFile("CONOUT$", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    try
                    {
                        W32.CSBI i;
                        W32.GetConsoleScreenBufferInfo(h, out i);
                        var all = new StringBuilder();
                        for (int y = i.Window.T; y <= i.Window.B; y++)
                        {
                            var sb = new StringBuilder(i.Size.X);
                            uint read;
                            W32.ReadConsoleOutputCharacter(h, sb, (uint)i.Size.X, new W32.COORD { X = 0, Y = (short)y }, out read);
                            all.AppendLine(sb.ToString(0, (int)read).TrimEnd());
                        }
                        return all.ToString();
                    }
                    finally { W32.CloseHandle(h); }
                }
                finally { W32.FreeConsole(); }
            }
        }

        static int lastPromptY = -1;

        /* Waits until the installer's cursor sits on a new line containing `text` (a prompt waiting for input). */
        static void WaitPrompt(Console2 c, string text, int timeoutMs = 90000)
        {
            long end = Util.MonotonicMs() + timeoutMs;
            while (Util.MonotonicMs() < end)
            {
                if (c.Host.HasExited)
                    throw new Exception("installer exited while waiting for '" + text + "'");
                int y;
                string line = c.CursorLine(out y);
                if (line != null && y != lastPromptY && line.IndexOf(text, StringComparison.Ordinal) >= 0)
                {
                    lastPromptY = y;
                    Say("prompt: " + line);
                    Loop.Pump(650); /* let the viewer read the question */
                    return;
                }
                Loop.Pump(120);
            }
            throw new Exception("timed out waiting for '" + text + "'; screen:\n" + c.Screen());
        }

        static W32.INPUT Key(ushort vk, ushort scan, uint flags)
        {
            return new W32.INPUT { Type = 1, Ki = new W32.KEYBDINPUT { Vk = vk, Scan = scan, Flags = flags } };
        }

        static void Send(Console2 c, params W32.INPUT[] inputs)
        {
            if (!W32.Front(c.Hwnd))
                throw new Exception("the installer console is not in front; refusing to type");
            W32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(W32.INPUT)));
        }

        static void Type(Console2 c, string text, bool enter = true)
        {
            foreach (char ch in text)
            {
                Send(c, Key(0, ch, 0x4 /* UNICODE */), Key(0, ch, 0x4 | 0x2));
                Loop.Pump(55);
            }
            Loop.Pump(250);
            if (enter)
                Send(c, Key(0x0D, 0, 0), Key(0x0D, 0, 2));
        }

        static void Combo(Console2 c, params ushort[] vks)
        {
            var list = new List<W32.INPUT>();
            foreach (ushort vk in vks) list.Add(Key(vk, 0, 0));
            for (int i = vks.Length - 1; i >= 0; i--) list.Add(Key(vks[i], 0, 2));
            Send(c, list.ToArray());
        }

        static int Install(string outDir, string repoCopy)
        {
            W32.FreeConsole(); /* to read the installer's console, this process must not have its own */
            var bd = new Backdrop(Path.Combine(repoCopy, "src", "Overlay.cs"));
            bd.Show();
            Loop.Pump(300);
            W32.Front(bd.Handle);
            Loop.Pump(500);

            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "conhost.exe"),
                "powershell.exe -NoProfile -NoExit -ExecutionPolicy Bypass -Command \"$Host.UI.RawUI.WindowTitle = 'cctv-overlay installer'; " +
                "function global:prompt { 'PS ~\\cctv-overlay-windows> ' }; " +
                "& '" + Path.Combine(repoCopy, "install.ps1") + "'\"");
            psi.UseShellExecute = true; /* inherits this process's environment (the throwaway profile) */
            psi.WorkingDirectory = repoCopy;
            var c = new Console2();
            c.Host = Process.Start(psi);
            /* find the PowerShell process attached to that console, then the console's window */
            long end = Util.MonotonicMs() + 15000;
            while (c.ShellPid == 0 && Util.MonotonicMs() < end)
            {
                foreach (Process p in Process.GetProcessesByName("powershell"))
                    if (ParentOf(p.Id) == c.Host.Id) c.ShellPid = p.Id;
                Loop.Pump(100);
            }
            if (c.ShellPid == 0)
                throw new Exception("installer shell not found");
            while (c.Hwnd == IntPtr.Zero && Util.MonotonicMs() < end)
            {
                if (W32.AttachConsole(c.ShellPid))
                {
                    c.Hwnd = W32.GetConsoleWindow();
                    W32.FreeConsole();
                }
                if (c.Hwnd == IntPtr.Zero)
                    Loop.Pump(100);
            }
            if (c.Hwnd == IntPtr.Zero)
                throw new Exception("no installer console window");
            Rectangle scr = Screen.PrimaryScreen.WorkingArea;
            float s = Native.DpiAt(scr.Left + 10, scr.Top + 10) / 96f;
            int ww = (int)(980 * s), wh = (int)(700 * s);
            W32.MoveWindow(c.Hwnd, scr.Left + (scr.Width - ww) / 2, scr.Top + (scr.Height - wh) / 2, ww, wh, true);
            W32.Front(c.Hwnd);
            Loop.Pump(800);
            W32.RECT wr;
            W32.GetWindowRect(c.Hwnd, out wr);
            var area = new Rectangle(wr.L, wr.T, wr.R - wr.L, wr.B - wr.T);
            area.Inflate(-(int)(7 * s), 0); /* drop the invisible resize borders */
            area.Height -= (int)(7 * s);
            var rec = new Recorder(area, Path.Combine(outDir, "install-frames"), 12, 1200);
            rec.Start();
            try
            {
                const ushort CTRL = 0x11, MUL = 0x6A, DIV = 0x6F, SUB = 0x6D;
                WaitPrompt(c, "Type a number [1]"); Type(c, "1");
                WaitPrompt(c, "Recorder IP address"); Type(c, "192.168.1.100");
                WaitPrompt(c, "RTSP port"); Type(c, "");
                WaitPrompt(c, "User name"); Type(c, "viewer");
                WaitPrompt(c, "Type a number [1]"); Type(c, "");
                WaitPrompt(c, "How many cameras"); Type(c, "4");
                string[] names = { "Front Door", "Driveway", "Backyard", "Garage" };
                foreach (string n in names)
                {
                    WaitPrompt(c, "Name to show"); Type(c, n);
                    WaitPrompt(c, "Channel number"); Type(c, "");
                }
                WaitPrompt(c, "Type a number [1]"); Type(c, "");
                WaitPrompt(c, "Type a number [3]"); Type(c, "");
                WaitPrompt(c, "Type a number [1]"); Type(c, "");
                WaitPrompt(c, "Press the keys now"); Combo(c, CTRL, MUL);
                WaitPrompt(c, "Press the keys now"); Combo(c, MUL);
                WaitPrompt(c, "Press the keys now"); Combo(c, DIV);
                WaitPrompt(c, "Press the keys now"); Combo(c, CTRL, SUB);
                WaitPrompt(c, "Use these keys?"); Type(c, "");
                WaitPrompt(c, "Password"); Type(c, "demo-password");
                WaitPrompt(c, "Type it again"); Type(c, "demo-password");
                WaitPrompt(c, "automatically when you sign in"); Type(c, "n");
                WaitPrompt(c, "Make the 'cctv-overlay' command"); Type(c, "n");
                WaitPrompt(c, "Open the overlay now"); Type(c, "n");
                end = Util.MonotonicMs() + 20000;
                while (!c.Host.HasExited && c.Screen().IndexOf("All done", StringComparison.Ordinal) < 0 && Util.MonotonicMs() < end)
                    Loop.Pump(200);
                Loop.Pump(3500);
            }
            finally
            {
                rec.Stop();
                Say("install frames: " + rec.Frames);
                Say("final screen:\n" + c.Screen());
                if (!c.Host.HasExited) c.Host.Kill();
                bd.Close();
            }
            return 0;
        }

        static int ParentOf(int pid)
        {
            using (var q = new System.Management.ManagementObjectSearcher("SELECT ParentProcessId FROM Win32_Process WHERE ProcessId=" + pid))
                foreach (System.Management.ManagementObject o in q.Get())
                    return Convert.ToInt32(o["ParentProcessId"]);
            return 0;
        }

        /* ---------------- encode with libmpv's encoding mode ---------------- */

        static int Encode(string frames, string output, int fps, int width)
        {
            string list = Path.Combine(frames, "frames.txt");
            IntPtr h = Mpv.mpv_create();
            Action<string, string> opt = (n, v) =>
            {
                int e = Mpv.mpv_set_option_string(h, Mpv.Z(n), Mpv.Z(v));
                if (e < 0) throw new Exception("mpv option " + n + "=" + v + ": " + Mpv.ErrorString(e));
            };
            opt("config", "no");
            opt("terminal", "no");
            opt("idle", "no");
            opt("audio", "no");
            opt("mf-fps", fps.ToString());
            opt("o", output);
            if (output.EndsWith(".gif"))
            {
                int gfps = Math.Min(fps, 10);
                opt("ovc", "gif");
                opt("vf", "lavfi=[fps=" + gfps + ",scale=" + width + ":-2:flags=lanczos,split[a][b];[a]palettegen=max_colors=128:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle]");
            }
            else
            {
                opt("ovc", "libx264");
                opt("ovcopts", "crf=20,preset=slow,profile=high,pix_fmt=yuv420p,movflags=+faststart");
                if (width > 0)
                    opt("vf", "lavfi=[scale=" + width + ":-2:flags=lanczos,format=yuv420p]");
                else
                    opt("vf", "format=yuv420p");
            }
            int r = Mpv.mpv_initialize(h);
            if (r < 0) throw new Exception("mpv_initialize: " + Mpv.ErrorString(r));
            Mpv.mpv_request_log_messages(h, Mpv.Z("warn"));
            byte[] a0 = Mpv.Z("loadfile"), a1 = Mpv.Z("mf://@" + list);
            IntPtr p0 = Marshal.AllocHGlobal(a0.Length), p1 = Marshal.AllocHGlobal(a1.Length);
            Marshal.Copy(a0, 0, p0, a0.Length);
            Marshal.Copy(a1, 0, p1, a1.Length);
            Mpv.mpv_command_async(h, 0, new[] { p0, p1, IntPtr.Zero });
            bool ok = true;
            while (true)
            {
                IntPtr ev = Mpv.mpv_wait_event(h, 600);
                int id = Marshal.ReadInt32(ev, 0);
                IntPtr data = Marshal.ReadIntPtr(ev, 16);
                if (id == Mpv.EventLogMessage)
                    Say("mpv: " + Mpv.Str(Marshal.ReadIntPtr(data, 2 * IntPtr.Size)));
                if (id == Mpv.EventEndFile && Marshal.ReadInt32(data, 0) == Mpv.EndReasonError)
                    ok = false;
                if (id == Mpv.EventShutdown || id == Mpv.EventNone)
                    break;
            }
            Mpv.mpv_terminate_destroy(h);
            Marshal.FreeHGlobal(p0);
            Marshal.FreeHGlobal(p1);
            Say("encoded " + output + " ok=" + ok + " size=" + (File.Exists(output) ? new FileInfo(output).Length : -1));
            return ok && File.Exists(output) ? 0 : 1;
        }

        [STAThread]
        static int Main(string[] args)
        {
            string outDir = args.Length > 1 ? args[1] : ".";
            Directory.CreateDirectory(outDir);
            log = new StreamWriter(Path.Combine(outDir, "director.log"), true);
            try
            {
                Loop.Init();
                switch (args[0])
                {
                    case "usage": return Usage(outDir, args[2]);
                    case "install": return Install(outDir, args[2]);
                    case "encode": return Encode(args[1], args[2], int.Parse(args[3]), args.Length > 4 ? int.Parse(args[4]) : 0);
                }
                Say("unknown mode " + args[0]);
                return 2;
            }
            catch (Exception e)
            {
                Say("FAILED: " + e);
                return 1;
            }
            finally
            {
                log.Flush();
            }
        }
    }
}
#endif
