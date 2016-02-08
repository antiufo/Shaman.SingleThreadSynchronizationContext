using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Shaman.Runtime
{
    public class ResponsivenessWatchdog
    {

        private static List<WeakReference<SingleThreadSynchronizationContext>> list = new List<WeakReference<SingleThreadSynchronizationContext>>();
        private static bool started;



        [Configuration]
        private static volatile int Configuration_CheckerIntervalMs = 10;

        [Configuration(PerformanceValue = false)]
        private static volatile bool Configuration_Enable = true;

        [Configuration]
        private static int Configuration_CleanupPeriodWhenDisabledMs = 2000;

        [Configuration]
        private static long Configuration_MaxHangReportDelay = 5000;

        public static void InstallForCurrentThread(TimeSpan timeSpan)
        {


            var ctx = SingleThreadSynchronizationContext.Current;
            if (ctx == null) throw new InvalidOperationException();


            lock (list)
            {
                if (ctx.hasInstalledResponsivenessWatchdog) throw new InvalidOperationException();
                ctx.hasInstalledResponsivenessWatchdog = true;
                ctx.responsivenessWatchdogThresholdStopwatchTicks = (long)(timeSpan.TotalSeconds * Stopwatch.Frequency);
                list.Add(new WeakReference<SingleThreadSynchronizationContext>(ctx));
                StartThreadInternal();
            }

        }



        private static void StartThreadInternal()
        {

            if (!started)
            {
                started = true;
                var checker = new Thread(() =>
                {
                    while (true)
                    {
                        Thread.Sleep(Configuration_Enable ? Configuration_CheckerIntervalMs : Configuration_CleanupPeriodWhenDisabledMs);
                        lock (list)
                        {
                            if (Configuration_Enable)
                            {
                                for (int i = 0; i < list.Count; i++)
                                {
                                    SingleThreadSynchronizationContext ctx;
                                    list[i].TryGetTarget(out ctx);
                                    if (ctx?.HasCompleted == false)
                                    {
                                        var currentLoopId = Volatile.Read(ref ctx.loopId);
                                        if (ctx.LastHangException != null && currentLoopId != ctx.lastHangLoopId)
                                        {
                                            ReportHang(ctx);
                                        }
                                        var ticksEnd = Stopwatch.GetTimestamp();
                                        var ticksStart = ctx.ticksStart;
                                        var elapsed = ticksEnd - ticksStart;
                                        if (ctx.IsBusy && elapsed >= ctx.responsivenessWatchdogThresholdStopwatchTicks && ctx.lastSubmittedHangLoopId != currentLoopId)
                                        {
                                            ApplicationHangException ex = ctx.LastHangException;
                                            if (ex == null)
                                            {
                                                ex = ApplicationHangException.CreateForThread(ctx.Thread);
                                                ex.LoopId = currentLoopId;
                                                ctx.LastHangException = ex;
                                            }
                                            ex.HangTime = TimeSpan.FromSeconds((double)elapsed / Stopwatch.Frequency);
                                            ctx.lastHangLoopId = currentLoopId;
                                            if (elapsed > Configuration_MaxHangReportDelay)
                                            {
                                                ReportHang(ctx);
                                            }
                                        }
                                    }
                                    else
                                    {
                                        list.RemoveAt(i);
                                        i--;
                                    }
                                }
                            }
                            else
                            {
                                RemoveWhere(list, x =>
                                {
                                    SingleThreadSynchronizationContext target;
                                    x.TryGetTarget(out target);

                                    return target?.HasCompleted != false;
                                });
                            }
                        }

                    }
                });
                checker.IsBackground = true;
#if !CORECLR
                checker.Priority = ThreadPriority.AboveNormal;
#endif
                checker.Start();
            }
        }


        private static int RemoveWhere<T>(IList<T> items, Func<T, bool> predicate)
        {
            List<int> indexes = null;
            var index = 0;
            foreach (var item in items)
            {
                if (predicate(item))
                {
                    if (indexes == null) indexes = new List<int>();
                    indexes.Add(index);
                }
                else index++;
            }
            if (indexes == null) return 0;
            foreach (var idx in indexes)
            {
                items.RemoveAt(idx);
            }
            return indexes.Count;
        }


        private static void ReportHang(SingleThreadSynchronizationContext ctx)
        {
            var handler = ReportHangHandler;
            handler?.Invoke(ctx.LastHangException);

            ctx.LastHangException = null;
            ctx.lastSubmittedHangLoopId = ctx.lastHangLoopId;
        }

        public static Action<ApplicationHangException> ReportHangHandler { get; set; }


    }
}
