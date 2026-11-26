namespace Wallfly.Diarization;

/// <summary>Level checks of channel archives before a diarization pass.</summary>
public static class ArchiveLevel
{
    /// <summary>−80 dBFS: a 10-ms block whose peak stays below this is silence.</summary>
    public const float SilencePeak = 1e-4f;

    /// <summary>Length of the blocks that are classified as audible or silent.</summary>
    public const double BlockSeconds = 0.01;

    /// <summary>
    /// An archive with less audible audio than this is silent. It matches the 10 s an acoustic identity needs to be kept
    /// (<c>FusionPolicy.MinIdentitySpeech</c>), so a few notification sounds in an otherwise digitally silent stream (the
    /// Others capture that delivered nothing on 20.11.: 7 s of non-zero samples in 23 min) still count as silent.
    /// </summary>
    public const double MinAudibleSeconds = 10;

    /// <summary>
    /// Seconds of audible 10-ms blocks in the archives, counting stops once <paramref name="stopAt"/> is reached. Null when
    /// a file cannot be read (the diarizer reports that itself).
    /// </summary>
    public static double? AudibleSeconds(IReadOnlyList<string> paths, double stopAt = double.PositiveInfinity, CancellationToken cancellationToken = default)
    {
        var block = (int)Math.Round(BlockSeconds * Wallfly.Speakers.DiarizationTimeline.SampleRate);
        var buffer = new float[block * 4096];
        long audible = 0;
        try
        {
            foreach (var path in paths)
            {
                using var archive = WavArchive.Open(path);
                for (long offset = 0; offset < archive.FrameCount; offset += buffer.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = archive.Read(offset, buffer);
                    if (read == 0) break;
                    for (var start = 0; start < read; start += block)
                    {
                        var span = buffer.AsSpan(start, Math.Min(block, read - start));
                        foreach (var sample in span)
                        {
                            if (Math.Abs(sample) <= SilencePeak) continue;
                            audible++;
                            break;
                        }
                    }
                    if (audible * BlockSeconds >= stopAt) return audible * BlockSeconds;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Wallfly.Speakers.DiarizationException or InvalidDataException)
        {
            return null;
        }
        return audible * BlockSeconds;
    }

    /// <summary>
    /// True when the archives contain frames but less than <see cref="MinAudibleSeconds"/> of audible audio. False for no
    /// frames and for unreadable files: the diarizer reports those itself.
    /// </summary>
    public static bool IsSilent(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        long frames = 0;
        try { frames = paths.Sum(WavArchive.FrameCountOf); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Wallfly.Speakers.DiarizationException or InvalidDataException)
        {
            return false;
        }
        return frames > 0 && AudibleSeconds(paths, MinAudibleSeconds, cancellationToken) is double seconds && seconds < MinAudibleSeconds;
    }
}
