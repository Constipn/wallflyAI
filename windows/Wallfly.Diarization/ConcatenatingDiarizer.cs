using System.Diagnostics;
using Wallfly.Speakers;

namespace Wallfly.Diarization;

/// <summary>
/// The shared part of every engine (port of the macOS <c>AcousticDiarizer.diarize(audioFiles:…)</c>): lays the recording
/// attempts out on one 16 kHz mono timeline with 1 s of silence between them, rejects empty input and input over 4 h,
/// lets the engine diarize the timeline once and maps the result back to archive time (segments in the inserted silence
/// are dropped). Engines implement <see cref="DiarizeAsync"/> only.
/// </summary>
public abstract class ConcatenatingDiarizer : IDiarizer
{
    public abstract string Engine { get; }
    public abstract string? ModelVersion { get; }
    public abstract double? Threshold { get; }

    /// <summary>Longest accepted remote audio in seconds (default 4 h).</summary>
    public double MaxAudioSeconds { get; init; } = DiarizationTimeline.MaxAudioSeconds;

    public async Task<DiarizationResult> RunAsync(IReadOnlyList<string> othersWavPaths, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(othersWavPaths);
        cancellationToken.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        using var audio = ConcatenatedWav.Open(othersWavPaths);
        if (!audio.Parts.Any(part => part.FrameCount > 0)) throw DiarizationException.NoAudio();
        var seconds = audio.Seconds;
        if (seconds > MaxAudioSeconds)
            throw DiarizationException.AudioTooLong((int)Math.Round(seconds, MidpointRounding.AwayFromZero), (int)MaxAudioSeconds);
        cancellationToken.ThrowIfCancellationRequested();
        var raw = await DiarizeAsync(audio, progress, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var merged = raw as DiarizedTimeline;
        List<ArchiveSegment>? unmerged = null;
        Dictionary<int, int>? voices = null;
        if (merged is { Unmerged: { } before, Voices: { } plan })
        {
            // Both lists are renumbered by first appearance: the voice map is expressed in the ids of the unmerged clusters.
            unmerged = audio.ToArchiveSegments(before, out var ids);
            voices = ArchiveVoices(plan, ids);
        }
        return new DiarizationResult(Engine, ModelVersion, Threshold, audio.Files, audio.ToArchiveSegments(raw))
        {
            Elapsed = clock.Elapsed, Merge = merged?.Merge, Unmerged = unmerged, Voices = voices
        };
    }

    /// <summary>
    /// The voice map in archive ids: each unmerged cluster → the archive id of the cluster that stands for its voice (the
    /// voice's own cluster, or the lowest member id when that cluster has no segment in a file).
    /// </summary>
    internal static Dictionary<int, int> ArchiveVoices(IReadOnlyDictionary<int, int> voices, IReadOnlyDictionary<int, int> ids)
    {
        var result = new Dictionary<int, int>();
        foreach (var group in voices.Where(pair => ids.ContainsKey(pair.Key)).GroupBy(pair => pair.Value))
        {
            var target = ids.TryGetValue(group.Key, out var own) ? own : group.Min(pair => ids[pair.Key]);
            foreach (var pair in group) result[ids[pair.Key]] = target;
        }
        return result;
    }

    /// <summary>
    /// Diarizes the whole timeline. Times in the result are seconds into the timeline; speaker ids may be arbitrary
    /// integers. Reports progress in [0, 1].
    /// </summary>
    protected abstract Task<IReadOnlyList<TimelineSegment>> DiarizeAsync(ConcatenatedWav audio, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// Timeline segments of an engine plus what its post-clustering voice merge changed (passed on in
/// <see cref="DiarizationResult.Merge"/>, <see cref="DiarizationResult.Unmerged"/> and <see cref="DiarizationResult.Voices"/>).
/// </summary>
public sealed class DiarizedTimeline(IEnumerable<TimelineSegment> segments, VoiceMergeSummary? merge, IReadOnlyList<TimelineSegment>? unmerged = null,
    IReadOnlyDictionary<int, int>? voices = null) : List<TimelineSegment>(segments)
{
    public VoiceMergeSummary? Merge { get; } = merge;
    /// <summary>The clusters before the merge.</summary>
    public IReadOnlyList<TimelineSegment>? Unmerged { get; } = unmerged;
    /// <summary>Cluster of <see cref="Unmerged"/> → voice.</summary>
    public IReadOnlyDictionary<int, int>? Voices { get; } = voices;
}
