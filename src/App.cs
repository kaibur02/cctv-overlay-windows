// Views (grid / single camera), the per-tile state machine with reconnect backoff, and the IPC command handler.
using System;
using System.Collections.Generic;

namespace CctvOverlay
{
    internal enum AppState { Hidden = 0, Connecting, Live, Reconnecting, Error }

    internal sealed class AppOptions
    {
        public bool OverlayEnabled = true;
        public double BackoffScale = 1.0;
        public Action QuitCb;
    }

    internal sealed class App : IVideoCallbacks
    {
        public const int MaxRetries = 5;
        static readonly int[] BackoffMs = { 500, 1000, 2000, 4000, 8000 };

        /* One video tile. The single-camera view uses tile 0 only; the grid view uses tiles 0..n-1. Each tile
         * has its own session, state and reconnect backoff, so one offline camera never takes the grid down. */
        sealed class Tile
        {
            public int Slot;
            public int Cam;
            public AppState State;
            public uint Session;
            public int Retries;
            public LoopTimer RetrySrc;
            public LoopTimer LiveSrc; /* resets the backoff only after a sustained LIVE period */
            public string Error;
        }

        Config cfg;
        CameraList cams;
        readonly AppOptions opt;
        bool visible;
        bool grid;   /* the selected view is the grid */
        int ntiles;  /* tiles in use while visible */
        readonly Tile[] t = new Tile[Video.MaxSlots];
        AppState state; /* cached aggregate, for logging */
        bool audio;
        readonly int stableMs;
        long startMs;

        public static string StateName(AppState s)
        {
            switch (s)
            {
                case AppState.Hidden: return "hidden";
                case AppState.Connecting: return "connecting";
                case AppState.Live: return "live";
                case AppState.Reconnecting: return "reconnecting";
                default: return "error";
            }
        }

        /* ---- accessors ---- */

        public AppState State { get { return state; } }
        public bool GridView { get { return grid; } }
        public bool Audio { get { return audio; } }
        public Config Config { get { return cfg; } }
        public Camera CurrentCamera { get { return cams.CurrentCamera; } }
        public int CurrentIndex { get { return cams.Current; } }
        public bool Visible { get { return visible; } }

        public int RetryCount
        {
            get
            {
                int r = 0;
                for (int i = 0; i < ntiles; i++)
                    r = Math.Max(r, t[i].Retries);
                return r;
            }
        }

        public string LastError
        {
            get
            {
                for (int i = 0; i < ntiles; i++)
                    if (t[i].Error != null)
                        return t[i].Error;
                return null;
            }
        }

        int GridCount { get { return cfg.GridCount; } }

        string TileName(Tile tile)
        {
            Camera c = cams.Get(tile.Cam);
            return c != null ? c.Name : "?";
        }

        /* ---- state ---- */

        AppState Aggregate()
        {
            bool live = false, conn = false, rec = false;
            if (!visible)
                return AppState.Hidden;
            for (int i = 0; i < ntiles; i++)
            {
                live |= t[i].State == AppState.Live;
                conn |= t[i].State == AppState.Connecting;
                rec |= t[i].State == AppState.Reconnecting;
            }
            return live ? AppState.Live : conn ? AppState.Connecting : rec ? AppState.Reconnecting : AppState.Error;
        }

        void RefreshState()
        {
            AppState s = Aggregate();
            if (s == state)
                return;
            if (grid && visible)
                Log.Info("state {0} -> {1} (grid view, {2} cameras)", StateName(state), StateName(s), ntiles);
            else
                Log.Info("state {0} -> {1} (camera '{2}')", StateName(state), StateName(s), CurrentCamera.Name);
            state = s;
        }

        static void TileSetError(Tile tile, string msg)
        {
            tile.Error = msg != null ? Log.Redact(msg) : null;
        }

        void TileSetState(Tile tile, AppState s)
        {
            if (tile.State != s && grid)
                Log.Debug("tile {0} ('{1}'): {2} -> {3}", tile.Slot, TileName(tile), StateName(tile.State), StateName(s));
            tile.State = s;
            if (opt.OverlayEnabled && visible && s != AppState.Hidden)
                Overlay.SetState(tile.Slot, s, tile.Error);
            RefreshState();
        }

        byte[] BuildUrl(int slot)
        {
            Camera c = cams.Get(t[slot].Cam);
            return CameraList.ResolveUrl(cfg, c, grid ? cfg.GridSubtype : -1);
        }

        void StopTile(Tile tile)
        {
            if (tile.RetrySrc != null)
            {
                tile.RetrySrc.Remove();
                tile.RetrySrc = null;
            }
            if (tile.LiveSrc != null)
            {
                tile.LiveSrc.Remove();
                tile.LiveSrc = null;
            }
            Video.Stop(tile.Slot);
            tile.Session = 0;
        }

        void ResetTile(Tile tile)
        {
            StopTile(tile);
            tile.Retries = 0;
            TileSetError(tile, null);
            tile.State = AppState.Hidden;
        }

        /* Begin a connection attempt for a tile (state must already allow it). */
        void StartTile(Tile tile)
        {
            StopTile(tile);
            string err;
            tile.Session = Video.Start(tile.Slot, cfg, audio && !grid, BuildUrl, this, out err);
            if (tile.Session == 0)
            {
                /* Configuration-class failure (e.g. missing password): retrying cannot help. */
                TileSetError(tile, err ?? "could not start the video stream");
                Log.Warn("cannot start stream for '{0}': {1}", TileName(tile), tile.Error);
                TileSetState(tile, AppState.Error);
                return;
            }
            TileSetState(tile, AppState.Connecting);
        }

        void StreamFailed(Tile tile, string msg)
        {
            StopTile(tile); /* no decode and no network while backing off */
            TileSetError(tile, msg);
            Log.Warn("stream problem on '{0}': {1}", TileName(tile), tile.Error);

            if (cfg.Reconnect && tile.Retries < MaxRetries)
            {
                int ms = (int)(BackoffMs[tile.Retries] * opt.BackoffScale);
                tile.Retries++;
                TileSetState(tile, AppState.Reconnecting);
                Log.Info("retry {0}/{1} in {2} ms", tile.Retries, MaxRetries, ms);
                Tile captured = tile;
                tile.RetrySrc = Loop.Timeout(ms > 0 ? ms : 1, delegate
                {
                    captured.RetrySrc = null;
                    if (captured.State == AppState.Reconnecting)
                        StartTile(captured);
                    return false;
                });
            }
            else
            {
                TileSetState(tile, AppState.Error);
            }
        }

        Tile TileFor(int slot, uint session)
        {
            if (slot >= ntiles || !visible || session != t[slot].Session)
                return null;
            return t[slot];
        }

        void IVideoCallbacks.FirstFrame(int slot, uint session)
        {
            Tile tile = TileFor(slot, session);
            if (tile == null || tile.State != AppState.Connecting)
                return;
            TileSetError(tile, null);
            TileSetState(tile, AppState.Live);
            /* Do not reset the backoff on the first frame: a stream that delivers one frame and drops would
             * otherwise restart at 0.5 s forever. Reset only after it has stayed live for stableMs. */
            if (stableMs == 0)
            {
                tile.Retries = 0;
            }
            else if (tile.Retries > 0 && tile.LiveSrc == null)
            {
                tile.LiveSrc = Loop.Timeout(stableMs, delegate
                {
                    tile.LiveSrc = null;
                    if (tile.State == AppState.Live)
                    {
                        tile.Retries = 0;
                        Log.Debug("stream stable; reconnect backoff reset");
                    }
                    return false;
                });
            }
        }

        void IVideoCallbacks.Error(int slot, uint session, string msg)
        {
            Tile tile = TileFor(slot, session);
            if (tile == null || (tile.State != AppState.Connecting && tile.State != AppState.Live))
                return;
            StreamFailed(tile, msg ?? "stream error");
        }

        void IVideoCallbacks.Ended(int slot, uint session)
        {
            ((IVideoCallbacks)this).Error(slot, session, "stream ended");
        }

        /* Open or (re)connect the selected view as a user action: resets every tile's backoff. */
        void OpenView()
        {
            var labels = new string[Video.MaxSlots];

            for (int i = 0; i < Video.MaxSlots; i++)
                ResetTile(t[i]); /* the overlay may rebuild its tiles: no slot may stay alive */
            if (grid && GridCount == 0)
                grid = false; /* e.g. reload removed cameras */
            if (grid)
            {
                ntiles = GridCount;
                for (int i = 0; i < ntiles; i++)
                    t[i].Cam = cfg.GridIdx[i];
            }
            else
            {
                ntiles = 1;
                t[0].Cam = cams.Current;
            }
            for (int i = 0; i < ntiles; i++)
                labels[i] = TileName(t[i]);

            visible = true;
            startMs = Util.MonotonicMs();
            /* Idempotent: re-shows a window that exists, and re-creates one that was destroyed behind our back. */
            if (opt.OverlayEnabled)
            {
                Overlay.Show(cfg, ntiles, labels);
                Overlay.SetAudio(audio, grid);
            }
            for (int i = 0; i < ntiles; i++)
                StartTile(t[i]); /* CONNECTING, or ERROR when the stream cannot even start */
            RefreshState();
        }

        /* ---- public ---- */

        public App(Config cfg, AppOptions opt)
        {
            this.cfg = cfg;
            cams = new CameraList(cfg);
            this.opt = opt;
            for (int i = 0; i < Video.MaxSlots; i++)
                t[i] = new Tile { Slot = i };
            if (!(opt.BackoffScale > 0) || opt.BackoffScale > 600)
                opt.BackoffScale = 1.0;
            /* Test knob; 0 restores the old "reset on first frame" behaviour. */
            string stable = Util.Env("CCTV_OVERLAY_LIVE_STABLE_MS");
            stableMs = 15000;
            long v;
            if (!string.IsNullOrEmpty(stable) && Util.TryParseInt64Strict(stable, out v))
                stableMs = (int)Math.Max(0, Math.Min(v, 600000));
            state = AppState.Hidden;
            audio = cfg.Audio;
            grid = cfg.GridCount > 0;
            startMs = Util.MonotonicMs();
            Log.Level = cfg.LogLevel;
        }

        public void Hide()
        {
            if (!visible)
                return;
            for (int i = 0; i < Video.MaxSlots; i++)
                ResetTile(t[i]);
            visible = false;
            RefreshState();
            ntiles = 0;
            if (opt.OverlayEnabled)
                Overlay.Hide();
        }

        public void Free()
        {
            Hide();
            cfg.Forget();
        }

        void SelectAndOpen(bool changed)
        {
            switch (state)
            {
                case AppState.Hidden:
                case AppState.Error:
                case AppState.Reconnecting:
                    OpenView();
                    break;
                case AppState.Connecting:
                case AppState.Live:
                    if (changed)
                        OpenView();
                    break;
            }
        }

        /* The view a closed overlay opens on: the grid when there is one. */
        void OpenStartView()
        {
            grid = GridCount > 0;
            OpenView();
        }

        public void Toggle()
        {
            if (!visible)
                OpenStartView();
            else if (state == AppState.Error)
                OpenView(); /* retry the same view */
            else
                Hide();
        }

        /// <summary>Throws CctvException for an unknown camera.</summary>
        public void Show(string spec)
        {
            bool changed = grid;
            if (!string.IsNullOrEmpty(spec))
            {
                int idx;
                if (!cams.Find(spec, out idx))
                    throw new CctvException("unknown camera '" + spec + "' (see 'cctv-overlay list')");
                changed |= idx != cams.Current;
                cams.SetCurrent(idx);
            }
            grid = false;
            SelectAndOpen(changed);
        }

        public void Grid()
        {
            if (GridCount == 0)
                throw new CctvException("no grid view: it needs at least 2 cameras and [grid] enabled = true");
            bool changed = !grid;
            grid = true;
            SelectAndOpen(changed);
        }

        void Step(bool forward)
        {
            int n = cams.Count;

            if (GridCount == 0)
            { /* plain camera ring */
                int before = cams.Current;
                if (forward)
                    cams.Next();
                else
                    cams.Prev();
                SelectAndOpen(before != cams.Current);
                return;
            }
            if (!visible)
            {
                OpenStartView();
                return;
            }
            /* Ring: grid, camera 0, ..., camera n-1, back to the grid. */
            if (grid)
            {
                grid = false;
                cams.SetCurrent(forward ? 0 : n - 1);
            }
            else if (cams.Current == (forward ? n - 1 : 0))
            {
                grid = true;
            }
            else if (forward)
            {
                cams.Next();
            }
            else
            {
                cams.Prev();
            }
            SelectAndOpen(true);
        }

        public void Next() { Step(true); }

        public void Prev() { Step(false); }

        public void SetAudio(bool on)
        {
            audio = on;
            Log.Info("audio {0}", on ? "enabled" : "disabled");
            if (!visible)
                return;
            if (!grid && t[0].Session != 0)
                Video.SetAudio(0, on);
            if (opt.OverlayEnabled)
                Overlay.SetAudio(on, grid);
        }

        /// <summary>Re-reads config and secrets. Throws CctvException (old config stays) on an invalid file.</summary>
        public void Reload()
        {
            Config nc = Config.Load(cfg.ConfigPath);

            string curId = CurrentCamera.Id;
            var nl = new CameraList(nc);
            int idx;
            bool found = nl.FindId(curId, out idx);
            nl.SetCurrent(found ? idx : 0);

            Config old = cfg;
            cfg = nc;
            cams = nl;
            old.Forget();
            Log.Level = nc.LogLevel;
            Log.Info("configuration reloaded ({0} cameras)", nc.Cameras.Count);
            if (Reloaded != null)
                Reloaded(nc);

            if (!visible)
            {
                grid = GridCount > 0;
                return;
            }
            if (grid || state == AppState.Error || state == AppState.Reconnecting || !found)
            {
                /* grid: tiles may have changed; error: e.g. the password was just fixed; gone: first camera */
                OpenView();
            }
            else
            {
                t[0].Cam = idx;
                if (opt.OverlayEnabled)
                    Overlay.Show(cfg, 1, new[] { CurrentCamera.Name }); /* same layout: relabels only */
            }
        }

        /// <summary>Raised after a successful reload (the daemon re-registers hotkeys).</summary>
        public event Action<Config> Reloaded;

        public void RequestQuit()
        {
            if (opt.QuitCb != null)
                opt.QuitCb();
        }

        /* ---- IPC glue ---- */

        JObj StatusObject()
        {
            var o = new JObj();
            Camera c = CurrentCamera;
            string err = LastError;

            o.Set("ok", true);
            o.Set("visible", visible);
            o.Set("state", StateName(state));
            o.Set("view", grid ? "grid" : "camera");
            o.Set("camera", c.Name);
            o.Set("camera_index", cams.Current);
            o.Set("camera_count", cams.Count);
            o.Set("audio", audio);
            o.Set("error", err);
            o.Set("retries", RetryCount);
            o.Set("uptime_ms", Util.MonotonicMs() - startMs);

            var tiles = new List<object>();
            for (int i = 0; visible && i < ntiles; i++)
            {
                var to = new JObj();
                to.Set("camera", TileName(t[i]));
                to.Set("state", StateName(t[i].State));
                tiles.Add(to);
            }
            o.Set("tiles", tiles);
            return o;
        }

        JObj ListObject()
        {
            var o = new JObj();
            var arr = new List<object>();

            for (int i = 0; i < cams.Count; i++)
            {
                Camera c = cams.Get(i);
                bool inGrid = false;
                for (int g = 0; g < cfg.GridCount; g++)
                    inGrid |= cfg.GridIdx[g] == i;
                var co = new JObj();
                co.Set("index", i);
                co.Set("id", c.Id);
                co.Set("name", c.Name);
                co.Set("channel", c.Channel);
                co.Set("current", i == cams.Current);
                co.Set("in_grid", inGrid);
                arr.Add(co);
            }
            o.Set("ok", true);
            o.Set("cameras", arr);
            return o;
        }

        static JObj ErrorReply(string msg)
        {
            return new JObj().Set("ok", false).Set("error", msg);
        }

        public JObj Handle(string cmd, JObj req)
        {
            try
            {
                switch (cmd)
                {
                    case "ping":
                        return new JObj().Set("ok", true);
                    case "status":
                        return StatusObject();
                    case "list":
                        return ListObject();
                    case "toggle":
                        Toggle();
                        break;
                    case "next":
                        Next();
                        break;
                    case "prev":
                        Prev();
                        break;
                    case "grid":
                        Grid();
                        break;
                    case "hide":
                        Hide();
                        break;
                    case "show":
                        Show(req != null ? req.GetString("camera") : null);
                        break;
                    case "audio":
                    {
                        /* optional "mode": "on" | "off" | "toggle" (default) */
                        string mode = req != null ? req.GetString("mode") : null;
                        if (mode == null || mode == "toggle")
                            SetAudio(!audio);
                        else if (mode == "on")
                            SetAudio(true);
                        else if (mode == "off")
                            SetAudio(false);
                        else
                            return ErrorReply("audio: use on, off or toggle");
                        break;
                    }
                    case "reload":
                        Reload();
                        break;
                    case "quit":
                        /* the loop is stopped by AfterReply() once the reply is written */
                        break;
                    default:
                        return ErrorReply("unknown command '" + (cmd.Length > 32 ? cmd.Substring(0, 32) : cmd) + "'");
                }
            }
            catch (CctvException e)
            {
                return ErrorReply(e.Message);
            }
            return StatusObject();
        }

        public void AfterReply(string cmd)
        {
            if (cmd == "quit")
                RequestQuit();
        }
    }
}
