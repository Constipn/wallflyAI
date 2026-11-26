namespace Wallfly.Transcription.Local;

/// <summary>
/// Shares one whisper-server per configuration between the two channel pipelines, reference-counted:
/// <see cref="Acquire"/> starts loading the model right away, and the server is stopped as soon as the last
/// lease is released (both channels finished or were cancelled, or a start attempt was discarded). A launcher
/// whose start failed is never reused. <see cref="Dispose"/> (or <see cref="WhisperServerLauncher.StopAll"/>
/// at app exit) stops everything.
/// </summary>
public sealed class LocalWhisperHost : IDisposable
{
    /// <summary>The app-wide host used by <see cref="TranscriptionPipelineFactory"/> unless one is injected.</summary>
    public static LocalWhisperHost Shared { get; } = new();

    private readonly object gate = new();
    private readonly Func<WhisperServerOptions, WhisperServerLauncher> create;
    private readonly Dictionary<WhisperServerOptions, WhisperServerLauncher> current = [];
    private readonly Dictionary<WhisperServerLauncher, int> leases = [];

    public LocalWhisperHost(Func<WhisperServerOptions, WhisperServerLauncher>? create = null) =>
        this.create = create ?? (options => new WhisperServerLauncher(options));

    /// <summary>Leases the running (or starting) server for these options, launching one if necessary.</summary>
    public LocalWhisperLease Acquire(WhisperServerOptions options)
    {
        WhisperServerLauncher launcher;
        lock (gate)
        {
            if (!current.TryGetValue(options, out var existing) || existing.HasStopped)
            {
                existing = create(options);
                current[options] = existing;
                leases[existing] = 0;
            }
            launcher = existing;
            leases[launcher]++;
        }
        _ = launcher.StartAsync();
        return new(this, launcher);
    }

    /// <summary>Open leases on the server currently used for <paramref name="options"/>.</summary>
    public int LeaseCount(WhisperServerOptions options)
    {
        lock (gate) return current.TryGetValue(options, out var launcher) && leases.TryGetValue(launcher, out var count) ? count : 0;
    }

    internal void Release(WhisperServerLauncher launcher)
    {
        lock (gate)
        {
            if (!leases.TryGetValue(launcher, out var count)) return;
            if (count > 1) { leases[launcher] = count - 1; return; }
            leases.Remove(launcher);
            if (current.TryGetValue(launcher.Options, out var active) && ReferenceEquals(active, launcher)) current.Remove(launcher.Options);
        }
        launcher.Stop();
    }

    public void Dispose()
    {
        List<WhisperServerLauncher> all;
        lock (gate)
        {
            all = [.. leases.Keys];
            leases.Clear();
            current.Clear();
        }
        foreach (var launcher in all) launcher.Stop();
    }
}

/// <summary>One holder's claim on a shared server; disposing it releases the claim exactly once.</summary>
public sealed class LocalWhisperLease : IDisposable
{
    private readonly LocalWhisperHost host;
    private int released;

    internal LocalWhisperLease(LocalWhisperHost host, WhisperServerLauncher server)
    {
        this.host = host;
        Server = server;
    }

    public WhisperServerLauncher Server { get; }

    /// <summary>Completes once the model is loaded; throws the start failure (<see cref="ChunkTranscriptionException"/>).</summary>
    public Task<Uri> ReadyAsync(CancellationToken cancellationToken = default) => Server.StartAsync().WaitAsync(cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref released, 1) == 0) host.Release(Server);
    }
}
