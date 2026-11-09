// Video engine interface + the simulated engine used by `build.ps1 check` (compiled with /define:STUB).
using System;

namespace CctvOverlay
{
    /// <summary>Returns the credentialed URL as UTF-8 bytes (the engine wipes them); throws CctvException.</summary>
    internal delegate byte[] VideoUrlBuilder(int slot);

    internal interface IVideoCallbacks
    {
        void FirstFrame(int slot, uint session);
        void Error(int slot, uint session, string msg);
        void Ended(int slot, uint session);
    }

#if STUB
    /* Simulated engine. Knobs (environment, read at every start):
     *   CCTV_OVERLAY_STUB_DELAY_MS=N     first-frame delay, default 150
     *   CCTV_OVERLAY_STUB_FAIL=1         every start fails after the delay ("always" also accepted)
     *   CCTV_OVERLAY_STUB_FAIL=first:N   only the first N starts after the value was set fail
     *   CCTV_OVERLAY_STUB_FAIL_SLOT=N    restrict CCTV_OVERLAY_STUB_FAIL to slot N (grid tests)
     *   CCTV_OVERLAY_STUB_DROP_AFTER_MS=N  after the first frame, simulate a stall/error N ms later
     */
    internal static class Video
    {
        public const int MaxSlots = 4;

        sealed class Slot
        {
            public int Index;
            public uint Session;
            public LoopTimer SrcFrame, SrcDrop;
            public bool Audio;
            public IVideoCallbacks Cb;
        }

        static readonly Slot[] S = { new Slot { Index = 0 }, new Slot { Index = 1 }, new Slot { Index = 2 }, new Slot { Index = 3 } };
        static uint nextSession;
        static int starts;
        static string lastSpec;
        static int failed;

        static long EnvLong(string name, long def)
        {
            string v = Util.Env(name);
            long r;
            return (!string.IsNullOrEmpty(v) && Util.TryParseInt64Strict(v, out r)) ? r : def;
        }

        static bool ShouldFail(int slot)
        {
            string v = Util.Env("CCTV_OVERLAY_STUB_FAIL");
            if (string.IsNullOrEmpty(v) || v == "0")
                return false;
            long fs = EnvLong("CCTV_OVERLAY_STUB_FAIL_SLOT", -1);
            if (fs >= 0 && fs != slot)
                return false;
            if (v.StartsWith("first:", StringComparison.Ordinal))
            {
                if (lastSpec != v)
                { /* new setting: count from zero again */
                    lastSpec = v;
                    failed = 0;
                }
                long n;
                Util.TryParseInt64Strict(v.Substring(6), out n);
                return failed++ < n;
            }
            return true;
        }

        static Slot BySession(uint session)
        {
            foreach (Slot s in S)
                if (session != 0 && s.Session == session)
                    return s;
            return null;
        }

        public static uint Start(int slot, Config cfg, bool audio, VideoUrlBuilder build, IVideoCallbacks cb, out string error)
        {
            error = null;
            Stop(slot);
            byte[] url;
            try
            {
                url = build(slot); /* exercises the real credential path */
            }
            catch (CctvException e)
            {
                error = e.Message;
                return 0;
            }
            Util.Wipe(url);

            Slot s = S[slot];
            s.Cb = cb;
            s.Audio = audio;
            starts++;
            s.Session = ++nextSession;
            uint session = s.Session;
            long delay = EnvLong("CCTV_OVERLAY_STUB_DELAY_MS", 150);
            s.SrcFrame = Loop.Timeout((int)Math.Max(0, delay), delegate
            {
                Slot x = BySession(session);
                if (x == null)
                    return false;
                x.SrcFrame = null;
                if (ShouldFail(x.Index))
                {
                    x.Cb.Error(x.Index, x.Session, "simulated stream failure");
                    return false;
                }
                long drop = EnvLong("CCTV_OVERLAY_STUB_DROP_AFTER_MS", 0);
                if (drop > 0)
                {
                    x.SrcDrop = Loop.Timeout((int)drop, delegate
                    {
                        Slot y = BySession(session);
                        if (y == null)
                            return false;
                        y.SrcDrop = null;
                        y.Cb.Error(y.Index, y.Session, "simulated stream stall");
                        return false;
                    });
                }
                x.Cb.FirstFrame(x.Index, x.Session);
                return false;
            });
            Log.Debug("video(stub): slot {0} start session {1}{2}", slot, s.Session, audio ? " (audio)" : "");
            return s.Session;
        }

        public static void Stop(int slot)
        {
            if (slot < 0 || slot >= MaxSlots)
                return;
            Slot s = S[slot];
            if (s.SrcFrame != null)
                s.SrcFrame.Remove();
            if (s.SrcDrop != null)
                s.SrcDrop.Remove();
            s.SrcFrame = s.SrcDrop = null;
            if (s.Session != 0)
                Log.Debug("video(stub): slot {0} stop session {1}", slot, s.Session);
            s.Session = 0;
            s.Audio = false;
        }

        public static void StopAll()
        {
            for (int i = 0; i < MaxSlots; i++)
                Stop(i);
        }

        public static void WaitForTeardown(int maxMs) { }

        public static void SetAudio(int slot, bool on)
        {
            if (slot >= 0 && slot < MaxSlots && S[slot].Session != 0)
            {
                S[slot].Audio = on;
                Log.Debug("video(stub): slot {0} audio {1}", slot, on ? "on" : "off");
            }
        }

        /* Test-only inspection. */
        public static bool StubActive
        {
            get
            {
                foreach (Slot s in S)
                    if (s.Session != 0)
                        return true;
                return false;
            }
        }

        public static int StubActiveCount
        {
            get
            {
                int n = 0;
                foreach (Slot s in S)
                    n += s.Session != 0 ? 1 : 0;
                return n;
            }
        }

        public static bool StubAudio(int slot) { return slot >= 0 && slot < MaxSlots && S[slot].Audio; }

        public static int StubStartCount { get { return starts; } }
    }

    /* Overlay stub: records what the real overlay would show. */
    internal static class Overlay
    {
        static bool visible;
        static int ntiles;
        static string label = "";
        static bool audio;

        public static Action CloseRequested;

        public static bool Init(out string error)
        {
            error = null;
            return true;
        }

        public static void Show(Config cfg, int n, string[] labels)
        {
            visible = true;
            ntiles = n;
            var parts = new string[n];
            for (int i = 0; i < n; i++)
                parts[i] = labels[i] ?? "";
            label = string.Join("|", parts);
        }

        public static void Hide()
        {
            Video.StopAll();
            visible = false;
            ntiles = 0;
        }

        public static void SetState(int tile, AppState state, string detail) { }

        public static void SetAudio(bool enabled, bool mutedView)
        {
            audio = enabled;
        }

        public static void Shutdown() { }

        /* Test-only inspection. */
        public static int StubTiles { get { return ntiles; } }
        public static bool StubAudio { get { return audio; } }
        public static bool StubVisible { get { return visible; } }
        public static string StubLabel { get { return label; } }

        /// <summary>The window went away behind our back (the compositor destroyed the surface / display change).</summary>
        public static void StubSimulateLost()
        {
            visible = false;
            ntiles = 0;
        }
    }
#endif
}
