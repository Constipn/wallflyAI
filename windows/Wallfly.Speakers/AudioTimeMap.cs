using Wallfly.Core;

namespace Wallfly.Speakers;

/// <summary>Sample index in one archive file and its meeting offset (seconds).</summary>
public readonly record struct TimeAnchor(long Frame, double Offset);

/// <summary>
/// Pure mapping between sample positions in one saved 16 kHz archive file and meeting offsets (port of AudioTimeMap.swift).
/// The archive omits capture gaps, so time advances linearly only between anchors:
/// <c>offset(f) = anchor.Offset + (f - anchor.Frame) / 16000</c>.
/// </summary>
public sealed class AudioTimeMap
{
    public const double SampleRate = 16_000;

    /// <summary>Sorted by frame; at most one anchor per frame (the last one reported wins).</summary>
    public IReadOnlyList<TimeAnchor> Anchors { get; }
    /// <summary>Optional file length; when set, offsets past the end of the file map to null.</summary>
    public long? FrameCount { get; }

    public AudioTimeMap(IEnumerable<TimeAnchor> anchors, long? frameCount = null)
    {
        var sorted = new List<TimeAnchor>();
        foreach (var anchor in anchors.Select((anchor, order) => (anchor, order)).OrderBy(pair => pair.anchor.Frame).ThenBy(pair => pair.order).Select(pair => pair.anchor))
        {
            if (sorted.Count > 0 && sorted[^1].Frame == anchor.Frame) sorted[^1] = anchor;
            else sorted.Add(anchor);
        }
        Anchors = sorted;
        FrameCount = frameCount;
    }

    /// <summary>The anchors of one file (by file name, case-insensitive) out of <c>Meeting.AudioAnchors</c>.</summary>
    public static AudioTimeMap ForFile(IEnumerable<AudioAnchor>? anchors, string fileName, long? frameCount = null) =>
        new((anchors ?? []).Where(anchor => string.Equals(anchor.File, fileName, StringComparison.OrdinalIgnoreCase))
            .Select(anchor => new TimeAnchor(anchor.Frame, anchor.Offset)), frameCount);

    /// <summary>
    /// Fallback for meetings saved before anchors existed: the file starts at <paramref name="fileStart"/> (0 for the first
    /// recording attempt) and every capture gap of <paramref name="source"/> shifts later frames by its duration.
    /// Overlapping gaps count once.
    /// </summary>
    public static AudioTimeMap FromRecordingGaps(IEnumerable<RecordingGap> recordingGaps, CaptureSource source, double fileStart = 0, long? frameCount = null)
    {
        var anchors = new List<TimeAnchor> { new(0, fileStart) };
        long frame = 0;
        var offset = fileStart;
        foreach (var gap in recordingGaps.Where(gap => gap.Source == source && gap.Duration > 0).OrderBy(gap => gap.Start))
        {
            var end = gap.Start + gap.Duration;
            if (!(end > offset)) continue;
            var start = Math.Max(gap.Start, offset);
            frame += (long)SwiftMath.Rounded((start - offset) * SampleRate);
            offset = end;
            if (anchors[^1].Frame == frame) anchors.RemoveAt(anchors.Count - 1);
            anchors.Add(new(frame, offset));
        }
        return new AudioTimeMap(anchors, frameCount);
    }

    /// <summary>Meeting offset of <paramref name="frame"/>. Frames before the first anchor extrapolate backwards; without anchors the file starts at 0.</summary>
    public double OffsetForFrame(long frame)
    {
        if (Anchors.Count == 0) return frame / SampleRate;
        var anchor = Anchors[0];
        for (var index = Anchors.Count - 1; index >= 0; index--)
        {
            if (Anchors[index].Frame <= frame) { anchor = Anchors[index]; break; }
        }
        return anchor.Offset + (frame - anchor.Frame) / SampleRate;
    }

    /// <summary>
    /// Frame recorded at meeting <paramref name="offset"/>, or null when no audio was written for that time (before the
    /// file, inside a gap, or past <see cref="FrameCount"/>). If a backwards jump recorded the same offset twice, the
    /// earliest frame wins.
    /// </summary>
    public long? FrameForOffset(double offset)
    {
        if (!double.IsFinite(offset)) return null;
        IReadOnlyList<TimeAnchor> anchors = Anchors.Count == 0 ? [new TimeAnchor(0, 0)] : Anchors;
        for (var index = 0; index < anchors.Count; index++)
        {
            var anchor = anchors[index];
            long? limit = index + 1 < anchors.Count ? anchors[index + 1].Frame : FrameCount;
            if (!(offset >= anchor.Offset - 1e-9)) continue;
            var relative = (long)Math.Max(0, Math.Floor((offset - anchor.Offset) * SampleRate + 1e-4));
            if (limit is long bound && relative >= bound - anchor.Frame) continue;
            return anchor.Frame + relative;
        }
        return null;
    }
}
