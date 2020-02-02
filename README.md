# Shaman.SingleThreadSynchronizationContext
Provides a single-threaded `SynchronizationContext` for console applications.

## Usage
```csharp
using Shaman.Runtime;

static void Main(string args[])
{
    SingleThreadSynchronizationContext.Run(async () => MainAsync(args));
}
static async Task MainAsync(string args[])
{
    // Tasks awaited here will complete their callbacks on the main thread.
    // This makes it easier to reason about async code (by always using coroutines/async, as opposed to real threading). 
}
```
