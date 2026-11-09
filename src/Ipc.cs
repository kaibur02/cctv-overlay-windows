// Control channel: one JSON object per line over a per-user named pipe (the Windows counterpart of the
// 0600 Unix socket). The pipe's ACL admits only your account and denies network logons; peers are also
// checked by impersonation, and one daemon per pipe is enforced with a named mutex (flock on Linux).
using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace CctvOverlay
{
    internal enum IpcResult { Ok = 0, Unavailable, Timeout, Failed }

    internal delegate JObj IpcHandler(string cmd, JObj req);

    internal sealed class IpcServer : IDisposable
    {
        public const int MaxRequest = 4096;
        public const int TimeoutMs = 2000;
        const int MaxConns = 8;

        readonly string name;
        readonly IpcHandler handler;
        readonly Action<string> after;
        readonly PipeSecurity security;
        readonly Mutex instanceLock;
        readonly Thread acceptThread;
        readonly ManualResetEvent stopping = new ManualResetEvent(false);
        int conns; /* live client connections, capped to keep the loop responsive */

        public static string DefaultPipeName()
        {
            return "cctv-overlay-" + WindowsIdentity.GetCurrent().User.Value;
        }

        /// <summary>Accepts "name" or "\\.\pipe\name".</summary>
        public static string NormalizeName(string spec)
        {
            const string prefix = "\\\\.\\pipe\\";
            string n = spec ?? "";
            if (n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                n = n.Substring(prefix.Length);
            if (n.Length == 0 || n.Length > 200 || n.IndexOfAny(new[] { '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
                throw new CctvException("invalid pipe name '" + spec + "' (use a plain name such as cctv-overlay-test)");
            return n;
        }

        public string PipePath { get { return "\\\\.\\pipe\\" + name; } }

        public IpcServer(string pipeName, IpcHandler handler, Action<string> after)
        {
            name = pipeName;
            this.handler = handler;
            this.after = after;

            bool created;
            string mutexName = "Local\\cctv-overlay-daemon." + name.Replace('\\', '_');
            instanceLock = new Mutex(true, mutexName, out created);
            if (!created)
            {
                bool got = false;
                try
                {
                    got = instanceLock.WaitOne(0); /* an abandoned lock from a crashed daemon is fine */
                }
                catch (AbandonedMutexException)
                {
                    got = true;
                }
                if (!got)
                {
                    instanceLock.Dispose();
                    throw new CctvException("another cctv-overlay daemon is already running (pipe " + PipePath + "); use " +
                                            "'cctv-overlay quit' to stop it");
                }
            }

            security = new PipeSecurity();
            SecurityIdentifier me = WindowsIdentity.GetCurrent().User;
            security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                                                      PipeAccessRights.FullControl, AccessControlType.Deny));

            /* Create the first instance now so a failure is reported before the daemon claims to be up. */
            NamedPipeServerStream first;
            try
            {
                first = NewInstance();
            }
            catch (Exception e)
            {
                ReleaseLock();
                throw new CctvException("cannot create the control pipe " + PipePath + ": " + e.Message +
                                        " (another program may be using that name)");
            }
            acceptThread = new Thread(() => AcceptLoop(first));
            acceptThread.IsBackground = true;
            acceptThread.Name = "cctv-ipc-accept";
            acceptThread.Start();
            Log.Info("control pipe listening on {0}", PipePath);
        }

        NamedPipeServerStream NewInstance()
        {
            return new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                                             PipeTransmissionMode.Byte, PipeOptions.Asynchronous, MaxRequest, MaxRequest,
                                             security);
        }

        void AcceptLoop(NamedPipeServerStream ps)
        {
            while (true)
            {
                try
                {
                    if (ps == null)
                        ps = NewInstance();
                    IAsyncResult ar = ps.BeginWaitForConnection(null, null);
                    int which = WaitHandle.WaitAny(new[] { ar.AsyncWaitHandle, stopping });
                    if (which == 1)
                    {
                        ps.Dispose();
                        return;
                    }
                    ps.EndWaitForConnection(ar);
                }
                catch (Exception e)
                {
                    if (stopping.WaitOne(0))
                        return;
                    Log.Debug("ipc: accept failed: {0}", e.Message);
                    if (ps != null)
                        ps.Dispose();
                    ps = null;
                    Thread.Sleep(50);
                    continue;
                }
                NamedPipeServerStream conn = ps;
                ps = null;
                if (Interlocked.Increment(ref conns) > MaxConns)
                { /* a same-user client cannot starve the control channel */
                    Interlocked.Decrement(ref conns);
                    Log.Warn("control: too many concurrent connections; rejecting one");
                    conn.Dispose();
                    continue;
                }
                ThreadPool.QueueUserWorkItem(delegate { Serve(conn); });
            }
        }

        static bool SameUser(NamedPipeServerStream conn)
        {
            try
            {
                SecurityIdentifier peer = null;
                conn.RunAsClient(delegate
                {
                    using (WindowsIdentity id = WindowsIdentity.GetCurrent(true) ?? WindowsIdentity.GetCurrent())
                        peer = id.User;
                });
                return peer != null && peer.Equals(WindowsIdentity.GetCurrent().User);
            }
            catch (Exception)
            {
                return false;
            }
        }

        void Serve(NamedPipeServerStream conn)
        {
            string cmd = null;
            try
            {
                if (!SameUser(conn))
                {
                    Log.Warn("ipc: rejected connection from a different user or with unreadable credentials");
                    return;
                }
                byte[] buf = new byte[MaxRequest + 1];
                int len = 0;
                long deadline = Util.MonotonicMs() + TimeoutMs;
                int nl = -1;
                while (true)
                {
                    int remaining = (int)(deadline - Util.MonotonicMs());
                    if (remaining <= 0)
                    {
                        Log.Debug("ipc: dropping idle connection ({0} s timeout)", TimeoutMs / 1000);
                        return;
                    }
                    IAsyncResult ar = conn.BeginRead(buf, len, MaxRequest - len, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(remaining))
                    {
                        Log.Debug("ipc: dropping idle connection ({0} s timeout)", TimeoutMs / 1000);
                        return; /* Dispose cancels the pending read */
                    }
                    int n = conn.EndRead(ar);
                    if (n <= 0)
                        return; /* EOF before a full line */
                    int old = len;
                    len += n;
                    nl = Array.IndexOf(buf, (byte)'\n', old, n);
                    if (nl >= 0)
                        break;
                    if (len >= MaxRequest)
                    {
                        SendReply(conn, new JObj().Set("ok", false).Set("error", "request too large (limit 4096 bytes)"));
                        return;
                    }
                }

                JObj reply;
                JObj req = null;
                try
                {
                    string line = new UTF8Encoding(false, true).GetString(buf, 0, nl).TrimEnd('\r');
                    req = Json.Parse(line) as JObj;
                }
                catch (Exception) { req = null; }
                if (req == null)
                {
                    reply = new JObj().Set("ok", false).Set("error", "malformed request: expected one JSON object");
                }
                else if (!(req.Get("cmd") is string))
                {
                    reply = new JObj().Set("ok", false).Set("error", "malformed request: missing string member \"cmd\"");
                }
                else
                {
                    cmd = (string)req.Get("cmd");
                    string c = cmd;
                    JObj r = req;
                    try
                    {
                        reply = Loop.Invoke(() => handler(c, r));
                    }
                    catch (Exception e)
                    {
                        Log.Error("internal error handling '{0}': {1}", c, e.Message);
                        reply = new JObj().Set("ok", false).Set("error", "internal error");
                    }
                }
                SendReply(conn, reply);
            }
            catch (Exception e)
            {
                Log.Debug("ipc: connection error: {0}", e.Message);
            }
            finally
            {
                try { conn.Dispose(); } catch (Exception) { }
                Interlocked.Decrement(ref conns);
                if (cmd != null && after != null)
                {
                    string c = cmd;
                    Loop.Idle(() => after(c));
                }
            }
        }

        static void SendReply(NamedPipeServerStream conn, JObj reply)
        {
            if (!reply.Has("version"))
                reply.Set("version", Util.Version);
            string err = reply.Get("error") as string;
            if (err != null)
                reply.Set("error", Log.Redact(err));
            byte[] data = Encoding.UTF8.GetBytes(Json.Write(reply) + "\n");
            IAsyncResult ar = conn.BeginWrite(data, 0, data.Length, null, null);
            if (ar.AsyncWaitHandle.WaitOne(TimeoutMs))
            {
                conn.EndWrite(ar);
                try { conn.WaitForPipeDrain(); } catch (Exception) { }
            }
            else
            {
                Log.Debug("ipc: reply write timed out");
            }
        }

        void ReleaseLock()
        {
            try { instanceLock.ReleaseMutex(); } catch (Exception) { }
            instanceLock.Dispose();
        }

        public void Dispose()
        {
            stopping.Set();
            if (acceptThread != null)
                acceptThread.Join(1000);
            ReleaseLock();
        }
    }

    internal static class IpcClient
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        static extern bool WaitNamedPipe(string name, int timeout);

        public static bool PipeExists(string name)
        {
            if (WaitNamedPipe("\\\\.\\pipe\\" + name, 1))
                return true;
            int e = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return e == 121 /* ERROR_SEM_TIMEOUT: exists, all instances busy */;
        }

        static bool OwnedByMe(NamedPipeClientStream c)
        {
            try
            {
                PipeSecurity ps = c.GetAccessControl();
                var owner = ps.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
                if (owner == null)
                    return false;
                return owner.Equals(WindowsIdentity.GetCurrent().User) ||
                       owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static IpcResult Request(string pipeName, string request, out string reply, out string error)
        {
            reply = null;
            error = null;
            if (!PipeExists(pipeName))
            {
                error = "daemon is not running";
                return IpcResult.Unavailable;
            }
            using (var c = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
                                                     TokenImpersonationLevel.Identification))
            {
                try
                {
                    c.Connect(IpcServer.TimeoutMs);
                }
                catch (TimeoutException)
                {
                    error = "timed out connecting to the daemon";
                    return IpcResult.Timeout;
                }
                catch (IOException e)
                {
                    error = e.Message;
                    return IpcResult.Unavailable;
                }
                catch (UnauthorizedAccessException)
                {
                    error = "access to the control pipe was denied (it belongs to another account)";
                    return IpcResult.Failed;
                }
                if (!OwnedByMe(c))
                {
                    error = "the control pipe \\\\.\\pipe\\" + pipeName + " is not owned by you; refusing to use it";
                    return IpcResult.Failed;
                }
                long deadline = Util.MonotonicMs() + IpcServer.TimeoutMs;
                try
                {
                    byte[] data = Encoding.UTF8.GetBytes(request + "\n");
                    c.Write(data, 0, data.Length);
                    c.Flush();
                    var ms = new MemoryStream();
                    byte[] buf = new byte[4096];
                    while (true)
                    {
                        int remaining = (int)(deadline - Util.MonotonicMs());
                        if (remaining <= 0)
                        {
                            error = "timed out waiting for the daemon";
                            return IpcResult.Timeout;
                        }
                        IAsyncResult ar = c.BeginRead(buf, 0, buf.Length, null, null);
                        if (!ar.AsyncWaitHandle.WaitOne(remaining))
                        {
                            error = "timed out waiting for the daemon";
                            return IpcResult.Timeout;
                        }
                        int n = c.EndRead(ar);
                        if (n <= 0)
                            break;
                        int nl = Array.IndexOf(buf, (byte)'\n', 0, n);
                        ms.Write(buf, 0, nl >= 0 ? nl : n);
                        if (nl >= 0)
                        {
                            reply = Encoding.UTF8.GetString(ms.ToArray());
                            return IpcResult.Ok;
                        }
                        if (ms.Length > 1 << 20)
                            break;
                    }
                    error = "daemon closed the connection without a reply";
                    return IpcResult.Failed;
                }
                catch (IOException e)
                {
                    error = e.Message;
                    return IpcResult.Failed;
                }
            }
        }
    }
}
