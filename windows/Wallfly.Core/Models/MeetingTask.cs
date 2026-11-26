using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wallfly.Ai;
using Wallfly.Core.Json;

namespace Wallfly.Core;

/// <summary>User decision on a proposed task.</summary>
[JsonConverter(typeof(RawValueEnumConverter<TaskReview>))]
public enum TaskReview
{
    [EnumMember(Value = "accepted")] Accepted,
    [EnumMember(Value = "dismissed")] Dismissed
}

/// <summary>State of the automatic task recognition for one meeting.</summary>
[JsonConverter(typeof(RawValueEnumConverter<TaskExtractionState>))]
public enum TaskExtractionState
{
    [EnumMember(Value = "pending")] Pending,
    [EnumMember(Value = "failed")] Failed,
    [EnumMember(Value = "complete")] Complete
}

/// <summary>An agreed action. Local completion is never overwritten by a model response.</summary>
public sealed class MeetingTask : Observable
{
    private DateTimeOffset? completedAt;
    private TaskReview? review;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public string? Owner { get; set; }
    public DateTimeOffset? CompletedAt { get => completedAt; set { if (Set(ref completedAt, value)) Changed(nameof(IsCompleted)); } }
    /// <summary>Set once the user accepted or dismissed the proposal; null means still pending approval.</summary>
    public TaskReview? Review { get => review; set => Set(ref review, value); }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
    [JsonIgnore] public bool IsCompleted { get => CompletedAt != null; set => CompletedAt = value ? DateTimeOffset.UtcNow : null; }

    /// <summary>Normalized comparison key, shared with the extractor.</summary>
    public static string Key(string? text) => TaskTextKey.Normalize(text);
}
