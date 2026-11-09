// Central logger: levels, redaction of credentials, control-character stripping, sinks.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CctvOverlay
{
    internal enum LogLevel { Debug = 0, Info, Warning, Error }

    internal delegate void LogSink(LogLevel level, string line);

    internal static class Log
    {
        static readonly object Lock = new object();
        static LogLevel level = LogLevel.Info;
        static readonly List<string> Secrets = new List<string>(); /* sorted by descending length */
        static LogSink sink;
        static TextWriter fileWriter;

        static readonly Regex UrlRe = new Regex("([A-Za-z][A-Za-z0-9+.\\-]*://)[^@\\s\"']*@", RegexOptions.CultureInvariant);
        static readonly Regex AuthRe = new Regex("(?i)(proxy-)?authorization:\\s*[^\\r\\n]+", RegexOptions.CultureInvariant);

        const string RedactedUnsafe = "[unredactable message]";
        static readonly string[] Names = { "debug", "info", "warning", "error" };

        public static LogLevel Level
        {
            get { return level; }
            set { level = value; }
        }

        public static bool LevelFromString(string s, out LogLevel l)
        {
            l = LogLevel.Info;
            if (s == null)
                return false;
            switch (s.ToLowerInvariant())
            {
                case "debug": l = LogLevel.Debug; return true;
                case "info": l = LogLevel.Info; return true;
                case "warning":
                case "warn": l = LogLevel.Warning; return true;
                case "error": l = LogLevel.Error; return true;
            }
            return false;
        }

        static void AddSecretForm(string form)
        {
            if (string.IsNullOrEmpty(form))
                return;
            if (Secrets.Contains(form))
                return;
            int pos = 0;
            while (pos < Secrets.Count && Secrets[pos].Length >= form.Length)
                pos++;
            Secrets.Insert(pos, form);
        }

        /// <summary>Registers a secret; it (and its percent-encoded form) is replaced by *** in every log line.</summary>
        public static void RegisterSecret(string secret)
        {
            if (string.IsNullOrEmpty(secret))
                return;
            lock (Lock)
            {
                AddSecretForm(secret);
                AddSecretForm(Util.PctEncode(secret));
            }
        }

        public static void ClearSecrets()
        {
            lock (Lock)
                Secrets.Clear();
        }

        public static string Redact(string text)
        {
            string cur = text ?? "";
            try
            {
                lock (Lock)
                {
                    foreach (string s in Secrets)
                        cur = cur.Replace(s, "***");
                }
                cur = UrlRe.Replace(cur, "$1***:***@");
                cur = AuthRe.Replace(cur, "Authorization: ***");
                return cur;
            }
            catch (Exception)
            {
                return RedactedUnsafe; /* never fail open */
            }
        }

        /// <summary>Keep log output single-line and free of terminal escape sequences.</summary>
        static string StripControls(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(((c < 0x20 && c != '\t') || c == 0x7f) ? '?' : c);
            return sb.ToString();
        }

        public static void SetSink(LogSink s)
        {
            sink = s;
        }

        /// <summary>Daemon without a terminal: append to a private log file, rotated once above 512 KiB.</summary>
        public static void UseFile(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 512 * 1024)
                {
                    string old = path + ".1";
                    if (File.Exists(old))
                        File.Delete(old);
                    File.Move(path, old);
                }
                var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                fileWriter = TextWriter.Synchronized(new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true });
            }
            catch (Exception)
            {
                fileWriter = null; /* nowhere to log: stay silent rather than crash */
            }
        }

        public static void Msg(LogLevel l, string fmt, params object[] args)
        {
            if (l < level)
                return;
            string raw = args != null && args.Length > 0 ? string.Format(System.Globalization.CultureInfo.InvariantCulture, fmt, args) : fmt;
            string safe = StripControls(Redact(raw));
            LogSink s = sink;
            if (s != null)
            {
                s(l, safe);
                return;
            }
            TextWriter fw = fileWriter;
            if (fw != null)
            {
                try
                {
                    fw.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " cctv-overlay: " + Names[(int)l] + ": " + safe);
                }
                catch (Exception) { }
                return;
            }
            try
            {
                Console.Error.WriteLine("cctv-overlay: " + Names[(int)l] + ": " + safe);
                Console.Error.Flush();
            }
            catch (Exception) { }
        }

        public static void Debug(string fmt, params object[] args) { Msg(LogLevel.Debug, fmt, args); }
        public static void Info(string fmt, params object[] args) { Msg(LogLevel.Info, fmt, args); }
        public static void Warn(string fmt, params object[] args) { Msg(LogLevel.Warning, fmt, args); }
        public static void Error(string fmt, params object[] args) { Msg(LogLevel.Error, fmt, args); }
    }
}
