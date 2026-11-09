// Global hotkeys ([keys] in config.ini). The Windows counterpart of the Hyprland key bindings: the daemon
// registers them with RegisterHotKey and runs the same command a bound `cctv-overlay <verb>` would send.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CctvOverlay
{
    internal sealed class Hotkey
    {
        public const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

        public uint Modifiers;
        public uint Vk;
        public string Key;  /* canonical key name, e.g. "KP_Multiply", "minus", "F9" */
        public string Text; /* how people write it, e.g. "Ctrl + Num *", "Ctrl + Shift + -"; parses back to the same key */

        sealed class KeyInfo
        {
            public uint Vk;
            public string Name, Display;
            public bool Typing; /* used for typing text: taking it without a modifier breaks other programs */
        }

        static readonly Dictionary<string, KeyInfo> Keys = new Dictionary<string, KeyInfo>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static void K(string name, uint vk, string display, bool typing, params string[] aliases)
        {
            Keys[name] = new KeyInfo { Vk = vk, Name = name, Display = display ?? name, Typing = typing };
            foreach (string a in aliases)
                Aliases[a] = name;
            if (display != null && display != name)
                Aliases[display] = name;
        }

        static Hotkey()
        {
            /* Numpad (the Hyprland/X11 names, plus "Num *" style) */
            K("KP_Multiply", 0x6A, "Num *", false, "Multiply", "*");
            K("KP_Divide", 0x6F, "Num /", false, "Divide");
            K("KP_Subtract", 0x6D, "Num -", false, "Subtract");
            K("KP_Add", 0x6B, "Num +", false, "Add");
            K("KP_Decimal", 0x6E, "Num .", false, "Decimal");
            K("KP_Separator", 0x6C, null, false, "Separator");
            for (uint i = 0; i <= 9; i++)
                K("KP_" + i, 0x60 + i, "Num " + i, false, "NumPad" + i);
            for (uint i = 1; i <= 24; i++)
                K("F" + i, 0x70 + i - 1, null, false);
            for (char c = 'A'; c <= 'Z'; c++)
                K(c.ToString(), c, null, true);
            for (char c = '0'; c <= '9'; c++)
                K(c.ToString(), c, null, true, "D" + c);
            K("minus", 0xBD, "-", true, "OemMinus", "hyphen", "dash");
            K("equal", 0xBB, "=", true, "OemPlus", "equals", "plus");
            K("comma", 0xBC, ",", true, "OemComma");
            K("period", 0xBE, ".", true, "OemPeriod", "dot");
            K("slash", 0xBF, "/", true, "Oem2");
            K("semicolon", 0xBA, ";", true, "Oem1");
            K("grave", 0xC0, "`", true, "Oem3", "backtick");
            K("bracketleft", 0xDB, "[", true, "Oem4");
            K("bracketright", 0xDD, "]", true, "Oem6");
            K("backslash", 0xDC, "\\", true, "Oem5");
            K("apostrophe", 0xDE, "'", true, "Oem7", "quote");
            K("Space", 0x20, null, true, "Spacebar");
            K("Tab", 0x09, null, true);
            K("Return", 0x0D, "Enter", true);
            K("BackSpace", 0x08, "Backspace", true);
            K("Escape", 0x1B, "Esc", false);
            K("Insert", 0x2D, null, false, "Ins");
            K("Delete", 0x2E, null, true, "Del");
            K("Home", 0x24, null, true);
            K("End", 0x23, null, true);
            K("Page_Up", 0x21, "Page Up", true, "Prior", "PageUp", "PgUp");
            K("Page_Down", 0x22, "Page Down", true, "Next", "PageDown", "PgDn");
            K("Up", 0x26, null, true, "UpArrow");
            K("Down", 0x28, null, true, "DownArrow");
            K("Left", 0x25, null, true, "LeftArrow");
            K("Right", 0x27, null, true, "RightArrow");
            K("Pause", 0x13, null, false, "Break");
            K("Scroll_Lock", 0x91, "Scroll Lock", false, "ScrollLock");
            K("Print", 0x2C, "Print Screen", false, "PrintScreen", "PrtSc", "PrtScn");
            K("XF86AudioPlay", 0xB3, "Play/Pause", false, "MediaPlay", "PlayPause");
            K("XF86AudioNext", 0xB0, "Next Track", false, "MediaNext");
            K("XF86AudioPrev", 0xB1, "Previous Track", false, "MediaPrevious", "MediaPrev");
            K("XF86AudioStop", 0xB2, "Stop Media", false, "MediaStop");
        }

        public const string Examples = "examples: Ctrl + Num *, Ctrl + Shift + -, Alt + F9, Num /";

        /* "Num *", "Numpad 5", "NumPad5", "KP_Add", "Num +" ... -> KP_ names, before the '+' separators are split. */
        static readonly System.Text.RegularExpressions.Regex NumpadRe = new System.Text.RegularExpressions.Regex(
            @"(?i)(?<![A-Za-z0-9])(?:numpad|num|keypad|kp)[ _]*(\*|multiply|/|divide|-|minus|subtract|\+|plus|add|\.|decimal|[0-9])(?![A-Za-z0-9])");

        static string NumpadName(string k)
        {
            switch (k.ToLowerInvariant())
            {
                case "*": case "multiply": return "KP_Multiply";
                case "/": case "divide": return "KP_Divide";
                case "-": case "minus": case "subtract": return "KP_Subtract";
                case "+": case "plus": case "add": return "KP_Add";
                case ".": case "decimal": return "KP_Decimal";
                default: return "KP_" + k;
            }
        }

        static KeyInfo Lookup(string name)
        {
            KeyInfo ki;
            string canon;
            if (Keys.TryGetValue(name, out ki))
                return ki;
            if (Aliases.TryGetValue(name, out canon) && Keys.TryGetValue(canon, out ki))
                return ki;
            return null;
        }

        /// <summary>"MODIFIERS + KEY", written the way people say it ("Ctrl + Shift + -", "Ctrl + Num *", "Alt+F9")
        /// or with the Hyprland names ("CTRL + KP_Multiply", "SUPER + F9"). "none" or "" = not bound (h == null).</summary>
        public static bool TryParse(string spec, out Hotkey h, out string err)
        {
            h = null;
            err = null;
            string s = (spec ?? "").Trim();
            if (s.Length == 0 || string.Equals(s, "none", StringComparison.OrdinalIgnoreCase))
                return true;
            s = NumpadRe.Replace(s, m => NumpadName(m.Groups[1].Value));
            if (s == "+")
                s = "plus";
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\+\s*\+\s*$", "+plus"); /* "Ctrl + +" */
            string[] parts = s.Split('+');
            uint m0 = 0;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string t = parts[i].Trim().ToUpperInvariant();
                switch (t)
                {
                    case "CTRL":
                    case "CTL":
                    case "CONTROL": m0 |= ModControl; break;
                    case "ALT": m0 |= ModAlt; break;
                    case "SHIFT": m0 |= ModShift; break;
                    case "SUPER":
                    case "WIN":
                    case "WINDOWS":
                    case "META": m0 |= ModWin; break;
                    case "":
                        err = "'" + spec.Trim() + "' has an empty part between the + signs (" + Examples + ")";
                        return false;
                    default:
                        err = "'" + parts[i].Trim() + "' is not a modifier; use Ctrl, Alt, Shift or Win before the key (" + Examples + ")";
                        return false;
                }
            }
            string key = parts[parts.Length - 1].Trim();
            string up = key.ToUpperInvariant();
            if (key.Length == 0 || up == "CTRL" || up == "CONTROL" || up == "ALT" || up == "SHIFT" || up == "WIN" || up == "SUPER")
            {
                err = "'" + spec.Trim() + "' has no key after the modifiers; add one, e.g. Ctrl + Shift + F9";
                return false;
            }
            KeyInfo ki = Lookup(key);
            if (ki == null)
            {
                err = "'" + key + "' is not a key name cctv-overlay knows (" + Examples + ")";
                return false;
            }
            h = new Hotkey();
            h.Modifiers = m0;
            h.Vk = ki.Vk;
            h.Key = ki.Name;
            var words = new List<string>();
            if ((m0 & ModControl) != 0) words.Add("Ctrl");
            if ((m0 & ModAlt) != 0) words.Add("Alt");
            if ((m0 & ModShift) != 0) words.Add("Shift");
            if ((m0 & ModWin) != 0) words.Add("Win");
            words.Add(ki.Display);
            h.Text = string.Join(" + ", words.ToArray());
            return true;
        }

        /// <summary>True when the key types text (letters, digits, punctuation, Space, arrows…) and has no modifier:
        /// registering it takes it away from every other program.</summary>
        public bool StealsTyping
        {
            get
            {
                KeyInfo ki = Lookup(Key);
                return (Modifiers & (ModControl | ModAlt | ModWin)) == 0 && ki != null && ki.Typing;
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        /// <summary>Tries to register the key for a moment. False when another program already uses it.</summary>
        public bool IsFree()
        {
            const int id = 0x7FF0;
            if (!RegisterHotKey(IntPtr.Zero, id, Modifiers | ModNoRepeat, Vk))
                return false;
            UnregisterHotKey(IntPtr.Zero, id);
            return true;
        }

        public override string ToString()
        {
            return Text;
        }
    }

    /// <summary>Registers the configured hotkeys on a hidden window and calls back with the verb.</summary>
    internal sealed class HotkeyManager : NativeWindow, IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        const int WM_HOTKEY = 0x0312;

        readonly Action<string> onKey;
        readonly Dictionary<int, string> verbs = new Dictionary<int, string>();
        int nextId = 1;

        public HotkeyManager(Action<string> onKey)
        {
            this.onKey = onKey;
            CreateHandle(new CreateParams());
        }

        public void Apply(Config cfg)
        {
            UnregisterAll();
            Register(cfg.KeyToggle, "toggle");
            Register(cfg.KeyNext, "next");
            Register(cfg.KeyPrev, "prev");
            Register(cfg.KeyAudio, "audio");
        }

        void Register(Hotkey h, string verb)
        {
            if (h == null)
                return;
            int id = nextId++;
            if (RegisterHotKey(Handle, id, h.Modifiers | Hotkey.ModNoRepeat, h.Vk))
            {
                verbs[id] = verb;
                Log.Debug("key '{0}' -> {1}", h.Text, verb);
            }
            else
            {
                int e = Marshal.GetLastWin32Error();
                Log.Warn("cannot use key '{0}' for {1}: {2}", h.Text, verb,
                         e == 1409 ? "it is already used by another program; choose another key in config.ini [keys]"
                                   : "error " + e);
            }
        }

        void UnregisterAll()
        {
            foreach (int id in verbs.Keys)
                UnregisterHotKey(Handle, id);
            verbs.Clear();
        }

        /// <summary>Windows asked the daemon to exit (taskkill without /F, sign-out, shutdown).</summary>
        public Action QuitRequested;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_CLOSE || (m.Msg == Native.WM_ENDSESSION && m.WParam != IntPtr.Zero))
            {
                Log.Info("Windows asked the daemon to exit; shutting down");
                if (QuitRequested != null)
                    QuitRequested();
                return;
            }
            if (m.Msg == WM_HOTKEY)
            {
                string verb;
                if (verbs.TryGetValue(m.WParam.ToInt32(), out verb))
                {
                    Log.Debug("key pressed: {0}", verb);
                    onKey(verb);
                }
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            UnregisterAll();
            DestroyHandle();
        }
    }
}
