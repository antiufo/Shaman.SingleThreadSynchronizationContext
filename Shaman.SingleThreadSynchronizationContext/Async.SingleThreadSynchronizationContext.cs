using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Shaman.Runtime
{
    public sealed class SingleThreadSynchronizationContext : SynchronizationContext
    {
        private int threadId;

        private struct Job
        {
            public Job(SendOrPostCallback action, object state, TaskCompletionSource<bool> onCompleted)
            {
                this.Action = action;
                this.State = state;
                this.OnCompleted = onCompleted;
            }
            public readonly SendOrPostCallback Action;
            public readonly object State;
            public readonly TaskCompletionSource<bool> OnCompleted;
        }

        private BlockingCollection<Job> m_queue = new BlockingCollection<Job>();
        private LateArrivalBehavior lateArrivalBehavior;
        private SingleThreadSynchronizationContext onFailureForwardTo;
        private int firstLateArrivalCallbackExecuted;
        private TaskCompletionSource<bool> drainingCompletion = new TaskCompletionSource<bool>();
        public Thread Thread { get; private set; }
#if SHAMAN_CORE
        internal bool hasInstalledResponsivenessWatchdog;
        private static MethodInfo SendMethod;
        private static Func<object, Action> SendGetAction;
#endif
        private static MethodInfo BoringAsyncMethod;

        private static Type ContinuationWrapperType;
        private static Func<object, Action> ContinuationWrapperGetContinuation;
        private static Func<object, Task> ContinuationWrapperGetInnerTask;
        private static Func<object, Action> ContinuationWrapperGetInvokeAction;
        private static Func<object, IAsyncStateMachine> MoveNextRunnerGetStateMachine;


        static SingleThreadSynchronizationContext()
        {
            var syncCtxAwaitContinuationType = Type.GetType("System.Threading.Tasks.SynchronizationContextAwaitTaskContinuation+<>c__DisplayClass2");
            if (syncCtxAwaitContinuationType != null)
                BoringAsyncMethod = syncCtxAwaitContinuationType.GetMethod("<_cctor>b__3", BindingFlags.Instance | BindingFlags.NonPublic);
            ContinuationWrapperType = Type.GetType("System.Runtime.CompilerServices.AsyncMethodBuilderCore+ContinuationWrapper");

            if (ContinuationWrapperType != null)
            {
                ContinuationWrapperGetContinuation = GetGetter<Action>(ContinuationWrapperType, "m_continuation");
                ContinuationWrapperGetInnerTask = GetGetter<Task>(ContinuationWrapperType, "m_innerTask");
                ContinuationWrapperGetInvokeAction = GetGetter<Action>(ContinuationWrapperType, "m_invokeAction");
            }

            var moveNextRunnerType = Type.GetType("System.Runtime.CompilerServices.AsyncMethodBuilderCore+MoveNextRunner");
            if (moveNextRunnerType != null)
                MoveNextRunnerGetStateMachine = GetGetter<IAsyncStateMachine>(moveNextRunnerType, "m_stateMachine");

#if SHAMAN_CORE
            SendMethod = typeof(SynchronizationContextExtensionMethods).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Select(x => x.GetMethod("<Send>b__0", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)).SingleOrDefault(x => x != null);
            if (SendMethod != null)
                SendGetAction = GetGetter<Action>(SendMethod.DeclaringType, "action");
#endif
        }

        private static Func<object, T> GetGetter<T>(Type declaringType, string fieldName)
        {
            var field = declaringType.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            var param = Expression.Parameter(typeof(object), "obj");
            var lambda = Expression.Lambda<Func<object, T>>(Expression.Field(Expression.Convert(param, declaringType), field), param);
            return lambda.Compile();
        }

        public override void Post(SendOrPostCallback d, object state)
        {
            Interlocked.Increment(ref CtxSwitchCount);

            EnqueueJob(new Job(d, state, null), true);
        }


#if CORECLR
        static Func<bool, StackTrace> StackTraceCtor;
#endif

        private static StackTrace GetStackTrace(bool needFileInfo)
        {
#if CORECLR
            if (StackTraceCtor == null)
            {
                StackTraceCtor = ReflectionHelper.GetWrapper<Func<bool, StackTrace>>(typeof(StackTrace), ".ctor");
            }
            return StackTraceCtor(needFileInfo);
#else
            return new StackTrace(needFileInfo);
#endif
        }

        private bool EnqueueJob(Job job, bool allowForward)
        {

            if (!completed)
            {
                try
                {
                    var q = m_queue;
                    q.Add(job);
                    return true;
                }
                catch (InvalidOperationException)
                {
                }
            }

            if (onFirstLateArrival != null)
            {
                if (Interlocked.Increment(ref firstLateArrivalCallbackExecuted) == 1)
                {
                    onFirstLateArrival();
                    onFirstLateArrival = null;
                }
            }

            if (allowForward && onFailureForwardTo != null) return onFailureForwardTo.EnqueueJob(job, false);

            if (lateArrivalBehavior == LateArrivalBehavior.Throw)
            {
#if DESKTOP
                var frames = GetStackTrace(false).GetFrames();
                foreach (var frame in frames)
                {
                    var m = frame.GetMethod();
                    if (m != null)
                    {
                        if (m.DeclaringType == typeof(SingleThreadSynchronizationContext)) continue;
                        if (m.DeclaringType.GetTypeInfo().Assembly == typeof(Task).GetTypeInfo().Assembly && m.DeclaringType.Name == "SynchronizationContextAwaitTaskContinuation")
                            return false; // We have to pretend nothing happened and not to throw the exception
                        break;
                    }
                }
#else
                var frames = new Exception().StackTrace.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var frame in frames)
                {
                    if (frame.Contains("SingleThreadSynchronizationContext")) continue;
                    if (frame.Contains("SynchronizationContextAwaitTaskContinuation")) return false;
                    break;
                }
#endif
                throw new InvalidOperationException("The SingleThreadSynchronizationContext has completed and is no longer accepting continuations or callbacks.");
            }
            if (lateArrivalBehavior == LateArrivalBehavior.SpawnNewThread)
            {
                lock (this)
                {
                    if (lateArrivalSyncCtx == null || !lateArrivalSyncCtx.EnqueueJob(job, false))
                    {
                        var readyTcs = new TaskCompletionSource<bool>();
                        var firstContinuationExecutedTcs = new TaskCompletionSource<bool>();
                        Action deleg = () =>
                        {
                            SingleThreadSynchronizationContext.Run(async () =>
                            {
                                var ctx = (SingleThreadSynchronizationContext)SynchronizationContext.Current;
                                ctx.onFailureForwardTo = this;
                                lateArrivalSyncCtx = ctx;
                                readyTcs.SetResult(true);
                                drainingCompletion.Task.Wait();
                                await firstContinuationExecutedTcs.Task;
                                await Task.Delay(10000);
                            }, LateArrivalBehavior.Suppress);
                        };
#if DESKTOP
                        new Thread(() => deleg()) { Name = "Respawned thread for SingleThreadSynchronizationContext" }.Start();
#else
                        Task.Run(deleg);
#endif
                        readyTcs.Task.Wait();
                        if (!lateArrivalSyncCtx.EnqueueJob(job, false)) throw new Exception();
                        firstContinuationExecutedTcs.SetResult(true);
                    }
                    return true;
                }
            }


            return false;

        }

        private SingleThreadSynchronizationContext lateArrivalSyncCtx;
        private bool completed;
        private Action onFirstLateArrival;

        public bool HasPendingContinuations
        {
            get
            {
                return this.m_queue.Count != 0;
            }
        }

        public bool HasCompleted
        {
            get { return completed; }
        }

        public override void Send(SendOrPostCallback d, object state)
        {
            if (Environment.CurrentManagedThreadId == threadId)
            {
                d(state);
            }
            else
            {
                var tcs = new TaskCompletionSource<bool>();
                if (!EnqueueJob(new Job(d, state, tcs), true))
                    throw new InvalidOperationException("The target SynchronizationContext has completed and is no longer accepting tasks.");
                Interlocked.Increment(ref CtxSwitchCount);
                tcs.Task.Wait();
            }
        }

        internal static long CtxSwitchCount;

        public long LoopId
        {
            get
            {
                return loopId;

            }

        }

        private void RunOnCurrentThread()
        {
            while (true)
            {
                Job workItem;
                Interlocked.Increment(ref loopId);
                try
                {
                    var queue = m_queue;
                    if (queue == null) return;
                    if (!queue.TryTake(out workItem, Timeout.Infinite, CancellationToken.None)) return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                Volatile.Write(ref IsBusy, true);
                Interlocked.Increment(ref loopId);
                bool faulted = false;
                ticksStart = Stopwatch.GetTimestamp();
                try
                {
                    workItem.Action(workItem.State);
                }
                catch (Exception ex)
                {
                    if (workItem.OnCompleted != null) workItem.OnCompleted.SetException(ex);
                    faulted = true;
                }
                if (!faulted && workItem.OnCompleted != null) workItem.OnCompleted.SetResult(true);


                var handler = OnEventLoopIterationExecuted;
                if (handler != null)
                {
                    var ticksEnd = Stopwatch.GetTimestamp();
                    var elapsedMs = (double)(ticksEnd - ticksStart) / Stopwatch.Frequency;
                    if (elapsedMs >= MinimumEventLoopIterationDurationForCallback.TotalSeconds)
                    {
                        handler(this, ticksStart, ticksEnd, GetMethod(workItem), GetMethod(previousWorkItem));
                    }
                }
                previousWorkItem = workItem;
                Volatile.Write(ref IsBusy, false);
                ticksStart = 0;
            }
        }

        private Job previousWorkItem;
        public TimeSpan MinimumEventLoopIterationDurationForCallback { get; set; }

        internal bool IsBusy;


        private MethodInfo GetMethod(Job job)
        {
            if (job.Action == null) return null;
            var method = job.Action.GetMethodInfo();
            if (method == BoringAsyncMethod)
            {
                var deleg = (Delegate)job.State;
                var contWrapper = deleg.Target;


                while (contWrapper.GetType() == ContinuationWrapperType) contWrapper = ContinuationWrapperGetContinuation(contWrapper).Target;
                var stateMachine = MoveNextRunnerGetStateMachine(contWrapper);
                method = stateMachine.GetType().GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
#if SHAMAN_CORE
            else if (method == SendMethod)
            {
                var action = SendGetAction(job.Action.Target);
                method = action.GetMethodInfo();
            }
#endif
            return method;
        }

        public Action<SingleThreadSynchronizationContext, long, long, MethodInfo, MethodInfo> OnEventLoopIterationExecuted { get; set; }

        public static new SingleThreadSynchronizationContext Current
        {
            get
            {
                return (SingleThreadSynchronizationContext)SynchronizationContext.Current;
            }
        }

        internal long ticksStart;

        private SingleThreadSynchronizationContext()
        {

            threadId = Environment.CurrentManagedThreadId;

        }


        //~SingleThreadSynchronizationContext()
        //{
        //    Console.WriteLine("Finalizing " + this.GetHashCode() + " ***************************");
        //}

        public static SingleThreadSynchronizationContext CreateInNewThread()
        {
            return CreateInNewThread(LateArrivalBehavior.Throw);
        }
        public static SingleThreadSynchronizationContext CreateInNewThread(LateArrivalBehavior lateArrivalBehavior)
        {
            return CreateInNewThread(lateArrivalBehavior, null);
        }
        public static SingleThreadSynchronizationContext CreateInNewThread(LateArrivalBehavior lateArrivalBehavior, string threadName)
        {
            var tcs = new TaskCompletionSource<SingleThreadSynchronizationContext>();
#if DESKTOP
            var thread = new Thread(() =>
            {
                try
                {
                    Run(() =>
                    {
                        tcs.TrySetResult((SingleThreadSynchronizationContext)SynchronizationContext.Current);
                        return new TaskCompletionSource<bool>().Task;
                    }, lateArrivalBehavior);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }

            });
            if (threadName != null) thread.Name = threadName;
            thread.IsBackground = true;
#if !CORECLR
            thread.SetApartmentState(ApartmentState.STA);
#endif
            thread.IsBackground = true;
            thread.Start();

#else
            Task.Run(()=>
            {
                try
                {
                    Run(() =>
                    {
                        tcs.TrySetResult((SingleThreadSynchronizationContext)SynchronizationContext.Current);
                        return new TaskCompletionSource<bool>().Task;
                    }, lateArrivalBehavior);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
#endif
            tcs.Task.Wait();
            return tcs.Task.Result;
        }

        public static void Run(Func<Task> func)
        {
            Run(func, LateArrivalBehavior.Throw, null);
        }
        public static void Run(Func<Task> func, LateArrivalBehavior lateArrivalBehavior)
        {
            Run(func, lateArrivalBehavior, null);
        }


        public static void Run(Func<Task> func, Action onFirstLateArrival)
        {
            Run(func, LateArrivalBehavior.Suppress, onFirstLateArrival);
        }

        private static void Run(Func<Task> func, LateArrivalBehavior lateArrivalBehavior, Action onFirstLateArrival)
        {
            if (func == null) throw new ArgumentNullException("func");

            var prevCtx = SynchronizationContext.Current;

            var syncCtx = new SingleThreadSynchronizationContext();
            try
            {
                syncCtx.Thread = Thread.CurrentThread;
                syncCtx.onFirstLateArrival = onFirstLateArrival;
                //Console.WriteLine("Running " + syncCtx.GetHashCode() + " on thread " + Environment.CurrentManagedThreadId);
                syncCtx.lateArrivalBehavior = lateArrivalBehavior;
                SynchronizationContext.SetSynchronizationContext(syncCtx);

                var t = func();
                if (t == null) throw new InvalidOperationException("No task provided.");
                t.ContinueWith(delegate
                {
                    try
                    {
                        var q = syncCtx.m_queue;
                        if (q != null)
                        {
                            q.CompleteAdding();
                        }

                    }
                    catch
                    {
                        // TODO should we do something here? can it happen?
                        //Console.WriteLine("Perdindirindina");
                    }
                    syncCtx.completed = true;
                }, TaskScheduler.Default);
                syncCtx.RunOnCurrentThread();
                if (!syncCtx.aborted)
                {
                    t.GetAwaiter().GetResult();
                }
                if (syncCtx.aborted) throw new OperationCanceledException("The SingleThreadSynchronizationContext was aborted.");
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(prevCtx);

                var q = syncCtx.m_queue;
                if (q != null)
                    q.Dispose();
                syncCtx.m_queue = null;

                var d = syncCtx.drainingCompletion;
                if (d != null) d.TrySetResult(true);

            }
        }

        internal long loopId;
        private volatile bool aborted;
#if SHAMAN_CORE
        internal long responsivenessWatchdogThresholdStopwatchTicks;
        public ApplicationHangException LastHangException { get; internal set; }
        
        internal long lastHangLoopId;
        internal long lastSubmittedHangLoopId;
#endif
        public enum LateArrivalBehavior
        {
            Throw,
            Suppress,
            SpawnNewThread
        }

        public void Abort()
        {
            completed = true;
            aborted = true;
            var q = this.m_queue;
            if (q != null)
            {

                try
                {
                    q.CompleteAdding();
                }
                catch (ObjectDisposedException)
                {
                }
                try
                {
                    if (q.Count == 0)
                    {
                        q.Dispose();
                        this.m_queue = null;
                    }
                }
                catch (ObjectDisposedException)
                {
                }
                var f = this.onFailureForwardTo;
                if (f != null)
                    f.Abort();
                this.onFailureForwardTo = null;
                f = this.lateArrivalSyncCtx;
                if (f != null)
                    f.Abort();
                this.lateArrivalSyncCtx = null;
                this.onFirstLateArrival = null;
            }
        }

        public static Task BackgroundAsync()
        {
            if (SingleThreadSynchronizationContext.Current == null) throw new InvalidOperationException("Must be called from a SingleThreadSynchronizationContext.");
            return new TaskCompletionSource<bool>().Task;
        }

    }
}
