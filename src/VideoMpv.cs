#if !STUB
// libmpv engine. Up to Video.MaxSlots independent players, one per overlay tile (the grid view uses four).
// Each player renders into its own child window inside its tile (mpv's "wid" embedding, D3D11 with
// D3D11VA zero-copy decoding by default) and fails/reconnects on its own.
//
// Threading: all mpv calls except mpv_terminate_destroy run on the loop (UI) thread; the foreign-thread
// wakeup callback only sets a flag and posts to the loop. mpv's video window lives in mpv's own thread and
// is a child of ours, so destroying a player must not block the UI thread (it would deadlock on the
// window messages mpv sends while closing): teardown runs on a worker, and the player's host window is
// parked off-screen until mpv has let go of it.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CctvOverlay
{
    internal static class Mpv
    {
        const string Dll = "libmpv-2.dll";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void WakeupCb(IntPtr d);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_create();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_initialize(IntPtr ctx);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void mpv_terminate_destroy(IntPtr ctx);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_set_option_string(IntPtr ctx, byte[] name, byte[] data);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_set_property_string(IntPtr ctx, byte[] name, byte[] data);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_get_property(IntPtr ctx, byte[] name, int format, out long data);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_get_property(IntPtr ctx, byte[] name, int format, out double data);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_get_property_string(IntPtr ctx, byte[] name);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void mpv_free(IntPtr data);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_command_async(IntPtr ctx, ulong reply, IntPtr[] args);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_request_log_messages(IntPtr ctx, byte[] minLevel);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_observe_property(IntPtr ctx, ulong reply, byte[] name, int format);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void mpv_set_wakeup_callback(IntPtr ctx, WakeupCb cb, IntPtr d);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_error_string(int error);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern ulong mpv_client_api_version();

        public const int EventNone = 0, EventShutdown = 1, EventLogMessage = 2, EventEndFile = 7, EventFileLoaded = 8,
                         EventPlaybackRestart = 21, EventPropertyChange = 22;
        public const int FormatString = 1, FormatInt64 = 4, FormatDouble = 5;
        public const int EndReasonEof = 0, EndReasonError = 4;
        public const int ErrorLoadingFailed = -13, ErrorUnknownFormat = -17;
        public const int LogLevelError = 20, LogLevelWarn = 30;

        public static byte[] Z(string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            byte[] z = new byte[b.Length + 1];
            Buffer.BlockCopy(b, 0, z, 0, b.Length);
            return z;
        }

        public static string Str(IntPtr p)
        {
            if (p == IntPtr.Zero)
                return null;
            int n = 0;
            while (Marshal.ReadByte(p, n) != 0)
                n++;
            byte[] b = new byte[n];
            Marshal.Copy(p, b, 0, n);
            return Encoding.UTF8.GetString(b);
        }

        public static string ErrorString(int e)
        {
            try { return Str(mpv_error_string(e)) ?? ("error " + e); }
            catch (Exception) { return "error " + e; }
        }

        /// <summary>True when libmpv-2.dll can be loaded (it must sit next to the program).</summary>
        public static bool Available(out string error)
        {
            error = null;
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            if (Native.LoadLibrary(System.IO.Path.Combine(dir, Dll)) != IntPtr.Zero || Native.LoadLibrary(Dll) != IntPtr.Zero)
            {
                try
                {
                    ulong v = mpv_client_api_version();
                    if ((v >> 16) < 2)
                    {
                        error = "libmpv-2.dll is too old (client API " + (v >> 16) + "." + (v & 0xffff) + ", need 2.0 or newer)";
                        return false;
                    }
                    return true;
                }
                catch (Exception e)
                {
                    error = "libmpv-2.dll could not be used: " + e.Message;
                    return false;
                }
            }
            error = "libmpv-2.dll was not found next to " + System.IO.Path.Combine(dir, "cctv-overlay.exe") +
                    " (error " + Marshal.GetLastWin32Error() + "); run install.cmd, which downloads it, or copy it there";
            return false;
        }
    }

    internal static class Video
    {
        public const int MaxSlots = 4;

        const int StallLiveMs = 10000;    /* no new frame while live */
        const int StallConnectMs = 15000; /* no first frame after start (the RTSP socket timeout is 5 s) */
        const string RtspTimeoutUs = "5000000";
        const int MaxMpvLogLines = 40;
        const int MaxMpvTraceLines = 20000;

        sealed class Player
        {
            public int Slot;
            public uint Session; /* active session id, 0 = none */
            public IntPtr Mpv;
            public Control Host;
            public IVideoCallbacks Cb;
            public int WakeQueued; /* atomic: a drain is pending */
            public bool FirstReported, OutcomeReported;
            public bool AuthFailed, NotFound, Refused, TimedOut;
            public long StartMs, LastFrameMs;
            public double LastTimePos = double.NaN;
            public LoopTimer Watchdog;
            public int LogLines;
            public Mpv.WakeupCb Wakeup; /* keeps the delegate alive while mpv holds it */
        }

        static readonly Player[] P = { new Player { Slot = 0 }, new Player { Slot = 1 }, new Player { Slot = 2 }, new Player { Slot = 3 } };
        static uint nextSession;
        static bool trace; /* CCTV_OVERLAY_MPV_TRACE: developer-only verbose mpv log (redacted) */
        static int pendingTeardowns;
        static Form parking;

        static Player BySession(uint session)
        {
            foreach (Player p in P)
                if (session != 0 && p.Session == session)
                    return p;
            return null;
        }

        /* ---------------- reporting (loop thread) ---------------- */

        static void ReportError(Player v, string msg)
        {
            if (v.Session == 0 || v.OutcomeReported)
                return;
            v.OutcomeReported = true;
            v.Cb.Error(v.Slot, v.Session, msg); /* normally calls Stop(), which resets v */
        }

        static void ReportEnded(Player v)
        {
            if (v.Session == 0 || v.OutcomeReported)
                return;
            v.OutcomeReported = true;
            v.Cb.Ended(v.Slot, v.Session);
        }

        static void ReportFirstFrame(Player v)
        {
            if (v.FirstReported)
                return;
            v.FirstReported = true;
            v.LastFrameMs = Util.MonotonicMs();
            Log.Debug("video[{0}]: session {1} first frame {2} ms after start", v.Slot, v.Session, Util.MonotonicMs() - v.StartMs);
            v.Cb.FirstFrame(v.Slot, v.Session);
        }

        static bool WatchdogTick(uint session)
        {
            Player v = BySession(session);
            if (v == null || v.Mpv == IntPtr.Zero)
                return false; /* Stop() already cleared it */
            long now = Util.MonotonicMs();
            /* CONNECTING times out from the start time. */
            if (!v.FirstReported && now - v.StartMs > StallConnectMs)
            {
                v.Watchdog = null;
                ReportError(v, "no video received (timed out)");
                return false;
            }
            if (v.FirstReported)
            {
                double pos;
                if (Mpv.mpv_get_property(v.Mpv, Mpv.Z("time-pos"), Mpv.FormatDouble, out pos) >= 0 && pos != v.LastTimePos)
                {
                    v.LastTimePos = pos;
                    v.LastFrameMs = now;
                }
                if (now - v.LastFrameMs > StallLiveMs)
                {
                    v.Watchdog = null;
                    ReportError(v, "stream stalled (no video for 10 s)");
                    return false;
                }
            }
            return true;
        }

        /* ---------------- events ---------------- */

        static void HandleLog(Player v, IntPtr data)
        {
            string prefix = Mpv.Str(Marshal.ReadIntPtr(data, 0));
            string text = Mpv.Str(Marshal.ReadIntPtr(data, 2 * IntPtr.Size)) ?? "";
            int level = Marshal.ReadInt32(data, 3 * IntPtr.Size);
            text = text.TrimEnd('\n', '\r', ' ');

            if (trace)
            {
                /* Developer diagnostic: verbose mpv lines with a time offset, always redacted. */
                if (v.LogLines++ < MaxMpvTraceLines)
                    Log.Info("mpvtrace[{0}] +{1}ms [{2}] {3}", v.Slot, Util.MonotonicMs() - v.StartMs, prefix ?? "?", Log.Redact(text));
                return;
            }
            if (level > Mpv.LogLevelWarn || v.LogLines > MaxMpvLogLines)
                return;
            if (++v.LogLines > MaxMpvLogLines)
            {
                Log.Warn("mpv[{0}]: further messages suppressed for this session", v.Slot);
                return;
            }
            if (text.Contains("401") || text.Contains("Unauthorized"))
                v.AuthFailed = true;
            if (text.Contains("404") || text.Contains("Not Found"))
                v.NotFound = true;
            /* FFmpeg on Windows often prints MSVC errno values: -107 ECONNREFUSED, -138 ETIMEDOUT,
             * -110 EHOSTUNREACH, -118 ENETUNREACH. */
            if (text.Contains("Connection refused") || text.Contains("actively refused") || text.Contains("Error number -107 "))
                v.Refused = true;
            if (text.Contains("timed out") || text.Contains("Timed out") || text.Contains("unreachable") ||
                text.Contains("No route") || text.Contains("Error number -138 ") || text.Contains("Error number -110 ") ||
                text.Contains("Error number -118 "))
                v.TimedOut = true;
            string safe = Log.Redact(text);
            if (level <= Mpv.LogLevelError)
                Log.Error("mpv[{0}:{1}]: {2}", v.Slot, prefix ?? "?", safe);
            else
                Log.Warn("mpv[{0}:{1}]: {2}", v.Slot, prefix ?? "?", safe);
        }

        static string EndFileMessage(Player v, int error)
        {
            if (v.AuthFailed)
                return "authentication failed (check username and password)";
            if (v.NotFound)
                return "stream not found (check channel and path)";
            if (v.Refused)
                return "connection refused (is the recorder address and port right?)";
            if (v.TimedOut)
                return "recorder unreachable (connection timed out)";
            if (error == Mpv.ErrorLoadingFailed || error == Mpv.ErrorUnknownFormat)
                return "could not open the stream (unreachable, refused or timed out)";
            return "playback failed: " + Mpv.ErrorString(error);
        }

        static void CheckAudioTrack(Player v)
        {
            bool has = false;
            long n;
            if (Mpv.mpv_get_property(v.Mpv, Mpv.Z("track-list/count"), Mpv.FormatInt64, out n) < 0)
                return;
            for (long i = 0; i < n && i < 64 && !has; i++)
            {
                IntPtr p = Mpv.mpv_get_property_string(v.Mpv, Mpv.Z("track-list/" + i + "/type"));
                has = Mpv.Str(p) == "audio";
                if (p != IntPtr.Zero)
                    Mpv.mpv_free(p);
            }
            Log.Info("video[{0}]: stream has {1}audio track", v.Slot, has ? "an " : "no ");
            Overlay.SetAudioTrack(v.Slot, has);
        }

        static void Drain(Player v, uint session)
        {
            Interlocked.Exchange(ref v.WakeQueued, 0);
            while (v.Mpv != IntPtr.Zero && v.Session == session)
            {
                IntPtr ev = Mpv.mpv_wait_event(v.Mpv, 0);
                int id = Marshal.ReadInt32(ev, 0);
                if (id == Mpv.EventNone)
                    break;
                IntPtr data = Marshal.ReadIntPtr(ev, 16);
                switch (id)
                {
                    case Mpv.EventLogMessage:
                        HandleLog(v, data);
                        break;
                    case Mpv.EventFileLoaded:
                        Log.Debug("video[{0}]: session {1} file loaded {2} ms after start", v.Slot, v.Session,
                                  Util.MonotonicMs() - v.StartMs);
                        CheckAudioTrack(v);
                        break;
                    case Mpv.EventPlaybackRestart:
                        ReportFirstFrame(v); /* the first frame is decoded and on its way to the screen */
                        break;
                    case Mpv.EventPropertyChange:
                    {
                        string name = Mpv.Str(Marshal.ReadIntPtr(data, 0));
                        int format = Marshal.ReadInt32(data, IntPtr.Size);
                        IntPtr pv = Marshal.ReadIntPtr(data, 2 * IntPtr.Size);
                        if (name == "hwdec-current" && format == Mpv.FormatString && pv != IntPtr.Zero)
                        {
                            string dec = Mpv.Str(Marshal.ReadIntPtr(pv));
                            if (!string.IsNullOrEmpty(dec))
                                Log.Info("video[{0}]: decoder '{1}'", v.Slot, dec);
                        }
                        break;
                    }
                    case Mpv.EventShutdown:
                        ReportError(v, "video engine shut down");
                        return;
                    case Mpv.EventEndFile:
                    {
                        int reason = Marshal.ReadInt32(data, 0);
                        int error = Marshal.ReadInt32(data, 4);
                        if (reason == Mpv.EndReasonError)
                        {
                            ReportError(v, EndFileMessage(v, error)); /* may stop the session */
                            return;
                        }
                        if (reason == Mpv.EndReasonEof)
                        {
                            ReportEnded(v);
                            return;
                        }
                        break;
                    }
                }
            }
        }

        /* ---------------- teardown ---------------- */

        static Form Parking()
        {
            if (parking == null || parking.IsDisposed)
            {
                parking = new Form();
                parking.ShowInTaskbar = false;
                parking.FormBorderStyle = FormBorderStyle.None;
                parking.StartPosition = FormStartPosition.Manual;
                parking.Location = new System.Drawing.Point(-32000, -32000);
                parking.Size = new System.Drawing.Size(16, 16);
                parking.CreateControl();
                IntPtr h = parking.Handle; /* created, never shown */
                GC.KeepAlive(h);
            }
            return parking;
        }

        public static void Stop(int slot)
        {
            if (slot < 0 || slot >= MaxSlots)
                return;
            Player v = P[slot];
            if (v.Session == 0 && v.Mpv == IntPtr.Zero)
                return;
            uint s = v.Session;

            if (v.Watchdog != null)
                v.Watchdog.Remove();
            v.Watchdog = null;
            IntPtr h = v.Mpv;
            Control host = v.Host;
            Mpv.WakeupCb keep = v.Wakeup;
            v.Mpv = IntPtr.Zero;
            v.Host = null;
            v.Session = 0;
            v.FirstReported = v.OutcomeReported = false;

            if (host != null && !host.IsDisposed)
            {
                host.Visible = false;
                try { host.Parent = Parking(); } catch (Exception) { }
            }
            if (h != IntPtr.Zero)
            {
                Mpv.mpv_set_wakeup_callback(h, null, IntPtr.Zero);
                Interlocked.Increment(ref pendingTeardowns);
                long t0 = Util.MonotonicMs();
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        Mpv.mpv_terminate_destroy(h); /* joins mpv's threads; closes the stream */
                    }
                    finally
                    {
                        GC.KeepAlive(keep);
                        Loop.Idle(delegate
                        {
                            if (host != null && !host.IsDisposed)
                                host.Dispose();
                            Log.Debug("video[{0}]: session {1} stopped in {2} ms", slot, s, Util.MonotonicMs() - t0);
                        });
                        Interlocked.Decrement(ref pendingTeardowns);
                    }
                });
            }
            else if (host != null && !host.IsDisposed)
            {
                host.Dispose();
            }
        }

        public static void StopAll()
        {
            for (int i = 0; i < MaxSlots; i++)
                Stop(i);
        }

        /// <summary>Daemon exit: wait (pumping messages) until every player has shut down.</summary>
        public static void WaitForTeardown(int maxMs)
        {
            long end = Util.MonotonicMs() + maxMs;
            while (Thread.VolatileRead(ref pendingTeardowns) > 0 && Util.MonotonicMs() < end)
            {
                Application.DoEvents();
                Thread.Sleep(5);
            }
        }

        /// <summary>The overlay window went away behind our back: every running player lost its surface.</summary>
        public static void SurfaceLost()
        {
            foreach (Player v in P)
            {
                if (v.Session == 0)
                    continue;
                uint s = v.Session;
                Loop.Idle(delegate
                {
                    Player p = BySession(s);
                    if (p != null)
                        ReportError(p, "overlay surface was lost");
                });
            }
        }

        public static void SetAudio(int slot, bool on)
        {
            if (slot < 0 || slot >= MaxSlots || P[slot].Mpv == IntPtr.Zero)
                return;
            int e = Mpv.mpv_set_property_string(P[slot].Mpv, Mpv.Z("aid"), Mpv.Z(on ? "auto" : "no"));
            if (e < 0)
                Log.Warn("video[{0}]: cannot switch audio {1}: {2}", slot, on ? "on" : "off", Mpv.ErrorString(e));
        }

        /* ---------------- start ---------------- */

        static void SetOpt(IntPtr h, string name, string value)
        {
            int e = Mpv.mpv_set_option_string(h, Mpv.Z(name), Mpv.Z(value));
            if (e < 0)
                Log.Warn("video: mpv option '{0}' rejected: {1}", name, Mpv.ErrorString(e));
        }

        public static uint Start(int slot, Config cfg, bool audio, VideoUrlBuilder build, IVideoCallbacks cb, out string error)
        {
            error = null;
            if (slot < 0 || slot >= MaxSlots)
            {
                error = "invalid video slot";
                return 0;
            }
            Player v = P[slot];
            Stop(slot);

            byte[] url;
            try
            {
                url = build(slot);
            }
            catch (CctvException ce)
            {
                error = ce.Message;
                return 0;
            }

            long t0 = Util.MonotonicMs();
            Control tile = Overlay.TileHost(slot);
            if (tile == null)
            {
                Util.Wipe(url);
                error = "no overlay window to render into";
                return 0;
            }
            /* A fresh child window per session: mpv embeds its video window in it. */
            var host = new Panel();
            host.BackColor = System.Drawing.Color.Black;
            host.Dock = DockStyle.Fill;
            host.Enabled = false;
            tile.Controls.Add(host);
            host.CreateControl();
            long wid = host.Handle.ToInt64();

            IntPtr h = Mpv.mpv_create();
            if (h == IntPtr.Zero)
            {
                Util.Wipe(url);
                host.Dispose();
                error = "mpv_create failed";
                return 0;
            }
            SetOpt(h, "config", "no");
            SetOpt(h, "terminal", "no");
            SetOpt(h, "load-scripts", "no");
            SetOpt(h, "ytdl", "no");
            SetOpt(h, "osc", "no");
            SetOpt(h, "osd-level", "0");
            SetOpt(h, "input-default-bindings", "no");
            SetOpt(h, "input-vo-keyboard", "no");
            SetOpt(h, "input-cursor", "no");
            SetOpt(h, "cursor-autohide", "no");
            SetOpt(h, "taskbar-progress", "no");
            SetOpt(h, "stop-screensaver", "no"); /* showing cameras must not keep the screen awake */
            SetOpt(h, "wid", wid.ToString());
            /* The overlay is a layered (click-through) window: DXGI flip-model swap chains do not present into
             * layered windows, so mpv must use the bit-blt model. */
            SetOpt(h, "d3d11-flip", "no");
            SetOpt(h, "profile", "low-latency");
            SetOpt(h, "hwdec", cfg.MpvHwdec);
            SetOpt(h, "cache", "no");
            SetOpt(h, "demuxer-readahead-secs", "0.2");
            /* Audio is opt-in (`cctv-overlay audio`): with aid=no mpv opens no audio output at all. */
            SetOpt(h, "aid", audio ? "auto" : "no");
            SetOpt(h, "audio-client-name", "cctv-overlay");
            SetOpt(h, "sid", "no");
            SetOpt(h, "rtsp-transport", cfg.Transport);
            /* RTSP socket timeout in microseconds (not network-timeout, which RTSP ignores). */
            SetOpt(h, "demuxer-lavf-o", "fflags=+nobuffer,timeout=" + RtspTimeoutUs);
            SetOpt(h, "keep-open", "no");
            SetOpt(h, "idle", "yes");
            SetOpt(h, "force-window", "no");

            /* Developer-only tuning hook: CCTV_OVERLAY_MPV_OPTS="name=value;name=value", applied last. Values are
             * not logged (names only). Not documented for end users. */
            string dev = Util.Env("CCTV_OVERLAY_MPV_OPTS");
            if (!string.IsNullOrEmpty(dev))
            {
                foreach (string pair in dev.Split(';'))
                {
                    int eq = pair.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    string n = pair.Substring(0, eq);
                    Log.Info("video: developer mpv option override '{0}'", n);
                    SetOpt(h, n, pair.Substring(eq + 1));
                }
            }

            int e = Mpv.mpv_initialize(h);
            if (e < 0)
            {
                Util.Wipe(url);
                error = "mpv_initialize failed: " + Mpv.ErrorString(e);
                Mpv.mpv_terminate_destroy(h); /* no window exists yet: safe on this thread */
                host.Dispose();
                return 0;
            }
            string traceEnv = Util.Env("CCTV_OVERLAY_MPV_TRACE");
            /* Only the exact values 1 or trace enable this; "0" and "" must not. */
            trace = traceEnv == "1" || traceEnv == "trace";
            Mpv.mpv_request_log_messages(h, Mpv.Z(trace ? (traceEnv == "trace" ? "trace" : "v") : "warn"));
            Mpv.mpv_observe_property(h, 0, Mpv.Z("hwdec-current"), Mpv.FormatString);

            v.Slot = slot;
            v.Mpv = h;
            v.Host = host;
            v.Cb = cb;
            v.Session = ++nextSession;
            v.FirstReported = v.OutcomeReported = false;
            v.AuthFailed = v.NotFound = v.Refused = v.TimedOut = false;
            v.LogLines = 0;
            v.LastTimePos = double.NaN;
            v.StartMs = t0;
            uint session = v.Session;

            Player captured = v;
            v.Wakeup = delegate(IntPtr d)
            {
                if (Interlocked.CompareExchange(ref captured.WakeQueued, 1, 0) == 0)
                    Loop.Idle(() => Drain(captured, session));
            };
            Mpv.mpv_set_wakeup_callback(h, v.Wakeup, IntPtr.Zero);

            /* The credentialed URL exists only for this call (mpv copies the arguments), then is wiped. */
            IntPtr cmd0 = Marshal.AllocHGlobal(16), urlp = Marshal.AllocHGlobal(url.Length + 1);
            byte[] lf = Mpv.Z("loadfile");
            Marshal.Copy(lf, 0, cmd0, lf.Length);
            Marshal.Copy(url, 0, urlp, url.Length);
            Marshal.WriteByte(urlp, url.Length, 0);
            e = Mpv.mpv_command_async(h, 0, new[] { cmd0, urlp, IntPtr.Zero });
            Marshal.Copy(new byte[url.Length + 1], 0, urlp, url.Length + 1);
            Util.Wipe(url);
            Marshal.FreeHGlobal(urlp);
            Marshal.FreeHGlobal(cmd0);
            if (e < 0)
            {
                error = "could not start playback: " + Mpv.ErrorString(e);
                Stop(slot);
                return 0;
            }

            v.LastFrameMs = Util.MonotonicMs();
            v.Watchdog = Loop.Timeout(1000, () => WatchdogTick(session));
            Log.Debug("video[{0}]: session {1} started, setup took {2} ms (hwdec={3}, rtsp={4}, audio={5})",
                      slot, session, v.LastFrameMs - t0, cfg.MpvHwdec, cfg.Transport, audio ? "on" : "off");
            return session;
        }
    }
}
#endif
