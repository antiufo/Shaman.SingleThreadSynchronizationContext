using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace Shaman.Runtime
{
    public class ApplicationHangException : Exception
    {
        public long LoopId { get; internal set; }

        public ApplicationHangException(StackTrace st, string threadName)
            : base()
        {
            if (st != null)
            {
                var toStringMethod = typeof(StackTrace).GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).First(x => x.Name == "ToString" && x.GetParameters().Length == 1);
                var stackTrace = toStringMethod.Invoke(st, new object[] { 0 });
                var stackTraceStringField = typeof(Exception).GetField("_stackTraceString", BindingFlags.Instance | BindingFlags.NonPublic);
                stackTraceStringField.SetValue(this, stackTrace);
            }
            this.ThreadName = threadName;
        }

        public TimeSpan HangTime { get; internal set; }
        public string ThreadName { get; private set; }
        private static bool  IsMono = typeof(string).GetTypeInfo().Assembly.GetType("Mono.Runtime") != null;

        public static ApplicationHangException CreateForThread(Thread thread)
        {
            StackTrace tt = null;
            if (!IsMono)
            {
#if !CORECLR
#pragma warning disable 0618
                try
                {
                    thread.Suspend();
                }
                catch
                {
                    return null;
                }
                try
                {
                    tt = new System.Diagnostics.StackTrace(thread, false);
                }
                catch (ThreadStateException)
                {
                }
                try
                {
                    thread.Resume();
                }
                catch (Exception)
                {
                    Thread.Sleep(3000);
                    thread.Resume();
                }
#pragma warning restore 0618
#endif
            }


            return new ApplicationHangException(
                tt, 
                thread.Name
                );
        }
    }
}
