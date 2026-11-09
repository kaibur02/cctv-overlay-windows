// Unit tests (stub video + stub overlay). Built and run by `.\build.ps1 check`; run from the repository root.
// Ports of the original test_util, test_config, test_camera, test_logging and test_app suites.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace CctvOverlay.Tests
{
    internal sealed class TestFailure : Exception
    {
        public TestFailure(string m) : base(m) { }
    }

    internal static class A
    {
        public static void True(bool c, string what) { if (!c) throw new TestFailure("expected true: " + what); }
        public static void False(bool c, string what) { if (c) throw new TestFailure("expected false: " + what); }
        public static void Eq<T>(T got, T want, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(got, want))
                throw new TestFailure(what + ": got '" + got + "', want '" + want + "'");
        }
        public static void Null(object o, string what) { if (o != null) throw new TestFailure("expected null: " + what + " (got '" + o + "')"); }
        public static void NotNull(object o, string what) { if (o == null) throw new TestFailure("expected non-null: " + what); }
        public static void Contains(string hay, string needle, string what)
        {
            if (hay == null || hay.IndexOf(needle, StringComparison.Ordinal) < 0)
                throw new TestFailure(what + ": '" + hay + "' does not contain '" + needle + "'");
        }
        public static void NotContains(string hay, string needle, string what)
        {
            if (hay != null && hay.IndexOf(needle, StringComparison.Ordinal) >= 0)
                throw new TestFailure(what + ": '" + hay + "' must not contain '" + needle + "'");
        }
    }

    internal static class TL
    {
        public const string GoodConfig =
            "[recorder]\nhost = 192.168.1.100\nusername = tester\n" +
            "[camera.front]\nname = Front Door\nchannel = 1\n" +
            "[camera.driveway]\nname = Driveway\nchannel = 2\nsubtype = 0\n" +
            "[camera.garden]\nname = Garden\nchannel = 3\n";

        /// <summary>Writes config (and optional secrets) into a fresh temp dir; returns the config path.</summary>
        public static string WriteConfig(string config, string secrets, bool secretsReadableByUsers, out string dir)
        {
            dir = Path.Combine(Path.GetTempPath(), "cctv-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            string cfg = Path.Combine(dir, "config.ini");
            File.WriteAllText(cfg, config, new UTF8Encoding(false));
            if (secrets != null)
            {
                string sp = Path.Combine(dir, "secrets.ini");
                File.WriteAllText(sp, secrets, new UTF8Encoding(false));
                FileSecurity fs = File.GetAccessControl(sp);
                fs.SetAccessRuleProtection(true, false);
                foreach (FileSystemAccessRule r in fs.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                    fs.RemoveAccessRuleSpecific(r);
                fs.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl, AccessControlType.Allow));
                if (secretsReadableByUsers)
                    fs.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                                                              FileSystemRights.Read, AccessControlType.Allow));
                File.SetAccessControl(sp, fs);
            }
            return cfg;
        }

        public static void RmDir(string dir)
        {
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }

        public static void Env(string name, string value)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    internal static class Runner
    {
        static readonly List<KeyValuePair<string, Action>> All = new List<KeyValuePair<string, Action>>();
        static void Add(string name, Action a) { All.Add(new KeyValuePair<string, Action>(name, a)); }

        [STAThread]
        static int Main(string[] args)
        {
            Loop.Init();
            UtilTests.Register(Add);
            ConfigTests.Register(Add);
            CameraTests.Register(Add);
            LoggingTests.Register(Add);
            JsonTests.Register(Add);
            AppTests.Register(Add);
            int failed = 0;
            foreach (KeyValuePair<string, Action> t in All)
            {
                if (args.Length > 0 && t.Key.IndexOf(args[0], StringComparison.Ordinal) < 0)
                    continue;
                try
                {
                    t.Value();
                    Console.WriteLine("ok   - " + t.Key);
                }
                catch (Exception e)
                {
                    failed++;
                    Console.WriteLine("FAIL - " + t.Key + ": " + e.Message);
                    if (!(e is TestFailure))
                        Console.WriteLine(e.StackTrace);
                }
                finally
                {
                    Log.SetSink(null);
                    Log.ClearSecrets();
                }
            }
            Console.WriteLine();
            Console.WriteLine("{0} tests, {1} failed", All.Count, failed);
            return failed == 0 ? 0 : 1;
        }
    }

    internal static class UtilTests
    {
        public static void Register(Action<string, Action> add)
        {
            add("/util/pct-encode", delegate
            {
                A.Eq(Util.PctEncode("p@ss:w/rd%1"), "p%40ss%3Aw%2Frd%251", "pct");
                A.Eq(Util.PctEncode("aZ09-._~"), "aZ09-._~", "unreserved");
                A.Eq(Util.PctEncode("\u00e9 x"), "%C3%A9%20x", "utf-8 bytes");
            });
        }
    }

    internal static class ConfigTests
    {
        static StringBuilder warnings;

        static void Begin()
        {
            warnings = new StringBuilder();
            Log.SetSink((l, line) => warnings.Append(line).Append('\n'));
            TL.Env("CCTV_OVERLAY_PASSWORD", null);
            TL.Env("CCTV_OVERLAY_USERNAME", null);
        }

        /* Load text; if wantErr is non-null the load must fail and the message must contain it. */
        static Config Load(string text, string wantErr)
        {
            string dir;
            string path = TL.WriteConfig(text, null, false, out dir);
            try
            {
                Config c = Config.Load(path);
                if (wantErr != null)
                    throw new TestFailure("expected error containing '" + wantErr + "' but the load succeeded");
                return c;
            }
            catch (CctvException e)
            {
                if (wantErr == null)
                    throw new TestFailure("unexpected error: " + e.Message);
                A.Contains(e.Message, wantErr, "error message");
                A.Contains(e.Message, path, "message names the file");
                return null;
            }
            finally
            {
                TL.RmDir(dir);
            }
        }

        public static void Register(Action<string, Action> add)
        {
            add("/config/defaults-and-order", delegate
            {
                Begin();
                Config c = Load(TL.GoodConfig, null);
                A.Eq(c.Width, 480, "width");
                A.Eq(c.Height, 270, "height");
                A.Eq(c.Anchor, "top-right", "anchor");
                A.Eq(c.Layer, "overlay", "layer");
                A.Eq(c.Port, 554, "port");
                A.Eq(c.Subtype, 1, "subtype");
                A.Eq(c.Transport, "tcp", "transport");
                A.Eq(c.Username, "tester", "username");
                A.Null(c.Password, "password");
                A.Eq(c.Cameras.Count, 3, "cameras");
                string[] want = { "front", "driveway", "garden" };
                for (int i = 0; i < 3; i++)
                    A.Eq(c.Cameras[i].Id, want[i], "order");
                A.Eq(c.Cameras[0].Subtype, -1, "subtype default");
                A.Eq(c.Cameras[1].Subtype, 0, "subtype override");
                A.Null(c.KeyToggle, "no keys by default");
            });
            add("/config/shipped-example", delegate
            {
                Begin();
                Config c = Config.Load(Path.Combine("examples", "config.example.ini"));
                A.Eq(c.Cameras.Count, 2, "cameras");
                A.Null(c.Password, "no secrets next to the example");
                A.NotNull(c.KeyToggle, "example binds keys");
                A.Eq(c.KeyToggle.Text, "Ctrl + Num *", "toggle key");
            });
            add("/config/key-names", delegate
            {
                /* everyday spellings and the Hyprland names parse to the same key, and Text parses back to itself */
                string[,] same = {
                    { "Ctrl + Shift + -", "Ctrl + Shift + -" },
                    { "ctrl+shift+minus", "Ctrl + Shift + -" },
                    { "CTRL + SHIFT + OemMinus", "Ctrl + Shift + -" },
                    { "CTRL + KP_Multiply", "Ctrl + Num *" },
                    { "Ctrl + Num *", "Ctrl + Num *" },
                    { "control + numpad *", "Ctrl + Num *" },
                    { "Ctrl+NumPad5", "Ctrl + Num 5" },
                    { "Ctrl + Num +", "Ctrl + Num +" },
                    { "Ctrl + Num -", "Ctrl + Num -" },
                    { "KP_Divide", "Num /" },
                    { "Num/", "Num /" },
                    { "Shift + Alt + Ctrl + f9", "Ctrl + Alt + Shift + F9" },
                    { "SUPER + F9", "Win + F9" },
                    { "Ctrl + +", "Ctrl + =" },
                    { "Alt + \\", "Alt + \\" },
                    { "Ctrl + PgUp", "Ctrl + Page Up" },
                    { "Ctrl + Page Up", "Ctrl + Page Up" },
                    { "Play/Pause", "Play/Pause" },
                    { "Ctrl + D5", "Ctrl + 5" },
                };
                for (int i = 0; i < same.GetLength(0); i++)
                {
                    Hotkey h, h2;
                    string err;
                    A.True(Hotkey.TryParse(same[i, 0], out h, out err), same[i, 0] + ": " + err);
                    A.Eq(h.Text, same[i, 1], same[i, 0]);
                    A.True(Hotkey.TryParse(h.Text, out h2, out err) && h2.Vk == h.Vk && h2.Modifiers == h.Modifiers, "round trip " + h.Text);
                }
                Hotkey k;
                string e;
                A.True(Hotkey.TryParse("Ctrl + Shift + -", out k, out e) && k.Vk == 0xBD && k.Modifiers == (Hotkey.ModControl | Hotkey.ModShift), "vk");
                A.True(Hotkey.TryParse("Ctrl + Num -", out k, out e) && k.Vk == 0x6D, "numpad minus is not the minus key");
                A.True(Hotkey.TryParse("none", out k, out e) && k == null, "none");
                A.False(Hotkey.TryParse("Ctrl + Shift", out k, out e), "modifiers only");
                A.Contains(e, "no key after the modifiers", "message");
                A.False(Hotkey.TryParse("Ctrl + Banana", out k, out e), "unknown key");
                A.Contains(e, "'Banana' is not a key name", "message");
                A.False(Hotkey.TryParse("Hyper + F9", out k, out e), "unknown modifier");
                A.Contains(e, "is not a modifier", "message");
                A.True(Hotkey.TryParse("-", out k, out e) && k.StealsTyping, "bare - steals typing");
                A.True(Hotkey.TryParse("Shift + A", out k, out e) && k.StealsTyping, "Shift + A still types");
                A.True(Hotkey.TryParse("Num *", out k, out e) && !k.StealsTyping, "numpad keys are fine");
                A.True(Hotkey.TryParse("Ctrl + -", out k, out e) && !k.StealsTyping, "with Ctrl");
            });
            add("/config/full-values", delegate
            {
                Begin();
                Config c = Load("[general]\nlog_level = debug\nmonitor = DISPLAY2\nstart_hidden = false\n" +
                                "[overlay]\nwidth = 640\nheight = 360\nanchor = bottom-left\nmargin_x = 0\nmargin_y = 10\n" +
                                "layer = top\nshow_label = false\nopacity = 0.5\n" +
                                "[stream]\ntransport = udp\nsubtype = 0\nhwdec = no\nreconnect = false\n" +
                                "[recorder]\nhost = nvr.example.invalid\nport = 8554\npath = /x\n" +
                                "[keys]\ntoggle = ctrl + kp_multiply\nnext = none\naudio = SUPER+F9\n" +
                                "[camera.a]\nname = A\nchannel = 4\n", null);
                A.Eq(c.LogLevel, LogLevel.Debug, "log level");
                A.Eq(c.Monitor, "DISPLAY2", "monitor");
                A.False(c.StartHidden, "start_hidden");
                A.Eq(c.Width, 640, "width");
                A.Eq(c.Anchor, "bottom-left", "anchor");
                A.Eq(c.Layer, "top", "layer");
                A.False(c.ShowLabel, "show_label");
                A.Eq(c.Opacity, 0.5, "opacity");
                A.Eq(c.Transport, "udp", "transport");
                A.False(c.Reconnect, "reconnect");
                A.Eq(c.Port, 8554, "port");
                A.Eq(c.KeyToggle.Text, "Ctrl + Num *", "toggle key");
                A.Eq(c.KeyToggle.Vk, 0x6Au, "toggle vk");
                A.Eq(c.KeyToggle.Modifiers, Hotkey.ModControl, "toggle mods");
                A.Null(c.KeyNext, "none = unbound");
                A.Eq(c.KeyAudio.Modifiers, Hotkey.ModWin, "SUPER");
            });
            add("/config/hwdec-linux-names", delegate
            {
                Begin();
                Config c = Load("[stream]\nhwdec = vaapi\n" + TL.GoodConfig, null);
                A.Eq(c.MpvHwdec, "d3d11va", "vaapi maps to d3d11va");
                c = Load(TL.GoodConfig, null);
                A.Eq(c.MpvHwdec, "d3d11va", "default");
            });
            add("/config/bom-and-crlf", delegate
            {
                Begin();
                Config c = Load("\uFEFF" + TL.GoodConfig.Replace("\n", "\r\n"), null);
                A.Eq(c.Cameras.Count, 3, "BOM and CRLF are fine");
            });
            add("/config/bad-inputs", delegate
            {
                Begin();
                string[,] bad = {
                    { "", "no cameras defined" },
                    { "[recorder]\nhost = h\n", "no cameras defined" },
                    { "[recorder]\nhost = h\n[camera.a]\nchannel = 1\n", "[camera.a] name: required" },
                    { "[recorder]\nhost = h\n[camera.a]\nname = A\n", "channel: required" },
                    { "[recorder]\nhost = h\n[camera.a]\nname = A\nchannel = 0\n", "out of range (1..1024)" },
                    { "[recorder]\nhost = h\n[camera.a]\nname = A\nchannel = x\n", "must be an integer" },
                    { "[recorder]\nhost = h\n[camera.a]\nname = A\nchannel = 1\n[camera.b]\nname = a\nchannel = 2\n", "duplicates [camera.a]" },
                    { "[camera.a]\nname = A\nchannel = 1\n", "[recorder] host: required" },
                    { "[recorder]\nhost =\n[camera.a]\nname = A\nchannel = 1\n", "[recorder] host: must not be empty" },
                    { "[recorder]\nhost = a b\n[camera.a]\nname = A\nchannel = 1\n", "not a valid host" },
                    { "[recorder]\nhost = h\nport = 0\n[camera.a]\nname = A\nchannel = 1\n", "port: 0 is out of range" },
                    { "[recorder]\nhost = h\nport = 70000\n[camera.a]\nname = A\nchannel = 1\n", "70000 is out of range" },
                    { "[recorder]\nhost = h\npath = nope\n[camera.a]\nname = A\nchannel = 1\n", "must start with '/'" },
                    { "[recorder]\nhost = h\npath = /x/{bogus}\n[camera.a]\nname = A\nchannel = 1\n", "unknown placeholder '{bogus}'" },
                    { "[recorder]\nhost = h\npath = /x/{channel\n[camera.a]\nname = A\nchannel = 1\n", "unterminated" },
                    { "[recorder]\nhost = h\n[camera.a]\nname = A\nchannel = 1\npath = /{nope}\n", "[camera.a] path: unknown placeholder" },
                    { "[recorder]\nhost = h\n[camera.a]\nname = A\nchannel = 1\n[camera.b]\nname = B\nchannel = 2\n[grid]\ncameras = a, zz\n", "'zz' is not a camera" },
                    { "[recorder]\nhost = h\n[camera.a]\nname = A\nchannel = 1\n[camera.b]\nname = B\nchannel = 2\n[grid]\ncameras = a\n", "at least 2 cameras" },
                    { "[recorder]\nhost = h\n[camera.a]\nname = A\nchannel = 1\n[grid]\nbogus = 1\n", "[grid] unknown key 'bogus'" },
                    { "[recorder]\nhost = h\npassword = x\n[camera.a]\nname = A\nchannel = 1\n", "not allowed in config.ini" },
                    { "[recorder]\nhost = h\nhots = x\n[camera.a]\nname = A\nchannel = 1\n", "unknown key 'hots'" },
                    { "[bogus]\nx = 1\n", "unknown section [bogus]" },
                    { "[camera.]\nname = A\n", "needs an id" },
                    { "[recorder]\nhost = h\n[camera.a]\nname = A\nchannel = 1\nurl = http://x/\n", "must start with rtsp://" },
                    { "[general]\nlog_level = loud\n", "log_level" },
                    { "[general]\nstart_hidden = maybe\n", "must be true or false" },
                    { "[overlay]\nanchor = middle\n", "'middle' is not valid; use one of: top-left | top-right" },
                    { "[overlay]\nlayer = background\n", "layer: 'background' is not valid" },
                    { "[overlay]\nwidth = 10\n", "width: 10 is out of range" },
                    { "[overlay]\nheight = 99999\n", "height: 99999 is out of range" },
                    { "[overlay]\nmargin_x = -1\n", "margin_x: -1 is out of range" },
                    { "[overlay]\nopacity = 2\n", "opacity: 2 is out of range" },
                    { "[overlay]\nopacity = abc\n", "opacity: must be a number" },
                    { "[stream]\ntransport = quic\n", "transport: 'quic' is not valid" },
                    { "[stream]\nhwdec = magic\n", "hwdec: 'magic' is not valid" },
                    { "[stream]\nsubtype = 9\n", "subtype: 9 is out of range" },
                    { "[overlay]\nwidth = 480 ; comment\n", "must be an integer" },
                    { "this is not ini\n", "which is not a key-value pair" },
                    { "[keys]\ntoggle = CTRL + Banana\n", "[keys] toggle: 'Banana' is not a key name" },
                    { "[keys]\nnext = HYPER + F9\n", "'HYPER' is not a modifier" },
                    { "[keys]\nfoo = F9\n", "[keys] unknown key 'foo'" },
                };
                for (int i = 0; i < bad.GetLength(0); i++)
                {
                    try { Load(bad[i, 0], bad[i, 1]); }
                    catch (TestFailure f) { throw new TestFailure("case " + i + ": " + f.Message); }
                }
            });
            add("/config/missing-file", delegate
            {
                Begin();
                try
                {
                    Config.Load("C:\\nonexistent-dir-xyz\\config.ini");
                    throw new TestFailure("loaded a missing file");
                }
                catch (CctvException e)
                {
                    A.Contains(e.Message, "not found", "message");
                    A.Contains(e.Message, "config.example.ini", "message");
                }
            });
            add("/config/url-only-camera", delegate
            {
                Begin();
                Config c = Load("[camera.x]\nname = X\nurl = rtsp://h.example.invalid/s\n", null);
                A.Null(c.Host, "no default recorder host in code");
            });
            add("/config/secrets-file-and-env", delegate
            {
                Begin();
                string dir;
                string path = TL.WriteConfig(TL.GoodConfig, "[recorder]\npassword = p@ss:w/rd%1\\x\nusername = fileuser\n", false, out dir);
                try
                {
                    Config c = Config.Load(path);
                    A.Eq(c.Password, "p@ss:w/rd%1\\x", "literal, no escape processing");
                    A.Eq(c.Username, "fileuser", "username");
                    A.NotContains(warnings.ToString(), "other accounts", "no warning for a private file");

                    TL.Env("CCTV_OVERLAY_PASSWORD", "envpass");
                    TL.Env("CCTV_OVERLAY_USERNAME", "envuser");
                    c = Config.Load(path);
                    A.Eq(c.Password, "envpass", "env wins");
                    A.Eq(c.Username, "envuser", "env wins");
                }
                finally
                {
                    TL.Env("CCTV_OVERLAY_PASSWORD", null);
                    TL.Env("CCTV_OVERLAY_USERNAME", null);
                    TL.RmDir(dir);
                }
            });
            add("/config/secrets-permission-warning", delegate
            {
                Begin();
                string dir;
                string path = TL.WriteConfig(TL.GoodConfig, "[recorder]\npassword = hunter2\n", true, out dir);
                try
                {
                    Config c = Config.Load(path); /* warn, do not fail */
                    A.NotNull(c, "config");
                    A.Contains(warnings.ToString(), "secrets.ini", "warning names the file");
                    A.Contains(warnings.ToString(), "other accounts", "warning");
                    A.NotContains(warnings.ToString(), "hunter2", "no password in the warning");
                }
                finally { TL.RmDir(dir); }
            });
            add("/config/malformed-secrets-no-leak", delegate
            {
                Begin();
                string dir;
                string path = TL.WriteConfig(TL.GoodConfig, "[recorder]\nhunter2-no-equals-sign\n", false, out dir);
                try
                {
                    Config c = Config.Load(path);
                    A.Null(c.Password, "password just missing");
                    A.NotContains(warnings.ToString(), "hunter2", "no leak");
                    A.Contains(warnings.ToString(), "could not be parsed", "warning");
                }
                finally { TL.RmDir(dir); }
            });
        }
    }

    internal static class CameraTests
    {
        const string PW = "p@ss:w/rd%1";

        static Config LoadWith(string text, string password)
        {
            string dir;
            string path = TL.WriteConfig(text, null, false, out dir);
            TL.Env("CCTV_OVERLAY_PASSWORD", password);
            TL.Env("CCTV_OVERLAY_USERNAME", null);
            try { return Config.Load(path); }
            finally
            {
                TL.Env("CCTV_OVERLAY_PASSWORD", null);
                TL.RmDir(dir);
            }
        }

        static string Url(Config c, int i, int sub) { return CameraList.ResolveUrlString(c, c.Cameras[i], sub); }

        public static void Register(Action<string, Action> add)
        {
            add("/camera/cycle-wrap", delegate
            {
                var l = new CameraList(LoadWith(TL.GoodConfig, null));
                A.Eq(l.Count, 3, "count");
                A.Eq(l.Current, 0, "current");
                A.Eq(l.Next(), 1, "next");
                A.Eq(l.Next(), 2, "next");
                A.Eq(l.Next(), 0, "wrap");
                A.Eq(l.Prev(), 2, "wrap back");
                A.Eq(l.Prev(), 1, "prev");
                A.Eq(l.CurrentCamera.Name, "Driveway", "name");
                l.SetCurrent(99);
                A.Eq(l.Current, 1, "ignored");
            });
            add("/camera/find", delegate
            {
                var l = new CameraList(LoadWith(TL.GoodConfig, null));
                int i;
                A.True(l.Find("Driveway", out i) && i == 1, "name");
                A.True(l.Find("garden", out i) && i == 2, "case-insensitive name");
                A.True(l.Find("front", out i) && i == 0, "id");
                A.True(l.Find("2", out i) && i == 2, "index");
                A.False(l.Find("3", out i), "out of range");
                A.False(l.Find("nope", out i), "unknown");
                A.False(l.Find("", out i), "empty");
                A.False(l.Find("-1", out i), "negative");
            });
            add("/camera/url-encoding", delegate
            {
                Config c = LoadWith(TL.GoodConfig, PW);
                A.Eq(Url(c, 0, -1), "rtsp://tester:p%40ss%3Aw%2Frd%251@192.168.1.100:554/cam/realmonitor?channel=1&subtype=1", "url");
                A.Contains(Url(c, 1, -1), "?channel=2&subtype=0", "per-camera subtype");
            });
            add("/camera/url-awkward-username", delegate
            {
                Config c = LoadWith("[recorder]\nhost = h.example.invalid\nusername = us er@x\n[camera.a]\nname = A\nchannel = 1\n", "a b?c#d");
                A.True(Url(c, 0, -1).StartsWith("rtsp://us%20er%40x:a%20b%3Fc%23d@h.example.invalid:554/", StringComparison.Ordinal), "userinfo");
            });
            add("/camera/ipv6-host", delegate
            {
                Config c = LoadWith("[recorder]\nhost = fe80::1\nusername = u\n[camera.a]\nname = A\nchannel = 1\n", "p");
                A.Contains(Url(c, 0, -1), "@[fe80::1]:554/", "brackets");
            });
            add("/camera/missing-credentials", delegate
            {
                Config c = LoadWith(TL.GoodConfig, null);
                try { Url(c, 0, -1); throw new TestFailure("no error"); }
                catch (CctvException e)
                {
                    A.Contains(e.Message, "password", "message");
                    A.NotContains(e.Message, "rtsp://", "no url");
                }
                c = LoadWith("[recorder]\nhost = h\n[camera.a]\nname = A\nchannel = 1\n", "pw");
                try { Url(c, 0, -1); throw new TestFailure("no error"); }
                catch (CctvException e) { A.Contains(e.Message, "username", "message"); }
            });
            add("/camera/override-url", delegate
            {
                Config c = LoadWith("[camera.x]\nname = X\nurl = rtsp://h.example.invalid/s\n", null);
                A.Eq(Url(c, 0, -1), "rtsp://h.example.invalid/s", "no credentials needed");
            });
            add("/camera/path-templates", delegate
            {
                Config c = LoadWith("[recorder]\nhost = nvr.example.invalid\nusername = u\npath = /Streaming/Channels/{channel}0{stream}\n" +
                                    "[stream]\nsubtype = 0\n[camera.a]\nname = A\nchannel = 3\n", "pw");
                A.Eq(Url(c, 0, -1), "rtsp://u:pw@nvr.example.invalid:554/Streaming/Channels/301", "hikvision");
                A.True(Url(c, 0, 1).EndsWith("/Streaming/Channels/302", StringComparison.Ordinal), "grid sub stream");
                c = LoadWith("[recorder]\nusername = admin\n[camera.a]\nname = A\nhost = 10.0.0.5\nport = 8554\nchannel = 1\n" +
                             "path = /h264Preview_{channel02}_{mainsub}\n", "pw");
                A.Eq(Url(c, 0, -1), "rtsp://admin:pw@10.0.0.5:8554/h264Preview_01_sub", "reolink standalone");
                c = LoadWith("[recorder]\nhost = h\nusername = u\n[camera.a]\nname = A\npath = /live\n", "pw");
                A.Eq(Url(c, 0, -1), "rtsp://u:pw@h:554/live", "path needs no channel");
            });
        }
    }

    internal static class LoggingTests
    {
        const string PW = "p@ss:w/rd%1";
        static StringBuilder captured;

        static void Setup()
        {
            Log.ClearSecrets();
            Log.Level = LogLevel.Debug;
            captured = new StringBuilder();
            Log.SetSink((l, line) => captured.Append(line).Append('\n'));
        }

        public static void Register(Action<string, Action> add)
        {
            add("/logging/url-userinfo", delegate
            {
                Setup();
                A.Eq(Log.Redact("open rtsp://tester:hunter2@192.168.1.100:554/cam?channel=1 now"),
                     "open rtsp://***:***@192.168.1.100:554/cam?channel=1 now", "rtsp");
                A.Eq(Log.Redact("see https://user@host/x and http://a:b@c"), "see https://***:***@host/x and http://***:***@c", "two urls");
                A.Eq(Log.Redact("no url here, mail me@example.invalid"), "no url here, mail me@example.invalid", "no url");
            });
            add("/logging/registered-secret-forms", delegate
            {
                Setup();
                Log.RegisterSecret(PW);
                A.Eq(Log.Redact("raw " + PW + " encoded p%40ss%3Aw%2Frd%251 end"), "raw *** encoded *** end", "forms");
                string r = Log.Redact("rtsp://tester:p%40ss%3Aw%2Frd%251@host:554/x");
                A.NotContains(r, "p%40ss", "encoded");
                A.NotContains(r, "tester", "user");
                r = Log.Redact("rtsp://tester:" + PW + "@host:554/x");
                A.NotContains(r, "p@ss", "raw");
                A.NotContains(r, "w/rd", "raw");
                A.NotContains(r, "rd%1", "raw");
            });
            add("/logging/log-msg-redacts", delegate
            {
                Setup();
                Log.RegisterSecret(PW);
                Log.Info("connecting with {0} to {1}", PW, "rtsp://tester:abc@h/p");
                Log.Debug("debug {0}", PW);
                A.NotContains(captured.ToString(), "p@ss", "password");
                A.NotContains(captured.ToString(), "abc", "url password");
                A.Contains(captured.ToString(), "***", "marker");
            });
            add("/logging/redaction-hard-cases", delegate
            {
                Setup();
                A.Eq(Log.Redact("Authorization: Basic dXNlcjpwYXNz\r\nCSeq: 2"), "Authorization: ***\r\nCSeq: 2", "basic");
                A.Eq(Log.Redact("Proxy-Authorization: Digest username=\"u\", response=\"abc\""), "Authorization: ***", "digest");
                A.NotContains(Log.Redact("rtsp://u:zz/yy@h:554/p"), "zz/yy", "slash in userinfo");
                Log.Info("esc \x1b[31mRED\x1b[0m end");
                A.False(captured.ToString().IndexOf('\x1b') >= 0, "escape removed");
                A.Contains(captured.ToString(), "?[31m", "replaced by ?");
            });
            add("/logging/url-override-secrets", delegate
            {
                Setup();
                TL.Env("CCTV_OVERLAY_PASSWORD", null);
                string dir;
                string path = TL.WriteConfig("[camera.a]\nname = A\nurl = rtsp://tester:zz/yy@127.0.0.1:1/live\n", null, false, out dir);
                try
                {
                    Config.Load(path);
                    A.NotContains(Log.Redact("rtsp://tester:zz/yy@127.0.0.1:1/live"), "zz/yy", "registered");
                    A.NotContains(captured.ToString(), "zz/yy", "load warning does not echo it");
                }
                finally { TL.RmDir(dir); }
            });
            add("/logging/levels", delegate
            {
                Setup();
                Log.Level = LogLevel.Warning;
                Log.Info("hidden");
                Log.Warn("shown");
                A.NotContains(captured.ToString(), "hidden", "info filtered");
                A.Contains(captured.ToString(), "shown", "warn shown");
                LogLevel l;
                A.True(Log.LevelFromString("Debug", out l) && l == LogLevel.Debug, "Debug");
                A.False(Log.LevelFromString("loud", out l), "loud");
            });
        }
    }

    internal static class JsonTests
    {
        public static void Register(Action<string, Action> add)
        {
            add("/json/roundtrip", delegate
            {
                var o = new JObj().Set("ok", true).Set("n", 3).Set("s", "Caf\u00e9 \"x\"\n").Set("e", null)
                                  .Set("a", new List<object> { 1L, "b", new JObj().Set("k", false) });
                string s = Json.Write(o);
                A.Eq(s, "{\"ok\":true,\"n\":3,\"s\":\"Caf\\u00e9 \\\"x\\\"\\n\",\"e\":null,\"a\":[1,\"b\",{\"k\":false}]}", "write");
                var p = (JObj)Json.Parse(s);
                A.Eq(p.GetString("s"), "Caf\u00e9 \"x\"\n", "string");
                A.Eq(p.GetInt("n"), 3L, "int");
                A.True(p.Has("e") && p.Get("e") == null, "null");
                A.Eq(p.GetArray("a").Count, 3, "array");
                try { Json.Parse("not json"); throw new TestFailure("parsed garbage"); }
                catch (FormatException) { }
            });
        }
    }

    internal static class AppTests
    {
        const string PW = "p@ss:w/rd%1";
        const string NoGrid = TL.GoodConfig + "[grid]\nenabled = false\n";
        const string FiveCams =
            "[recorder]\nhost = 192.168.1.100\nusername = tester\n" +
            "[camera.a]\nname = A\nchannel = 1\n[camera.b]\nname = B\nchannel = 2\n" +
            "[camera.c]\nname = C\nchannel = 3\n[camera.d]\nname = D\nchannel = 4\n" +
            "[camera.e]\nname = E\nchannel = 5\n";

        static string dir, path;
        static bool quitCalled;

        static App Make(string text, string password, bool overlay)
        {
            TL.Env("CCTV_OVERLAY_PASSWORD", null);
            TL.Env("CCTV_OVERLAY_USERNAME", null);
            TL.Env("CCTV_OVERLAY_STUB_FAIL", null);
            TL.Env("CCTV_OVERLAY_STUB_DROP_AFTER_MS", null);
            TL.Env("CCTV_OVERLAY_STUB_DELAY_MS", "10");
            if (string.IsNullOrEmpty(Util.Env("CCTV_OVERLAY_LIVE_STABLE_MS")))
                TL.Env("CCTV_OVERLAY_LIVE_STABLE_MS", "0"); /* unit tests: reset backoff at once */
            if (password != null)
                TL.Env("CCTV_OVERLAY_PASSWORD", password);
            Log.SetSink(delegate(LogLevel l, string line)
            {
                if (line.Contains("p@ss") || line.Contains("rd%1"))
                    throw new TestFailure("password in log line: " + line);
            });
            path = TL.WriteConfig(text, null, false, out dir);
            Config c = Config.Load(path);
            Log.Level = LogLevel.Debug;
            var o = new AppOptions { OverlayEnabled = overlay, BackoffScale = 0.01, QuitCb = () => quitCalled = true };
            return new App(c, o);
        }

        static void Destroy(App a)
        {
            a.Free();
            A.False(Video.StubActive, "no stream after free");
            A.False(Overlay.StubVisible, "no overlay after free");
            TL.Env("CCTV_OVERLAY_PASSWORD", null);
            TL.Env("CCTV_OVERLAY_STUB_FAIL", null);
            TL.Env("CCTV_OVERLAY_STUB_FAIL_SLOT", null);
            TL.Env("CCTV_OVERLAY_STUB_DROP_AFTER_MS", null);
            TL.Env("CCTV_OVERLAY_LIVE_STABLE_MS", null);
            TL.RmDir(dir);
        }

        static bool WaitState(App a, AppState want, int timeoutMs)
        {
            long end = Util.MonotonicMs() + timeoutMs;
            while (a.State != want && Util.MonotonicMs() < end)
                Loop.Pump(1);
            return a.State == want;
        }

        public static void Register(Action<string, Action> add)
        {
            add("/app/toggle-cycle", delegate
            {
                App a = Make(NoGrid, PW, true);
                A.Eq(a.State, AppState.Hidden, "initial");
                A.False(Video.StubActive, "no stream");
                a.Toggle();
                A.Eq(a.State, AppState.Connecting, "connecting");
                A.True(Video.StubActive, "stream");
                A.True(Overlay.StubVisible, "overlay");
                A.Eq(Overlay.StubLabel, "Front Door", "label");
                A.True(WaitState(a, AppState.Live, 1000), "live");
                A.Null(a.LastError, "error");
                a.Toggle();
                A.Eq(a.State, AppState.Hidden, "hidden");
                A.False(Video.StubActive, "hidden = no stream");
                A.False(Overlay.StubVisible, "no overlay");
                Destroy(a);
            });
            add("/app/grid-start-view-and-ring", delegate
            {
                App a = Make(FiveCams, PW, true);
                a.Toggle();
                A.True(a.GridView, "grid");
                A.Eq(Overlay.StubTiles, 4, "tiles");
                A.Eq(Overlay.StubLabel, "A|B|C|D", "first four");
                A.Eq(Video.StubActiveCount, 4, "4 streams");
                A.True(WaitState(a, AppState.Live, 1000), "live");
                foreach (string want in new[] { "A", "B", "C", "D", "E" })
                {
                    a.Next();
                    A.False(a.GridView, "single");
                    A.Eq(Overlay.StubLabel, want, "ring");
                    A.Eq(Video.StubActiveCount, 1, "one stream");
                }
                a.Next();
                A.True(a.GridView, "back to grid");
                A.Eq(Video.StubActiveCount, 4, "4 streams");
                a.Prev();
                A.Eq(Overlay.StubLabel, "E", "grid -> last");
                a.Show("A");
                a.Prev();
                A.True(a.GridView, "first -> grid");
                a.Next();
                a.Next();
                A.Eq(Overlay.StubLabel, "B", "B");
                a.Toggle();
                A.Eq(a.State, AppState.Hidden, "hidden");
                A.False(Video.StubActive, "no stream");
                a.Toggle();
                A.True(a.GridView, "always back on the grid");
                a.Hide();
                a.Next();
                A.True(a.GridView, "next while hidden opens the start view");
                Destroy(a);
            });
            add("/app/grid-bad-tile", delegate
            {
                App a = Make(FiveCams, PW, true);
                TL.Env("CCTV_OVERLAY_STUB_FAIL", "1");
                TL.Env("CCTV_OVERLAY_STUB_FAIL_SLOT", "2");
                a.Toggle();
                A.True(WaitState(a, AppState.Live, 1000), "live");
                Loop.Pump(300);
                A.Eq(a.State, AppState.Live, "aggregate stays live");
                A.NotNull(a.LastError, "failing tile reports");
                Destroy(a);
            });
            add("/app/grid-config", delegate
            {
                App a = Make(FiveCams + "[grid]\ncameras = e, B\n", PW, true);
                a.Toggle();
                A.Eq(Overlay.StubLabel, "E|B", "explicit list");
                Destroy(a);
                a = Make("[recorder]\nhost = h\nusername = u\n[camera.a]\nname = Solo\nchannel = 1\n", PW, true);
                a.Toggle();
                A.False(a.GridView, "no grid");
                A.Eq(Overlay.StubLabel, "Solo", "label");
                try { a.Grid(); throw new TestFailure("grid worked"); }
                catch (CctvException) { }
                a.Next();
                A.Eq(Overlay.StubLabel, "Solo", "wraps onto itself");
                Destroy(a);
            });
            add("/app/audio-toggle", delegate
            {
                App a = Make(FiveCams, PW, true);
                A.False(a.Audio, "off");
                a.SetAudio(true);
                A.True(a.Audio, "remembered");
                a.Toggle();
                A.True(Overlay.StubAudio, "icon in grid");
                for (int i = 0; i < 4; i++)
                    A.False(Video.StubAudio(i), "grid never plays audio");
                a.Next();
                A.True(Video.StubAudio(0), "single starts with audio");
                a.SetAudio(false);
                A.False(Video.StubAudio(0), "switched live");
                A.False(Overlay.StubAudio, "icon off");
                a.SetAudio(true);
                A.True(Video.StubAudio(0), "on again");
                JObj r = a.Handle("status", null);
                A.Eq(r.GetString("view"), "camera", "view");
                A.True(r.GetBool("audio"), "audio");
                r = a.Handle("audio", new JObj().Set("mode", "off"));
                A.True(r.GetBool("ok"), "ok");
                A.False(r.GetBool("audio"), "off");
                r = a.Handle("grid", null);
                A.Eq(r.GetString("view"), "grid", "grid");
                A.Eq(r.GetArray("tiles").Count, 4, "tiles");
                Destroy(a);
            });
            add("/app/audio-config-default", delegate
            {
                App a = Make(FiveCams + "[stream]\naudio = true\n[grid]\nenabled = false\n", PW, true);
                A.True(a.Audio, "audio");
                a.Toggle();
                A.False(a.GridView, "no grid");
                A.True(Video.StubAudio(0), "plays");
                Destroy(a);
            });
            add("/app/next-prev-while-hidden-opens", delegate
            {
                App a = Make(NoGrid, PW, true);
                a.Next();
                A.Eq(a.State, AppState.Connecting, "opened on neighbour");
                A.Eq(a.CurrentIndex, 1, "index");
                A.Eq(Overlay.StubLabel, "Driveway", "label");
                A.True(WaitState(a, AppState.Live, 1000), "live");
                a.Next();
                A.Eq(a.CurrentIndex, 2, "index");
                A.Eq(a.State, AppState.Connecting, "reconnect");
                A.Eq(Overlay.StubLabel, "Garden", "label");
                a.Next();
                A.Eq(a.CurrentIndex, 0, "wrap");
                a.Prev();
                A.Eq(a.CurrentIndex, 2, "wrap back");
                A.True(WaitState(a, AppState.Live, 1000), "live");
                a.Hide();
                A.Eq(a.State, AppState.Hidden, "hidden");
                a.Prev();
                A.Eq(a.CurrentIndex, 1, "prev opens previous");
                A.Eq(a.State, AppState.Connecting, "connecting");
                Destroy(a);
            });
            add("/app/show", delegate
            {
                App a = Make(NoGrid, PW, true);
                try { a.Show("nope"); throw new TestFailure("unknown camera accepted"); }
                catch (CctvException) { }
                A.Eq(a.State, AppState.Hidden, "hidden");
                a.Show("garden");
                A.Eq(a.CurrentIndex, 2, "index");
                A.True(WaitState(a, AppState.Live, 1000), "live");
                int starts = Video.StubStartCount;
                a.Show("Garden");
                A.Eq(Video.StubStartCount, starts, "same camera while live: no-op");
                A.Eq(a.State, AppState.Live, "live");
                a.Show(null);
                A.Eq(a.State, AppState.Live, "live");
                Destroy(a);
            });
            add("/app/hide-during-connecting", delegate
            {
                App a = Make(NoGrid, PW, true);
                TL.Env("CCTV_OVERLAY_STUB_DELAY_MS", "50");
                a.Toggle();
                A.Eq(a.State, AppState.Connecting, "connecting");
                a.Hide();
                A.Eq(a.State, AppState.Hidden, "hidden");
                A.False(Video.StubActive, "cancelled");
                Loop.Pump(120);
                A.Eq(a.State, AppState.Hidden, "late first frame never arrives");
                Destroy(a);
            });
            add("/app/failure-retries-then-error", delegate
            {
                App a = Make(NoGrid, PW, true);
                TL.Env("CCTV_OVERLAY_STUB_FAIL", "1");
                a.Toggle();
                A.True(WaitState(a, AppState.Reconnecting, 1000), "reconnecting");
                A.Eq(a.RetryCount, 1, "retries");
                A.False(Video.StubActive, "no stream while backing off");
                A.NotNull(a.LastError, "error");
                A.True(WaitState(a, AppState.Error, 3000), "error");
                A.Eq(a.RetryCount, App.MaxRetries, "max retries");
                A.False(Video.StubActive, "no stream");
                A.Contains(a.LastError, "simulated", "error text");
                A.True(Overlay.StubVisible, "ERROR keeps the overlay up");
                TL.Env("CCTV_OVERLAY_STUB_FAIL", null);
                a.Toggle();
                A.Eq(a.State, AppState.Connecting, "toggle retries");
                A.Eq(a.RetryCount, 0, "backoff reset");
                A.True(WaitState(a, AppState.Live, 1000), "live");
                A.Null(a.LastError, "error cleared");
                Destroy(a);
            });
            add("/app/recovery-resets-backoff", delegate
            {
                App a = Make(NoGrid, PW, false);
                TL.Env("CCTV_OVERLAY_STUB_FAIL", "first:2");
                a.Toggle();
                A.True(WaitState(a, AppState.Reconnecting, 1000), "reconnecting");
                A.True(WaitState(a, AppState.Live, 2000), "third attempt succeeds");
                A.Eq(a.RetryCount, 0, "reset");
                A.False(Overlay.StubVisible, "--no-overlay: overlay never touched");
                Destroy(a);
            });
            add("/app/reconnect-disabled", delegate
            {
                App a = Make("[stream]\nreconnect = false\n" + TL.GoodConfig, PW, true);
                TL.Env("CCTV_OVERLAY_STUB_FAIL", "1");
                a.Toggle();
                A.True(WaitState(a, AppState.Error, 1000), "error");
                A.Eq(a.RetryCount, 0, "no retries");
                Destroy(a);
            });
            add("/app/live-drop-reconnects", delegate
            {
                App a = Make(NoGrid, PW, true);
                TL.Env("CCTV_OVERLAY_STUB_DROP_AFTER_MS", "30");
                a.Toggle();
                A.True(WaitState(a, AppState.Live, 1000), "live");
                A.True(WaitState(a, AppState.Reconnecting, 1000), "live -> reconnecting");
                TL.Env("CCTV_OVERLAY_STUB_DROP_AFTER_MS", null);
                A.True(WaitState(a, AppState.Live, 2000), "live again");
                Destroy(a);
            });
            add("/app/overlay-lost-while-live", delegate
            {
                App a = Make(NoGrid, PW, true);
                a.Toggle();
                A.True(WaitState(a, AppState.Live, 1000), "live");
                Overlay.StubSimulateLost();
                A.False(Overlay.StubVisible, "lost");
                a.Next();
                A.Eq(a.State, AppState.Connecting, "connecting");
                A.True(Overlay.StubVisible, "re-created instead of staying stuck");
                A.Eq(Overlay.StubLabel, "Driveway", "label");
                A.True(WaitState(a, AppState.Live, 1000), "live");
                Destroy(a);
            });
            add("/app/flapping-stream-bounded", delegate
            {
                TL.Env("CCTV_OVERLAY_LIVE_STABLE_MS", "1000");
                App a = Make(NoGrid, PW, true);
                TL.Env("CCTV_OVERLAY_STUB_DROP_AFTER_MS", "10");
                int before = Video.StubStartCount;
                a.Toggle();
                A.True(WaitState(a, AppState.Error, 8000), "one frame per connection must not loop forever");
                A.Eq(a.RetryCount, App.MaxRetries, "retries");
                A.True(Video.StubStartCount - before <= 1 + App.MaxRetries, "bounded starts");
                Destroy(a);
            });
            add("/app/reload-in-error", delegate
            {
                App a = Make(NoGrid, PW, true);
                TL.Env("CCTV_OVERLAY_STUB_FAIL", "1");
                a.Toggle();
                A.True(WaitState(a, AppState.Error, 5000), "error");
                TL.Env("CCTV_OVERLAY_STUB_FAIL", null);
                a.Reload();
                A.Eq(a.State, AppState.Connecting, "reload retries");
                A.True(WaitState(a, AppState.Live, 2000), "live");
                Destroy(a);
            });
            add("/app/missing-password", delegate
            {
                App a = Make(NoGrid, null, true);
                A.Eq(a.State, AppState.Hidden, "fine without it");
                A.True(a.Handle("status", null).GetBool("ok"), "status");
                a.Toggle();
                A.Eq(a.State, AppState.Error, "immediate error");
                A.Contains(a.LastError, "password", "message");
                A.Eq(a.RetryCount, 0, "no retries");
                A.False(Video.StubActive, "no stream");
                a.Hide();
                A.Eq(a.State, AppState.Hidden, "hidden");
                Destroy(a);
            });
            add("/app/reload", delegate
            {
                App a = Make(NoGrid, PW, true);
                a.Show("driveway");
                A.True(WaitState(a, AppState.Live, 1000), "live");
                File.WriteAllText(path, "[recorder]\nhost = h.example.invalid\nusername = tester\n" +
                                        "[camera.garden]\nname = Garden\nchannel = 3\n" +
                                        "[camera.driveway]\nname = Driveway\nchannel = 2\n");
                int starts = Video.StubStartCount;
                a.Reload();
                A.Eq(a.CurrentIndex, 1, "still Driveway, new position");
                A.Eq(a.CurrentCamera.Name, "Driveway", "name");
                A.Eq(a.State, AppState.Live, "live");
                A.Eq(Video.StubStartCount, starts, "no needless reconnect");
                File.WriteAllText(path, "[recorder]\nhost = h.example.invalid\nusername = tester\n[camera.garden]\nname = Garden\nchannel = 3\n");
                a.Reload();
                A.Eq(a.CurrentIndex, 0, "first");
                A.Eq(Overlay.StubLabel, "Garden", "label");
                A.Eq(a.State, AppState.Connecting, "reconnect");
                File.WriteAllText(path, "[overlay]\nwidth = 1\n");
                try { a.Reload(); throw new TestFailure("bad reload accepted"); }
                catch (CctvException) { }
                A.Eq(a.CurrentCamera.Name, "Garden", "previous config kept");
                Destroy(a);
            });
            add("/app/reload-hidden", delegate
            {
                App a = Make(NoGrid, PW, true);
                a.Reload();
                A.Eq(a.State, AppState.Hidden, "hidden");
                A.False(Video.StubActive, "no stream");
                Destroy(a);
            });
            add("/app/ipc-handler-json", delegate
            {
                App a = Make(NoGrid, PW, true);
                JObj r = a.Handle("next", null);
                A.True(r.GetBool("ok"), "ok");
                A.True(r.GetBool("visible"), "visible");
                A.Eq(r.GetString("state"), "connecting", "state");
                A.Eq(r.GetString("camera"), "Driveway", "camera");
                A.Eq(r.GetInt("camera_index"), 1L, "index");
                A.Eq(r.GetInt("camera_count"), 3L, "count");
                A.True(r.Has("error") && r.Get("error") == null, "error null");
                A.Eq(a.Handle("list", null).GetArray("cameras").Count, 3, "list");
                A.False(a.Handle("bogus", null).GetBool("ok"), "unknown");
                quitCalled = false;
                a.Handle("quit", null);
                A.False(quitCalled, "only after the reply is written");
                a.AfterReply("quit");
                A.True(quitCalled, "quit");
                Destroy(a);
            });
        }
    }
}
