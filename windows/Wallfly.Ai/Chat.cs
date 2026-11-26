namespace Wallfly.Ai;

/// <summary>The three hosted enhancement services. "Off" is not a provider; callers model it as null.</summary>
public enum ChatProviderKind { Anthropic, OpenAi, Custom }

/// <summary>Everything one provider needs for a request. Keys are never written to settings.json.</summary>
public sealed record ChatProviderSettings(
    ChatProviderKind Kind,
    string Model,
    string ApiKey = "",
    string Endpoint = "",
    string AnthropicWorkspaceId = "");

/// <summary>One completion: system instructions plus a single user prompt (port of <c>NotesEnhancer.generate</c>).</summary>
public sealed record ChatRequest(string Instructions, string Prompt, int MaximumResponseTokens = 4000, bool JsonResponse = false);

/// <summary>
/// The decoded answer. <see cref="FinishedCleanly"/> is false when Anthropic did not stop with <c>end_turn</c> or
/// OpenAI reported a <c>finish_reason</c> other than <c>stop</c>; such text is never saved.
/// </summary>
public sealed record ChatResult(string Text, bool FinishedCleanly, string? StopReason = null, ChatProviderKind Provider = ChatProviderKind.OpenAi)
{
    /// <summary>A finished answer, for fakes and adapters.</summary>
    public static ChatResult Complete(string text, ChatProviderKind provider = ChatProviderKind.OpenAi) =>
        new(text, true, provider == ChatProviderKind.Anthropic ? "end_turn" : "stop", provider);

    /// <summary>Throws the provider-specific message of <c>decodeResponse</c> for a truncated or refused answer.</summary>
    public void EnsureFinished()
    {
        if (FinishedCleanly) return;
        throw AiError.Transient(Provider == ChatProviderKind.Anthropic
            ? AiMessages.ClaudeIncomplete(StopReason)
            : AiMessages.ModelIncomplete(StopReason ?? "unknown"));
    }
}

/// <summary>One text completion. Providers never fall back to each other and never touch saved notes.</summary>
public interface IChatProvider
{
    Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken);
}

public static class ChatProviderExtensions
{
    /// <summary>
    /// Port of <c>NotesEnhancer.generate</c>: the answer must be finished and non-empty, otherwise an
    /// <see cref="AiError"/> is thrown and nothing may be saved.
    /// </summary>
    public static async Task<string> GenerateAsync(this IChatProvider provider, ChatRequest request, CancellationToken cancellationToken)
    {
        var result = await provider.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        result.EnsureFinished();
        return NotesEnhancer.ValidateOutput(result.Text);
    }
}

public static class ChatProviderNames
{
    /// <summary>The picker label of <c>EnhancementProvider.displayName</c>.</summary>
    public static string DisplayName(ChatProviderKind kind) => kind switch
    {
        ChatProviderKind.Anthropic => "Anthropic",
        ChatProviderKind.OpenAi => "OpenAI",
        _ => "Custom API (OpenAI-compatible)"
    };
}
