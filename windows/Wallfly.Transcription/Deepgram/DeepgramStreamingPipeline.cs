using System.Globalization;
using Wallfly.Audio;

namespace Wallfly.Transcription.Deepgram;

/// <summary>User-visible wording of the streaming pipeline; English, copied from the macOS build.</summary>
public static class DeepgramMessages
{
    public const string MissingKey = "No Deepgram API key found. Set DEEPGRAM_API_KEY or enter it in Settings. Your notes stay usable locally.";
    public const string Connecting = "Connecting…";
    public const string Live = "Live · Nova-3";
    public const string Finalizing = "Finalizing transcript…";
    public const string Paused = "Paused · check Deepgram access and reconnect";
    public const string Ended = "Ended";
    public const string NotResponding = "Deepgram connection not responding";
    public const string Interrupted = "Deepgram connection interrupted";
    public const string StreamingError = "Deepgram reported a streaming error";
    public const string Unreadable = "Unreadable Deepgram response";
    public const string Reconnecting = "Reconnecting transcription";
    public const string BufferFull = "Audio buffer full";
    public static string Retrying(string reason, double delay) =>
        $"{reason} · retrying in {((int)Math.Ceiling(delay)).ToString(CultureInfo.InvariantCulture)} s";

    // Gap reasons (persisted with the meeting and shown in the export).
    public const string GapPaused = "Transcription paused: check access or configuration";
    public const string GapBufferFull = "Audio buffer full: not transmitted";
    public const string GapUnconfirmed = "Sent but not finally confirmed; possibly incomplete";
    public const string GapFinalizationTimeout = "Transcript finalization exceeded 20 seconds";
    public const string GapAccessInvalid = "Deepgram access or configuration invalid";
    public const string GapCancelled = "Transcription cancelled";

    // Failures that end the recording: local overload, never the network.
    public const string IngressOverloaded = "Audio processing overloaded; the recording is being stopped to avoid further unnoticed audio loss.";
    public const string ProcessingFailed = "Audio could not be processed. The recording is being stopped.";
}

/// <summary>
/// Deepgram Nova-3 live streaming for one channel (1:1 port of the macOS
/// <c>DeepgramTranscriptionPipeline</c>, spec 4.1). All mutable recognition state is confined to a
/// serial queue; <see cref="Append"/> only admits and posts, so the engine drain thread never waits for
/// the network. Network loss never fails the pipeline: it reconnects after 0.5 / 1 / 2 s and then every
/// 30 s for as long as audio arrives, records audio that was sent but not finally confirmed as a gap,
/// and fences every late callback with the generation id of its connection. Terminal upgrade statuses
/// pause recognition until <see cref="Reconnect"/>; audio keeps being captured and archived meanwhile.
/// </summary>
public sealed class DeepgramStreamingPipeline : ITranscriptionPipeline, IReconnectableTranscriptionPipeline
{
    public sealed record Policy
    {
        /// <summary>Upper bound of unsent audio and of admitted-but-unprocessed audio.</summary>
        public double BufferSeconds { get; init; } = 10;
        public double OperationTimeout { get; init; } = 10;
        public double FinishTimeout { get; init; } = 20;
        public IReadOnlyList<double> RetryDelays { get; init; } = [.5, 1, 2];
        public double RecoveryInterval { get; init; } = 30;
        public double KeepAliveInterval { get; init; } = 3;
    }

    private enum State { Idle, Connecting, Streaming, Recovering, Paused, Closing, Ended }

    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(20);
    /// <summary>A partial packet is sent once no audio arrived for this long.</summary>
    public const double PartialFlushSeconds = .1;

    private readonly SerialQueue queue;
    private readonly object admission = new();
    private double admittedSeconds;
    private bool accepting = true;
    private bool ingressFailed;
    private readonly Guid sessionId;
    private readonly string locale;
    private string key;
    private readonly Policy policy;
    private readonly TimeProvider time;
    private readonly IWebSocketFactory sockets;
    private readonly bool sampleAccurateTimestamps;
    private readonly string? archiveFileName;
    private readonly Func<IReadOnlyList<string>>? keyterms;
    /// <summary>Keyterms of the connection being opened; 0 for the bare contract.</summary>
    private int requestKeyterms;
    /// <summary>Language hints of the connection being opened.</summary>
    private int requestHints;
    /// <summary>
    /// What the connections of this pipeline add to the macOS contract. A 400/422 on opening a connection steps down once per
    /// level and the pipeline keeps the lower level: <see cref="RequestLevel.Full"/> (hints and keyterms) →
    /// <see cref="RequestLevel.WithoutLanguageHints"/> (keyterms kept) → <see cref="RequestLevel.Bare"/> (the macOS contract).
    /// </summary>
    private RequestLevel requestLevel = RequestLevel.Full;

    private enum RequestLevel { Full, WithoutLanguageHints, Bare }
    private bool archiving;
    private long writtenFrames;
    private ITimer? timer;
    private int tickPosted;
    private State state = State.Idle;
    private IWebSocket? transport;
    private Guid generation = Guid.NewGuid();
    private DeepgramResultAssembler? assembler;
    private double? anchor;
    private double sentEnd;
    private readonly Queue<DeepgramPacket> pending = new();
    private int pendingBytes;
    private readonly PacketAssembler packets = new();
    private double? expectedOffset;
    private double lastAudioArrival;
    private readonly DeepgramSender sender;
    private double? operationDeadline;
    private double nextRetry;
    private int failures;
    private bool finishing;
    private double? finishDeadline;
    private readonly List<TaskCompletionSource> finishWaiters = [];
    private string? failureReason;
    private int confirmedCloses;
    // Numeric diagnostics only: no audio, recognized text, API credentials or request URLs.
    private double? diagnosticsStarted;
    private long diagnosticSamples;
    private double diagnosticEnergy;
    private int diagnosticResults;
    private int diagnosticNonemptyResults;
    private int diagnosticFinalResults;
    private int diagnosticPublishedFinals;
    private int diagnosticRejectedNonempty;
    private double diagnosticLastResultEnd;

    public AudioSource Source { get; }
    public string? FailureReason => Volatile.Read(ref failureReason);
    public bool HasFailed => FailureReason != null;
    /// <summary>CloseStream rounds that ended with the server's <c>Metadata</c> message.</summary>
    public int ConfirmedCloses => Volatile.Read(ref confirmedCloses);

    public event Action<TranscriptionSegment>? SegmentReceived;
    public event Action<TranscriptionEvent>? EventRaised;
    public event Action<string>? Failed;
    /// <summary>One line of numeric counters per 30 s and per connection change; never text, audio, keys or URLs.</summary>
    public event Action<string>? DiagnosticsReported;

    /// <param name="archiveFileName">
    /// Name of the archive file the caller writes for this channel. When set, the pipeline reports an
    /// <see cref="TranscriptionEventKind.Anchor"/> at every timeline discontinuity, with the same snapped
    /// offset that its segments use. Null when the caller anchors the archive itself.
    /// </param>
    /// <param name="automaticTimer">False in tests, which drive the clock and call <see cref="SynchronizeAsync"/>.</param>
    /// <param name="keyterms">
    /// The keyterms of the next connection (names, vocabulary), read on the pipeline's queue at every connect and
    /// reconnect, so it must be thread-safe; null sends none. Values are never logged, only their count.
    /// </param>
    public DeepgramStreamingPipeline(AudioSource source, string apiKey, Guid sessionId, string? locale = "auto",
        IWebSocketFactory? sockets = null, TimeProvider? time = null, Policy? policy = null,
        bool sampleAccurateTimestamps = true, bool automaticTimer = true, string? archiveFileName = null,
        Func<IReadOnlyList<string>>? keyterms = null)
    {
        this.keyterms = keyterms;
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException(DeepgramMessages.MissingKey);
        Source = source;
        key = apiKey.Trim();
        this.sessionId = sessionId;
        this.locale = locale ?? "auto";
        this.sockets = sockets ?? ClientWebSocketFactory.Default;
        this.time = time ?? TimeProvider.System;
        this.policy = policy ?? new();
        this.sampleAccurateTimestamps = sampleAccurateTimestamps;
        this.archiveFileName = archiveFileName;
        archiving = archiveFileName != null;
        sender = new(this.policy.KeepAliveInterval);
        queue = new(error => Report($"Deepgram source={Source} internalError={error.GetType().Name}"));
        if (automaticTimer)
            timer = this.time.CreateTimer(_ =>
            {
                if (Interlocked.Exchange(ref tickPosted, 1) == 0) queue.Post(() => { Volatile.Write(ref tickPosted, 0); Tick(); });
            }, null, TimeSpan.Zero, TickInterval);
    }

    private double Now() => time.GetTimestamp() / (double)time.TimestampFrequency;

    public void Append(ReadOnlyMemory<float> samples16k, double meetingOffset)
    {
        var seconds = samples16k.Length / (double)AudioFormat.SampleRate;
        var report = false;
        lock (admission)
        {
            if (!accepting) return;
            if (!double.IsFinite(seconds) || seconds < 0 || admittedSeconds + seconds > policy.BufferSeconds)
            {
                report = !ingressFailed;
                ingressFailed = true;
            }
            else
            {
                admittedSeconds += seconds;
                var copy = samples16k.ToArray();
                // Posted under the admission lock so a finish cannot overtake an accepted block.
                queue.Post(() =>
                {
                    try { Process(copy, meetingOffset); }
                    finally { lock (admission) admittedSeconds -= seconds; }
                });
            }
        }
        // This is a local processing overload, not a network outage.
        if (report) Fail(DeepgramMessages.IngressOverloaded);
    }

    private void Process(float[] samples, double offset)
    {
        if (state == State.Ended) return;
        try
        {
            if (samples.Length == 0 || !double.IsFinite(offset) || offset < 0) return;
            diagnosticsStarted ??= Now();
            foreach (var sample in samples)
            {
                var value = float.IsFinite(sample) ? (double)sample : 0;
                diagnosticEnergy += value * value;
            }
            diagnosticSamples += samples.Length;
            var firstFrame = writtenFrames;
            var duration = samples.Length / (double)AudioFormat.SampleRate;
            var discontinuous = expectedOffset == null;
            if (expectedOffset is double expected)
            {
                var tolerance = sampleAccurateTimestamps ? 1.5 / AudioFormat.SampleRate : .3;
                if (Math.Abs(expected - offset) <= tolerance) offset = expected;
                else { FlushPartial(); discontinuous = true; }
            }
            expectedOffset = offset + duration;
            if (archiving)
            {
                // Same snapped offset that recognition packets (and so segment offsets) use.
                if (discontinuous) Raise(new(TranscriptionEventKind.Anchor, Source, archiveFileName!, offset, 0, firstFrame));
                writtenFrames += samples.Length;
            }
            lastAudioArrival = Now();
            if (state == State.Paused)
            {
                Gap(offset, duration, DeepgramMessages.GapPaused);
                return;
            }
            foreach (var packet in packets.Append(PacketAssembler.EncodePcm(samples), offset)) Enqueue(packet);
            Tick();
        }
        catch (Exception)
        {
            Fail(DeepgramMessages.ProcessingFailed);
        }
    }

    private void FlushPartial()
    {
        if (packets.Flush() is DeepgramPacket packet) Enqueue(packet);
    }

    private void Enqueue(DeepgramPacket packet)
    {
        pending.Enqueue(packet);
        pendingBytes += packet.Data.Length;
        double? droppedStart = null;
        var droppedEnd = 0.0;
        while (pendingBytes > (int)(policy.BufferSeconds * DeepgramPacket.BytesPerSecond) && pending.Count > 0)
        {
            var dropped = pending.Dequeue();
            pendingBytes -= dropped.Data.Length;
            droppedStart ??= dropped.Offset;
            droppedEnd = dropped.Offset + dropped.Duration;
        }
        if (droppedStart is double start)
        {
            Gap(start, droppedEnd - start, DeepgramMessages.GapBufferFull);
            if (anchor != null) Disconnect(DeepgramMessages.BufferFull, retry: false);
        }
    }

    private void Tick()
    {
        if (state == State.Ended) return;
        if (diagnosticsStarted is double started && Now() - started >= 30) ReportDiagnostics();
        // A selected app can stop delivering audio during silence. Do not strand the last short packet
        // until another speaker starts or the user stops recording.
        if (packets.HasPartial && Now() - lastAudioArrival >= PartialFlushSeconds) FlushPartial();
        if (finishDeadline is double finishBy && Now() >= finishBy)
        {
            DiscardPending(DeepgramMessages.GapFinalizationTimeout);
            Invalidate(markUnconfirmed: true);
            End();
            return;
        }
        if (operationDeadline is double operationBy && Now() >= operationBy)
            Disconnect(DeepgramMessages.NotResponding, retry: true);
        if (finishing && pending.Count == 0 && !packets.HasPartial && state is State.Idle or State.Recovering or State.Paused)
        {
            End();
            return;
        }
        if ((state == State.Idle || state == State.Recovering && Now() >= nextRetry) && pending.Count > 0) Connect();
        if (state != State.Streaming || sender.InFlight) return;
        if (pending.Count > 0 && sender.AudioDue(Now()))
        {
            var packet = pending.Peek();
            if (anchor is double start && Math.Abs(packet.Offset - (start + sentEnd)) > 1.5 / AudioFormat.SampleRate)
            {
                // Never splice capture gaps out of Deepgram's sample clock.
                CloseStream();
                return;
            }
            if (anchor == null)
            {
                anchor = packet.Offset;
                assembler = new(Source, generation, packet.Offset, sessionId);
            }
            pending.Dequeue();
            pendingBytes -= packet.Data.Length;
            sentEnd += packet.Duration; // includes an ambiguous in-flight send if the transport fails afterwards
            sender.AudioScheduled(packet.Duration, Now());
            Send(WebSocketFrame.FromBinary(packet.Data));
        }
        else if (pending.Count == 0 && finishing) CloseStream();
        else if (sender.KeepAliveDue(Now())) Send(WebSocketFrame.FromText(DeepgramSender.KeepAliveMessage));
    }

    private void Connect()
    {
        generation = Guid.NewGuid(); anchor = null; sentEnd = 0; assembler = null;
        state = State.Connecting;
        RaiseState(DeepgramMessages.Connecting);
        operationDeadline = Now() + policy.OperationTimeout;
        var current = generation;
        IReadOnlyList<string> terms = requestLevel == RequestLevel.Bare ? [] : CurrentKeyterms();
        var hints = requestLevel == RequestLevel.Full && DeepgramUrlBuilder.Language(locale) == "multi";
        requestKeyterms = terms.Count;
        requestHints = hints ? DeepgramUrlBuilder.LanguageHints.Count : 0;
        Report($"Deepgram source={Source} connect keyterms={terms.Count} languageHints={requestHints}" + (requestLevel == RequestLevel.Full ? "" : $" level={requestLevel}"));
        IWebSocket socket;
        try { socket = sockets.Create(DeepgramUrlBuilder.Request(key, locale, terms, languageHints: requestLevel == RequestLevel.Full)); }
        catch (Exception error)
        {
            queue.Post(() => OnOpened(current, Task.FromException(error)));
            return;
        }
        transport = socket;
        Observe(() => socket.ConnectAsync(CancellationToken.None), task => OnOpened(current, task));
    }

    private void OnOpened(Guid current, Task task)
    {
        if (generation != current || state != State.Connecting) return;
        if (task.IsCompletedSuccessfully)
        {
            operationDeadline = null;
            state = State.Streaming;
            sender.Opened(Now());
            RaiseState(DeepgramMessages.Live);
            Receive(current);
            Tick();
        }
        else TransportFailed(Error(task));
    }

    private void Receive(Guid current)
    {
        if (transport is not { } socket) return;
        Observe(() => socket.ReceiveAsync(CancellationToken.None), task => OnReceived(current, task));
    }

    private void OnReceived(Guid current, Task<WebSocketFrame> task)
    {
        if (generation != current || state == State.Ended) return;
        if (!task.IsCompletedSuccessfully)
        {
            if (state == State.Closing) CompleteClose(confirmed: false);
            else TransportFailed(Error(task));
            return;
        }
        if (!DeepgramResultParser.TryParse(task.Result.Data.Span, out var result) || result == null)
        {
            Disconnect(DeepgramMessages.Unreadable, retry: true);
            return;
        }
        if (result.Type == "Error")
        {
            // No raw server text in logs or the UI: it may echo request details.
            Disconnect(DeepgramMessages.StreamingError, retry: true);
            return;
        }
        if (result.Type == "Results" && assembler != null)
        {
            var nonempty = !string.IsNullOrEmpty(result.FirstAlternative?.Transcript);
            diagnosticResults++;
            if (nonempty) diagnosticNonemptyResults++;
            if (result.IsFinal == true) diagnosticFinalResults++;
            diagnosticLastResultEnd = (result.Start ?? 0) + (result.Duration ?? 0);
            var segments = assembler.Accept(result, sentEnd);
            diagnosticPublishedFinals += segments.Count(segment => segment.IsFinal && segment.Text.Length > 0);
            if (nonempty && !segments.Any(segment => segment.Text.Length > 0)) diagnosticRejectedNonempty++;
            foreach (var segment in segments) Emit(segment);
            // speech_final is deliberately ignored; only is_final resets the retry schedule.
            if (result.IsFinal == true) failures = 0;
        }
        if (result.Type == "Metadata" && state == State.Closing)
        {
            CompleteClose(confirmed: true);
            return;
        }
        Receive(current);
    }

    private void Send(WebSocketFrame frame)
    {
        var now = Now();
        sender.Began(now);
        operationDeadline = now + policy.OperationTimeout;
        var current = generation;
        if (transport is not { } socket) return;
        Observe(() => socket.SendAsync(frame, CancellationToken.None), task => OnSent(current, task));
    }

    private void OnSent(Guid current, Task task)
    {
        if (generation != current || state == State.Ended) return;
        sender.Completed();
        if (!task.IsCompletedSuccessfully) { TransportFailed(Error(task)); return; }
        // CloseStream keeps its deadline until the terminal receive.
        if (state != State.Closing) operationDeadline = null;
        Tick();
    }

    private void CloseStream()
    {
        state = State.Closing;
        RaiseState(DeepgramMessages.Finalizing);
        Send(WebSocketFrame.FromText(DeepgramSender.CloseStreamMessage));
    }

    private void CompleteClose(bool confirmed)
    {
        // Metadata follows the final results of a successful CloseStream. A transport failure alone is
        // not proof of processing and must keep an uncertainty marker.
        if (confirmed) Interlocked.Increment(ref confirmedCloses);
        Invalidate(markUnconfirmed: !confirmed);
        state = State.Idle;
        if (finishing && pending.Count == 0) End();
        else Tick();
    }

    /// <summary>The normalized keyterms of the next connection; a failing source sends none.</summary>
    private IReadOnlyList<string> CurrentKeyterms()
    {
        if (keyterms == null) return [];
        try { return DeepgramUrlBuilder.Keyterms(keyterms()); }
        catch (Exception error)
        {
            Report($"Deepgram source={Source} keytermSourceError={error.GetType().Name}");
            return [];
        }
    }

    private void TransportFailed(Exception error)
    {
        if (error is DeepgramTransportException { Status: 400 or 422 } rejected && state == State.Connecting && (requestHints > 0 || requestKeyterms > 0))
        {
            // A configuration rejection of the additions must not pause recognition: reconnect at once one level lower and keep
            // that level for the rest of this pipeline. First without the language hints (the keyterms carry the names), and
            // only when that is rejected too with the bare contract that the macOS build sends. Each level is tried once, so
            // this never loops; a rejection of the bare contract pauses as before.
            requestLevel = requestHints > 0 && requestKeyterms > 0 ? RequestLevel.WithoutLanguageHints : RequestLevel.Bare;
            Report($"Deepgram source={Source} enrichedRequestRejected status={rejected.Status} keyterms={requestKeyterms} languageHints={requestHints}; reconnecting level={requestLevel}");
            Disconnect(DeepgramMessages.Reconnecting, retry: false);
            Tick();
            return;
        }
        if (error is DeepgramTransportException { IsTerminal: true })
        {
            Invalidate(markUnconfirmed: true);
            state = State.Paused;
            DiscardPending(DeepgramMessages.GapAccessInvalid);
            Raise(new(TranscriptionEventKind.Paused, Source, DeepgramMessages.Paused));
            if (finishing) End();
        }
        else Disconnect(DeepgramMessages.Interrupted, retry: true);
    }

    private void Disconnect(string reason, bool retry)
    {
        Invalidate(markUnconfirmed: true);
        state = State.Recovering;
        var delay = retry ? (failures < policy.RetryDelays.Count ? policy.RetryDelays[failures] : policy.RecoveryInterval) : 0;
        if (retry) failures++;
        nextRetry = Now() + delay;
        RaiseState(DeepgramMessages.Retrying(reason, delay));
    }

    private void Invalidate(bool markUnconfirmed)
    {
        ReportDiagnostics();
        if (markUnconfirmed && anchor is double start)
        {
            var confirmed = assembler?.ConfirmedThrough ?? 0;
            Gap(start + confirmed, Math.Max(0, sentEnd - confirmed), DeepgramMessages.GapUnconfirmed);
        }
        if (assembler != null) foreach (var segment in assembler.ClearPreview()) Emit(segment);
        generation = Guid.NewGuid();
        transport?.Close(); transport = null; assembler = null; anchor = null; sentEnd = 0;
        sender.Reset(); operationDeadline = null;
    }

    private void Gap(double start, double duration, string reason)
    {
        if (duration <= .0001) return;
        Raise(new(TranscriptionEventKind.Gap, Source, reason, start, duration));
    }

    private void DiscardPending(string reason)
    {
        FlushPartial();
        foreach (var packet in pending) Gap(packet.Offset, packet.Duration, reason);
        pending.Clear(); pendingBytes = 0;
    }

    /// <summary>"Reconnect": drops the current connection, resets the retry schedule, optionally with a new key.</summary>
    public void Reconnect(string? apiKey = null) => queue.Post(() =>
    {
        if (state == State.Ended || finishing) return;
        if (!string.IsNullOrWhiteSpace(apiKey)) key = apiKey.Trim();
        Disconnect(DeepgramMessages.Reconnecting, retry: false);
        failures = 0;
        Tick();
    });

    /// <summary>Flushes the remainder, sends CloseStream and waits for Metadata; at most 20 s.</summary>
    public Task FinishAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (admission)
        {
            accepting = false;
            queue.Post(() =>
            {
                if (state == State.Ended) { done.TrySetResult(); return; }
                finishWaiters.Add(done);
                if (!finishing)
                {
                    finishing = true;
                    finishDeadline = Now() + policy.FinishTimeout;
                    FlushPartial();
                    archiving = false;
                }
                Tick();
            });
        }
        return done.Task;
    }

    /// <summary>Drops the connection and everything unsent; unsent and unconfirmed audio become gaps.</summary>
    public Task CancelAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (admission)
        {
            accepting = false;
            queue.Post(() =>
            {
                Invalidate(markUnconfirmed: true);
                DiscardPending(DeepgramMessages.GapCancelled);
                End();
                done.TrySetResult();
            });
        }
        return done.Task;
    }

    private void End()
    {
        if (state == State.Ended) return;
        ReportDiagnostics();
        state = State.Ended;
        timer?.Dispose(); timer = null;
        transport?.Close(); transport = null;
        RaiseState(DeepgramMessages.Ended);
        var waiters = finishWaiters.ToArray();
        finishWaiters.Clear();
        foreach (var waiter in waiters) waiter.TrySetResult();
    }

    /// <summary>Deterministic scheduler boundary: tests advance the injected clock, then run one tick.</summary>
    public Task SynchronizeAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Post(() => { Tick(); done.TrySetResult(); });
        return done.Task;
    }

    public void Dispose()
    {
        lock (admission) accepting = false;
        if (queue.IsCurrent) { Invalidate(markUnconfirmed: true); DiscardPending(DeepgramMessages.GapCancelled); End(); }
        else _ = CancelAsync();
    }

    private void Fail(string message)
    {
        var reason = $"{Source}: {message}";
        if (Interlocked.CompareExchange(ref failureReason, reason, null) == null) Failed?.Invoke(reason);
    }

    // Subscribers only enqueue; one that throws must not stop recognition or fail the recording.
    private void Emit(TranscriptionSegment segment)
    {
        try { SegmentReceived?.Invoke(segment); }
        catch (Exception error) { Report($"Deepgram source={Source} segmentHandlerError={error.GetType().Name}"); }
    }

    private void RaiseState(string text) => Raise(new(TranscriptionEventKind.State, Source, text));

    private void Raise(TranscriptionEvent raised)
    {
        try { EventRaised?.Invoke(raised); }
        catch (Exception error) { Report($"Deepgram source={Source} eventHandlerError={error.GetType().Name}"); }
    }

    private void Report(string line)
    {
        try { DiagnosticsReported?.Invoke(line); } catch (Exception) { }
    }

    private static Exception Error(Task task) => task.Exception?.InnerException ?? new OperationCanceledException();

    /// <summary>Starts a socket operation and posts its completion to the queue in completion order.</summary>
    private void Observe(Func<Task> start, Action<Task> completion)
    {
        Task task;
        try { task = start(); }
        catch (Exception error) { task = Task.FromException(error); }
        task.ContinueWith(finished => { _ = finished.Exception; queue.Post(() => completion(finished)); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void Observe(Func<Task<WebSocketFrame>> start, Action<Task<WebSocketFrame>> completion)
    {
        Task<WebSocketFrame> task;
        try { task = start(); }
        catch (Exception error) { task = Task.FromException<WebSocketFrame>(error); }
        task.ContinueWith(finished => { _ = finished.Exception; queue.Post(() => completion(finished)); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void ReportDiagnostics()
    {
        if (diagnosticSamples == 0 && diagnosticResults == 0) return;
        var rms = Math.Sqrt(diagnosticEnergy / Math.Max(1, diagnosticSamples));
        Report(string.Create(CultureInfo.InvariantCulture,
            $"Deepgram source={Source} inputSamples={diagnosticSamples} rms={rms:0.######} sentSeconds={sentEnd:0.###} queuedBytes={pendingBytes} results={diagnosticResults} nonempty={diagnosticNonemptyResults} finalResults={diagnosticFinalResults} publishedFinals={diagnosticPublishedFinals} ignoredNonempty={diagnosticRejectedNonempty} lastResultEnd={diagnosticLastResultEnd:0.###}"));
        diagnosticSamples = 0; diagnosticEnergy = 0; diagnosticResults = 0;
        diagnosticNonemptyResults = 0; diagnosticFinalResults = 0; diagnosticPublishedFinals = 0;
        diagnosticRejectedNonempty = 0; diagnosticLastResultEnd = 0; diagnosticsStarted = Now();
    }
}
