using Wallfly.Speakers;

namespace Wallfly.Diarization;

/// <summary>
/// Settings of the post-clustering voice merge (<see cref="VoiceMerge"/>); the microphone and the Others pass each have
/// their own instance (<see cref="SherpaOnnxDiarizerOptions.Microphone"/>, <see cref="SherpaOnnxDiarizerOptions.Others"/>).
/// </summary>
public sealed record VoiceMergeOptions
{
    /// <summary>
    /// Two clusters whose fingerprint centroids have at least this cosine similarity are one voice and are merged (repeatedly,
    /// most similar pair first, centroids recomputed from the members' fingerprints). Measured with ERes2Net on the far-field
    /// three-person meeting of 20.11. (threshold-0.9 clusters, 4-s fingerprints): parts of one person 0.81–0.95, different people
    /// 0.22–0.46; 0.7 lies in the middle of that gap.
    /// </summary>
    public double MergeSimilarity { get; init; } = 0.7;
    /// <summary>A further voice must hold at least this much speech (the 10-s identity rule) …</summary>
    public double MinVoiceSeconds { get; init; } = 10;
    /// <summary>
    /// … and at least this share of the channel's speech; smaller clusters join the most similar voice that qualifies.
    /// The far-field meeting of 20.11. had two fragments of one remote
    /// voice with 1.4 % and 1.9 % of the channel, so 3 % (37 s of its 20 min of speech, 1:48 min of an hour) keeps such
    /// fragments out while a real participant who speaks for 30 s in a short meeting still counts.
    /// </summary>
    public double MinVoiceShare { get; init; } = 0.03;
    /// <summary>Fingerprints are taken from stretches of one cluster without another cluster's speech that last at least this long (seconds).</summary>
    public double MinFingerprintSeconds { get; init; } = 1.0;
    /// <summary>Longer stretches are cut into pieces of about this length; each piece gives one fingerprint.</summary>
    public double FingerprintSeconds { get; init; } = 4.0;
    /// <summary>At most this much audio per cluster is fingerprinted (pieces spread evenly over the meeting); bounds the run time.</summary>
    public double MaxFingerprintSecondsPerCluster { get; init; } = 120;
}

/// <summary>One fingerprint: the speaker embedding of <see cref="Seconds"/> of one cluster's speech.</summary>
public readonly record struct VoiceFingerprint(int Cluster, double Seconds, float[] Embedding);

/// <summary>A stretch of the timeline to fingerprint, in samples.</summary>
public readonly record struct FingerprintSpan(int Cluster, long Start, long End)
{
    public double Seconds => (End - Start) / DiarizationTimeline.SampleRate;
}

/// <summary>What <see cref="VoiceMerge.Plan"/> decided: cluster → voice (a surviving cluster id).</summary>
/// <param name="Merged">Clusters merged into a more similar voice.</param>
/// <param name="Absorbed">Clusters below the minimum voice size that joined the most similar voice.</param>
public sealed record VoiceMergePlan(IReadOnlyDictionary<int, int> Voices, int Merged, int Absorbed);

/// <summary>
/// Post-clustering step of the microphone and the Others pass. sherpa-onnx' agglomerative clustering links clusters by
/// their two most distant windows, so one voice breaks into several clusters: far-field speech (loudspeaker playback
/// through echo cancellation) on the microphone, one remote person in up to six retained clusters on Others (Teams call of 24.11.).
/// The merge compares whole clusters instead: each cluster gets a fingerprint centroid (mean of L2-normalised ERes2Net
/// embeddings of its clean speech), the most similar pair is merged while its cosine similarity reaches
/// <see cref="VoiceMergeOptions.MergeSimilarity"/>, and clusters too small to be a voice of their own join the most similar
/// voice. Pure and deterministic; the embeddings come from <see cref="SherpaOnnxDiarizer"/>.
/// </summary>
public static class VoiceMerge
{
    /// <summary>Union speech seconds per cluster.</summary>
    public static Dictionary<int, double> Speech(IReadOnlyList<TimelineSegment> segments)
    {
        var result = new Dictionary<int, double>();
        foreach (var own in segments.GroupBy(segment => segment.Speaker))
        {
            double total = 0, until = double.NegativeInfinity;
            foreach (var segment in own.OrderBy(segment => segment.Start))
            {
                var from = Math.Max(segment.Start, until);
                if (segment.End > from) total += segment.End - from;
                until = Math.Max(until, segment.End);
            }
            result[own.Key] = total;
        }
        return result;
    }

    /// <summary>
    /// The stretches to fingerprint: each segment minus every other cluster's segments, pieces of at least
    /// <see cref="VoiceMergeOptions.MinFingerprintSeconds"/>, cut into about <see cref="VoiceMergeOptions.FingerprintSeconds"/>,
    /// at most <see cref="VoiceMergeOptions.MaxFingerprintSecondsPerCluster"/> per cluster (evenly spread in time).
    /// </summary>
    public static List<FingerprintSpan> Spans(IReadOnlyList<TimelineSegment> segments, VoiceMergeOptions options, double sampleRate = DiarizationTimeline.SampleRate)
    {
        var minimum = (long)Math.Round(options.MinFingerprintSeconds * sampleRate);
        var pieces = new List<FingerprintSpan>();
        foreach (var segment in segments)
        {
            var parts = new List<(long Start, long End)> { ((long)Math.Round(segment.Start * sampleRate), (long)Math.Round(segment.End * sampleRate)) };
            foreach (var other in segments)
            {
                if (other.Speaker == segment.Speaker || other.End <= segment.Start || other.Start >= segment.End) continue;
                var (otherStart, otherEnd) = ((long)Math.Round(other.Start * sampleRate), (long)Math.Round(other.End * sampleRate));
                parts = parts.SelectMany(part => otherEnd <= part.Start || otherStart >= part.End ? [part]
                    : new[] { (part.Start, Math.Min(part.End, otherStart)), (Math.Max(part.Start, otherEnd), part.End) }.Where(piece => piece.Item2 > piece.Item1)).ToList();
            }
            foreach (var (start, end) in parts)
            {
                if (end - start < minimum) continue;
                var count = Math.Max(1, (int)Math.Round((end - start) / (options.FingerprintSeconds * sampleRate), MidpointRounding.AwayFromZero));
                for (var index = 0; index < count; index++)
                {
                    var from = start + (end - start) * index / count;
                    var to = start + (end - start) * (index + 1) / count;
                    if (to - from >= minimum) pieces.Add(new FingerprintSpan(segment.Speaker, from, to));
                }
            }
        }
        var result = new List<FingerprintSpan>();
        foreach (var own in pieces.GroupBy(piece => piece.Cluster).OrderBy(group => group.Key))
        {
            var ordered = own.OrderBy(piece => piece.Start).ToList();
            var total = ordered.Sum(piece => (double)(piece.End - piece.Start)) / sampleRate;
            if (total <= options.MaxFingerprintSecondsPerCluster) { result.AddRange(ordered); continue; }
            // Evenly spread: take the piece at every step of total / budget seconds of the cluster's fingerprintable speech.
            var keep = Math.Max(1, (int)Math.Floor(ordered.Count * options.MaxFingerprintSecondsPerCluster / total));
            for (var index = 0; index < keep; index++) result.Add(ordered[(int)((long)index * ordered.Count / keep)]);
        }
        return result;
    }

    /// <summary>Cosine similarity of two L2-normalised vectors.</summary>
    public static double Similarity(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        double sum = 0;
        for (var index = 0; index < a.Count; index++) sum += a[index] * b[index];
        return sum;
    }

    /// <summary>The normalised mean of the members' normalised fingerprints (null without fingerprints).</summary>
    public static double[]? Centroid(IEnumerable<VoiceFingerprint> fingerprints)
    {
        double[]? sum = null;
        foreach (var fingerprint in fingerprints)
        {
            sum ??= new double[fingerprint.Embedding.Length];
            double norm = 0;
            foreach (var value in fingerprint.Embedding) norm += value * (double)value;
            norm = Math.Sqrt(norm);
            if (!(norm > 0)) continue;
            for (var index = 0; index < sum.Length; index++) sum[index] += fingerprint.Embedding[index] / norm;
        }
        if (sum is null) return null;
        var length = Math.Sqrt(sum.Sum(value => value * value));
        if (!(length > 0)) return null;
        for (var index = 0; index < sum.Length; index++) sum[index] /= length;
        return sum;
    }

    public static VoiceMergePlan Plan(IReadOnlyList<TimelineSegment> segments, IReadOnlyList<VoiceFingerprint> fingerprints, VoiceMergeOptions options)
    {
        var speech = Speech(segments);
        var voices = speech.Keys.ToDictionary(cluster => cluster, cluster => cluster);
        var members = speech.Keys.ToDictionary(cluster => cluster, cluster => new List<int> { cluster });
        var byCluster = fingerprints.GroupBy(fingerprint => fingerprint.Cluster).ToDictionary(group => group.Key, group => group.ToList());
        double[]? CentroidOf(int voice) => Centroid(members[voice].SelectMany(cluster => byCluster.GetValueOrDefault(cluster) ?? []));
        double SpeechOf(int voice) => members[voice].Sum(cluster => speech[cluster]);
        void Join(int from, int into)
        {
            members[into].AddRange(members[from]);
            foreach (var cluster in members[from]) voices[cluster] = into;
            members.Remove(from);
        }

        // 1. Merge the most similar pair while it reaches the threshold (ties: the lower ids, for determinism).
        var merged = 0;
        while (true)
        {
            var centroids = members.Keys.Order().Select(voice => (Voice: voice, Centroid: CentroidOf(voice))).Where(pair => pair.Centroid is not null).ToList();
            (double Similarity, int A, int B)? best = null;
            for (var i = 0; i < centroids.Count; i++)
                for (var j = i + 1; j < centroids.Count; j++)
                {
                    var similarity = Similarity(centroids[i].Centroid!, centroids[j].Centroid!);
                    if (best is not { } current || similarity > current.Similarity) best = (similarity, centroids[i].Voice, centroids[j].Voice);
                }
            if (best is not { } pair || pair.Similarity < options.MergeSimilarity) break;
            // The voice with more speech keeps its id.
            var (keep, drop) = SpeechOf(pair.A) >= SpeechOf(pair.B) ? (pair.A, pair.B) : (pair.B, pair.A);
            Join(drop, keep);
            merged++;
        }

        // 2. Minimum voice size: the largest voice always stays; a further voice needs max(MinVoiceSeconds, MinVoiceShare × speech).
        var total = speech.Values.Sum();
        var minimum = Math.Max(options.MinVoiceSeconds, options.MinVoiceShare * total);
        var dominant = members.Keys.OrderByDescending(SpeechOf).ThenBy(voice => voice).FirstOrDefault();
        var qualified = members.Keys.Where(voice => voice == dominant || SpeechOf(voice) >= minimum).ToList();
        var absorbed = 0;
        foreach (var small in members.Keys.Except(qualified).OrderBy(voice => voice).ToList())
        {
            // Too little clean speech for a fingerprint: left as it is (the fusion's 10-s rule suppresses it).
            if (CentroidOf(small) is not { } centroid) continue;
            var target = qualified.Select(voice => (Voice: voice, Centroid: CentroidOf(voice))).Where(pair => pair.Centroid is not null)
                .OrderByDescending(pair => Similarity(centroid, pair.Centroid!)).ThenBy(pair => pair.Voice).Select(pair => (int?)pair.Voice).FirstOrDefault();
            if (target is not int into) continue;
            Join(small, into);
            absorbed++;
        }
        return new VoiceMergePlan(voices, merged, absorbed);
    }

    /// <summary>Relabels the segments with their voice; overlapping or touching segments of one voice become one.</summary>
    public static List<TimelineSegment> Apply(IReadOnlyList<TimelineSegment> segments, VoiceMergePlan plan)
    {
        var result = new List<TimelineSegment>();
        foreach (var own in segments.Select(segment => segment with { Speaker = plan.Voices.GetValueOrDefault(segment.Speaker, segment.Speaker) })
                     .GroupBy(segment => segment.Speaker))
        {
            TimelineSegment? open = null;
            foreach (var segment in own.OrderBy(segment => segment.Start).ThenBy(segment => segment.End))
            {
                if (open is { } current && segment.Start <= current.End) { open = current with { End = Math.Max(current.End, segment.End) }; continue; }
                if (open is { } done) result.Add(done);
                open = segment;
            }
            if (open is { } last) result.Add(last);
        }
        return result.OrderBy(segment => segment.Start).ThenBy(segment => segment.Speaker).ToList();
    }
}
