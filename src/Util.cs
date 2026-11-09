// Small helpers: percent-encoding, wiping, monotonic time, version.
using System;
using System.Diagnostics;
using System.Text;

namespace CctvOverlay
{
    internal static class Util
    {
        public const string Version = "0.1.0";

        static readonly Stopwatch Clock = Stopwatch.StartNew();

        /// <summary>RFC 3986 unreserved characters stay; every other UTF-8 byte becomes %XX.</summary>
        public static string PctEncode(string s)
        {
            const string hex = "0123456789ABCDEF";
            byte[] bytes = Encoding.UTF8.GetBytes(s ?? "");
            var sb = new StringBuilder(bytes.Length * 3);
            foreach (byte b in bytes)
            {
                bool keep = (b >= (byte)'a' && b <= (byte)'z') || (b >= (byte)'A' && b <= (byte)'Z') ||
                            (b >= (byte)'0' && b <= (byte)'9') || b == '-' || b == '.' || b == '_' || b == '~';
                if (keep)
                {
                    sb.Append((char)b);
                }
                else
                {
                    sb.Append('%');
                    sb.Append(hex[b >> 4]);
                    sb.Append(hex[b & 15]);
                }
            }
            Wipe(bytes);
            return sb.ToString();
        }

        public static void Wipe(byte[] b)
        {
            if (b != null)
                Array.Clear(b, 0, b.Length);
        }

        public static void Wipe(char[] c)
        {
            if (c != null)
                Array.Clear(c, 0, c.Length);
        }

        public static long MonotonicMs()
        {
            return Clock.ElapsedMilliseconds;
        }

        /// <summary>Like strtoll(s, end, 10) with "the whole string must be a number".</summary>
        public static bool TryParseInt64Strict(string s, out long v)
        {
            v = 0;
            if (string.IsNullOrEmpty(s))
                return false;
            int i = 0;
            bool neg = false;
            if (s[0] == '+' || s[0] == '-')
            {
                neg = s[0] == '-';
                i = 1;
            }
            if (i >= s.Length)
                return false;
            long acc = 0;
            for (; i < s.Length; i++)
            {
                char c = s[i];
                if (c < '0' || c > '9')
                    return false;
                if (acc > (long.MaxValue - (c - '0')) / 10)
                    return false; /* overflow, like ERANGE */
                acc = acc * 10 + (c - '0');
            }
            v = neg ? -acc : acc;
            return true;
        }

        public static bool IsAsciiSpace(char c)
        {
            return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\v' || c == '\f';
        }

        public static string Env(string name)
        {
            string v = Environment.GetEnvironmentVariable(name);
            return v;
        }
    }

    /// <summary>The error type used where the C code used GError.</summary>
    internal sealed class CctvException : Exception
    {
        public readonly int Code;

        public CctvException(string message) : base(message) { Code = 1; }

        public CctvException(string message, int code) : base(message) { Code = code; }
    }
}
