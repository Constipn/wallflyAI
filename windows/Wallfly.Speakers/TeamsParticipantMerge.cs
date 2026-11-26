using System.Text.RegularExpressions;
using Wallfly.Core;

namespace Wallfly.Speakers;

/// <summary>
/// The shapes of a Teams participant's <see cref="MeetingParticipant.SourceKey"/>: <c>&lt;context&gt;/stable/&lt;key&gt;</c>
/// (a stable identifier of the tile), <c>&lt;context&gt;/name/&lt;normalized name&gt;</c> (Windows since 2026-11-24: one person per
/// name) and <c>&lt;context&gt;/element/&lt;key&gt;</c> (macOS: a stable AX identifier; Windows before 2026-11-24: a transient UIA
/// runtime id such as <c>42.1234.5</c>). Acoustic participants (<c>acoustic/…</c>) and manual ones (no key) are not Teams'.
/// </summary>
public static partial class TeamsParticipantKeys
{
    private const string NamePart = "/name/";
    private const string StablePart = "/stable/";
    private const string ElementPart = "/element/";

    /// <summary>A participant named by the Teams attribution (not acoustic, not manual).</summary>
    public static bool IsTeams(MeetingParticipant person) =>
        person.SourceKey is { Length: > 0 } key && !key.StartsWith(AcousticSpeakerLabels.AcousticSourceKeyPrefix, StringComparison.Ordinal);

    /// <summary>The normalized name of a <c>…/name/…</c> key; null for every other shape.</summary>
    public static string? ObservedName(string? sourceKey) => Part(sourceKey, NamePart);

    /// <summary>The stable key of a <c>…/stable/…</c> key; null for every other shape.</summary>
    public static string? StableKey(string? sourceKey) => Part(sourceKey, StablePart);

    /// <summary>
    /// A Windows UIA runtime id key (<c>…/element/42.1234.5</c> or a fallback <c>…/element/u7</c>): transient, never an
    /// identity. macOS element keys (AX identifiers) do not match.
    /// </summary>
    public static bool IsTransientElement(string? sourceKey) => Part(sourceKey, ElementPart) is { } element && RuntimeId().IsMatch(element);

    /// <summary>The context part (<c>&lt;teamsPid&gt;/&lt;contextGuid&gt;</c>) of a key; null when it has none of the three shapes.</summary>
    public static string? Context(string? sourceKey)
    {
        if (sourceKey is null) return null;
        foreach (var part in new[] { NamePart, StablePart, ElementPart })
        {
            var index = sourceKey.IndexOf(part, StringComparison.Ordinal);
            if (index >= 0) return sourceKey[..index];
        }
        return null;
    }

    public static string NameKey(string? context, string normalizedName) => $"{context ?? "unknown"}{NamePart}{normalizedName}";

    public static string Normalized(string name) => name.Trim().ToLowerInvariant();

    private static string? Part(string? sourceKey, string marker)
    {
        if (sourceKey is null) return null;
        var index = sourceKey.IndexOf(marker, StringComparison.Ordinal);
        return index < 0 ? null : sourceKey[(index + marker.Length)..];
    }

    [GeneratedRegex(@"^(-?\d+(\.-?\d+)*|u\d+)$")]
    private static partial Regex RuntimeId();
}

/// <summary>
/// Repairs meetings recorded on Windows before 2026-11-24, where one Teams participant became many entries (a new entry for
/// every transient UIA runtime id). Idempotent and cheap; changes only the meeting object (the caller saves as usual).
/// </summary>
public static class TeamsParticipantMerge
{
    /// <summary>
    /// Merges the Teams participants of <paramref name="meeting"/> that share a normalized name and are keyed by a transient
    /// Windows element key or by name (<see cref="TeamsParticipantKeys"/>), together with at most one stable-keyed entry of
    /// that name (two different stable keys: genuinely two people, nothing is merged for that name). The earliest entry
    /// (id, colour) stays; a transient key becomes the name key; words (<c>participantID</c>, <c>uiParticipantID</c>,
    /// <c>utteranceParticipantID</c>), segments, the speaker timeline and acoustic bindings point to it; the others are
    /// removed. A name that differs from the group's most common spelling (a manual edit) wins. Meetings without a
    /// transient Windows element key (macOS data, new recordings) are never touched.
    /// </summary>
    /// <param name="replacedBy">When given, receives removed id → kept id.</param>
    /// <returns>The number of removed entries.</returns>
    public static int Merge(Meeting meeting, IDictionary<Guid, Guid>? replacedBy = null)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        if (meeting.Participants is not { Count: > 1 } people || !people.Any(person => TeamsParticipantKeys.IsTransientElement(person.SourceKey))) return 0;
        var replaced = new Dictionary<Guid, Guid>();
        var groups = people.Where(TeamsParticipantKeys.IsTeams)
            .GroupBy(person => TeamsParticipantKeys.Normalized(person.Name), StringComparer.Ordinal)
            .Where(group => group.Key.Length > 0)
            .ToList();
        foreach (var group in groups)
        {
            var mergeable = group.Where(person => TeamsParticipantKeys.IsTransientElement(person.SourceKey) || TeamsParticipantKeys.ObservedName(person.SourceKey) is not null).ToList();
            var stable = group.Where(person => TeamsParticipantKeys.StableKey(person.SourceKey) is not null).ToList();
            if (stable.Select(person => TeamsParticipantKeys.StableKey(person.SourceKey)).Distinct(StringComparer.Ordinal).Count() > 1) continue;
            var members = mergeable.Concat(stable).OrderBy(people.IndexOf).ToList();
            if (members.Count < 2 || !members.Any(person => TeamsParticipantKeys.IsTransientElement(person.SourceKey))) continue;
            var keeper = members[0];
            var common = members.GroupBy(person => person.Name, StringComparer.Ordinal).OrderByDescending(spelling => spelling.Count()).First().Key;
            var edited = members.FirstOrDefault(person => !string.Equals(person.Name, common, StringComparison.Ordinal));
            keeper.Name = edited?.Name ?? keeper.Name;
            if (TeamsParticipantKeys.IsTransientElement(keeper.SourceKey))
                keeper.SourceKey = stable.Count == 1 ? stable[0].SourceKey : TeamsParticipantKeys.NameKey(TeamsParticipantKeys.Context(keeper.SourceKey), group.Key);
            foreach (var duplicate in members.Skip(1)) replaced[duplicate.Id] = keeper.Id;
        }
        if (replaced.Count == 0) return 0;
        Guid? Map(Guid? id) => id is Guid value && replaced.TryGetValue(value, out var keeper) ? keeper : id;
        foreach (var segment in meeting.Transcript)
        {
            segment.ParticipantID = Map(segment.ParticipantID);
            foreach (var word in segment.Words ?? [])
            {
                word.ParticipantID = Map(word.ParticipantID);
                word.UiParticipantID = Map(word.UiParticipantID);
                word.UtteranceParticipantID = Map(word.UtteranceParticipantID);
            }
        }
        foreach (var entry in meeting.SpeakerTimeline ?? []) entry.ParticipantID = Map(entry.ParticipantID);
        foreach (var speaker in meeting.AcousticSpeakers ?? []) speaker.ParticipantID = Map(speaker.ParticipantID);
        if (replacedBy != null) foreach (var (from, to) in replaced) replacedBy[from] = to;
        return people.RemoveAll(person => replaced.ContainsKey(person.Id));
    }
}
