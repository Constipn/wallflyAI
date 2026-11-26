using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Wallfly.Core.Diagnostics;

/// <summary>
/// The app log: one line per trace event (<c>yyyy-MM-ddTHH:mm:ss.fffzzz LEVEL message</c>) in
/// <c>&lt;directory&gt;\wallfly-YYYYMMDD.log</c>, rolled to <c>-1</c>, <c>-2</c>, … at <see cref="MaxBytes"/> and pruned to the
/// newest <see cref="MaxFiles"/> files. Callers only enqueue: one background thread writes and flushes every line, so a slow
/// or unwritable disk never stalls an audio, drain or UI thread. Nothing here throws into a caller; lines that cannot be
/// written are dropped. Key-like tokens (<c>sk-…</c>, <c>Bearer …</c>, <c>Token …</c>, 40 hex digits) are masked.
/// </summary>
public sealed partial class RollingFileTraceListener : TraceListener
{
    public const long DefaultMaxBytes = 5 * 1024 * 1024;
    public const int DefaultMaxFiles = 14;
    /// <summary>The folder below the data root (<c>%LOCALAPPDATA%\Wallfly\logs</c>); it stays with the app on a library move.</summary>
    public const string FolderName = "logs";

    private readonly ConcurrentQueue<string> queue = new();
    private readonly SemaphoreSlim signal = new(0);
    private readonly Func<DateTimeOffset> now;
    private readonly Thread writer;
    private long enqueued;
    private long handled;
    private volatile bool closing;
    private FileStream? stream;
    private string? streamPath;
    private DateOnly streamDay;

    /// <param name="directory">The log folder; created on the first line.</param>
    /// <param name="prefix">File name prefix (<c>wallfly</c>).</param>
    /// <param name="now">The clock of the line stamps and file dates (tests); null: local time.</param>
    public RollingFileTraceListener(string directory, string prefix = "wallfly", long maxBytes = DefaultMaxBytes, int maxFiles = DefaultMaxFiles,
        Func<DateTimeOffset>? now = null) : base("WallflyLog")
    {
        Directory = directory;
        Prefix = prefix;
        MaxBytes = Math.Max(1024, maxBytes);
        MaxFiles = Math.Max(1, maxFiles);
        this.now = now ?? (() => DateTimeOffset.Now);
        writer = new Thread(WriteLoop) { IsBackground = true, Name = "Wallfly.Log", Priority = ThreadPriority.BelowNormal };
        writer.Start();
    }

    public string Directory { get; }
    public string Prefix { get; }
    public long MaxBytes { get; }
    public int MaxFiles { get; }
    public override bool IsThreadSafe => true;

    /// <summary>The file a line written on <paramref name="day"/> goes to first (index 0), then <c>-1</c>, <c>-2</c>, ….</summary>
    public string FileFor(DateOnly day, int index = 0) =>
        Path.Combine(Directory, $"{Prefix}-{day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}{(index > 0 ? "-" + index.ToString(CultureInfo.InvariantCulture) : "")}.log");

    /// <summary>
    /// Adds a listener for <paramref name="directory"/> to <see cref="Trace.Listeners"/> once (a second call returns the first
    /// instance), so every <c>Trace.Trace*</c> call and <see cref="WallflyLog"/> line of the process lands in the file.
    /// </summary>
    public static RollingFileTraceListener Register(string directory, string prefix = "wallfly")
    {
        var full = Path.GetFullPath(directory);
        lock (Trace.Listeners)
        {
            foreach (var existing in Trace.Listeners.OfType<RollingFileTraceListener>())
                if (string.Equals(existing.Directory, full, StringComparison.OrdinalIgnoreCase) && existing.Prefix == prefix) return existing;
            var listener = new RollingFileTraceListener(full, prefix);
            Trace.Listeners.Add(listener);
            return listener;
        }
    }

    // ---- TraceListener ------------------------------------------------------------------------------------------

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message) =>
        Enqueue(eventType, message);

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args)
    {
        string? message;
        try { message = args is { Length: > 0 } && format != null ? string.Format(CultureInfo.InvariantCulture, format, args) : format; }
        catch (FormatException) { message = format; }
        Enqueue(eventType, message);
    }

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id) => Enqueue(eventType, "");

    public override void TraceData(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, object? data) =>
        Enqueue(eventType, data?.ToString());

    public override void TraceData(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, params object?[]? data) =>
        Enqueue(eventType, data == null ? "" : string.Join(", ", data.Select(item => item?.ToString())));

    public override void Fail(string? message, string? detailMessage) =>
        Enqueue(TraceEventType.Error, string.IsNullOrEmpty(detailMessage) ? message : message + " " + detailMessage);

    public override void Write(string? message) => Enqueue(TraceEventType.Information, message);
    public override void WriteLine(string? message) => Enqueue(TraceEventType.Information, message);

    /// <summary>Waits (at most 5 s) until every line enqueued so far is written (or dropped).</summary>
    public override void Flush()
    {
        var target = Interlocked.Read(ref enqueued);
        if (Thread.CurrentThread == writer) return;
        SpinWait.SpinUntil(() => Interlocked.Read(ref handled) >= target || !writer.IsAlive, TimeSpan.FromSeconds(5));
    }

    public override void Close()
    {
        Flush();
        closing = true;
        try { signal.Release(); } catch (ObjectDisposedException) { }
        writer.Join(TimeSpan.FromSeconds(2));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Close();
            lock (queue) { stream?.Dispose(); stream = null; }
        }
        base.Dispose(disposing);
    }

    // ---- formatting -----------------------------------------------------------------------------------------------

    /// <summary>"yyyy-MM-ddTHH:mm:ss.fffzzz LEVEL message" on one line, with key-like tokens masked.</summary>
    public static string Format(DateTimeOffset stamp, TraceEventType type, string? message) =>
        stamp.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture) + " " + Level(type) + " " + Clean(message);

    public static string Level(TraceEventType type) => type switch
    {
        TraceEventType.Critical => "FATAL",
        TraceEventType.Error => "ERROR",
        TraceEventType.Warning => "WARN",
        TraceEventType.Verbose => "DEBUG",
        _ => "INFO"
    };

    /// <summary>One line (line breaks become " | ") with API-key-like tokens replaced by "***".</summary>
    public static string Clean(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "";
        var single = message.Replace("\r\n", " | ").Replace('\n', ' ').Replace('\r', ' ');
        return KeyLike().Replace(single, match => match.Groups["keep"].Success ? match.Groups["keep"].Value + "***" : "***");
    }

    [GeneratedRegex(@"(?<keep>\b(?:Bearer|Token|token|bearer)\s+)\S+|\bsk-[A-Za-z0-9_\-]{6,}|\b[0-9a-fA-F]{40}\b")]
    private static partial Regex KeyLike();

    private void Enqueue(TraceEventType type, string? message)
    {
        if (closing) return;
        try
        {
            queue.Enqueue(Format(now(), type, message));
            Interlocked.Increment(ref enqueued);
            signal.Release();
        }
        catch (Exception) { /* logging never fails a caller */ }
    }

    // ---- the writer thread ----------------------------------------------------------------------------------------

    private void WriteLoop()
    {
        while (true)
        {
            try { signal.Wait(); }
            catch (ObjectDisposedException) { return; }
            while (queue.TryDequeue(out var line))
            {
                Append(line);
                Interlocked.Increment(ref handled);
            }
            if (closing && queue.IsEmpty)
            {
                lock (queue) { CloseStream(); }
                return;
            }
        }
    }

    private void Append(string line)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
            // The line's own date names the file (the stamp is the first 10 characters).
            var day = DateOnly.TryParseExact(line.AsSpan(0, Math.Min(10, line.Length)), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed : DateOnly.FromDateTime(now().DateTime);
            lock (queue)
            {
                var target = Target(day, bytes.Length);
                target.Write(bytes);
                target.Flush();
            }
        }
        catch (Exception)
        {
            // Unwritable folder, full disk, a locked file: the line is dropped and the next one tries again.
            lock (queue) { CloseStream(); }
        }
    }

    /// <summary>The open file for a line of <paramref name="length"/> bytes on <paramref name="day"/>, rolling and pruning as needed.</summary>
    private FileStream Target(DateOnly day, int length)
    {
        if (stream != null && streamDay == day && (stream.Length == 0 || stream.Length + length <= MaxBytes)) return stream;
        CloseStream();
        System.IO.Directory.CreateDirectory(Directory);
        for (var index = 0; ; index++)
        {
            var path = FileFor(day, index);
            var size = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (size > 0 && size + length > MaxBytes) continue;
            stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            streamPath = path;
            streamDay = day;
            Prune();
            return stream;
        }
    }

    private void CloseStream()
    {
        try { stream?.Dispose(); } catch (Exception) { }
        stream = null;
        streamPath = null;
    }

    /// <summary>Keeps the newest <see cref="MaxFiles"/> log files of this prefix (the open one always stays).</summary>
    private void Prune()
    {
        try
        {
            var files = new DirectoryInfo(Directory).GetFiles(Prefix + "-*.log")
                .OrderByDescending(file => string.Equals(file.FullName, streamPath, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(file => file.LastWriteTimeUtc).ThenByDescending(file => file.Name, StringComparer.Ordinal)
                .ToList();
            foreach (var old in files.Skip(MaxFiles))
            {
                try { old.Delete(); } catch (Exception) { }
            }
        }
        catch (Exception) { }
    }
}
