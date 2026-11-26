using Wallfly.Speakers;

namespace Wallfly.Diarization;

/// <summary>One diarized stretch on the concatenated timeline, in seconds from its start.</summary>
public readonly record struct TimelineSegment(int Speaker, double Start, double End);

/// <summary>
/// Several 16 kHz mono archives on one sample timeline, separated by silence, read on demand (port of the macOS
/// <c>ConcatenatedAudio</c>). Only the headers are read up front. Thread-safe: reads are serialized.
/// </summary>
public sealed class ConcatenatedWav : IDisposable
{
    private readonly List<WavArchive> archives;
    private readonly Lock gate = new();

    /// <param name="Start">First sample of this file on the timeline.</param>
    public sealed record Part(string Path, long Start, long FrameCount);

    private ConcatenatedWav(List<WavArchive> archives, List<Part> parts, long sampleCount, long separatorFrames)
    {
        this.archives = archives; Parts = parts; SampleCount = sampleCount; SeparatorFrames = separatorFrames;
    }

    public IReadOnlyList<Part> Parts { get; }
    public long SampleCount { get; }
    public long SeparatorFrames { get; }
    public double Seconds => SampleCount / DiarizationTimeline.SampleRate;
    public IReadOnlyList<ArchiveFile> Files => Parts.Select(part => new ArchiveFile(part.Path, part.FrameCount)).ToList();

    /// <summary>Opens all files in order. Silence is inserted only between two non-empty files.</summary>
    public static ConcatenatedWav Open(IReadOnlyList<string> paths, double separatorSeconds = DiarizationTimeline.SeparatorSeconds)
    {
        var separator = (long)Math.Round(separatorSeconds * DiarizationTimeline.SampleRate, MidpointRounding.AwayFromZero);
        var archives = new List<WavArchive>();
        try
        {
            foreach (var path in paths) archives.Add(WavArchive.Open(path));
        }
        catch
        {
            foreach (var archive in archives) archive.Dispose();
            throw;
        }
        var parts = new List<Part>();
        long cursor = 0;
        foreach (var archive in archives)
        {
            if (archive.FrameCount > 0 && parts.Any(part => part.FrameCount > 0)) cursor += separator;
            parts.Add(new Part(archive.Path, cursor, archive.FrameCount));
            cursor += archive.FrameCount;
        }
        return new ConcatenatedWav(archives, parts, cursor, separator);
    }

    /// <summary>Writes timeline samples <c>[offset, offset + destination.Length)</c>; separators and anything outside the files are zero.</summary>
    public void CopySamples(Span<float> destination, long offset)
    {
        destination.Clear();
        lock (gate)
        {
            for (var index = 0; index < Parts.Count; index++)
            {
                var part = Parts[index];
                if (part.FrameCount == 0) continue;
                var lower = Math.Max(offset, part.Start);
                var upper = Math.Min(offset + destination.Length, part.Start + part.FrameCount);
                if (upper <= lower) continue;
                archives[index].Read(lower - part.Start, destination.Slice((int)(lower - offset), (int)(upper - lower)));
            }
        }
    }

    /// <summary>The whole timeline in memory (about 230 MB per hour of audio).</summary>
    public float[] ReadAll()
    {
        if (SampleCount > Array.MaxLength) throw DiarizationException.ProcessingFailed("audio too long for one buffer");
        var samples = new float[SampleCount];
        const int chunk = 1 << 20;
        for (long offset = 0; offset < SampleCount; offset += chunk)
        {
            CopySamples(samples.AsSpan((int)offset, (int)Math.Min(chunk, SampleCount - offset)), offset);
        }
        return samples;
    }

    /// <summary>
    /// Maps timeline segments to archive time: clipped to each file and dropped where they only cover inserted silence.
    /// Speaker ids are renumbered 0, 1, … by first appearance on the timeline.
    /// </summary>
    public List<ArchiveSegment> ToArchiveSegments(IEnumerable<TimelineSegment> segments) => ToArchiveSegments(segments, out _);

    /// <inheritdoc cref="ToArchiveSegments(IEnumerable{TimelineSegment})"/>
    /// <param name="ids">Timeline speaker id → archive speaker id (only speakers with a segment in a file).</param>
    public List<ArchiveSegment> ToArchiveSegments(IEnumerable<TimelineSegment> segments, out Dictionary<int, int> ids)
    {
        var result = new List<ArchiveSegment>();
        foreach (var segment in segments)
        {
            if (!(segment.End > segment.Start)) continue;
            var lower = (long)Math.Round(segment.Start * DiarizationTimeline.SampleRate, MidpointRounding.AwayFromZero);
            var upper = (long)Math.Round(segment.End * DiarizationTimeline.SampleRate, MidpointRounding.AwayFromZero);
            for (var index = 0; index < Parts.Count; index++)
            {
                var part = Parts[index];
                if (part.FrameCount == 0) continue;
                var first = Math.Max(lower, part.Start) - part.Start;
                var last = Math.Min(upper, part.Start + part.FrameCount) - part.Start;
                if (last > first) result.Add(new ArchiveSegment(segment.Speaker, index, first, last));
            }
        }
        var renumbered = new Dictionary<int, int>();
        var mapped = result.Select((segment, order) => (segment, order))
            .OrderBy(pair => Parts[pair.segment.File].Start + pair.segment.StartFrame).ThenBy(pair => pair.order)
            .Select(pair =>
            {
                if (!renumbered.TryGetValue(pair.segment.Speaker, out var id)) renumbered[pair.segment.Speaker] = id = renumbered.Count;
                return pair.segment with { Speaker = id };
            }).ToList();
        ids = renumbered;
        return mapped;
    }

    public void Dispose()
    {
        foreach (var archive in archives) archive.Dispose();
    }
}
