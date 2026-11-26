using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Wallfly.Ai;

/// <summary>
/// The HTTP part of <c>NotesEnhancer.generate</c>/<c>makeRequest</c>: validation before any request, no redirects,
/// no cookies, a 600 s budget above 4 000 response tokens and 240 s otherwise, and a status/network classification
/// that never exposes the response body.
/// </summary>
public abstract class HttpChatProvider : IChatProvider, IDisposable
{
    private readonly HttpClient http;

    protected HttpChatProvider(ChatProviderSettings settings, HttpMessageHandler? handler)
    {
        Settings = settings;
        http = new HttpClient(handler ?? ChatProviders.CreateHandler()) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    public ChatProviderSettings Settings { get; }

    /// <summary>The fixed service address, or the configured one for the custom API.</summary>
    public abstract string Endpoint { get; }

    /// <summary>OpenAI and Anthropic need a key; the custom API sends <c>Authorization</c> only when one is set.</summary>
    protected abstract bool RequiresKey { get; }

    protected abstract void AddHeaders(HttpRequestMessage message, string key);

    /// <summary>Port of <c>NotesEnhancer.requestBody</c>.</summary>
    public abstract byte[] RequestBody(ChatRequest request);

    /// <summary>Port of <c>NotesEnhancer.decodeResponse</c>, without the finish check (see <see cref="ChatResult.EnsureFinished"/>).</summary>
    public abstract ChatResult DecodeResponse(byte[] body);

    /// <summary>Test seam for the deadline; null uses <see cref="RequestTimeout"/>.</summary>
    internal TimeSpan? TimeoutOverride { get; set; }

    /// <summary>Test seam for the offline check that the macOS URL loading system performs itself.</summary>
    internal Func<bool> NetworkAvailable { get; set; } = NetworkInterface.GetIsNetworkAvailable;

    public static TimeSpan RequestTimeout(int maximumResponseTokens) => TimeSpan.FromSeconds(maximumResponseTokens > 4000 ? 600 : 240);

    /// <summary>Port of <c>NotesEnhancer.makeRequest</c>: every configuration problem fails before the network is used.</summary>
    public HttpRequestMessage CreateRequest(ChatRequest request)
    {
        var endpoint = Endpoint;
        if (ChatProviders.EndpointIssue(endpoint) is { } issue) throw AiError.Configuration(issue);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) throw AiError.Configuration(AiMessages.InvalidAddress);
        if (string.IsNullOrWhiteSpace(Settings.Model)) throw AiError.Configuration(AiMessages.ChooseModel);
        var key = (Settings.ApiKey ?? "").Trim();
        if (RequiresKey && key.Length == 0) throw AiError.Configuration(AiMessages.EnterKey(Settings.Kind));
        var message = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(RequestBody(request)) };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        AddHeaders(message, key);
        return message;
    }

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        using var message = CreateRequest(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeoutOverride ?? RequestTimeout(request.MaximumResponseTokens));
        byte[] body;
        try
        {
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            // Redirects are not followed, so a 3xx arrives here and is reported like any other status.
            if (status is < 200 or >= 300) throw AiError.Http(status);
            body = await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException failure) when (!cancellationToken.IsCancellationRequested)
        {
            throw AiError.Network(NetworkFailure.TimedOut, failure);
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException)
        {
            throw AiError.Network(AiError.Classify(failure, NetworkAvailable()), failure);
        }
        try { return DecodeResponse(body); }
        catch (Exception failure) when (failure is JsonException or InvalidOperationException and not AiError or KeyNotFoundException or FormatException)
        {
            throw AiError.Transient(AiMessages.UnreadableData, failure);
        }
    }

    /// <summary>Request bodies keep non-ASCII text readable; the escaping is irrelevant to the services.</summary>
    protected static byte[] Json(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>A required JSON string, or null when absent or JSON null; any other type is a decoding error.</summary>
    protected static string? OptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw new JsonException(name);
    }

    public void Dispose()
    {
        http.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary><c>POST https://api.anthropic.com/v1/messages</c> with the Messages schema; JSON is requested through the prompt only.</summary>
public sealed class AnthropicProvider(ChatProviderSettings settings, HttpMessageHandler? handler = null) : HttpChatProvider(settings, handler)
{
    public override string Endpoint => ChatProviders.AnthropicEndpoint;
    protected override bool RequiresKey => true;

    protected override void AddHeaders(HttpRequestMessage message, string key)
    {
        message.Headers.TryAddWithoutValidation("x-api-key", key);
        message.Headers.TryAddWithoutValidation("anthropic-version", ChatProviders.AnthropicVersion);
        var workspace = (Settings.AnthropicWorkspaceId ?? "").Trim();
        if (workspace.Length > 0) message.Headers.TryAddWithoutValidation("anthropic-workspace-id", workspace);
    }

    /// <summary>No <c>store</c>, <c>response_format</c>, <c>temperature</c>, <c>top_p</c> or <c>top_k</c>: this API version rejects sampling overrides.</summary>
    public override byte[] RequestBody(ChatRequest request) => Json(writer =>
    {
        writer.WriteString("model", Settings.Model);
        writer.WriteNumber("max_tokens", request.MaximumResponseTokens);
        writer.WriteBoolean("stream", false);
        writer.WriteString("system", request.Instructions);
        writer.WriteStartArray("messages");
        writer.WriteStartObject();
        writer.WriteString("role", "user");
        writer.WriteString("content", request.Prompt);
        writer.WriteEndObject();
        writer.WriteEndArray();
    });

    /// <summary>Joins the <c>text</c> blocks; anything but <c>stop_reason == "end_turn"</c> is unfinished.</summary>
    public override ChatResult DecodeResponse(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            throw new JsonException("content");
        var texts = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) throw new JsonException("block");
            var type = OptionalString(block, "type") ?? throw new JsonException("type");
            var text = OptionalString(block, "text");
            if (type == "text" && text != null) texts.Add(text);
        }
        var stopReason = OptionalString(root, "stop_reason");
        return new(string.Join("\n", texts), stopReason == "end_turn", stopReason, ChatProviderKind.Anthropic);
    }
}

/// <summary>The Chat Completions schema shared by OpenAI and OpenAI-compatible services.</summary>
public abstract class OpenAiCompatibleProvider(ChatProviderSettings settings, HttpMessageHandler? handler) : HttpChatProvider(settings, handler)
{
    protected abstract void AddBudgetAndSampling(Utf8JsonWriter writer, ChatRequest request);

    protected override void AddHeaders(HttpRequestMessage message, string key)
    {
        if (key.Length > 0) message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
    }

    public override byte[] RequestBody(ChatRequest request) => Json(writer =>
    {
        writer.WriteString("model", Settings.Model);
        writer.WriteBoolean("stream", false);
        writer.WriteBoolean("store", false);
        writer.WriteStartArray("messages");
        writer.WriteStartObject(); writer.WriteString("role", "system"); writer.WriteString("content", request.Instructions); writer.WriteEndObject();
        writer.WriteStartObject(); writer.WriteString("role", "user"); writer.WriteString("content", request.Prompt); writer.WriteEndObject();
        writer.WriteEndArray();
        AddBudgetAndSampling(writer, request);
    });

    /// <summary>A missing <c>finish_reason</c> counts as finished, as in the macOS app; any other value than <c>stop</c> does not.</summary>
    public override ChatResult DecodeResponse(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            throw new JsonException("choices");
        string? text = null, reason = null;
        var first = true;
        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.ValueKind != JsonValueKind.Object || !choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                throw new JsonException("message");
            var content = OptionalString(message, "content");
            var finish = OptionalString(choice, "finish_reason");
            if (first) { text = content; reason = finish; first = false; }
        }
        return new(text ?? "", reason == null || reason == "stop", reason, Settings.Kind);
    }
}

/// <summary><c>POST https://api.openai.com/v1/chat/completions</c>: <c>max_completion_tokens</c>, JSON mode on the main call, <c>temperature</c> only for gpt-4*.</summary>
public sealed class OpenAiProvider(ChatProviderSettings settings, HttpMessageHandler? handler = null) : OpenAiCompatibleProvider(settings, handler)
{
    public override string Endpoint => ChatProviders.OpenAiEndpoint;
    protected override bool RequiresKey => true;

    protected override void AddBudgetAndSampling(Utf8JsonWriter writer, ChatRequest request)
    {
        writer.WriteNumber("max_completion_tokens", request.MaximumResponseTokens);
        if (request.JsonResponse)
        {
            writer.WriteStartObject("response_format");
            writer.WriteString("type", "json_object");
            writer.WriteEndObject();
        }
        // New reasoning models don't universally accept custom sampling parameters.
        if (Settings.Model.StartsWith("gpt-4", StringComparison.Ordinal)) writer.WriteNumber("temperature", 0.2);
    }
}

/// <summary>Any OpenAI-compatible address: HTTPS (HTTP on localhost only), Bearer only with a key, <c>max_tokens</c> and <c>temperature</c> 0.2.</summary>
public sealed class CustomOpenAiProvider(ChatProviderSettings settings, HttpMessageHandler? handler = null) : OpenAiCompatibleProvider(settings, handler)
{
    public override string Endpoint => Settings.Endpoint ?? "";
    protected override bool RequiresKey => false;

    protected override void AddBudgetAndSampling(Utf8JsonWriter writer, ChatRequest request)
    {
        writer.WriteNumber("max_tokens", request.MaximumResponseTokens);
        writer.WriteNumber("temperature", 0.2);
    }
}
