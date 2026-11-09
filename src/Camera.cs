// Ordered camera list (current index, next/prev with wrap) and the RTSP URL builder.
using System;
using System.Text;

namespace CctvOverlay
{
    internal sealed class CameraList
    {
        readonly Config cfg;
        int cur;

        public CameraList(Config cfg)
        {
            this.cfg = cfg;
        }

        public int Count { get { return cfg.Cameras.Count; } }

        public Camera Get(int i)
        {
            return i >= 0 && i < cfg.Cameras.Count ? cfg.Cameras[i] : null;
        }

        public int Current { get { return cur; } }

        public Camera CurrentCamera { get { return Get(cur); } }

        public void SetCurrent(int i)
        {
            if (i >= 0 && i < Count)
                cur = i;
        }

        public int Next()
        {
            cur = (cur + 1) % Count;
            return cur;
        }

        public int Prev()
        {
            int n = Count;
            cur = (cur + n - 1) % n;
            return cur;
        }

        public bool FindId(string id, out int index)
        {
            for (int i = 0; i < Count; i++)
            {
                if (Get(i).Id == id)
                {
                    index = i;
                    return true;
                }
            }
            index = 0;
            return false;
        }

        /// <summary>Name (case-insensitive), then id, then a zero-based index.</summary>
        public bool Find(string spec, out int index)
        {
            for (int i = 0; i < Count; i++)
            {
                if (string.Equals(Get(i).Name, spec, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    return true;
                }
            }
            if (FindId(spec, out index))
                return true;
            if (spec.Length > 0 && spec.Length < 6)
            {
                bool digits = true;
                foreach (char c in spec)
                    digits &= c >= '0' && c <= '9';
                if (digits)
                {
                    int v = int.Parse(spec, System.Globalization.CultureInfo.InvariantCulture);
                    if (v < Count)
                    {
                        index = v;
                        return true;
                    }
                }
            }
            index = 0;
            return false;
        }

        /// <summary>Builds rtsp://user:pass@host:port/path as UTF-8 bytes. The caller wipes the array after use.
        /// Credentials come from secrets only and are percent-encoded.</summary>
        public static byte[] ResolveUrl(Config cfg, Camera cam, int subtypeOverride)
        {
#if DEMO
            if (cam.Url != null && DemoScenes.IsScene(cam.Url))
                return DemoScenes.Url(cam.Url); /* synthetic demo camera (demo build only) */
#endif
            if (cam.Url != null)
                return Encoding.UTF8.GetBytes(cam.Url);

            string host = cam.Host ?? cfg.Host;
            int port = cam.Port > 0 ? cam.Port : cfg.Port;
            if (host == null)
                throw new CctvException("no recorder host configured ([recorder] host)", 2);
            if (string.IsNullOrEmpty(cfg.Username))
                throw new CctvException("no recorder username configured ([recorder] username, secrets.ini or " +
                                        "CCTV_OVERLAY_USERNAME)", 2);
            if (string.IsNullOrEmpty(cfg.Password))
                throw new CctvException("no recorder password configured (run install.cmd --set-password, or set " +
                                        "CCTV_OVERLAY_PASSWORD or 'password' in secrets.ini next to config.ini)", 2);

            int sub = subtypeOverride >= 0 ? subtypeOverride : cam.Subtype >= 0 ? cam.Subtype : cfg.Subtype;
            string rest = Config.ExpandPath(cam.Path ?? cfg.PathTemplate, cam.Path == null, cam.Channel, sub);
            bool v6 = host.IndexOf(':') >= 0;

            /* Assemble in a char[] so the credential-bearing text can be wiped. */
            string u = Util.PctEncode(cfg.Username);
            string p = Util.PctEncode(cfg.Password);
            var sb = new StringBuilder(64 + u.Length + p.Length + host.Length + rest.Length);
            sb.Append("rtsp://").Append(u).Append(':').Append(p).Append('@');
            sb.Append(v6 ? "[" : "").Append(host).Append(v6 ? "]" : "").Append(':').Append(port).Append(rest);
            char[] chars = new char[sb.Length];
            sb.CopyTo(0, chars, 0, sb.Length);
            sb.Clear();
            sb.Append('\0', chars.Length); /* overwrite the builder's buffer */
            byte[] url = Encoding.UTF8.GetBytes(chars);
            Util.Wipe(chars);
            return url;
        }

        /// <summary>Test helper only: the URL as a string (never use this for a real credential).</summary>
        internal static string ResolveUrlString(Config cfg, Camera cam, int subtypeOverride)
        {
            byte[] b = ResolveUrl(cfg, cam, subtypeOverride);
            string s = Encoding.UTF8.GetString(b);
            Util.Wipe(b);
            return s;
        }
    }
}
