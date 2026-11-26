namespace Wallfly.Transcription.Deepgram;

/// <summary>
/// A FIFO executor on the thread pool (the counterpart of a serial <c>DispatchQueue</c>): posted
/// actions run one at a time in posting order, never inline in <see cref="Post"/>. All mutable
/// pipeline state is confined to it.
/// </summary>
internal sealed class SerialQueue(Action<Exception> onError)
{
    private readonly object gate = new();
    private readonly Queue<Action> items = new();
    private bool running;
    private int drainingThread;

    /// <summary>True while the caller runs inside a queued action.</summary>
    public bool IsCurrent => Volatile.Read(ref drainingThread) == Environment.CurrentManagedThreadId;

    public void Post(Action action)
    {
        lock (gate)
        {
            items.Enqueue(action);
            if (running) return;
            running = true;
        }
        ThreadPool.UnsafeQueueUserWorkItem(static state => ((SerialQueue)state!).Drain(), this);
    }

    private void Drain()
    {
        Volatile.Write(ref drainingThread, Environment.CurrentManagedThreadId);
        while (true)
        {
            Action next;
            lock (gate)
            {
                if (items.Count == 0)
                {
                    Volatile.Write(ref drainingThread, 0);
                    running = false;
                    return;
                }
                next = items.Dequeue();
            }
            try { next(); }
            catch (Exception error)
            {
                try { onError(error); } catch (Exception) { }
            }
        }
    }
}
