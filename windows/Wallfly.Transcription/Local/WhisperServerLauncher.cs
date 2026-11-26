using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Wallfly.Transcription.Local;

/// <summary>User-visible wording of the local whisper-server path (macOS <c>LocalWhisperServer</c>, adapted to Windows).</summary>
public static class LocalWhisperMessages
{
    public const string ServerNotFound = "The local Whisper server was not found (whisper\\whisper-server.exe next to Wallfly). Reinstall Wallfly to restore it.";
    public const string ModelNotFound = "The local Whisper Turbo model was not found. Choose the downloaded file ggml-large-v3-turbo.bin in Settings.";
    public const string LaunchFailed = "The local Whisper server could not be started.";
    public const string NoPort = "Could not choose a local Whisper port.";
    public const string LoadTimeout = "Loading the local Whisper model took too long. Check that whisper-server.exe and the model are available.";
    public const string NotRunning = "The local Whisper server is no longer running. Start the recording again.";
    public const string MustStayLocal = "Audio for local Whisper must stay on this PC.";
    public const string Unreadable = "The local Whisper model returned an unreadable transcript.";
    public const string TimedOut = "The local Whisper server did not answer in time.";
    public const string ConnectionFailed = "The connection to the local Whisper server failed.";

    public static string Exited(int exitCode) =>
        $"The Whisper server exited while loading the model (exit code {exitCode}). Check the model path and available memory.";

    public static string HttpFailed(int status) =>
        $"The local Whisper model could not transcribe this segment (HTTP {status}).";
}

/// <summary>Where the server and its models are, and whether word times are needed (which disables VAD).</summary>
public sealed record WhisperServerOptions
{
    public const string ExecutableName = "whisper-server.exe";
    public const string DefaultModelFileName = "ggml-large-v3-turbo.bin";
    public const string VadModelFileName = "ggml-silero-v5.1.2.bin";

    /// <summary>Empty: <c>whisper\whisper-server.exe</c> next to the app.</summary>
    public string? ExecutablePath { get; init; }
    /// <summary>Empty: <c>models\whisper\ggml-large-v3-turbo.bin</c> next to the app.</summary>
    public string? ModelPath { get; init; }
    /// <summary>Empty: <c>ggml-silero-v5.1.2.bin</c> next to the model, else in <c>models\whisper</c> next to the app.</summary>
    public string? VadModelPath { get; init; }
    /// <summary>Teams speaker names: VAD would compress silence and shift token times, so it stays off.</summary>
    public bool WordTimestamps { get; init; }
    public TimeSpan ReadinessTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan ProbeInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    public static string DefaultExecutablePath => Path.Combine(AppContext.BaseDirectory, "whisper", ExecutableName);
    public static string DefaultModelPath => Path.Combine(AppContext.BaseDirectory, "models", "whisper", DefaultModelFileName);

    public string ResolvedExecutablePath => Resolve(ExecutablePath) ?? DefaultExecutablePath;
    public string ResolvedModelPath => Resolve(ModelPath) ?? DefaultModelPath;

    /// <summary>The VAD model to pass, or null when none exists.</summary>
    public string? ResolvedVadModelPath
    {
        get
        {
            if (Resolve(VadModelPath) is string configured) return File.Exists(configured) ? configured : null;
            var nextToModel = Path.Combine(Path.GetDirectoryName(ResolvedModelPath) ?? "", VadModelFileName);
            if (File.Exists(nextToModel)) return nextToModel;
            var bundled = Path.Combine(AppContext.BaseDirectory, "models", "whisper", VadModelFileName);
            return File.Exists(bundled) ? bundled : null;
        }
    }

    private static string? Resolve(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
}

/// <summary>A launched server process; the seam lets tests run the launcher without whisper-server.exe.</summary>
public interface IWhisperProcess : IDisposable
{
    bool HasExited { get; }
    int ExitCode { get; }
    /// <summary>Ends the process and everything it started.</summary>
    void Kill();
}

public interface IWhisperProcessStarter
{
    IWhisperProcess Start(string executablePath, IReadOnlyList<string> arguments);
}

/// <summary>
/// Starts and owns one <c>whisper-server.exe</c> (port of the macOS <c>LocalWhisperServer.prepare</c> minus the
/// OpenWhispr sharing, which Windows omits): a free loopback port, a random <c>--request-path
/// /meetingnotes-&lt;uuid&gt;</c>, the macOS argument list, readiness by <c>GET &lt;base&gt;/</c> every 100 ms
/// for up to 60 s (aborted as soon as the process exits), and <c>POST &lt;base&gt;/inference</c> afterwards.
/// A failed start stops the process. The process is killed with its tree on <see cref="Stop"/>, at process
/// exit, and, through a kill-on-close job object, even if Wallfly itself crashes.
/// </summary>
public sealed class WhisperServerLauncher : IDisposable
{
    private static readonly ConcurrentDictionary<WhisperServerLauncher, byte> Live = new();

    static WhisperServerLauncher() => AppDomain.CurrentDomain.ProcessExit += (_, _) => StopAll();

    private readonly object gate = new();
    private readonly IWhisperProcessStarter starter;
    private readonly HttpClient http;
    private readonly TimeProvider time;
    private readonly Func<int> choosePort;
    private readonly CancellationTokenSource stopping = new();
    private Task<Uri>? starting;
    private IWhisperProcess? process;
    private Uri? inference;
    private bool stopped;

    public WhisperServerLauncher(WhisperServerOptions options, IWhisperProcessStarter? starter = null, HttpMessageHandler? handler = null,
        TimeProvider? time = null, Func<int>? port = null)
    {
        Options = options;
        this.starter = starter ?? new SystemWhisperProcessStarter();
        this.time = time ?? TimeProvider.System;
        choosePort = port ?? FreeLoopbackPort;
        // Audio stays on loopback: no proxy, no redirects, no cookies.
        http = new(handler ?? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public WhisperServerOptions Options { get; }
    /// <summary>The argument list of the launch (null before it).</summary>
    public IReadOnlyList<string>? Arguments { get; private set; }
    /// <summary><c>http://127.0.0.1:&lt;port&gt;/meetingnotes-&lt;uuid&gt;/</c> once launched.</summary>
    public Uri? BaseUri { get; private set; }
    public Uri? InferenceUri { get { lock (gate) return inference; } }
    public bool IsRunning { get { lock (gate) return !stopped && inference != null && process is { HasExited: false }; } }
    public bool HasStopped { get { lock (gate) return stopped; } }
    /// <summary>The last lines the server wrote to stderr (diagnostics only, never shown to the user).</summary>
    public IReadOnlyList<string> RecentOutput { get { lock (gate) return (process as SystemWhisperProcess)?.RecentOutput ?? []; } }

    /// <summary><c>min(8, max(2, cores / 2))</c>.</summary>
    public static int DefaultThreads => Math.Min(8, Math.Max(2, Environment.ProcessorCount / 2));

    /// <summary>
    /// The macOS argument list: the model path is one literal argument, the server binds loopback only, and
    /// <c>--vad --vad-model</c> is added only if that file exists and no word times are needed.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(string modelPath, int port, string prefix, bool wordTimestamps,
        string? vadModelPath, int? threads = null)
    {
        var arguments = new List<string>
        {
            "--model", modelPath, "--host", "127.0.0.1", "--port", port.ToString(CultureInfo.InvariantCulture),
            "--language", "auto", "--threads", (threads ?? DefaultThreads).ToString(CultureInfo.InvariantCulture),
            "--best-of", "1", "--no-fallback", "--max-len", "4096", "--request-path", prefix
        };
        // VAD can compress silence and shift token timestamps; timed (Teams) mode uses the original chunk
        // timeline, and RMS chunking still skips silence.
        if (!wordTimestamps && !string.IsNullOrEmpty(vadModelPath) && File.Exists(vadModelPath))
            arguments.AddRange(["--vad", "--vad-model", vadModelPath]);
        return arguments;
    }

    public static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    /// <summary>Starts the server once (later calls share the same task) and completes with the inference URI.</summary>
    public Task<Uri> StartAsync()
    {
        lock (gate)
        {
            if (starting != null) return starting;
            starting = Task.Run(() => LaunchAsync(stopping.Token));
            _ = starting.ContinueWith(static done => _ = done.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return starting;
        }
    }

    /// <summary>Any failed start stops the launcher (and its process), so the host replaces it next time.</summary>
    private async Task<Uri> LaunchAsync(CancellationToken token)
    {
        try { return await LaunchCoreAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException error)
        {
            Stop();
            throw new ChunkTranscriptionException(LocalWhisperMessages.NotRunning, inner: error);
        }
        catch
        {
            Stop();
            throw;
        }
    }

    private async Task<Uri> LaunchCoreAsync(CancellationToken token)
    {
        var executable = Options.ResolvedExecutablePath;
        var model = Options.ResolvedModelPath;
        if (!File.Exists(executable)) throw new ChunkTranscriptionException(LocalWhisperMessages.ServerNotFound);
        if (!File.Exists(model)) throw new ChunkTranscriptionException(LocalWhisperMessages.ModelNotFound);
        int port;
        try { port = choosePort(); }
        catch (SocketException error) { throw new ChunkTranscriptionException(LocalWhisperMessages.NoPort, inner: error); }
        var prefix = "/meetingnotes-" + Guid.NewGuid().ToString("D");
        var baseUri = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}{prefix}/");
        var arguments = BuildArguments(model, port, prefix, Options.WordTimestamps, Options.ResolvedVadModelPath);
        IWhisperProcess launched;
        lock (gate)
        {
            if (stopped) throw new ChunkTranscriptionException(LocalWhisperMessages.NotRunning);
            try { launched = starter.Start(executable, arguments); }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
            { throw new ChunkTranscriptionException(LocalWhisperMessages.LaunchFailed, inner: error); }
            process = launched;
            Arguments = arguments;
            BaseUri = baseUri;
        }
        Live.TryAdd(this, 0);
        var started = time.GetTimestamp();
        while (time.GetElapsedTime(started) < Options.ReadinessTimeout)
        {
            token.ThrowIfCancellationRequested();
            if (launched.HasExited) throw new ChunkTranscriptionException(LocalWhisperMessages.Exited(launched.ExitCode));
            if (await ProbeAsync(baseUri, token).ConfigureAwait(false))
            {
                var uri = new Uri(baseUri, "inference");
                lock (gate)
                {
                    if (stopped) throw new ChunkTranscriptionException(LocalWhisperMessages.NotRunning);
                    inference = uri;
                }
                return uri;
            }
            await Task.Delay(Options.ProbeInterval, time, token).ConfigureAwait(false);
        }
        throw new ChunkTranscriptionException(LocalWhisperMessages.LoadTimeout);
    }

    /// <summary>One readiness probe with a 0.4 s budget; the model is loaded before the server binds its port.</summary>
    private async Task<bool> ProbeAsync(Uri baseUri, CancellationToken token)
    {
        using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(.4), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, probe.Token);
        try
        {
            using var response = await http.GetAsync(baseUri, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }

    /// <summary>Kills the server and its process tree; idempotent. A stopped launcher is never restarted.</summary>
    public void Stop()
    {
        IWhisperProcess? running;
        lock (gate)
        {
            stopped = true;
            inference = null;
            running = process;
            process = null;
        }
        try { stopping.Cancel(); } catch (AggregateException) { }
        Live.TryRemove(this, out _);
        if (running == null) return;
        try { if (!running.HasExited) running.Kill(); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException) { }
        running.Dispose();
    }

    /// <summary>Stops every server this process launched (app exit).</summary>
    public static void StopAll()
    {
        foreach (var launcher in Live.Keys) launcher.Stop();
    }

    public void Dispose() => Stop();
}

internal sealed class SystemWhisperProcessStarter : IWhisperProcessStarter
{
    public IWhisperProcess Start(string executablePath, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? ""
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        var process = Process.Start(info) ?? throw new InvalidOperationException("whisper-server.exe did not start.");
        ChildProcessJob.TryAssign(process);
        process.StandardInput.Close();
        return new SystemWhisperProcess(process);
    }
}

internal sealed class SystemWhisperProcess : IWhisperProcess
{
    private const int KeptLines = 40;
    private readonly Process process;
    private readonly ConcurrentQueue<string> output = new();

    public SystemWhisperProcess(Process process)
    {
        this.process = process;
        // Both pipes are drained so a chatty server never blocks on a full buffer.
        process.OutputDataReceived += (_, line) => Keep(line.Data);
        process.ErrorDataReceived += (_, line) => Keep(line.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public IReadOnlyList<string> RecentOutput => output.ToArray();

    public bool HasExited
    {
        get
        {
            try { return process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }
    }

    public int ExitCode
    {
        get
        {
            try { return process.ExitCode; }
            catch (InvalidOperationException) { return -1; }
        }
    }

    public void Kill()
    {
        process.Kill(entireProcessTree: true);
        process.WaitForExit(2000);
    }

    public void Dispose() => process.Dispose();

    private void Keep(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        output.Enqueue(line);
        while (output.Count > KeptLines) output.TryDequeue(out _);
    }
}
