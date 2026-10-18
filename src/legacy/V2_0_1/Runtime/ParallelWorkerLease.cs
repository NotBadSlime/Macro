namespace MacroHid.Runtime;

internal sealed class ParallelWorkerLease : IDisposable
{
    private static readonly object Gate = new();
    private static int count;
    private readonly int workers;
    private int disposed;
    private ParallelWorkerLease(int workers) => this.workers = workers;
    internal static ParallelWorkerLease Acquire(int workers)
    {
        lock (Gate)
        {
            if (count + workers > 32) throw new InvalidOperationException("并行工作线程超过 32 个安全上限，请减少嵌套或同时运行的宏。");
            count += workers;
            return new(workers);
        }
    }
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) { lock (Gate) count -= workers; } }
}
