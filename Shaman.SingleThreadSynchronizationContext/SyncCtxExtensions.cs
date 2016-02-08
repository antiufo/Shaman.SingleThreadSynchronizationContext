using System;
using System.Threading;
using System.Threading.Tasks;

namespace Shaman.Runtime
{

    public static class SynchronizationContextExtensionMethods
    {

        public static Task SendAsync(this SynchronizationContext syncCtx, Action action)
        {
            var tcs = new TaskCompletionSource<bool>();
            syncCtx.Post(dummy =>
            {
                try
                {
                    action();
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }, null);
            return tcs.Task;
        }

        public static Task<T> SendAsync<T>(this SynchronizationContext syncCtx, Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>();
            syncCtx.Post(dummy =>
            {
                try
                {
                    var result = func();
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }, null);
            return tcs.Task;
        }

        public static void Post(this SynchronizationContext ctx, Action action)
        {
            if ((ctx as SingleThreadSynchronizationContext)?.Thread == Thread.CurrentThread) action();
            else ctx.Post(dummy => action(), null);
        }
        public static void Send(this SynchronizationContext ctx, Action action)
        {
            if ((ctx as SingleThreadSynchronizationContext)?.Thread == Thread.CurrentThread) action();
            else ctx.Send(dummy => action(), null);
        }

        public static void Post(this SynchronizationContext ctx, Func<Task> func)
        {
            if ((ctx as SingleThreadSynchronizationContext)?.Thread == Thread.CurrentThread) func();
            else ctx.Post(dummy => func(), null);

        }

        public static Action SynchronousSendCallback { get; set; }

        public static void Send(this SynchronizationContext ctx, Func<Task> func)
        {
            if (ctx == SynchronizationContext.Current)
                throw new InvalidOperationException("Cannot call a synchronous Send() when the argument is an asynchronous function and the synchronization context is the currently active one.");
            SynchronousSendCallback?.Invoke();
            var t = ctx.SendAsync(func);
            try
            {
                t.Wait();
            }
            catch (AggregateException ex) when (ex.InnerException != null)
            {
                if (ex.InnerException != null) throw ex.InnerException;
            }

        }
        public static Task SendAsync(this SynchronizationContext ctx, Func<Task> func)
        {
            var tcs = new TaskCompletionSource<bool>();
            ctx.Post(async dummy =>
            {
                try
                {
                    await func();
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }

            }, null);
            return tcs.Task;
        }


    }
}