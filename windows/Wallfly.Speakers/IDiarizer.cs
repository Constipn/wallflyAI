namespace Wallfly.Speakers;

/// <summary>
/// Offline acoustic speaker diarization of a meeting's remote channel (spec 5.3; contract of the macOS
/// <c>AcousticDiarizer</c>). Implementations lay all recording attempts out on one 16 kHz mono timeline with 1 s of
/// silence between them, so one clustering run keeps identities consistent across attempts, and return segments per
/// archive file. <see cref="DiarizationTimeline.ToMeetingSegments"/> maps them to meeting offsets.
/// </summary>
public interface IDiarizer
{
    /// <summary>Engine id stored in <c>Meeting.Diarization.Engine</c>.</summary>
    string Engine { get; }
    string? ModelVersion { get; }
    /// <summary>Clustering threshold in the engine's own semantics (not comparable across engines).</summary>
    double? Threshold { get; }

    /// <summary>
    /// Diarizes the given Others archives (16 kHz mono WAV, in recording-attempt order) in one run.
    /// </summary>
    /// <exception cref="DiarizationException">Models missing, no audio, unreadable audio, longer than 4 h, or the engine failed.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled; no result is produced.</exception>
    Task<DiarizationResult> RunAsync(IReadOnlyList<string> othersWavPaths, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// An engine whose running computation can be held while a recording is active (the laptop must not be loaded during a
/// meeting). <see cref="Pause"/> and <see cref="Resume"/> apply to whatever run of the instance is going or starts later.
/// </summary>
public interface IPausableDiarizer
{
    /// <summary>Holds the running computation at the next point the engine allows and lowers its threads to the idle priority.</summary>
    void Pause();
    /// <summary>Lets the computation continue at its normal priority.</summary>
    void Resume();
}

/// <summary>One input archive and its length in samples (16 kHz).</summary>
public sealed record ArchiveFile(string Path, long FrameCount)
{
    public double Seconds => FrameCount / DiarizationTimeline.SampleRate;
}

/// <summary>
/// One diarized stretch in archive time: samples <c>[StartFrame, EndFrame)</c> of <see cref="ArchiveFile"/> number
/// <see cref="File"/>. <see cref="Speaker"/> is the cluster index of the run (0-based, by first appearance).
/// </summary>
public readonly record struct ArchiveSegment(int Speaker, int File, long StartFrame, long EndFrame)
{
    /// <summary>Seconds from the start of the archive file.</summary>
    public double Start => StartFrame / DiarizationTimeline.SampleRate;
    public double End => EndFrame / DiarizationTimeline.SampleRate;
}

/// <summary>Result of one diarization run: segments per archive file plus engine and model information.</summary>
public sealed record DiarizationResult(string Engine, string? ModelVersion, double? Threshold,
    IReadOnlyList<ArchiveFile> Files, IReadOnlyList<ArchiveSegment> Segments)
{
    /// <summary>Wall-clock duration of the run (model loading included).</summary>
    public TimeSpan Elapsed { get; init; }
    /// <summary>What a post-clustering voice merge changed; null when none ran.</summary>
    public VoiceMergeSummary? Merge { get; init; }
    /// <summary>The clusters before the voice merge (null when none ran), so the merge can be checked against other evidence.</summary>
    public IReadOnlyList<ArchiveSegment>? Unmerged { get; init; }
    /// <summary>
    /// Voice merge: cluster of <see cref="Unmerged"/> → the cluster of <see cref="Unmerged"/> that stands for its voice (ids of
    /// <see cref="Unmerged"/>, not of <see cref="Segments"/>, which are renumbered on their own; null when none ran).
    /// </summary>
    public IReadOnlyDictionary<int, int>? Voices { get; init; }
    /// <summary>Clusters in <see cref="Segments"/> (after a voice merge).</summary>
    public int SpeakerCount => Segments.Select(segment => segment.Speaker).Distinct().Count();
}

/// <summary>Statistics of a post-clustering voice merge (numbers only).</summary>
/// <param name="Clusters">Clusters the clustering found before the merge.</param>
/// <param name="Merged">Clusters merged into a voice with a similar fingerprint centroid.</param>
/// <param name="Absorbed">Clusters below the minimum voice size that joined the most similar voice.</param>
/// <param name="Elapsed">Time for the fingerprints and the merge.</param>
public sealed record VoiceMergeSummary(int Clusters, int Merged, int Absorbed, TimeSpan Elapsed);

/// <summary>Port of <c>AcousticDiarizationError</c>.</summary>
public enum DiarizationErrorKind
{
    /// <summary>Models could not be found, verified or loaded. Callers skip quietly.</summary>
    ModelsUnavailable,
    /// <summary>None of the given recordings contains audio.</summary>
    NoAudio,
    /// <summary>A recording could not be read or is not 16 kHz mono.</summary>
    UnreadableAudio,
    ProcessingFailed,
    /// <summary>Remote audio longer than <see cref="DiarizationTimeline.MaxAudioSeconds"/>; callers skip with a message.</summary>
    AudioTooLong
}

/// <summary>A diarization run that ended without result. <see cref="Exception.Message"/> is the macOS user-facing text.</summary>
public sealed class DiarizationException : Exception
{
    private DiarizationException(DiarizationErrorKind kind, string message, string? reason = null, string? file = null, int seconds = 0, int limit = 0)
        : base(message)
    {
        Kind = kind; Reason = reason; File = file; Seconds = seconds; Limit = limit;
    }

    public DiarizationErrorKind Kind { get; }
    public string? Reason { get; }
    public string? File { get; }
    public int Seconds { get; }
    public int Limit { get; }

    public static DiarizationException ModelsUnavailable(string reason) =>
        new(DiarizationErrorKind.ModelsUnavailable, $"Speaker models unavailable: {reason}", reason);
    public static DiarizationException NoAudio() =>
        new(DiarizationErrorKind.NoAudio, "No audio recording available for speaker detection.");
    public static DiarizationException UnreadableAudio(string file, string reason) =>
        new(DiarizationErrorKind.UnreadableAudio, $"Audio file {file} unreadable: {reason}", reason, file);
    public static DiarizationException ProcessingFailed(string reason) =>
        new(DiarizationErrorKind.ProcessingFailed, $"Speaker detection failed: {reason}", reason);
    public static DiarizationException AudioTooLong(int seconds, int limit) =>
        new(DiarizationErrorKind.AudioTooLong, $"Recording too long for speaker detection ({seconds / 60} min, limit {limit / 60} min).",
            seconds: seconds, limit: limit);
}
