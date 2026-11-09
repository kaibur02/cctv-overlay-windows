// Entry point: CLI client, daemon, offline `check`.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CctvOverlay
{
    internal static class Program
    {
        const int ExitOk = 0, ExitFailed = 1, ExitUsage = 2, ExitUnavailable = 3, ExitTimeout = 4;

        static string optConfig, optSocket;
        static bool optNoSpawn, optJson, optNoOverlay;

        const string Help =
            "Usage:\n" +
            "  cctv-overlay [OPTION...] COMMAND [CAMERA]\n" +
            "\n" +
            "Commands:\n" +
            "  daemon           run the overlay daemon in the foreground\n" +
            "  toggle           show/hide the overlay (opens on the grid view when there is one)\n" +
            "  next | prev      cycle grid -> cameras -> grid (opens the overlay if hidden)\n" +
            "  grid             open/switch to the grid view (first four cameras by default)\n" +
            "  audio [on|off]   toggle (or set) camera audio; a speaker icon shows while it is on\n" +
            "  show [CAMERA]    open the overlay on CAMERA (name, id or zero-based index)\n" +
            "  hide             close the overlay and free all resources\n" +
            "  status [--json]  print the current state\n" +
            "  list             list configured cameras\n" +
            "  reload           re-read the configuration\n" +
            "  quit             stop the daemon\n" +
            "  ping             check that the daemon answers\n" +
            "  check            validate config.ini and secrets offline (prints no secrets)\n" +
            "\n" +
            "Options:\n" +
            "  -c, --config PATH   Config file (default: %APPDATA%\\cctv-overlay\\config.ini)\n" +
            "  --socket NAME       Control pipe name (default: cctv-overlay-<your SID>)\n" +
            "  --no-spawn          Do not start the daemon automatically\n" +
            "  --json              Print the raw JSON reply\n" +
            "  --no-overlay        daemon: run without any overlay window\n" +
            "  --version           Print version and exit\n" +
            "  -h, --help          Show this help\n" +
            "\n" +
            "Exit codes: 0 ok, 1 command failed, 2 usage/config error, 3 daemon unavailable, 4 timeout\n";

        static string StateDir()
        {
            string s = Util.Env("CCTV_OVERLAY_STATE_DIR"); /* tests / demo: keep the real log untouched */
            if (!string.IsNullOrEmpty(s))
                return s;
            string local = Util.Env("LOCALAPPDATA");
            if (string.IsNullOrEmpty(local))
                local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "cctv-overlay");
        }

        public static string LogPath { get { return Path.Combine(StateDir(), "daemon.log"); } }

        /* ---------------- daemon ---------------- */

        static int RunDaemon()
        {
            /* The daemon holds the RTSP password in memory; keep it out of crash dumps. */
            Native.NoCrashDumps();
#if WINEXE
            Log.UseFile(LogPath); /* no terminal: the (already redacted) log goes to a private file */
#endif
            Config cfg;
            try
            {
                cfg = Config.Load(optConfig);
            }
            catch (CctvException e)
            {
                Console.Error.WriteLine("cctv-overlay: configuration error: " + e.Message);
                Log.Error("configuration error: {0}", e.Message);
                return ExitUsage;
            }
            Log.Level = cfg.LogLevel;

            Loop.Init();
            string err;
            if (!optNoOverlay && !Overlay.Init(out err))
            {
                Console.Error.WriteLine("cctv-overlay: overlay unavailable: " + err);
                Log.Error("overlay unavailable: {0}", err);
                return ExitFailed;
            }

            double scale = 1.0;
            string sc = Util.Env("CCTV_OVERLAY_BACKOFF_SCALE"); /* test knob */
            if (!string.IsNullOrEmpty(sc))
            {
                if (!double.TryParse(sc, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out scale) ||
                    double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0 || scale > 600)
                    scale = 1.0;
            }

            bool quitting = false;
            Action quit = delegate
            {
                if (quitting)
                    return;
                quitting = true;
                Application.ExitThread();
            };
            var ao = new AppOptions { OverlayEnabled = !optNoOverlay, BackoffScale = scale, QuitCb = quit };
            var app = new App(cfg, ao);

            IpcServer srv;
            try
            {
                string pipe = optSocket != null ? IpcServer.NormalizeName(optSocket) : IpcServer.DefaultPipeName();
                srv = new IpcServer(pipe, app.Handle, app.AfterReply);
            }
            catch (CctvException e)
            {
                Console.Error.WriteLine("cctv-overlay: " + e.Message);
                Log.Error("{0}", e.Message);
                app.Free();
                return ExitFailed;
            }

            var keys = new HotkeyManager(verb => app.Handle(verb, null));
            keys.QuitRequested = () => Loop.Idle(quit);
            keys.Apply(cfg);
            app.Reloaded += c => keys.Apply(c);
            Overlay.CloseRequested = () => Loop.Idle(quit);
            Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                Log.Info("received {0}, shutting down", e.SpecialKey == ConsoleSpecialKey.ControlC ? "Ctrl+C" : "Ctrl+Break");
                Loop.Idle(quit);
            };

            Log.Info("cctv-overlay {0} daemon started ({1} cameras, hidden)", Util.Version, cfg.Cameras.Count);
            Application.Run();

            app.Free();
            Overlay.Shutdown();
            Video.WaitForTeardown(3000);
            keys.Dispose();
            srv.Dispose();
            Log.Info("daemon stopped");
            return ExitOk;
        }

        /* ---------------- client ---------------- */

        static string BuildRequest(string cmd, string arg)
        {
            var o = new JObj().Set("cmd", cmd);
            if (arg != null)
                o.Set(cmd == "audio" ? "mode" : "camera", arg);
            return Json.Write(o);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO
        {
            public int cb;
            public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateProcess(string app, StringBuilder cmdLine, IntPtr pa, IntPtr ta, bool inherit, uint flags,
                                         IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        static string Quote(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
                return a;
            var sb = new StringBuilder("\"");
            int bs = 0;
            foreach (char c in a)
            {
                if (c == '\\')
                {
                    bs++;
                    continue;
                }
                if (c == '"')
                {
                    sb.Append('\\', bs * 2 + 1);
                    bs = 0;
                    sb.Append('"');
                    continue;
                }
                sb.Append('\\', bs);
                bs = 0;
                sb.Append(c);
            }
            sb.Append('\\', bs * 2);
            sb.Append('"');
            return sb.ToString();
        }

        static void SpawnDaemon()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            string self = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            string w = Path.Combine(dir, "cctv-overlayw.exe");
            string exe = File.Exists(w) ? w : self;

            var args = new List<string> { exe, "daemon" };
            if (optConfig != null)
            {
                args.Add("--config");
                args.Add(Path.GetFullPath(optConfig));
            }
            if (optSocket != null)
            {
                args.Add("--socket");
                args.Add(optSocket);
            }
            if (optNoOverlay)
                args.Add("--no-overlay");
            var cmd = new StringBuilder();
            foreach (string a in args)
                cmd.Append(cmd.Length > 0 ? " " : "").Append(Quote(a));

            /* Config path only on the command line; secrets stay in files/environment. A detached process
             * outlives this one; break away from a job (terminal, CI) when allowed so it is not killed with it. */
            const uint DETACHED_PROCESS = 0x8, CREATE_NEW_PROCESS_GROUP = 0x200, CREATE_BREAKAWAY_FROM_JOB = 0x01000000,
                       CREATE_NO_WINDOW = 0x08000000;
            uint flags = CREATE_NEW_PROCESS_GROUP | (exe == w ? DETACHED_PROCESS : CREATE_NO_WINDOW);
            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            PROCESS_INFORMATION pi;
            bool ok = CreateProcess(exe, new StringBuilder(cmd.ToString()), IntPtr.Zero, IntPtr.Zero, false,
                                    flags | CREATE_BREAKAWAY_FROM_JOB, IntPtr.Zero, dir, ref si, out pi);
            if (!ok)
                ok = CreateProcess(exe, new StringBuilder(cmd.ToString()), IntPtr.Zero, IntPtr.Zero, false, flags,
                                   IntPtr.Zero, dir, ref si, out pi);
            if (!ok)
                throw new CctvException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
        }

        static void PrintStatusHuman(JObj o)
        {
            string state = o.GetString("state");
            string view = o.GetString("view") ?? "camera";
            if (view == "grid")
            {
                List<object> t = o.GetArray("tiles");
                var sb = new StringBuilder(state + ": grid view");
                for (int i = 0; t != null && i < t.Count; i++)
                {
                    var to = (JObj)t[i];
                    sb.Append(i > 0 ? ", " : " [").Append(to.GetString("camera")).Append(" (").Append(to.GetString("state")).Append(")");
                    if (i + 1 == t.Count)
                        sb.Append("]");
                }
                Console.Out.WriteLine(sb.ToString());
            }
            else
            {
                Console.Out.WriteLine("{0}: {1} (camera {2} of {3}, zero-based index)", state, o.GetString("camera"),
                                      o.GetInt("camera_index"), o.GetInt("camera_count"));
            }
            Console.Out.WriteLine("audio: " + (o.GetBool("audio") ? "on" : "off"));
            if (o.Get("error") is string)
                Console.Out.WriteLine("error: " + o.GetString("error"));
        }

        static void PrintHuman(string cmd, JObj o)
        {
            if (cmd == "status")
            {
                PrintStatusHuman(o);
            }
            else if (cmd == "list")
            {
                List<object> a = o.GetArray("cameras");
                foreach (object x in a)
                {
                    var c = (JObj)x;
                    Console.Out.WriteLine("{0} {1}  {2,-16} {3} (channel {4})", c.GetBool("current") ? '*' : ' ', c.GetInt("index"),
                                          c.GetString("id"), c.GetString("name"), c.GetInt("channel"));
                }
            }
            else if (cmd == "ping")
            {
                Console.Out.WriteLine("cctv-overlay daemon " + (o.GetString("version") ?? "?") + " is running");
            }
        }

        static int RunClient(string cmd, string arg)
        {
            string pipe;
            try
            {
                pipe = optSocket != null ? IpcServer.NormalizeName(optSocket) : IpcServer.DefaultPipeName();
            }
            catch (CctvException e)
            {
                Console.Error.WriteLine("cctv-overlay: " + e.Message);
                return ExitUsage;
            }
            string req = BuildRequest(cmd, arg);
            string reply, error;
            IpcResult r = IpcClient.Request(pipe, req, out reply, out error);

            if (r == IpcResult.Unavailable && !optNoSpawn && cmd != "quit")
            {
                /* Catch config errors here, with a real message, instead of a silent daemon exit. */
                try
                {
                    Config.Load(optConfig).Forget();
                }
                catch (CctvException e)
                {
                    Console.Error.WriteLine("cctv-overlay: configuration error: " + e.Message);
                    return ExitUsage;
                }
                try
                {
                    SpawnDaemon();
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine("cctv-overlay: cannot start the daemon: " + e.Message);
                    return ExitUnavailable;
                }
                long deadline = Util.MonotonicMs() + 2000;
                do
                {
                    Thread.Sleep(25);
                    r = IpcClient.Request(pipe, req, out reply, out error);
                } while (r == IpcResult.Unavailable && Util.MonotonicMs() < deadline);
            }

            int rc = ExitFailed;
            if (r == IpcResult.Unavailable)
            {
                Console.Error.WriteLine("cctv-overlay: daemon is not running" +
                                        (optNoSpawn || cmd == "quit" ? "" :
                                         " and did not start within 2 s (run 'cctv-overlay daemon' in a terminal to see why)"));
                rc = ExitUnavailable;
            }
            else if (r == IpcResult.Timeout)
            {
                Console.Error.WriteLine("cctv-overlay: timed out waiting for the daemon");
                rc = ExitTimeout;
            }
            else if (r == IpcResult.Failed)
            {
                Console.Error.WriteLine("cctv-overlay: " + (error ?? "request failed"));
            }
            else
            {
                JObj o = null;
                try { o = Json.Parse(reply) as JObj; }
                catch (FormatException) { }
                if (o != null)
                {
                    bool ok = o.GetBool("ok");
                    if (optJson)
                        Console.Out.WriteLine(reply);
                    if (ok)
                    {
                        if (!optJson)
                            PrintHuman(cmd, o);
                        rc = ExitOk;
                    }
                    else if (!optJson)
                    {
                        Console.Error.WriteLine("cctv-overlay: " + (o.GetString("error") ?? "command failed"));
                    }
                }
                else
                {
                    Console.Error.WriteLine("cctv-overlay: unreadable reply from the daemon");
                }
            }
            return rc;
        }

        /* Offline validation for installers and humans: loads config.ini + secrets, prints a summary. The
         * password is never printed; only whether one was found. Needs no daemon and no display. */
        static int RunCheck()
        {
            Config cfg;
            try
            {
                cfg = Config.Load(optConfig);
            }
            catch (CctvException e)
            {
                Console.Error.WriteLine("cctv-overlay: configuration error: " + e.Message);
                return ExitUsage;
            }
            Console.Out.WriteLine("config:   " + cfg.ConfigPath);
            Console.Out.WriteLine("cameras:  " + cfg.Cameras.Count);
            for (int i = 0; i < cfg.Cameras.Count; i++)
            {
                Camera c = cfg.Cameras[i];
                bool inGrid = false;
                for (int g = 0; g < cfg.GridCount; g++)
                    inGrid |= cfg.GridIdx[g] == i;
                Console.Out.WriteLine("  {0}  {1,-20} {2}{3}", i, c.Name, c.Url != null ? "(url override)" : c.Host ?? "",
                                      inGrid ? "  [grid]" : "");
            }
            Console.Out.WriteLine("grid:     " + (cfg.GridCount > 0 ? "on" : "off"));
            var keys = new List<string>();
            if (cfg.KeyToggle != null) keys.Add("toggle = " + cfg.KeyToggle);
            if (cfg.KeyNext != null) keys.Add("next = " + cfg.KeyNext);
            if (cfg.KeyPrev != null) keys.Add("prev = " + cfg.KeyPrev);
            if (cfg.KeyAudio != null) keys.Add("audio = " + cfg.KeyAudio);
            Console.Out.WriteLine("keys:     " + (keys.Count > 0 ? string.Join(", ", keys.ToArray()) : "none"));
            Console.Out.WriteLine("username: " + (!string.IsNullOrEmpty(cfg.Username) ? "set" : "MISSING"));
            Console.Out.WriteLine("password: " + (!string.IsNullOrEmpty(cfg.Password) ? "set" : "MISSING (run install.cmd --set-password)"));
            bool ok = !string.IsNullOrEmpty(cfg.Password) && !string.IsNullOrEmpty(cfg.Username);
            foreach (Camera c in cfg.Cameras)
                ok |= c.Url != null;
            cfg.Forget();
            return ok ? ExitOk : ExitFailed;
        }

        /* Used by the installer: validate a key combination with the real parser and check that it is free.
         * stdout line 1: the key as written in config.ini ("none" when unbound); line 2: "typing" when the key has
         * no modifier and is used for typing, else "ok". Exit 0 ok, 2 not a valid key (message on stderr),
         * 3 another program already uses it. */
        static int RunCheckKey(string spec)
        {
            Hotkey h;
            string err;
            if (!Hotkey.TryParse(spec, out h, out err))
            {
                Console.Error.WriteLine(err);
                return ExitUsage;
            }
            if (h == null)
            {
                Console.Out.WriteLine("none");
                Console.Out.WriteLine("ok");
                return ExitOk;
            }
            Console.Out.WriteLine(h.Text);
            Console.Out.WriteLine(h.StealsTyping ? "typing" : "ok");
            if (!h.IsFree())
            {
                Console.Error.WriteLine(h.Text + " is already used by another program");
                return ExitUnavailable;
            }
            return ExitOk;
        }

        /* ---------------- entry ---------------- */

        [STAThread]
        static int Main(string[] argv)
        {
            Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            if (argv.Length >= 1 && argv[0] == "check-key") /* the key text is taken as-is: "-" is a key, not an option */
                return RunCheckKey(string.Join(" ", argv, 1, argv.Length - 1));
            var pos = new List<string>();
            bool version = false, help = false;
            for (int i = 0; i < argv.Length; i++)
            {
                string a = argv[i];
                if (a == "--")
                {
                    for (i++; i < argv.Length; i++)
                        pos.Add(argv[i]);
                    break;
                }
                if (!a.StartsWith("-", StringComparison.Ordinal) || a == "-")
                {
                    pos.Add(a);
                    continue;
                }
                string name = a, value = null;
                int eq = a.IndexOf('=');
                if (a.StartsWith("--", StringComparison.Ordinal) && eq > 0)
                {
                    name = a.Substring(0, eq);
                    value = a.Substring(eq + 1);
                }
                switch (name)
                {
                    case "-c":
                    case "--config":
                    case "--socket":
                        if (value == null)
                        {
                            if (i + 1 >= argv.Length)
                            {
                                Console.Error.WriteLine("cctv-overlay: Missing argument for " + name);
                                return ExitUsage;
                            }
                            value = argv[++i];
                        }
                        if (name == "--socket")
                            optSocket = value;
                        else
                            optConfig = value;
                        break;
                    case "--no-spawn": optNoSpawn = true; break;
                    case "--json": optJson = true; break;
                    case "--no-overlay": optNoOverlay = true; break;
                    case "--version": version = true; break;
                    case "-h":
                    case "-?":
                    case "--help": help = true; break;
                    default:
                        Console.Error.WriteLine("cctv-overlay: Unknown option " + a);
                        return ExitUsage;
                }
            }
            if (help)
            {
                Console.Out.Write(Help);
                return ExitOk;
            }
            if (version)
            {
                Console.Out.WriteLine("cctv-overlay " + Util.Version);
                return ExitOk;
            }
            if (pos.Count < 1)
            {
                Console.Error.Write(Help);
                return ExitUsage;
            }

            string cmd = pos[0];
            string arg = pos.Count > 1 ? pos[1] : null;
            string[] simple = { "toggle", "next", "prev", "grid", "hide", "status", "list", "reload", "quit", "ping" };

            if (pos.Count > 2 || (arg != null && cmd != "show" && cmd != "audio"))
            {
                Console.Error.WriteLine("cctv-overlay: unexpected argument '{0}' for '{1}'", pos[pos.Count > 2 ? 2 : 1], cmd);
                return ExitUsage;
            }
            if (cmd == "daemon")
                return RunDaemon();
            if (cmd == "check")
                return RunCheck();
            if (cmd == "show")
                return RunClient(cmd, arg);
            if (cmd == "audio")
            {
                if (arg != null && arg != "on" && arg != "off" && arg != "toggle")
                {
                    Console.Error.WriteLine("cctv-overlay: audio takes on, off or toggle");
                    return ExitUsage;
                }
                return RunClient(cmd, arg);
            }
            if (Array.IndexOf(simple, cmd) >= 0)
                return RunClient(cmd, null);

            Console.Error.WriteLine("cctv-overlay: unknown command '{0}' (try --help)", cmd);
            return ExitUsage;
        }
    }
}
