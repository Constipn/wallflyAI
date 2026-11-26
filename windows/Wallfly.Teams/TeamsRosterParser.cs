namespace Wallfly.Teams;

/// <summary>A participant tile as observed in one read (port of TeamsObservedParticipant).</summary>
public sealed record TeamsObservedParticipant(string Key, string Name, bool Active, bool IsSelf = false, string? StableKey = null);

/// <summary>Status of the speaker signal (subset of macOS TeamsSpeakerStatus used by the pure parts).</summary>
public enum TeamsSpeakerStatus
{
    Active,
    WaitingForSpeaker,
    Ambiguous,
    SelfSpeaking,
    Unavailable,
    TeamsNotRunning,
    MeetingNotFound,
}

public enum TeamsObservationFailure
{
    NotRunning,
    MeetingNotFound,
    Unreadable,
    Truncated,
    ConflictingViews,
    UnidentifiedSpeaker,
    LocalUserSpeaking,
}

/// <summary>Participants of one meeting view plus the two barrier signals.</summary>
public sealed class TeamsRoster
{
    public List<TeamsObservedParticipant> Participants { get; set; } = [];

    /// <summary>A speaking tile without a usable name (or a repeated key): attribution must fall back to Others.</summary>
    public bool UnidentifiedActive { get; set; }

    /// <summary>The local user's tile speaks: remote attribution pauses.</summary>
    public bool LocalUserSpeaking { get; set; }

    /// <summary><c>gallery</c> (tiles) or <c>compact</c> (call monitor note with the current speaker).</summary>
    public string Layout { get; set; } = "gallery";
}

/// <summary>
/// Read-only, selector-driven roster parser (port of TeamsAXParser, macOS 1.13.0, extended with the Windows
/// literals: tiles are MenuItems with a <c>fui-</c> class and a "Name, …" label, self tiles start with
/// "Myself video, ", " (Guest)" is stripped, speaking = descendant with class <c>vdi-frame-occlusion</c>, and the
/// compact call-monitor layout). A participant name never counts as evidence of speech.
/// </summary>
public static class TeamsRosterParser
{
    public static IReadOnlyList<TeamsObservedParticipant>? Participants(TeamsNode root, TeamsSelectors selectors) =>
        Roster(root, selectors)?.Participants;

    public static bool IsMeeting(TeamsNode root, TeamsSelectors selectors) =>
        selectors.SafeNodes(root).Any(node => selectors.MatchesAny(selectors.MeetingMarkers, node, structureOnly: true));

    /// <summary>Tile test; with <paramref name="structureOnly"/> text conditions are assumed true (pass 1 candidates).</summary>
    public static bool IsTile(TeamsNode node, TeamsSelectors selectors, bool structureOnly = false) =>
        !selectors.IsExcluded(node) && selectors.MatchesAny(selectors.TileMarkers, node, structureOnly);

    /// <summary>Outermost tile candidates (structure only): the nodes whose subtrees pass 2 may read text from.</summary>
    public static IEnumerable<TeamsNode> TileCandidates(TeamsNode root, TeamsSelectors selectors)
    {
        var stack = new Stack<TeamsNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (selectors.IsExcluded(node)) continue;
            if (IsTile(node, selectors, structureOnly: true))
            {
                yield return node;
                continue;
            }
            for (var i = node.Children.Count - 1; i >= 0; i--) stack.Push(node.Children[i]);
        }
    }

    public static TeamsRoster? Roster(TeamsNode root, TeamsSelectors selectors)
    {
        if (!IsMeeting(root, selectors)) return null;
        var result = new TeamsRoster();
        Visit(root, selectors, result);
        var grouped = result.Participants.GroupBy(participant => participant.Key).ToDictionary(group => group.Key, group => group.Count());
        var stableCounts = result.Participants.Where(participant => participant.StableKey is not null)
            .GroupBy(participant => participant.StableKey!).ToDictionary(group => group.Key, group => group.Count());
        // A repeated identifier is not a stable participant identifier; repeated keys are dropped entirely.
        if (result.Participants.GroupBy(participant => participant.Key).Any(group => group.Count() > 1 && group.Any(participant => participant.Active)))
            result.UnidentifiedActive = true;
        result.Participants = result.Participants
            .Where(participant => grouped[participant.Key] == 1)
            .Select(participant => participant.StableKey is { } stable && stableCounts[stable] != 1 ? participant with { StableKey = null } : participant)
            .OrderBy(participant => participant.Key, StringComparer.Ordinal)
            .ToList();
        if (result.Participants.Count == 0 && !result.UnidentifiedActive && !result.LocalUserSpeaking) Compact(root, selectors, result);
        return result;
    }

    private static void Visit(TeamsNode node, TeamsSelectors selectors, TeamsRoster result)
    {
        if (selectors.IsExcluded(node)) return;
        if (IsTile(node, selectors))
        {
            ParseTile(node, selectors, result);
            return;
        }
        foreach (var child in node.Children) Visit(child, selectors, result);
    }

    private static void ParseTile(TeamsNode tile, TeamsSelectors selectors, TeamsRoster result)
    {
        var descendants = selectors.SafeNodes(tile).ToList();
        var nameNode = descendants.FirstOrDefault(element => selectors.MatchesAny(selectors.NameMarkers, element));
        var vdiNames = descendants
            .Where(element => element.Classes.Any(token => selectors.NameTextClasses.Contains(token, StringComparer.Ordinal)))
            .SelectMany(selectors.SafeNodes)
            .Where(element => selectors.NameTextControlTypes.Contains(element.ControlType))
            .Select(FirstText);
        var candidates = new List<string>();
        if (nameNode is not null) candidates.Add(FirstText(nameNode));
        candidates.AddRange(vdiNames);
        candidates.Add(LabelName(tile, selectors));
        var nameText = candidates.FirstOrDefault(text => CleanName(text, selectors).Length > 0) ?? "";
        var label = tile.Name;
        var lowerLabel = label.ToLowerInvariant();
        var selfText = string.Join(' ', nameText, label).ToLowerInvariant();
        var self = selectors.SelfMarkers;
        var vdiTile = selectors.VdiClasses.Length > 0 && descendants.Any(element => element.Classes.Any(token => selectors.VdiClasses.Contains(token, StringComparer.Ordinal)));
        var isSelf = (vdiTile && self.VdiTileControlTypes.Contains(tile.ControlType))
            || self.LabelPrefixes.Any(prefix => lowerLabel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || self.IdContains.Any(marker => tile.Id.Contains(marker, StringComparison.OrdinalIgnoreCase))
            || self.TextContains.Any(marker => selfText.Contains(marker, StringComparison.OrdinalIgnoreCase));
        var name = CleanName(nameText, selectors);
        var active = descendants.Any(element => Speaking(element, ReferenceEquals(element, tile), selectors));
        result.LocalUserSpeaking = result.LocalUserSpeaking || (isSelf && active);
        result.UnidentifiedActive = result.UnidentifiedActive || (!isSelf && active && name.Length == 0);
        var stableKey = tile.Id.Length > 0 ? "id/" + tile.Id : null;
        if (name.Length > 0)
            result.Participants.Add(new TeamsObservedParticipant(tile.Key ?? tile.Id, name, active, isSelf, stableKey));
    }

    /// <summary>Primary signal anywhere in the tile; secondary class/id/text signals not on menu items and not on buttons other than the tile.</summary>
    public static bool Speaking(TeamsNode node, bool isTile, TeamsSelectors selectors)
    {
        if (selectors.MatchesAny(selectors.SpeakingMarkers, node)) return true;
        var guard = selectors.SpeakingGuard;
        if (guard.ExcludedControlTypes.Contains(node.ControlType)) return false;
        if (!isTile && guard.TileOnlyControlTypes.Contains(node.ControlType)) return false;
        if (node.Classes.Any(token => selectors.SpeakingClasses.Contains(token, StringComparer.OrdinalIgnoreCase))) return true;
        if (selectors.SpeakingIdSuffix.Any(suffix => node.Id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))) return true;
        var text = node.Name.Trim().ToLowerInvariant();
        if (text.Length == 0) return false;
        if (selectors.SpeakingTexts.EqualsTexts.Any(value => text == value.ToLowerInvariant())) return true;
        return selectors.SpeakingTexts.Suffixes.Any(suffix => text.EndsWith(suffix.ToLowerInvariant(), StringComparison.Ordinal));
    }

    /// <summary>The tile label is a fallback only: "Myself video, Name, …" → Name; otherwise the text before the first comma.</summary>
    public static string LabelName(TeamsNode tile, TeamsSelectors selectors)
    {
        var text = tile.Name;
        foreach (var prefix in selectors.NameStrip.SelfLabelPrefixes)
        {
            if (!text.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var parts = text.Split(", ");
            return parts.Length > 1 ? parts[1] : "";
        }
        return text.Split(',')[0];
    }

    public static string CleanName(string raw, TeamsSelectors selectors)
    {
        var strip = selectors.NameStrip;
        var value = raw.Trim();
        foreach (var suffix in strip.Suffixes)
            if (value.EndsWith(suffix, StringComparison.Ordinal)) value = value[..^suffix.Length];
        if (strip.ControlOnly.Any(label => string.Equals(label, value, StringComparison.OrdinalIgnoreCase))) return "";
        return (value.Length > strip.MaxLength ? value[..strip.MaxLength] : value).Trim();
    }

    private static string FirstText(TeamsNode node)
    {
        if (!string.IsNullOrWhiteSpace(node.Name)) return node.Name;
        foreach (var child in node.Children)
        {
            var text = FirstText(child);
            if (text.Length > 0) return text;
        }
        return "";
    }

    /// <summary>
    /// Compact layout (call monitor): a window with the call-monitor title shows the current speaker
    /// in a <c>note</c> group; "You are speaking" means the local user.
    /// </summary>
    private static void Compact(TeamsNode root, TeamsSelectors selectors, TeamsRoster result)
    {
        var compact = selectors.CompactLayout;
        var nodes = selectors.SafeNodes(root).ToList();
        if (compact.WindowMarkers.Length == 0 || !nodes.Any(node => selectors.MatchesAny(compact.WindowMarkers, node))) return;
        foreach (var note in nodes.Where(node => selectors.MatchesAny(compact.NoteMarkers, node)))
        {
            var inside = selectors.SafeNodes(note).ToList();
            if (inside.Any(node => compact.SkipNotesContaining.Contains(node.ControlType))) continue;
            var text = inside.FirstOrDefault(node => compact.TextControlTypes.Contains(node.ControlType) && node.Name.Trim().Length > 0)?.Name.Trim();
            if (text is null) continue;
            result.Layout = "compact";
            if (compact.SelfTexts.Any(self => string.Equals(self, text, StringComparison.Ordinal)))
            {
                result.LocalUserSpeaking = true;
                return;
            }
            var name = CleanName(text, selectors);
            if (name.Length > 0) result.Participants.Add(new TeamsObservedParticipant(note.Key ?? "compact", name, Active: true));
            return;
        }
    }
}

/// <summary>A candidate meeting view (one window) and its roster.</summary>
public sealed record TeamsMeetingScene(string SurfaceId, TeamsRoster Roster);

/// <summary>Pure scene election across windows (port of TeamsSceneSelector).</summary>
public sealed class TeamsSceneSelector
{
    private string? recoverySurface;

    public string? PreferredSurface { get; private set; }

    public int WindowPriority(string surface, string? focused)
    {
        if (surface == recoverySurface) return 0;
        if (surface == PreferredSurface) return 1;
        return surface == focused ? 2 : 3;
    }

    /// <summary>A failed walk moves recovery to the next window, so one unreadable chat window cannot eat every poll.</summary>
    public void Retry(string? after, IReadOnlyList<string> windows)
    {
        if (after is null || windows.Count == 0) return;
        var index = windows.ToList().IndexOf(after);
        if (index < 0) return;
        recoverySurface = windows[(index + 1) % windows.Count];
    }

    public (TeamsMeetingScene Scene, TeamsSpeakerStatus Status, TeamsObservationFailure? Failure)? Select(IReadOnlyList<TeamsMeetingScene> scenes)
    {
        if (scenes.Count == 0) return null;
        // Swift max(by:) semantics: the first of equally large rosters wins.
        var best = scenes.FirstOrDefault(scene => scene.SurfaceId == PreferredSurface)
            ?? scenes.First(scene => scene.Roster.Participants.Count == scenes.Max(other => other.Roster.Participants.Count));
        PreferredSurface = best.SurfaceId;
        recoverySurface = null;
        var speakingNames = scenes.SelectMany(scene => scene.Roster.Participants.Where(participant => participant.Active && !participant.IsSelf)
            .Select(participant => participant.Name.Trim().ToLowerInvariant())).ToHashSet();
        if (speakingNames.Count > 1 || scenes.Any(scene => SpeakingPeople(scene.Roster) > 1))
            return (best, TeamsSpeakerStatus.Ambiguous, TeamsObservationFailure.ConflictingViews);
        if (scenes.Any(scene => scene.Roster.UnidentifiedActive)) return (best, TeamsSpeakerStatus.Ambiguous, TeamsObservationFailure.UnidentifiedSpeaker);
        if (scenes.Any(scene => scene.Roster.LocalUserSpeaking)) return (best, TeamsSpeakerStatus.SelfSpeaking, TeamsObservationFailure.LocalUserSpeaking);
        if (!best.Roster.Participants.Any(participant => participant.Active)
            && scenes.FirstOrDefault(scene => scene.Roster.Participants.Any(participant => participant.Active)) is { } active)
        {
            best = active;
            PreferredSurface = active.SurfaceId;
        }
        return (best, best.Roster.Participants.Any(participant => participant.Active) ? TeamsSpeakerStatus.Active : TeamsSpeakerStatus.WaitingForSpeaker, null);
    }

    /// <summary>
    /// Speaking people of one roster: two speaking tiles with the same name (one person shown twice, Windows) are one
    /// person unless they carry two different stable keys.
    /// </summary>
    private static int SpeakingPeople(TeamsRoster roster) =>
        roster.Participants.Where(participant => participant.Active)
            .GroupBy(participant => participant.Name.Trim().ToLowerInvariant(), StringComparer.Ordinal)
            .Sum(group => Math.Max(1, group.Select(participant => participant.StableKey).Where(key => key is not null).Distinct(StringComparer.Ordinal).Count()));
}
