using Wallfly.Core;

namespace Wallfly.Speakers;

/// <summary>
/// Pure parts of the macOS <c>AcousticDiarization</c>: which archives are diarized, how archive time maps to meeting
/// offsets (anchors, else the recording-gap fallback), and the renumbering of cluster ids by first appearance.
/// </summary>
public static class DiarizationTimeline
{
    public const double SampleRate = 16_000;
    /// <summary>Silence inserted between two recording attempts on the diarization timeline.</summary>
    public const double SeparatorSeconds = 1.0;
    /// <summary>4 h bounds forgotten recordings (memory grows with the audio length).</summary>
    public const double MaxAudioSeconds = 4 * 3600;
    /// <summary>Windows archive suffix of the remote channel (<c>&lt;attempt&gt;-others.wav</c>; macOS: <c>-others.caf</c>, not readable here).</summary>
    public const string OthersSuffix = "-others.wav";

    /// <summary>macOS archive suffix of the remote channel (<c>&lt;attempt&gt;-others.caf</c>); a Mac library keeps these.</summary>
    public const string MacOthersSuffix = "-others.caf";

    /// <summary>Windows archive suffix of the microphone channel (<c>&lt;attempt&gt;-me.wav</c>).</summary>
    public const string MeSuffix = "-me.wav";
    /// <summary>macOS archive suffix of the microphone channel (<c>&lt;attempt&gt;-me.caf</c>, not readable here).</summary>
    public const string MacMeSuffix = "-me.caf";

    /// <summary>Remote-channel archives of a meeting in attempt order (<c>AudioFiles</c> keeps that order).</summary>
    public static List<string> OthersFiles(Meeting meeting) => Files(meeting, Speaker.Others);

    /// <summary>Remote-channel archives recorded on macOS (Core Audio Format): listed, but not readable by the Windows diarizer.</summary>
    public static List<string> MacOthersFiles(Meeting meeting) => MacFiles(meeting, Speaker.Others);

    /// <summary>The readable archives of one channel in attempt order.</summary>
    public static List<string> Files(Meeting meeting, Speaker channel) =>
        meeting.AudioFiles.Where(path => path.EndsWith(channel == Speaker.Me ? MeSuffix : OthersSuffix, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>The macOS (<c>.caf</c>) archives of one channel.</summary>
    public static List<string> MacFiles(Meeting meeting, Speaker channel) =>
        meeting.AudioFiles.Where(path => path.EndsWith(channel == Speaker.Me ? MacMeSuffix : MacOthersSuffix, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// Time maps of the given remote-channel files (parallel to <paramref name="files"/>): the saved anchors when a file
    /// has some, otherwise the <c>RecordingGaps</c> fallback. Fallback caveats (as on macOS): the last file (also a single
    /// one) is placed so that it ends at <c>meeting.Duration</c> (the stop time), other files start at 0 (first) or where
    /// the previous one ends; the capture source whose gaps apply is guessed by <see cref="FallbackSource"/>.
    /// </summary>
    /// <remarks>The Me channel's fallback uses the microphone gaps.</remarks>
    public static List<AudioTimeMap> TimeMaps(Meeting meeting, IReadOnlyList<ArchiveFile> files, Speaker channel = Speaker.Others)
    {
        var gaps = meeting.RecordingGaps ?? [];
        var totalSeconds = files.Sum(file => file.FrameCount / SampleRate);
        var source = channel == Speaker.Me ? CaptureSource.Microphone : FallbackSource(gaps, Math.Max(0, meeting.Duration - totalSeconds));
        var result = new List<AudioTimeMap>();
        double previousEnd = 0;
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            var map = AudioTimeMap.ForFile(meeting.AudioAnchors, Path.GetFileName(file.Path), file.FrameCount);
            if (map.Anchors.Count == 0)
            {
                var start = index == 0 ? 0 : previousEnd;
                if (index == files.Count - 1)
                {
                    var seconds = file.FrameCount / SampleRate;
                    var lost = MergedGapDuration(gaps.Where(gap => gap.Source == source), previousEnd, meeting.Duration);
                    start = Math.Max(previousEnd, meeting.Duration - seconds - lost);
                }
                map = AudioTimeMap.FromRecordingGaps(gaps, source, start, file.FrameCount);
            }
            result.Add(map);
            previousEnd = Math.Max(previousEnd, map.OffsetForFrame(file.FrameCount));
        }
        return result;
    }

    /// <summary>
    /// The remote channel comes from the app-specific Meeting stream when a meeting app was selected and from the global
    /// Reference loopback otherwise; old meetings do not record which. Meeting gaps prove the former; Reference gaps
    /// longer than the time missing from the files cannot have been cut out of them.
    /// </summary>
    public static CaptureSource FallbackSource(IReadOnlyList<RecordingGap> gaps, double missingSeconds)
    {
        if (gaps.Any(gap => gap.Source == CaptureSource.Meeting && gap.Duration > 0)) return CaptureSource.Meeting;
        var reference = MergedGapDuration(gaps.Where(gap => gap.Source == CaptureSource.Reference), 0, double.PositiveInfinity);
        return reference > missingSeconds + 0.5 ? CaptureSource.Meeting : CaptureSource.Reference;
    }

    /// <summary>Union length of the gaps inside [from, to]; overlapping gaps count once.</summary>
    public static double MergedGapDuration(IEnumerable<RecordingGap> gaps, double from, double to)
    {
        double end = from, total = 0;
        foreach (var gap in gaps.OrderBy(gap => gap.Start))
        {
            var start = Math.Max(gap.Start, end);
            var stop = Math.Min(gap.Start + gap.Duration, to);
            if (stop > start) total += stop - start;
            end = Math.Max(end, Math.Min(gap.Start + gap.Duration, to));
        }
        return total;
    }

    /// <summary>Sorts by start and renumbers speakers 0, 1, … by first appearance so ids do not depend on engine internals.</summary>
    public static List<AcousticSegment> Renumbered(IEnumerable<AcousticSegment> segments)
    {
        var ids = new Dictionary<int, int>();
        return segments.Select((segment, order) => (segment, order)).OrderBy(pair => pair.segment.Start).ThenBy(pair => pair.order)
            .Select(pair =>
            {
                if (!ids.TryGetValue(pair.segment.Speaker, out var id)) ids[pair.segment.Speaker] = id = ids.Count;
                return new AcousticSegment { Speaker = id, Start = pair.segment.Start, End = pair.segment.End };
            }).ToList();
    }

    /// <summary>
    /// Maps archive segments to meeting offsets (port of <c>ConcatenatedAudio.meetingSegments</c>): clipped to the file,
    /// split where the file's time map jumps (capture gaps). Speaker ids are kept.
    /// </summary>
    public static List<AcousticSegment> MapToMeeting(IEnumerable<ArchiveSegment> segments, IReadOnlyList<ArchiveFile> files, IReadOnlyList<AudioTimeMap> maps)
    {
        var result = new List<AcousticSegment>();
        foreach (var segment in segments)
        {
            if (segment.File < 0 || segment.File >= files.Count || segment.File >= maps.Count) continue;
            var file = files[segment.File];
            var map = maps[segment.File];
            var first = Math.Max(segment.StartFrame, 0);
            var last = Math.Min(segment.EndFrame, file.FrameCount);
            if (!(last > first)) continue;
            var cuts = new List<long> { first };
            cuts.AddRange(map.Anchors.Select(anchor => anchor.Frame).Where(frame => frame > first && frame < last));
            cuts.Add(last);
            for (var index = 0; index + 1 < cuts.Count; index++)
            {
                var (from, to) = (cuts[index], cuts[index + 1]);
                if (!(to > from)) continue;
                var start = map.OffsetForFrame(from);
                result.Add(new AcousticSegment { Speaker = segment.Speaker, Start = start, End = start + (to - from) / SampleRate });
            }
        }
        return result;
    }

    /// <summary>
    /// The meeting-offset segments of a run, sorted by start and renumbered by first appearance — what is stored in
    /// <c>Meeting.Diarization.Segments</c> and fed to <see cref="AcousticSpeakerLabels.ApplyAcousticFusion"/>.
    /// </summary>
    public static List<AcousticSegment> ToMeetingSegments(DiarizationResult result, Meeting meeting, Speaker channel = Speaker.Others) =>
        Renumbered(MapToMeeting(result.Segments, result.Files, TimeMaps(meeting, result.Files, channel)));
}
