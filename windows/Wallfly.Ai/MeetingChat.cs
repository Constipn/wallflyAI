namespace Wallfly.Ai;

/// <summary>One earlier message of a meeting chat: the user's question or the model's answer.</summary>
public sealed record MeetingChatTurn(bool FromUser, string Text);

/// <summary>Everything a meeting chat question may read; like <see cref="EnhancementInput"/>, no persistence models.</summary>
/// <param name="TranscriptText">Lines <c>[mm:ss] Name: Text</c>, as the enhancer reads them.</param>
/// <param name="History">The earlier messages of this chat, oldest first.</param>
public sealed record MeetingChatInput(string Title, string Notes, string TranscriptText, IReadOnlyList<MeetingChatTurn> History, string Question);

/// <summary>
/// Windows addition: answers a question about one finished meeting from the user's typed notes and the transcript.
/// <see cref="IChatProvider"/> is single-turn, so the earlier messages are flattened into a conversation block of the
/// prompt. The provider and model are those of the enhancement; nothing here writes to a meeting.
/// </summary>
public static class MeetingChatAnswerer
{
    public const int MaximumResponseTokens = 2000;
    /// <summary>Only the most recent messages are sent; notes and transcript take precedence over a long chat.</summary>
    public const int MaximumHistoryMessages = 20;

    public static string Instructions => Prompts.Get("MeetingChatInstructions");

    /// <summary>The one request for <paramref name="input"/>; exposed for tests.</summary>
    public static ChatRequest Request(MeetingChatInput input)
    {
        var prompt = Prompts.Fill("MeetingChatPrompt",
            ("TITLE", input.Title), ("TYPED_NOTES", input.Notes), ("TRANSCRIPT", input.TranscriptText),
            ("CONVERSATION", Conversation(input.History)), ("QUESTION", input.Question.Trim()));
        return new(Instructions, prompt, MaximumResponseTokens);
    }

    /// <summary>The last <see cref="MaximumHistoryMessages"/> messages as <c>&lt;user&gt;…&lt;/user&gt;</c> / <c>&lt;assistant&gt;…&lt;/assistant&gt;</c> lines.</summary>
    public static string Conversation(IReadOnlyList<MeetingChatTurn> history)
    {
        var recent = history.Where(turn => !string.IsNullOrWhiteSpace(turn.Text)).TakeLast(MaximumHistoryMessages);
        return string.Join('\n', recent.Select(turn => turn.FromUser ? $"<user>{turn.Text.Trim()}</user>" : $"<assistant>{turn.Text.Trim()}</assistant>"));
    }

    /// <summary>
    /// The trimmed answer. A truncated or refused response, an empty answer and every provider failure throw an
    /// <see cref="AiError"/> (the same validation as the enhancement), so no partial answer is ever returned.
    /// </summary>
    public static async Task<string> AskAsync(IChatProvider provider, MeetingChatInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Question)) throw new ArgumentException("The question is empty.", nameof(input));
        if (string.IsNullOrWhiteSpace(input.Notes) && string.IsNullOrWhiteSpace(input.TranscriptText)) throw AiError.Transient(AiMessages.NothingToAskAbout);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await provider.CompleteAsync(Request(input), cancellationToken).ConfigureAwait(false);
        result.EnsureFinished();
        var answer = (result.Text ?? "").Trim();
        return answer.Length == 0 ? throw AiError.Transient(AiMessages.EmptyAnswer) : answer;
    }
}
