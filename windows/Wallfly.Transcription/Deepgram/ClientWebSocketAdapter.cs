using System.Buffers;
using System.Net;
using System.Net.WebSockets;

namespace Wallfly.Transcription.Deepgram;

/// <summary>
/// Production <see cref="IWebSocket"/> over <see cref="ClientWebSocket"/> (port of the macOS
/// <c>DeepgramWebSocket</c>): <c>Authorization</c> header, 10 s connect timeout, no redirects, no
/// cookies, no proxy credentials. An HTTP status of 400 or more on the upgrade becomes a
/// <see cref="DeepgramTransportException"/>; everything else is an ordinary transport failure.
/// </summary>
public sealed class ClientWebSocketAdapter : IWebSocket
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    /// <summary>Upper bound of one received message; Deepgram results are a few kilobytes.</summary>
    public const int MaximumMessageBytes = 4 * 1024 * 1024;

    private readonly WebSocketRequest request;
    private readonly HttpMessageInvoker invoker;
    private readonly CancellationTokenSource closing = new();
    private readonly ClientWebSocket socket = new();
    private int closed;

    public ClientWebSocketAdapter(WebSocketRequest request, HttpMessageInvoker? invoker = null)
    {
        this.request = request;
        this.invoker = invoker ?? SharedInvoker;
        socket.Options.SetRequestHeader("Authorization", request.Authorization);
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
    }

    /// <summary>One handler for all sockets: never follows a redirect, so the credential never reaches another host.</summary>
    public static readonly HttpMessageInvoker SharedInvoker = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = ConnectTimeout,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }, disposeHandler: false);

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closing.Token);
        deadline.CancelAfter(ConnectTimeout);
        try
        {
            await socket.ConnectAsync(request.Uri, invoker, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is WebSocketException or HttpRequestException or OperationCanceledException)
        {
            // 3xx is reported too: the redirect is never followed, and only 4xx statuses are terminal.
            var status = (int)socket.HttpStatusCode;
            if (status >= 300) throw new DeepgramTransportException(status);
            if (error is OperationCanceledException && !cancellationToken.IsCancellationRequested && !closing.IsCancellationRequested)
                throw new TimeoutException("The WebSocket connection was not established within 10 seconds.");
            throw;
        }
    }

    public Task SendAsync(WebSocketFrame frame, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref closed) != 0) return Task.FromException(new WebSocketException(WebSocketError.InvalidState));
        var type = frame.Kind == WebSocketFrameKind.Text ? WebSocketMessageType.Text : WebSocketMessageType.Binary;
        return SendCoreAsync(frame.Data, type, cancellationToken);
    }

    private async Task SendCoreAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closing.Token);
        await socket.SendAsync(data, type, endOfMessage: true, linked.Token).ConfigureAwait(false);
    }

    public async Task<WebSocketFrame> ReceiveAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closing.Token);
        var buffer = new ArrayBufferWriter<byte>(8192);
        while (true)
        {
            var memory = buffer.GetMemory(8192);
            var result = await socket.ReceiveAsync(memory, linked.Token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "The server closed the connection.");
            buffer.Advance(result.Count);
            if (buffer.WrittenCount > MaximumMessageBytes)
                throw new WebSocketException(WebSocketError.Faulted, "The server message is too large.");
            if (result.EndOfMessage)
                return new(result.MessageType == WebSocketMessageType.Text ? WebSocketFrameKind.Text : WebSocketFrameKind.Binary, buffer.WrittenMemory.ToArray());
        }
    }

    /// <summary>Abandons the connection at once (the macOS port cancels with "going away"); pending operations fail.</summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        try { closing.Cancel(); } catch (ObjectDisposedException) { }
        socket.Abort();
        socket.Dispose();
    }

    /// <summary>For diagnostics in tests: the status of the upgrade response, 0 before it arrived.</summary>
    public HttpStatusCode UpgradeStatus => socket.HttpStatusCode;
}

/// <summary>Creates <see cref="ClientWebSocketAdapter"/> instances.</summary>
public sealed class ClientWebSocketFactory(HttpMessageInvoker? invoker = null) : IWebSocketFactory
{
    public static readonly ClientWebSocketFactory Default = new();
    public IWebSocket Create(WebSocketRequest request) => new ClientWebSocketAdapter(request, invoker);
}
