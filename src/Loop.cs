// The main-loop helpers the C code got from GLib: one-shot/repeating timeouts and "run this on the UI thread".
// Everything that touches App, the overlay or a player runs on the thread that called Loop.Init().
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;

namespace CctvOverlay
{
    internal sealed class LoopTimer
    {
        System.Windows.Forms.Timer timer;
        readonly Func<bool> cb;

        internal LoopTimer(int ms, Func<bool> cb)
        {
            this.cb = cb;
            timer = new System.Windows.Forms.Timer();
            timer.Interval = Math.Max(1, ms);
            timer.Tick += OnTick;
            timer.Start();
        }

        void OnTick(object sender, EventArgs e)
        {
            if (timer == null)
                return;
            bool again = false;
            try
            {
                again = cb();
            }
            catch (Exception ex)
            {
                Log.Error("internal error in a timer: {0}", ex.Message);
            }
            if (!again)
                Remove();
        }

        /// <summary>Like g_source_remove(); safe to call more than once.</summary>
        public void Remove()
        {
            if (timer == null)
                return;
            timer.Stop();
            timer.Tick -= OnTick;
            timer.Dispose();
            timer = null;
        }

        public bool Active { get { return timer != null; } }
    }

    internal static class Loop
    {
        static Control invoker;
        static int ownerThread;

        public static void Init()
        {
            if (invoker != null && ownerThread == Thread.CurrentThread.ManagedThreadId)
                return;
            invoker = new Control();
            invoker.CreateControl();
            IntPtr force = invoker.Handle; /* the handle must exist before other threads post to it */
            GC.KeepAlive(force);
            ownerThread = Thread.CurrentThread.ManagedThreadId;
        }

        public static bool OnLoopThread { get { return Thread.CurrentThread.ManagedThreadId == ownerThread; } }

        /// <summary>g_timeout_add: cb returns true to keep running.</summary>
        public static LoopTimer Timeout(int ms, Func<bool> cb)
        {
            return new LoopTimer(ms, cb);
        }

        /// <summary>g_idle_add from any thread: run once on the loop thread.</summary>
        public static void Idle(Action a)
        {
            Control c = invoker;
            if (c == null)
                return;
            try
            {
                c.BeginInvoke(a);
            }
            catch (InvalidOperationException) { } /* loop is gone (shutdown) */
        }

        /// <summary>Run on the loop thread and wait for the result (used by the control-pipe workers).</summary>
        public static T Invoke<T>(Func<T> f)
        {
            if (OnLoopThread)
                return f();
            return (T)invoker.Invoke(f);
        }

        /// <summary>Test helper: process pending messages and timers for ms milliseconds.</summary>
        public static void Pump(int ms)
        {
            long end = Util.MonotonicMs() + ms;
            do
            {
                Application.DoEvents();
                Thread.Sleep(1);
            } while (Util.MonotonicMs() < end);
        }
    }
}
