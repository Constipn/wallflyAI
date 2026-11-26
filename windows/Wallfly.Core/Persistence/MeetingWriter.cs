using System.Diagnostics;

namespace Wallfly.Core.Persistence;

/// <summary>
/// Write cadence of spec 8 / the appendix table: an edit is saved 1.5 s later, at the latest 5 s after
/// the first unsaved change, and a recording meeting is checkpointed every 30 s.
/// </summary>
public sealed record WriteCadence(TimeSpan Debounce, TimeSpan Cap, TimeSpan? Checkpoint)
{
    public static readonly WriteCadence Default = new(TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));

    /// <summary>
    /// Pure cadence decision. While a meeting is recording, only the checkpoint (and an explicit
    /// flush) writes — matching <c>PersistenceCadenceTests.withoutCheckpointsNothingIsWrittenWhileRecording</c>
    /// of the macOS source, where 120 s of edits during a recording produce no write at all.
    /// </summary>
    public bool IsDue(DateTimeOffset now, bool recording, DateTimeOffset firstDirty, DateTimeOffset lastDirty, DateTimeOffset? lastWrite)
    {
        if (recording)
            return Checkpoint is TimeSpan checkpoint && now - (lastWrite ?? firstDirty) >= checkpoint;
        return now - lastDirty >= Debounce || now - firstDirty >= Cap;
    }
}

/// <summary>
/// One background thread that saves meetings. <see cref="Mark"/> serializes on the caller's thread
/// (the thread that owns the meeting) and only hands bytes to the writer, so a save can never observe
/// a half-mutated transcript. Per meeting the newest snapshot wins; a revision counter makes sure a
/// write that started before a newer edit never marks the meeting clean.
/// </summary>
public sealed class MeetingWriter : IDisposable
{
    private const int PollMilliseconds = 15;

    private sealed class Entry
    {
        public byte[] Bytes = [];
        public long Revision;
        public DateTimeOffset FirstDirty;
        public DateTimeOffset LastDirty;
    }

    private readonly MeetingLibrary library;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly Dictionary<Guid, Entry> dirty = [];
    private readonly Dictionary<Guid, DateTimeOffset> lastWrite = [];
    private readonly ManualResetEventSlim signal = new(false);
    private readonly Thread pump;
    private volatile bool running = true;
    private volatile bool flushRequested;
    private int startedWrites;
    private int completedWrites;
    private Exception? lastError;

    public WriteCadence Cadence { get; }

    /// <summary>The meeting that is currently recording; it follows the checkpoint rule instead of the debounce.</summary>
    public Guid? RecordingMeetingId { get; set; }

    /// <summary>Write attempts since construction (including failed ones).</summary>
    public int StartedWrites => Volatile.Read(ref startedWrites);
    public int CompletedWrites => Volatile.Read(ref completedWrites);

    public bool HasPendingWork { get { lock (gate) return dirty.Count > 0; } }

    /// <summary>Raised on the writer thread when a save failed; the meeting stays dirty.</summary>
    public event Action<Guid, Exception>? WriteFailed;

    /// <summary>
    /// Raised after a successful write with the bytes that were written (on the writer thread, or on the caller's thread
    /// for <see cref="WriteNow"/>). The transcript mirror runs here: its failures can never fail the internal save.
    /// </summary>
    public event Action<Guid, byte[]>? Saved;

    public MeetingWriter(MeetingLibrary library, TimeProvider? time = null, WriteCadence? cadence = null)
    {
        this.library = library;
        this.time = time ?? TimeProvider.System;
        Cadence = cadence ?? WriteCadence.Default;
        pump = new(Pump) { IsBackground = true, Name = "Wallfly.MeetingWriter" };
        pump.Start();
    }

    public bool IsDirty(Guid id) { lock (gate) return dirty.ContainsKey(id); }

    /// <summary>Takes a snapshot of <paramref name="meeting"/> and schedules it according to the cadence.</summary>
    public void Mark(Meeting meeting)
    {
        var bytes = library.Serialize(meeting);
        var now = time.GetUtcNow();
        lock (gate)
        {
            if (!dirty.TryGetValue(meeting.Id, out var entry))
            {
                entry = new() { FirstDirty = now };
                dirty[meeting.Id] = entry;
            }
            entry.Bytes = bytes;
            entry.LastDirty = now;
            entry.Revision++;
        }
        signal.Set();
    }

    /// <summary>Writes one meeting immediately on the calling thread and drops its pending snapshot.</summary>
    public void WriteNow(Meeting meeting)
    {
        var bytes = library.Serialize(meeting);
        lock (gate) dirty.Remove(meeting.Id);
        Interlocked.Increment(ref startedWrites);
        library.WriteBytes(meeting.Id, bytes);
        Interlocked.Increment(ref completedWrites);
        lock (gate) lastWrite[meeting.Id] = time.GetUtcNow();
        RaiseSaved(meeting.Id, bytes);
    }

    private void RaiseSaved(Guid id, byte[] bytes)
    {
        try { Saved?.Invoke(id, bytes); }
        catch (Exception error) { System.Diagnostics.Trace.TraceError("Wallfly: a save observer failed: " + error.Message); }
    }

    /// <summary>Drops a pending snapshot so a deletion is not undone by a late write.</summary>
    public void Forget(Guid id)
    {
        lock (gate) { dirty.Remove(id); lastWrite.Remove(id); }
    }

    /// <summary>
    /// Blocks until every dirty meeting is written. Returns false on timeout or when a save failed;
    /// the meeting then stays dirty and the next flush retries it.
    /// </summary>
    public bool Flush(TimeSpan timeout)
    {
        lock (gate) { lastError = null; if (dirty.Count == 0) return true; }
        flushRequested = true;
        signal.Set();
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            lock (gate)
            {
                if (lastError != null) { flushRequested = false; return false; }
                if (dirty.Count == 0) { flushRequested = false; return true; }
            }
            Thread.Sleep(1);
        }
        flushRequested = false;
        lock (gate) return dirty.Count == 0 && lastError == null;
    }

    private void Pump()
    {
        while (running)
        {
            signal.Wait(PollMilliseconds);
            signal.Reset();
            List<(Guid Id, byte[] Bytes, long Revision, DateTimeOffset Scheduled)> due = [];
            lock (gate)
            {
                var now = time.GetUtcNow();
                var force = flushRequested;
                foreach (var (id, entry) in dirty)
                {
                    lastWrite.TryGetValue(id, out var written);
                    if (force || Cadence.IsDue(now, RecordingMeetingId == id, entry.FirstDirty, entry.LastDirty, lastWrite.ContainsKey(id) ? written : null))
                        due.Add((id, entry.Bytes, entry.Revision, now));
                }
            }
            foreach (var job in due)
            {
                Interlocked.Increment(ref startedWrites);
                try
                {
                    library.WriteBytes(job.Id, job.Bytes);
                    Interlocked.Increment(ref completedWrites);
                    lock (gate)
                    {
                        // The interval is measured from when the write was due, not from when the
                        // disk returned: a slow write must not postpone the next checkpoint.
                        lastWrite[job.Id] = job.Scheduled;
                        // A newer snapshot arrived while this write ran: stay dirty, never report clean.
                        if (dirty.TryGetValue(job.Id, out var entry) && entry.Revision == job.Revision) dirty.Remove(job.Id);
                    }
                    RaiseSaved(job.Id, job.Bytes);
                }
                catch (Exception error)
                {
                    lock (gate)
                    {
                        lastError = error;
                        // Back off: the next attempt waits one more debounce instead of spinning.
                        if (dirty.TryGetValue(job.Id, out var entry))
                        {
                            var now = time.GetUtcNow();
                            entry.FirstDirty = now;
                            entry.LastDirty = now;
                        }
                    }
                    WriteFailed?.Invoke(job.Id, error);
                }
            }
        }
    }

    public void Dispose()
    {
        running = false;
        signal.Set();
        pump.Join(TimeSpan.FromSeconds(2));
        signal.Dispose();
    }
}
