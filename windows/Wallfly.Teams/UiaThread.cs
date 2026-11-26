using System.Collections.Concurrent;

namespace Wallfly.Teams;

public enum UiaRunStatus
{
    Completed,
    /// <summary>The work took longer than <see cref="UiaThreadOptions.StuckFactor"/> × budget; its thread was abandoned.</summary>
    Abandoned,
    /// <summary>Paused after repeated abandonments; nothing was run.</summary>
    Unavailable,
    /// <summary>The work threw; the thread stays usable.</summary>
    Faulted,
}

public readonly record struct UiaRunResult<T>(UiaRunStatus Status, T? Value, Exception? Error = null);

public sealed class UiaThreadOptions
{
    /// <summary>Per-run budget; a run is abandoned after <see cref="StuckFactor"/> × this (0.22 s → 0.66 s).</summary>
    public TimeSpan Budget { get; init; } = TimeSpan.FromMilliseconds(220);
    public double StuckFactor { get; init; } = 3;
    public int AbandonLimit { get; init; } = 3;
    public TimeSpan AbandonWindow { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan UnavailableFor { get; init; } = TimeSpan.FromSeconds(30);
    public string Name { get; init; } = "Wallfly UIA";
}

/// <summary>
/// Runs UI Automation work on a dedicated MTA thread with a stuck watchdog. A UIA call into a hung Chromium
/// renderer can block far beyond the IUIAutomation2 timeouts, and a blocked COM call cannot be cancelled, so a
/// run that exceeds 3× its budget is answered with <see cref="UiaRunStatus.Abandoned"/>: the thread is left to
/// finish on its own (background thread, its session is disposed by that thread when the call returns) and a
/// fresh thread with a fresh session serves the next run. Three abandonments within 60 s pause the reader:
/// runs answer <see cref="UiaRunStatus.Unavailable"/> for 30 s. The session (e.g. a <see cref="UiaReader"/>) is
/// created on the worker thread by the factory, so its COM objects belong to that MTA thread.
/// One run at a time; concurrent callers wait their turn.
/// </summary>
public sealed class UiaThread<TSession> : IDisposable where TSession : class
{
    private readonly Func<TSession> factory;
    private readonly UiaThreadOptions options;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim turn = new(1, 1);
    private readonly Queue<DateTimeOffset> abandonments = new();
    private readonly object gate = new();
    private Worker? worker;
    private DateTimeOffset unavailableUntil = DateTimeOffset.MinValue;
    private bool disposed;

    public UiaThread(Func<TSession> factory, UiaThreadOptions? options = null, TimeProvider? time = null)
    {
        this.factory = factory;
        this.options = options ?? new UiaThreadOptions();
        this.time = time ?? TimeProvider.System;
    }

    public int TotalAbandonments { get; private set; }
    public int ThreadsStarted { get; private set; }

    public bool IsUnavailable
    {
        get { lock (gate) return time.GetUtcNow() < unavailableUntil; }
    }

    public async Task<UiaRunResult<T>> RunAsync<T>(Func<TSession, T> work, TimeSpan? budget = null, CancellationToken cancellationToken = default)
    {
        await turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Worker current;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (time.GetUtcNow() < unavailableUntil) return new UiaRunResult<T>(UiaRunStatus.Unavailable, default);
                if (worker is null)
                {
                    worker = new Worker(factory, options.Name);
                    ThreadsStarted++;
                }
                current = worker;
            }
            var completion = new TaskCompletionSource<UiaRunResult<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
            current.Post(session =>
            {
                try { completion.TrySetResult(new UiaRunResult<T>(UiaRunStatus.Completed, work(session))); }
                catch (Exception error) { completion.TrySetResult(new UiaRunResult<T>(UiaRunStatus.Faulted, default, error)); }
            }, error => completion.TrySetResult(new UiaRunResult<T>(UiaRunStatus.Faulted, default, error)));
            var limit = TimeSpan.FromTicks((long)((budget ?? options.Budget).Ticks * options.StuckFactor));
            var finished = await Task.WhenAny(completion.Task, Task.Delay(limit, cancellationToken)).ConfigureAwait(false);
            if (finished == completion.Task) return await completion.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Abandon(current);
            return new UiaRunResult<T>(UiaRunStatus.Abandoned, default);
        }
        finally { turn.Release(); }
    }

    private void Abandon(Worker stuck)
    {
        lock (gate)
        {
            stuck.Abandon();
            if (ReferenceEquals(worker, stuck)) worker = null;
            TotalAbandonments++;
            var now = time.GetUtcNow();
            abandonments.Enqueue(now);
            while (abandonments.Count > 0 && now - abandonments.Peek() > options.AbandonWindow) abandonments.Dequeue();
            if (abandonments.Count >= options.AbandonLimit)
            {
                unavailableUntil = now + options.UnavailableFor;
                abandonments.Clear();
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            worker?.Stop();
            worker = null;
        }
    }

    /// <summary>One MTA thread with its own session. Abandoned or stopped workers dispose the session on their own thread.</summary>
    private sealed class Worker
    {
        private readonly BlockingCollection<(Action<TSession> Work, Action<Exception> Failed)> queue = new();
        private readonly Func<TSession> factory;
        private volatile bool abandoned;

        public Worker(Func<TSession> factory, string name)
        {
            this.factory = factory;
            var thread = new Thread(Run) { IsBackground = true, Name = name };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public void Post(Action<TSession> work, Action<Exception> failed) => queue.Add((work, failed));

        public void Abandon()
        {
            abandoned = true;
            queue.CompleteAdding();
        }

        public void Stop() => queue.CompleteAdding();

        private void Run()
        {
            TSession? session = null;
            try
            {
                foreach (var (work, failed) in queue.GetConsumingEnumerable())
                {
                    if (abandoned) break;
                    if (session is null)
                    {
                        try { session = factory(); }
                        catch (Exception error)
                        {
                            failed(error);
                            continue;
                        }
                    }
                    work(session);
                }
            }
            finally
            {
                (session as IDisposable)?.Dispose();
                queue.Dispose();
            }
        }
    }
}
