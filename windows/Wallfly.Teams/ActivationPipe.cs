using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace Wallfly.Teams;

public enum ActivationDelivery
{
    /// <summary>The app accepted the request (it consumed request.json and opens a note).</summary>
    Accepted,
    /// <summary>The app answered but refused (stale/unknown/duplicate request, or busy).</summary>
    Rejected,
    /// <summary>No app listens on the pipe.</summary>
    NoServer,
    /// <summary>The app could not be reached reliably (timeout, broken pipe).</summary>
    Failed,
    /// <summary>The pipe was unreachable and <c>Wallfly.exe --detected-teams</c> was started instead.</summary>
    Launched,
}

/// <summary>
/// Activation hand-over from the helper to the app over <c>\\.\pipe\Wallfly.Activation</c> (current user only).
/// Protocol: one UTF-8 line <c>detected-teams/&lt;UUID&gt;</c>, answered by one line <c>ok</c> or <c>rejected</c>.
/// The pipe only carries the ID; the authority is the file protocol (the app consumes the matching, fresh
/// <c>request.json</c> once), so another local process cannot start a recording through the pipe.
/// </summary>
public sealed class ActivationPipeServer : IAsyncDisposable, IDisposable
{
    public const int MaxMessageLength = 256;
    private readonly string pipeName;
    private readonly Func<Guid, CancellationToken, Task<bool>> handler;
    private readonly CancellationTokenSource stop = new();
    private Task? loop;

    /// <param name="handler">Called for each well-formed message; returns whether the request was accepted.</param>
    public ActivationPipeServer(Func<Guid, CancellationToken, Task<bool>> handler, string pipeName = WallflyPaths.ActivationPipeName)
    {
        this.handler = handler;
        this.pipeName = pipeName;
    }

    public string PipeName => pipeName;

    /// <summary>Raised for each message with the outcome (for logging); never throws into the server.</summary>
    public event Action<string, bool>? Received;

    /// <summary>Starts listening. The first server instance is created synchronously so a client can connect immediately.</summary>
    public void Start()
    {
        if (loop is not null) return;
        var first = Create();
        loop = Task.Run(() => RunAsync(first, stop.Token));
    }

    private NamedPipeServerStream Create() => new(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task RunAsync(NamedPipeServerStream server, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                // Serve this client while the next instance already listens.
                var connected = server;
                server = Create();
                _ = Task.Run(() => ServeAsync(connected, token), CancellationToken.None);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                server = Create();
            }
        }
        await server.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ServeAsync(NamedPipeServerStream connection, CancellationToken token)
    {
        await using (connection.ConfigureAwait(false))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            string message = "";
            var accepted = false;
            try
            {
                message = await ReadLineAsync(connection, timeout.Token).ConfigureAwait(false) ?? "";
                if (DetectionRequest.ParseActivation(message) is { } id)
                {
                    try { accepted = await handler(id, timeout.Token).ConfigureAwait(false); }
                    catch (Exception error) when (error is not OperationCanceledException) { accepted = false; }
                }
                await WriteLineAsync(connection, accepted ? "ok" : "rejected", timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            try { Received?.Invoke(message, accepted); } catch (Exception) { /* logging only */ }
        }
    }

    internal static async Task<string?> ReadLineAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[1];
        var bytes = new List<byte>();
        while (bytes.Count <= MaxMessageLength)
        {
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer[0] == (byte)'\n') return Encoding.UTF8.GetString([.. bytes]).TrimEnd('\r');
            bytes.Add(buffer[0]);
        }
        return bytes.Count == 0 || bytes.Count > MaxMessageLength ? null : Encoding.UTF8.GetString([.. bytes]).TrimEnd('\r');
    }

    internal static async Task WriteLineAsync(Stream stream, string line, CancellationToken token)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        stop.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}

/// <summary>Client side of the activation pipe (used by Wallfly Detector and by a second Wallfly.exe instance).</summary>
public static class ActivationPipeClient
{
    public static async Task<ActivationDelivery> SendAsync(Guid id, TimeSpan timeout, string pipeName = WallflyPaths.ActivationPipeName, CancellationToken cancellationToken = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using (client.ConfigureAwait(false))
        {
            try
            {
                // Connect quickly: an absent server is the common case (app not running), not an error.
                await client.ConnectAsync((int)Math.Clamp(timeout.TotalMilliseconds / 2, 50, 2000), limit.Token).ConfigureAwait(false);
            }
            catch (TimeoutException) { return ActivationDelivery.NoServer; }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return ActivationDelivery.NoServer; }
            catch (IOException) { return ActivationDelivery.NoServer; }
            catch (UnauthorizedAccessException) { return ActivationDelivery.Failed; }
            try
            {
                await ActivationPipeServer.WriteLineAsync(client, new DetectionRequest(id).ActivationMessage, limit.Token).ConfigureAwait(false);
                var answer = await ActivationPipeServer.ReadLineAsync(client, limit.Token).ConfigureAwait(false);
                return answer switch
                {
                    "ok" => ActivationDelivery.Accepted,
                    "rejected" => ActivationDelivery.Rejected,
                    _ => ActivationDelivery.Failed,
                };
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
            {
                return cancellationToken.IsCancellationRequested ? throw new OperationCanceledException(cancellationToken) : ActivationDelivery.Failed;
            }
        }
    }
}

/// <summary>"Take notes" hand-over: the pipe first; if no app listens, start <c>Wallfly.exe --detected-teams &lt;UUID&gt;</c>.</summary>
public static class DetectedTeamsActivation
{
    public static async Task<ActivationDelivery> ActivateAsync(Guid id, string appDirectory, TimeSpan timeout,
        string pipeName = WallflyPaths.ActivationPipeName, Func<ProcessStartInfo, bool>? start = null)
    {
        var delivery = await ActivationPipeClient.SendAsync(id, timeout, pipeName).ConfigureAwait(false);
        if (delivery != ActivationDelivery.NoServer) return delivery;
        var executable = Path.Combine(appDirectory, WallflyPaths.AppExecutableName);
        if (!File.Exists(executable)) return ActivationDelivery.NoServer;
        var info = LaunchInfo(id, executable);
        start ??= StartProcess;
        return start(info) ? ActivationDelivery.Launched : ActivationDelivery.Failed;
    }

    public static ProcessStartInfo LaunchInfo(Guid id, string executable)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable) ?? "" };
        info.ArgumentList.Add(WallflyPaths.DetectedTeamsSwitch);
        info.ArgumentList.Add(id.ToString("D").ToUpperInvariant());
        return info;
    }

    /// <summary>The UUID after <c>--detected-teams</c> in an app command line, if present and well-formed.</summary>
    public static Guid? FromArguments(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i < arguments.Count - 1; i++)
            if (arguments[i].Equals(WallflyPaths.DetectedTeamsSwitch, StringComparison.OrdinalIgnoreCase))
                return Guid.TryParseExact(arguments[i + 1], "D", out var id) ? id : null;
        foreach (var argument in arguments)
            if (argument.StartsWith(DetectionRequest.UrlScheme + "://", StringComparison.OrdinalIgnoreCase) && DetectionRequest.ParseActivation(argument) is { } fromUrl)
                return fromUrl;
        return null;
    }

    private static bool StartProcess(ProcessStartInfo info)
    {
        try
        {
            using var process = Process.Start(info);
            return process is not null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return false; }
    }
}
