using Wallfly.Audio;

namespace Wallfly.Transcription;

/// <summary>One recognized word, in seconds relative to <see cref="TranscriptionSegment.Offset"/>.</summary>
public readonly record struct TranscriptionWord(string Text, double Start, double End);

/// <summary>
/// What a pipeline recognized for one audio interval. Identity is the id plus the audio interval,
/// never the text: a preview never downgrades a final with the same id.
/// </summary>
public sealed class TranscriptionSegment
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public AudioSource Source { get; init; }
    /// <summary>Seconds from the start of the recording session (the store adds the meeting base).</summary>
    public double Offset { get; init; }
    public double Duration { get; init; }
    public string Text { get; init; } = "";
    public bool IsFinal { get; init; }
    public Guid? SessionId { get; init; }
    public IReadOnlyList<TranscriptionWord>? Words { get; init; }
    /// <summary>Streaming connection (generation) the passage arrived on; null for chunked providers.</summary>
    public Guid? ConnectionId { get; init; }
}

public enum TranscriptionEventKind
{
    /// <summary>A visible pipeline state such as "reconnecting".</summary>
    State,
    /// <summary>Audio was captured but could not be recognized.</summary>
    Gap,
    /// <summary>Position of an archive frame on the meeting timeline.</summary>
    Anchor,
    /// <summary>
    /// Recognition stopped for a terminal access or configuration error (Deepgram HTTP 400/401/402/403/404/422).
    /// Audio keeps being captured and archived; only an explicit reconnect resumes recognition.
    /// </summary>
    Paused
}

public readonly record struct TranscriptionEvent(
    TranscriptionEventKind Kind,
    AudioSource Source,
    string Detail = "",
    double Start = 0,
    double Duration = 0,
    long Frame = 0);

/// <summary>
/// Turns 16 kHz mono float frames of one channel into transcript segments. Implementations are fed
/// from the engine drain thread and raise their events on their own worker threads, so consumers
/// must hand results to an inbox instead of touching UI state.
/// </summary>
public interface ITranscriptionPipeline : IDisposable
{
    AudioSource Source { get; }
    bool HasFailed { get; }
    string? FailureReason { get; }

    event Action<TranscriptionSegment>? SegmentReceived;
    event Action<TranscriptionEvent>? EventRaised;
    event Action<string>? Failed;

    /// <summary>Hands over one block of 16 kHz mono samples starting at <paramref name="meetingOffset"/> seconds.</summary>
    void Append(ReadOnlyMemory<float> samples16k, double meetingOffset);

    /// <summary>Finishes the remaining audio and all in-flight requests.</summary>
    Task FinishAsync();

    /// <summary>Drops everything pending; late results are discarded.</summary>
    Task CancelAsync();
}

/// <summary>A streaming pipeline that can be reconnected by hand, for example after a key change ("Reconnect").</summary>
public interface IReconnectableTranscriptionPipeline
{
    /// <summary>Drops the current connection and starts a fresh one; a non-empty <paramref name="apiKey"/> replaces the key.</summary>
    void Reconnect(string? apiKey = null);
}
