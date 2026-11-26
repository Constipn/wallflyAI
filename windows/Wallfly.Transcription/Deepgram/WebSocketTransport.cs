using System.Text;

namespace Wallfly.Transcription.Deepgram;

public enum WebSocketFrameKind { Text, Binary }

/// <summary>One complete WebSocket message.</summary>
public readonly record struct WebSocketFrame(WebSocketFrameKind Kind, ReadOnlyMemory<byte> Data)
{
    public static WebSocketFrame FromText(string text) => new(WebSocketFrameKind.Text, Encoding.UTF8.GetBytes(text));
    public static WebSocketFrame FromBinary(ReadOnlyMemory<byte> data) => new(WebSocketFrameKind.Binary, data);
    public string Text => Encoding.UTF8.GetString(Data.Span);
}

/// <summary>
/// What a socket is opened with. <see cref="ToString"/> deliberately omits the credential so the
/// request can never reach a log or a message by accident.
/// </summary>
public sealed class WebSocketRequest(Uri uri, string authorization)
{
    public Uri Uri { get; } = uri;
    /// <summary>Value of the <c>Authorization</c> header, e.g. <c>Token &lt;key&gt;</c>.</summary>
    public string Authorization { get; } = authorization;
    public override string ToString() => Uri.GetLeftPart(UriPartial.Path);
}

/// <summary>
/// The socket boundary of the streaming pipeline (port of the macOS <c>DeepgramTransport</c>):
/// replaceable in tests, and the only place that talks to the network. Callers issue at most one
/// send and one receive at a time. <see cref="Close"/> returns immediately and abandons pending work.
/// </summary>
public interface IWebSocket
{
    Task ConnectAsync(CancellationToken cancellationToken);
    Task SendAsync(WebSocketFrame frame, CancellationToken cancellationToken);
    /// <summary>The next complete message. A close frame or a broken connection fails the task.</summary>
    Task<WebSocketFrame> ReceiveAsync(CancellationToken cancellationToken);
    void Close();
}

public interface IWebSocketFactory
{
    IWebSocket Create(WebSocketRequest request);
}

/// <summary>
/// The HTTP upgrade was answered with an error status. 400/401/402/403/404/422 are terminal for
/// Deepgram (access or configuration), so the pipeline pauses instead of retrying.
/// </summary>
public sealed class DeepgramTransportException(int status)
    : Exception($"The WebSocket upgrade was rejected with HTTP {status}.")
{
    public static readonly IReadOnlySet<int> TerminalStatuses = new HashSet<int> { 400, 401, 402, 403, 404, 422 };
    public int Status { get; } = status;
    public bool IsTerminal => TerminalStatuses.Contains(Status);
}
