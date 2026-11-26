namespace Wallfly.Diarization;

/// <summary>How a cloud diarization run that produced no usable result should be treated by its caller.</summary>
public enum CloudFailureKind
{
    /// <summary>Worth repeating later (network gone, timeout, rate limit, server error other than 504): wait for the network and retry.</summary>
    Transient,
    /// <summary>Repeating the same request will fail again (key rejected, upload too large, server-side processing limit, malformed answer).</summary>
    Permanent,
    /// <summary>The service answered, but the result fails a consistency check (time offset, duration); repeating gives the same answer.</summary>
    Untrustworthy
}

/// <summary>The cause behind a <see cref="CloudFailureKind"/>, for logs and run states (numbers only).</summary>
public enum CloudFailureReason
{
    /// <summary>The connection failed or broke during the upload or while waiting (transient).</summary>
    Network,
    /// <summary>The upload stalled, or the answer did not arrive within the response budget (transient).</summary>
    Timeout,
    /// <summary>HTTP 429 (transient; see <see cref="CloudDiarizationException.RetryAfter"/>).</summary>
    RateLimited,
    /// <summary>HTTP 5xx other than 504, and 408 (transient).</summary>
    ServerError,
    /// <summary>HTTP 401 or 403: the key is missing, wrong or lacks the scope (permanent).</summary>
    Unauthorized,
    /// <summary>HTTP 413 (permanent).</summary>
    TooLarge,
    /// <summary>HTTP 504: Deepgram's own processing limit (10 min for Nova) was hit; the same audio hits it again (permanent).</summary>
    ServerTimeout,
    /// <summary>Any other 4xx, e.g. 400 for an invalid parameter or 402 for an exhausted balance (permanent).</summary>
    Rejected,
    /// <summary>A 2xx answer that is not the expected JSON, or words without speaker numbers (permanent).</summary>
    Malformed,
    /// <summary>The answer's audio duration differs from the uploaded timeline (untrustworthy).</summary>
    DurationMismatch,
    /// <summary>The words do not line up with the live transcript (<see cref="DeepgramSelfCheck"/>; untrustworthy).</summary>
    TimeOffset,
    /// <summary>Too few words could be matched with the live transcript to trust the timing (<see cref="DeepgramSelfCheck"/>; untrustworthy).</summary>
    TooFewMatches,
    /// <summary>The answer has far fewer words than the live transcript of the same audio (<see cref="DeepgramSelfCheck"/>; untrustworthy).</summary>
    TooFewWords
}

/// <summary>
/// A cloud diarization run that ended without a usable result. <see cref="Exception.Message"/> is a short English text without
/// keys, paths or transcript content; <see cref="Kind"/> tells the caller whether to wait for the network and retry
/// (<see cref="CloudFailureKind.Transient"/>) or give up for this audio.
/// </summary>
public sealed class CloudDiarizationException : Exception
{
    public CloudDiarizationException(CloudFailureKind kind, CloudFailureReason reason, string message, int? statusCode = null, TimeSpan? retryAfter = null,
        Exception? inner = null) : base(message, inner)
    {
        Kind = kind; Reason = reason; StatusCode = statusCode; RetryAfter = retryAfter;
    }

    public CloudFailureKind Kind { get; }
    public CloudFailureReason Reason { get; }
    /// <summary>The HTTP status of the answer, null when none arrived.</summary>
    public int? StatusCode { get; }
    /// <summary>The server's <c>Retry-After</c> (429/503), null when it sent none.</summary>
    public TimeSpan? RetryAfter { get; }
    public bool IsTransient => Kind == CloudFailureKind.Transient;

    /// <summary>The classification of an HTTP status that is not a success (see <see cref="CloudFailureReason"/>).</summary>
    public static (CloudFailureKind Kind, CloudFailureReason Reason) Classify(int status) => status switch
    {
        401 or 403 => (CloudFailureKind.Permanent, CloudFailureReason.Unauthorized),
        413 => (CloudFailureKind.Permanent, CloudFailureReason.TooLarge),
        504 => (CloudFailureKind.Permanent, CloudFailureReason.ServerTimeout),
        429 => (CloudFailureKind.Transient, CloudFailureReason.RateLimited),
        408 => (CloudFailureKind.Transient, CloudFailureReason.ServerError),
        >= 500 and <= 599 => (CloudFailureKind.Transient, CloudFailureReason.ServerError),
        _ => (CloudFailureKind.Permanent, CloudFailureReason.Rejected)
    };

    internal static CloudDiarizationException ForStatus(int status, TimeSpan? retryAfter)
    {
        var (kind, reason) = Classify(status);
        return new(kind, reason, $"Cloud speaker separation failed: HTTP {status}{(kind == CloudFailureKind.Transient ? " (will retry)" : "")}.", status, retryAfter);
    }

    internal static CloudDiarizationException Network(Exception inner) =>
        new(CloudFailureKind.Transient, CloudFailureReason.Network, "Cloud speaker separation failed: network error (will retry).", inner: inner);

    internal static CloudDiarizationException Timeout(bool uploading) =>
        new(CloudFailureKind.Transient, CloudFailureReason.Timeout,
            uploading ? "Cloud speaker separation failed: the upload stalled (will retry)." : "Cloud speaker separation failed: no answer in time (will retry).");

    internal static CloudDiarizationException Malformed(string what, Exception? inner = null) =>
        new(CloudFailureKind.Permanent, CloudFailureReason.Malformed, $"Cloud speaker separation failed: unexpected answer ({what}).", inner: inner);

    internal static CloudDiarizationException Untrustworthy(CloudFailureReason reason, string what) =>
        new(CloudFailureKind.Untrustworthy, reason, $"Cloud speaker separation result discarded: {what}.");
}

/// <summary>Phases of a cloud diarization run, for the progress text ("Uploading… NN %", "Separating speakers (cloud)…").</summary>
public enum CloudDiarizationPhase
{
    /// <summary>Compressing the timeline to Ogg/Opus (determinate).</summary>
    Encoding,
    /// <summary>Sending the body (determinate when <see cref="CloudDiarizationProgress.BytesTotal"/> is known).</summary>
    Uploading,
    /// <summary>Upload done, waiting for the service (indeterminate).</summary>
    Processing
}

/// <param name="Fraction">Share of the phase done in [0, 1]; null while indeterminate.</param>
/// <param name="BytesSent">Body bytes written so far (upload and processing phases).</param>
/// <param name="BytesTotal">Body size when known.</param>
public readonly record struct CloudDiarizationProgress(CloudDiarizationPhase Phase, double? Fraction, long BytesSent, long? BytesTotal);
