// config.ini / secrets.ini: strict loading and validation, URL path templates.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace CctvOverlay
{
    internal sealed class Camera
    {
        public string Id;      /* group suffix: [camera.<id>] */
        public string Name;    /* display name, unique (case-insensitive) */
        public int Channel;    /* >= 1; 0 when only a url override is used */
        public int Subtype = -1; /* -1 = use [stream] subtype */
        public string Url;     /* optional full RTSP URL override (discouraged: may contain a secret) */
        public string Host;    /* optional per-camera host (standalone IP cameras); null = [recorder] host */
        public int Port;       /* 0 = [recorder] port */
        public string Path;    /* optional per-camera path/template; null = [recorder] path */
    }

    internal sealed class Config
    {
        public const int GridMax = 4;

        public string ConfigPath;
        public string SecretsPath;

        /* [general] */
        public LogLevel LogLevel = LogLevel.Info;
        public string Monitor = "focused";
        public bool StartHidden = true;

        /* [overlay] */
        public int Width = 480, Height = 270;
        public string Anchor = "top-right";
        public int MarginX = 24, MarginY = 24;
        public string Layer = "overlay";
        public bool ShowLabel = true, ShowStatus = true;
        public double Opacity = 1.0;

        /* [stream] */
        public string Transport = "tcp";
        public int Subtype = 1;
        public string Hwdec = "d3d11va";
        public bool PreviewSnapshot = true, PreloadNext, Reconnect = true;
        public bool Audio;

        /* [grid] */
        public bool GridEnabled = true;
        public int GridSubtype = -1;
        public int GridCount;                      /* resolved: 0 = no grid view */
        public int[] GridIdx = new int[GridMax];   /* camera indexes shown in the grid, in tile order */

        /* [recorder] */
        public string Host;                        /* may be null only if every camera has a url/host */
        public int Port = 554;
        public string PathTemplate = "/cam/realmonitor";
        public string Username = "";

        /* [keys] (Windows: global hotkeys; null = not bound) */
        public Hotkey KeyToggle, KeyNext, KeyPrev, KeyAudio;

        /* Secret. Never print, log or copy this; only Camera resolving reads it. */
        public string Password;

        public readonly List<Camera> Cameras = new List<Camera>();

        static readonly string[] Anchors = { "top-left", "top-right", "bottom-left", "bottom-right" };
        static readonly string[] Layers = { "overlay", "top" };
        static readonly string[] Transports = { "tcp", "udp" };
        static readonly string[] Hwdecs = { "d3d11va", "d3d11va-copy", "dxva2", "dxva2-copy", "nvdec", "nvdec-copy",
                                            "auto-safe", "auto-copy", "no", "vaapi", "vaapi-copy" };

        static readonly string[] KGeneral = { "log_level", "monitor", "start_hidden" };
        static readonly string[] KOverlay = { "width", "height", "anchor", "margin_x", "margin_y", "layer",
                                              "show_label", "show_status", "opacity" };
        static readonly string[] KStream = { "transport", "subtype", "hwdec", "preview_snapshot", "preload_next",
                                             "reconnect", "audio" };
        static readonly string[] KRecorder = { "host", "port", "username", "path" };
        static readonly string[] KCamera = { "name", "channel", "subtype", "url", "host", "port", "path" };
        static readonly string[] KGrid = { "enabled", "cameras", "subtype" };
        static readonly string[] KKeys = { "toggle", "next", "prev", "audio" };

        /// <summary>%APPDATA%\cctv-overlay\config.ini (the environment variable wins, like XDG_CONFIG_HOME on Linux).</summary>
        public static string DefaultPath()
        {
            string appdata = Util.Env("APPDATA");
            if (string.IsNullOrEmpty(appdata))
                appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return System.IO.Path.Combine(appdata, "cctv-overlay", "config.ini");
        }

        /// <summary>The hwdec value handed to mpv (Linux names are mapped to their Windows equivalents).</summary>
        public string MpvHwdec
        {
            get
            {
                if (Hwdec == "vaapi") return "d3d11va";
                if (Hwdec == "vaapi-copy") return "d3d11va-copy";
                return Hwdec;
            }
        }

        static CctvException Fail(string path, string msg)
        {
            return new CctvException(path + ": " + msg);
        }

        /* ---- path templates ---- */

        /// <summary>Expand a [recorder]/camera path. With dahuaQuery and no '{' in path, "?channel=N&amp;subtype=S" is appended.
        /// Placeholders: {channel} {channel02} {subtype} {stream} {mainsub}. Throws on an unknown or unterminated one.</summary>
        public static string ExpandPath(string path, bool dahuaQuery, int channel, int subtype)
        {
            if (dahuaQuery && path.IndexOf('{') < 0)
                return path + "?channel=" + channel + "&subtype=" + subtype;

            var sb = new StringBuilder();
            int p = 0;
            while (p < path.Length)
            {
                if (path[p] != '{')
                {
                    sb.Append(path[p++]);
                    continue;
                }
                int end = path.IndexOf('}', p);
                if (end < 0)
                    throw new CctvException("unterminated '{' in path template");
                string name = path.Substring(p + 1, end - p - 1);
                switch (name)
                {
                    case "channel": sb.Append(channel); break;
                    case "channel02": sb.Append(channel.ToString("00")); break;
                    case "subtype": sb.Append(subtype); break;
                    case "stream": sb.Append(subtype + 1); break;
                    case "mainsub": sb.Append(subtype == 0 ? "main" : "sub"); break;
                    default:
                        throw new CctvException("unknown placeholder '{" + (name.Length > 32 ? name.Substring(0, 32) : name) +
                                                "}' in path template (use {channel} {channel02} {subtype} {stream} {mainsub})");
                }
                p = end + 1;
            }
            return sb.ToString();
        }

        /* ---- typed getters (raw values: no escape processing, no inline comments) ---- */

        static void GetStr(IniFile kf, string path, string g, string k, ref string field, bool allowEmpty)
        {
            string v = kf.Get(g, k);
            if (v == null)
                return;
            if (v.Length == 0 && !allowEmpty)
                throw Fail(path, "[" + g + "] " + k + ": must not be empty");
            field = v;
        }

        static void GetInt(IniFile kf, string path, string g, string k, int min, int max, ref int field)
        {
            string raw = kf.Get(g, k);
            if (raw == null)
                return;
            long v;
            if (!Util.TryParseInt64Strict(raw, out v))
                throw Fail(path, string.Format("[{0}] {1}: must be an integer between {2} and {3} (no trailing comments)", g, k, min, max));
            if (v < min || v > max)
                throw Fail(path, string.Format("[{0}] {1}: {2} is out of range ({3}..{4})", g, k, v, min, max));
            field = (int)v;
        }

        static void GetBool(IniFile kf, string path, string g, string k, ref bool field)
        {
            string v = kf.Get(g, k);
            if (v == null)
                return;
            bool b;
            if (!IniFile.ParseBool(v, out b))
                throw Fail(path, "[" + g + "] " + k + ": must be true or false");
            field = b;
        }

        static void GetEnum(IniFile kf, string path, string g, string k, string[] allowed, ref string field)
        {
            string v = kf.Get(g, k);
            if (v == null)
                return;
            if (Array.IndexOf(allowed, v) >= 0)
            {
                field = v;
                return;
            }
            throw Fail(path, string.Format("[{0}] {1}: '{2}' is not valid; use one of: {3}", g, k, v, string.Join(" | ", allowed)));
        }

        static void GetKey(IniFile kf, string path, string k, ref Hotkey field)
        {
            string v = kf.Get("keys", k);
            if (v == null)
                return;
            string err;
            Hotkey h;
            if (!Hotkey.TryParse(v, out h, out err))
                throw Fail(path, "[keys] " + k + ": " + err);
            field = h; /* null for "none" */
        }

        static void CheckKeys(IniFile kf, string path, string g, string[] allowed)
        {
            foreach (string key in kf.Keys(g))
            {
                if (Array.IndexOf(allowed, key) >= 0)
                    continue;
                if (key == "password")
                    throw Fail(path, "[" + g + "] password: passwords are not allowed in config.ini; put it in secrets.ini " +
                                     "(readable only by you) or set CCTV_OVERLAY_PASSWORD");
                throw Fail(path, "[" + g + "] unknown key '" + key + "'");
            }
        }

        static bool HostOk(string h)
        {
            foreach (char c in h)
                if (Util.IsAsciiSpace(c) || "/@?#\\".IndexOf(c) >= 0)
                    return false;
            return true;
        }

        void LoadCameras(IniFile kf)
        {
            string path = ConfigPath;
            foreach (string g in kf.Groups)
            {
                if (!g.StartsWith("camera.", StringComparison.Ordinal))
                    continue;
                string id = g.Substring("camera.".Length);
                if (id.Length == 0)
                    throw Fail(path, "[" + g + "]: camera section needs an id, e.g. [camera.front]");
                CheckKeys(kf, path, g, KCamera);

                var c = new Camera();
                c.Id = id;
                Cameras.Add(c);

                GetStr(kf, path, g, "name", ref c.Name, false);
                GetStr(kf, path, g, "url", ref c.Url, false);
                GetInt(kf, path, g, "channel", 1, 1024, ref c.Channel);
                GetInt(kf, path, g, "subtype", 0, 3, ref c.Subtype);
                GetStr(kf, path, g, "host", ref c.Host, false);
                GetInt(kf, path, g, "port", 1, 65535, ref c.Port);
                GetStr(kf, path, g, "path", ref c.Path, false);

                if (c.Host != null && !HostOk(c.Host))
                    throw Fail(path, "[" + g + "] host: not a valid host name or address");
                if (c.Path != null)
                {
                    if (c.Path[0] != '/')
                        throw Fail(path, "[" + g + "] path: must start with '/'");
                    try
                    {
                        ExpandPath(c.Path, false, 1, 0);
                    }
                    catch (CctvException te)
                    {
                        throw Fail(path, "[" + g + "] path: " + te.Message);
                    }
                }
                if (c.Name == null)
                    throw Fail(path, "[" + g + "] name: required (the label shown on the overlay)");
                if (c.Url != null)
                {
#if DEMO
                    if (DemoScenes.IsScene(c.Url)) { }
                    else
#endif
                    if (!c.Url.StartsWith("rtsp://", StringComparison.Ordinal) && !c.Url.StartsWith("rtsps://", StringComparison.Ordinal))
                        throw Fail(path, "[" + g + "] url: must start with rtsp:// or rtsps://");
                }
                else if (c.Channel < 1 && c.Path != null)
                {
                    c.Channel = 1; /* a per-camera path may not use {channel} at all */
                }
                else if (c.Channel < 1)
                {
                    throw Fail(path, "[" + g + "] channel: required (>= 1) unless 'url' or 'path' is set");
                }
                for (int j = 0; j + 1 < Cameras.Count; j++)
                {
                    Camera o = Cameras[j];
                    if (string.Equals(o.Name, c.Name, StringComparison.OrdinalIgnoreCase))
                        throw Fail(path, string.Format("[{0}] name: '{1}' duplicates [camera.{2}]; names must be unique", g, c.Name, o.Id));
                }
            }
            if (Cameras.Count == 0)
                throw Fail(path, "no cameras defined; add at least one [camera.<id>] section with name and channel");
        }

        /* [grid] cameras = comma-separated ids or names (2..4); default: the first four cameras. */
        void LoadGrid(IniFile kf)
        {
            string path = ConfigPath;
            int n = Cameras.Count;

            GridCount = 0;
            string raw = kf.Get("grid", "cameras");
            if (raw == null)
            {
                if (GridEnabled && n >= 2)
                    for (int i = 0; i < n && i < GridMax; i++)
                        GridIdx[GridCount++] = i;
                return;
            }
            int count = 0;
            var idx = new int[GridMax];
            foreach (string part in raw.Split(','))
            {
                string want = part.Trim(' ', '\t', '\n', '\r', '\v', '\f');
                if (want.Length == 0)
                    continue;
                if (count == GridMax)
                    throw Fail(path, "[grid] cameras: at most " + GridMax + " cameras fit in the grid");
                bool found = false;
                for (int i = 0; i < n && !found; i++)
                {
                    Camera c = Cameras[i];
                    if (c.Id == want || string.Equals(c.Name, want, StringComparison.OrdinalIgnoreCase))
                    {
                        idx[count++] = i;
                        found = true;
                    }
                }
                if (!found)
                    throw Fail(path, "[grid] cameras: '" + want + "' is not a camera id or name");
            }
            if (count < 2)
                throw Fail(path, "[grid] cameras: list at least 2 cameras (or remove the key)");
            if (GridEnabled)
            {
                GridCount = count;
                Array.Copy(idx, GridIdx, count);
            }
        }

        /// <summary>Names of accounts other than you (and SYSTEM / Administrators) that can read the file, or null.</summary>
        internal static string OtherReaders(string file)
        {
            try
            {
                FileSecurity sec = File.GetAccessControl(file);
                AuthorizationRuleCollection rules = sec.GetAccessRules(true, true, typeof(SecurityIdentifier));
                SecurityIdentifier me = WindowsIdentity.GetCurrent().User;
                var names = new List<string>();
                foreach (FileSystemAccessRule r in rules)
                {
                    if (r.AccessControlType != AccessControlType.Allow)
                        continue;
                    if ((r.PropagationFlags & PropagationFlags.InheritOnly) != 0)
                        continue;
                    int rights = (int)r.FileSystemRights;
                    const int ReadData = 0x1, GenericAll = 0x10000000;
                    const int GenericRead = unchecked((int)0x80000000);
                    if ((rights & (ReadData | GenericAll | GenericRead)) == 0)
                        continue;
                    var sid = r.IdentityReference as SecurityIdentifier;
                    if (sid == null)
                        continue;
                    if (sid.Equals(me) || sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
                        sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || sid.Value == "S-1-3-4" /* OWNER RIGHTS */)
                        continue;
                    string name;
                    try { name = sid.Translate(typeof(NTAccount)).Value; }
                    catch (Exception) { name = sid.Value; }
                    if (!names.Contains(name))
                        names.Add(name);
                }
                return names.Count > 0 ? string.Join(", ", names.ToArray()) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        void LoadSecrets()
        {
            string u = Util.Env("CCTV_OVERLAY_USERNAME");
            string p = Util.Env("CCTV_OVERLAY_PASSWORD");
            string dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(ConfigPath));

            SecretsPath = System.IO.Path.Combine(dir, "secrets.ini");
            if (File.Exists(SecretsPath))
            {
                string others = OtherReaders(SecretsPath);
                if (others != null)
                    Log.Warn("secrets file {0} can be read by other accounts ({1}); run: install.cmd --set-password " +
                             "(it rewrites the file readable only by you)", SecretsPath, others);
                var kf = new IniFile();
                string text = null;
                try
                {
                    text = IniFile.ReadText(SecretsPath);
                    kf.Parse(text);
                    string fp = kf.Get("recorder", "password");
                    string fu = kf.Get("recorder", "username");
                    if (!string.IsNullOrEmpty(fp))
                        Password = fp;
                    if (!string.IsNullOrEmpty(fu))
                        Username = fu;
                }
                catch (Exception)
                {
                    /* The parser message can quote the offending line, which may be a password. */
                    Log.Warn("secrets file {0} could not be parsed (details withheld); expected a [recorder] " +
                             "section with 'password = ...'", SecretsPath);
                }
            }
            if (!string.IsNullOrEmpty(u))
                Username = u;
            if (!string.IsNullOrEmpty(p))
                Password = p;
            Log.RegisterSecret(Password);

            /* Basic auth is base64(user:password); register it so an RTSP Authorization header cannot leak it. */
            if (!string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(Password))
            {
                byte[] pair = Encoding.UTF8.GetBytes(Username + ":" + Password);
                Log.RegisterSecret(Convert.ToBase64String(pair));
                Util.Wipe(pair);
            }

            /* Credentials may also be embedded in a per-camera url = override. Register them as secrets. */
            foreach (Camera cam in Cameras)
            {
                if (cam.Url == null)
                    continue;
                int sep = cam.Url.IndexOf("://", StringComparison.Ordinal);
                if (sep < 0)
                    continue;
                int auth = sep + 3;
                int at = cam.Url.IndexOf('@', auth);
                int slash = cam.Url.IndexOf('/', auth);
                if (at < 0 || (slash >= 0 && slash < at))
                    continue;
                int colon = cam.Url.IndexOf(':', auth, at - auth);
                if (colon < 0)
                    continue;
                string raw = cam.Url.Substring(colon + 1, at - colon - 1);
                string dec;
                try { dec = Uri.UnescapeDataString(raw); }
                catch (Exception) { dec = raw; }
                if (raw.Length > 0)
                    Log.RegisterSecret(raw);
                if (dec.Length > 0 && dec != raw)
                    Log.RegisterSecret(dec);
                Log.Warn("camera '{0}' has credentials in its url override; prefer secrets.ini", cam.Id);
            }
        }

        /// <summary>Loads and strictly validates. path == null -> default path. Only the first problem is reported.
        /// A missing password is NOT an error here. Warns when secrets.ini is readable by other accounts.</summary>
        public static Config Load(string path)
        {
            var cfg = new Config();
            cfg.ConfigPath = path ?? DefaultPath();
            string p = cfg.ConfigPath;

            var kf = new IniFile();
            string text;
            try
            {
                text = IniFile.ReadText(p);
            }
            catch (FileNotFoundException)
            {
                throw Fail(p, "config file not found; copy config.example.ini there (in the source tree: examples\\, " +
                              "or run install.cmd; see docs\\install.md)");
            }
            catch (DirectoryNotFoundException)
            {
                throw Fail(p, "config file not found; copy config.example.ini there (in the source tree: examples\\, " +
                              "or run install.cmd; see docs\\install.md)");
            }
            catch (Exception e)
            {
                throw Fail(p, e.Message);
            }
            try
            {
                kf.Parse(text);
            }
            catch (CctvException e)
            {
                throw Fail(p, e.Message);
            }

            /* unknown groups / keys */
            foreach (string g in kf.Groups)
            {
                if (g == "general") CheckKeys(kf, p, g, KGeneral);
                else if (g == "overlay") CheckKeys(kf, p, g, KOverlay);
                else if (g == "stream") CheckKeys(kf, p, g, KStream);
                else if (g == "recorder") CheckKeys(kf, p, g, KRecorder);
                else if (g == "grid") CheckKeys(kf, p, g, KGrid);
                else if (g == "keys") CheckKeys(kf, p, g, KKeys);
                else if (!g.StartsWith("camera.", StringComparison.Ordinal))
                    throw Fail(p, "unknown section [" + g + "]; expected general, overlay, stream, recorder, grid, keys or camera.<id>");
            }

            string ll = kf.Get("general", "log_level");
            if (ll != null && !Log.LevelFromString(ll, out cfg.LogLevel))
                throw Fail(p, "[general] log_level: '" + ll + "' is not valid; use debug | info | warning | error");

            GetStr(kf, p, "general", "monitor", ref cfg.Monitor, false);
            GetBool(kf, p, "general", "start_hidden", ref cfg.StartHidden);
            GetInt(kf, p, "overlay", "width", 64, 7680, ref cfg.Width);
            GetInt(kf, p, "overlay", "height", 64, 4320, ref cfg.Height);
            GetEnum(kf, p, "overlay", "anchor", Anchors, ref cfg.Anchor);
            GetInt(kf, p, "overlay", "margin_x", 0, 10000, ref cfg.MarginX);
            GetInt(kf, p, "overlay", "margin_y", 0, 10000, ref cfg.MarginY);
            GetEnum(kf, p, "overlay", "layer", Layers, ref cfg.Layer);
            GetBool(kf, p, "overlay", "show_label", ref cfg.ShowLabel);
            GetBool(kf, p, "overlay", "show_status", ref cfg.ShowStatus);
            GetEnum(kf, p, "stream", "transport", Transports, ref cfg.Transport);
            GetInt(kf, p, "stream", "subtype", 0, 3, ref cfg.Subtype);
            GetEnum(kf, p, "stream", "hwdec", Hwdecs, ref cfg.Hwdec);
            GetBool(kf, p, "stream", "preview_snapshot", ref cfg.PreviewSnapshot);
            GetBool(kf, p, "stream", "preload_next", ref cfg.PreloadNext);
            GetBool(kf, p, "stream", "reconnect", ref cfg.Reconnect);
            GetBool(kf, p, "stream", "audio", ref cfg.Audio);
            GetBool(kf, p, "grid", "enabled", ref cfg.GridEnabled);
            GetInt(kf, p, "grid", "subtype", 0, 3, ref cfg.GridSubtype);
            GetStr(kf, p, "recorder", "host", ref cfg.Host, false);
            GetInt(kf, p, "recorder", "port", 1, 65535, ref cfg.Port);
            GetStr(kf, p, "recorder", "path", ref cfg.PathTemplate, false);
            GetStr(kf, p, "recorder", "username", ref cfg.Username, true);
            GetKey(kf, p, "toggle", ref cfg.KeyToggle);
            GetKey(kf, p, "next", ref cfg.KeyNext);
            GetKey(kf, p, "prev", ref cfg.KeyPrev);
            GetKey(kf, p, "audio", ref cfg.KeyAudio);

            string op = kf.Get("overlay", "opacity");
            if (op != null)
            {
                double o;
                if (op.Length == 0 || !double.TryParse(op, System.Globalization.NumberStyles.Float,
                                                       System.Globalization.CultureInfo.InvariantCulture, out o))
                    throw Fail(p, "[overlay] opacity: must be a number between 0.1 and 1.0");
                if (o < 0.1 || o > 1.0)
                    throw Fail(p, "[overlay] opacity: " + o.ToString(System.Globalization.CultureInfo.InvariantCulture) + " is out of range (0.1..1.0)");
                cfg.Opacity = o;
            }
            if (cfg.PathTemplate[0] != '/')
                throw Fail(p, "[recorder] path: must start with '/'");
            try
            {
                ExpandPath(cfg.PathTemplate, true, 1, 0);
            }
            catch (CctvException te)
            {
                throw Fail(p, "[recorder] path: " + te.Message);
            }
            if (cfg.Host != null && !HostOk(cfg.Host))
                throw Fail(p, "[recorder] host: not a valid host name or address");

            cfg.LoadCameras(kf);

            foreach (Camera c in cfg.Cameras)
            {
                if (c.Url == null && c.Host == null && cfg.Host == null)
                    throw Fail(p, "[recorder] host: required because [camera." + c.Id + "] has no 'url' or 'host' " +
                                  "(set host to your recorder's address)");
            }

            cfg.LoadGrid(kf);
            cfg.LoadSecrets();
            return cfg;
        }

        /// <summary>Drops the reference to the password (managed strings cannot be wiped; see docs/behaviour.md).</summary>
        public void Forget()
        {
            Password = null;
        }
    }

    /// <summary>A GKeyFile-compatible INI reader: [groups], key = value, full-line '#' comments, literal values.</summary>
    internal sealed class IniFile
    {
        readonly List<string> groups = new List<string>();
        readonly Dictionary<string, List<KeyValuePair<string, string>>> data =
            new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.Ordinal);

        public IEnumerable<string> Groups { get { return groups; } }

        public static string ReadText(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            try
            {
                int off = (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ? 3 : 0;
                var enc = new UTF8Encoding(false, true);
                return enc.GetString(bytes, off, bytes.Length - off);
            }
            catch (DecoderFallbackException)
            {
                throw new CctvException("Key file contains invalid UTF-8");
            }
            finally
            {
                Util.Wipe(bytes);
            }
        }

        public static bool ParseBool(string v, out bool b)
        {
            b = false;
            switch (v.ToLowerInvariant())
            {
                case "true":
                case "1": b = true; return true;
                case "false":
                case "0": b = false; return true;
            }
            return false;
        }

        public void Parse(string text)
        {
            string cur = null;
            string[] lines = text.Split('\n');
            foreach (string l0 in lines)
            {
                string line = l0.TrimEnd('\r');
                int s = 0;
                while (s < line.Length && Util.IsAsciiSpace(line[s]))
                    s++;
                string t = line.Substring(s);
                if (t.Length == 0 || t[0] == '#')
                    continue;
                if (t[0] == '[')
                {
                    int close = t.IndexOf(']');
                    string name = close > 0 ? t.Substring(1, close - 1) : null;
                    if (name == null || name.Length == 0 || name.IndexOf('[') >= 0 || t.Substring(close + 1).Trim().Length > 0)
                        throw new CctvException("Invalid group name: " + (t.Length > 64 ? t.Substring(0, 64) : t));
                    foreach (char c in name)
                        if (c < 0x20)
                            throw new CctvException("Invalid group name: " + name);
                    cur = name;
                    if (!data.ContainsKey(name))
                    {
                        data[name] = new List<KeyValuePair<string, string>>();
                        groups.Add(name);
                    }
                    continue;
                }
                int eq = t.IndexOf('=');
                if (eq < 0)
                    throw new CctvException("Key file contains line “" + t + "” which is not a key-value pair, group, or comment");
                string key = t.Substring(0, eq).TrimEnd(' ', '\t', '\v', '\f');
                if (key.Length == 0 || key.IndexOfAny(new[] { '[', ']' }) >= 0)
                    throw new CctvException("Invalid key name: " + key);
                string value = t.Substring(eq + 1).Trim(' ', '\t', '\v', '\f', '\r');
                if (cur == null)
                    throw new CctvException("Key file does not start with a group");
                List<KeyValuePair<string, string>> list = data[cur];
                int existing = list.FindIndex(kv => kv.Key == key);
                if (existing >= 0)
                    list[existing] = new KeyValuePair<string, string>(key, value);
                else
                    list.Add(new KeyValuePair<string, string>(key, value));
            }
        }

        public string Get(string group, string key)
        {
            List<KeyValuePair<string, string>> list;
            if (!data.TryGetValue(group, out list))
                return null;
            foreach (KeyValuePair<string, string> kv in list)
                if (kv.Key == key)
                    return kv.Value;
            return null;
        }

        public IEnumerable<string> Keys(string group)
        {
            List<KeyValuePair<string, string>> list;
            if (!data.TryGetValue(group, out list))
                yield break;
            foreach (KeyValuePair<string, string> kv in list)
                yield return kv.Key;
        }
    }
}
