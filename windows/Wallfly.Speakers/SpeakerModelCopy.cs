using System.Text.Json;
using Wallfly.Core;

namespace Wallfly.Speakers;

/// <summary>
/// Value-semantics helpers for the mutable Core model classes. The Swift originals are structs, so every
/// algorithm here copies before it changes a segment that belongs to the caller.
/// </summary>
internal static class SpeakerModelCopy
{
    public static TranscriptWord Clone(TranscriptWord word) => new()
    {
        Text = word.Text, Start = word.Start, End = word.End, ParticipantID = word.ParticipantID, Attribution = word.Attribution,
        UiParticipantID = word.UiParticipantID, AcousticSpeaker = word.AcousticSpeaker,
        UtteranceParticipantID = word.UtteranceParticipantID, AdditionalData = Clone(word.AdditionalData)
    };

    public static TranscriptSegment Clone(TranscriptSegment segment) => new()
    {
        Id = segment.Id, Speaker = segment.Speaker, Offset = segment.Offset, Text = segment.Text, Duration = segment.Duration,
        IsFinal = segment.IsFinal, SessionID = segment.SessionID, Words = segment.Words?.Select(Clone).ToList(),
        ParticipantID = segment.ParticipantID, Attribution = segment.Attribution, ConnectionID = segment.ConnectionID,
        AdditionalData = Clone(segment.AdditionalData)
    };

    private static Dictionary<string, JsonElement>? Clone(Dictionary<string, JsonElement>? data) =>
        data is null ? null : new Dictionary<string, JsonElement>(data);
}

/// <summary>
/// The macOS code orders UUIDs by <c>uuidString</c> (uppercase). Lowercase "D" strings sort identically under
/// ordinal comparison (digits before letters, letters in order); <see cref="Guid.CompareTo(Guid)"/> does not.
/// </summary>
internal static class UuidOrder
{
    public static int Compare(Guid left, Guid right) => string.CompareOrdinal(left.ToString("D"), right.ToString("D"));
}

/// <summary>Swift <c>rounded()</c> (to nearest, ties away from zero); <see cref="Math.Round(double)"/> rounds ties to even.</summary>
internal static class SwiftMath
{
    public static double Rounded(double value) => Math.Round(value, MidpointRounding.AwayFromZero);
    public static double RoundedMillis(double value) => Math.Round(value * 1000, MidpointRounding.AwayFromZero) / 1000;
}
