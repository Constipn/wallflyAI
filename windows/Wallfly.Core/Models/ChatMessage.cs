using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wallfly.Core;

/// <summary>
/// Windows addition (<see cref="Meeting.Chat"/>): one message of the chat about a finished meeting, the user's question
/// (<see cref="UserRole"/>) or the model's answer (<see cref="AssistantRole"/>). Only complete answers are stored.
/// </summary>
public sealed class ChatMessage
{
    public const string UserRole = "user";
    public const string AssistantRole = "assistant";

    public string Role { get; set; } = UserRole;
    public string Text { get; set; } = "";
    public DateTimeOffset At { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }

    [JsonIgnore] public bool FromUser => string.Equals(Role, UserRole, StringComparison.Ordinal);
}
