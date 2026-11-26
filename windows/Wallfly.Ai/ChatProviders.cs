namespace Wallfly.Ai;

/// <summary>Creates the provider for the configured service and owns the constants of <c>makeRequest</c>.</summary>
public static class ChatProviders
{
    public const string AnthropicEndpoint = "https://api.anthropic.com/v1/messages";
    public const string OpenAiEndpoint = "https://api.openai.com/v1/chat/completions";
    public const string AnthropicVersion = "2026-10-21";

    /// <summary>Refuses redirects and cookies so keys and transcripts cannot move to another host.</summary>
    public static HttpMessageHandler CreateHandler() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };

    public static HttpChatProvider Create(ChatProviderSettings settings, HttpMessageHandler? handler = null) => settings.Kind switch
    {
        ChatProviderKind.Anthropic => new AnthropicProvider(settings, handler),
        ChatProviderKind.OpenAi => new OpenAiProvider(settings, handler),
        _ => new CustomOpenAiProvider(settings, handler)
    };

    /// <summary>Port of <c>NotesEnhancer.endpointIssue</c>: HTTPS, or HTTP on localhost only, and no embedded credentials.</summary>
    public static string? EndpointIssue(string? endpoint)
    {
        if (string.IsNullOrEmpty(endpoint) || endpoint.Trim().Length != endpoint.Length
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            return AiMessages.EndpointWithCredentials;
        var local = uri.Host is "localhost" or "127.0.0.1" or "::1" or "[::1]";
        return uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && local ? null : AiMessages.EndpointNotHttps;
    }
}

/// <summary>A curated model of one provider.</summary>
public sealed record ModelChoice(string Id, string Name);

/// <summary>
/// Port of <c>SummaryModels</c>: curated text models, checked against the official provider catalogs on 2026-10-29.
/// Existing or manually entered model IDs stay selectable; availability depends on the user's API account.
/// </summary>
public static class ModelCatalog
{
    public const string AnthropicKeyUrl = "https://platform.claude.com/settings/keys";
    public const string OpenAiKeyUrl = "https://platform.openai.com/api-keys";

    private static readonly ModelChoice[] Anthropic =
    [
        new("claude-sonnet-5-5", "Claude Sonnet 5.5"), new("claude-haiku-5-5", "Claude Haiku 5.5"),
        new("claude-opus-5-5", "Claude Opus 5.5"), new("claude-fable-5-1", "Claude Fable 5.1")
    ];

    private static readonly ModelChoice[] OpenAi =
    [
        new("gpt-6-luna", "GPT-6 Luna"), new("gpt-6.1-terra", "GPT-6.1 Terra"),
        new("gpt-6.1-sol", "GPT-6.1 Sol"), new("gpt-6-astra", "GPT-6 Astra")
    ];

    /// <summary>The list for the model picker; empty for the custom API and for "Off" (null).</summary>
    public static IReadOnlyList<ModelChoice> Choices(ChatProviderKind? provider) => provider switch
    {
        ChatProviderKind.Anthropic => Anthropic,
        ChatProviderKind.OpenAi => OpenAi,
        _ => []
    };

    /// <summary>The first curated model; empty for the custom API and for "Off".</summary>
    public static string DefaultModel(ChatProviderKind? provider) => Choices(provider).FirstOrDefault()?.Id ?? "";

    /// <summary>
    /// Port of <c>MeetingStore.selectEnhancementProvider</c>: remembers the model of the provider being left
    /// (keyed by its stored raw value) and returns the remembered or default model of the new one.
    /// </summary>
    public static string ModelAfterSwitch(IDictionary<string, string> remembered, string fromKey, string currentModel, string toKey, ChatProviderKind? to)
    {
        remembered[fromKey] = currentModel;
        return remembered.TryGetValue(toKey, out var model) ? model : DefaultModel(to);
    }
}
