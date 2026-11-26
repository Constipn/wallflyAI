using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Wallfly.Teams;

/// <summary>
/// One structural condition on a <see cref="TeamsNode"/>. All present parts must hold: control type, the
/// property match, <see cref="And"/> on the same node and <see cref="HasDescendant"/> on the node or a
/// non-excluded descendant. <see cref="Property"/> is logical (<c>id</c>, <c>class</c>, <c>text</c>, <c>role</c>) and
/// resolved through the selector <c>properties</c> remap by the reader.
/// </summary>
public sealed class SelectorMatcher
{
    [JsonPropertyName("controlTypes"), JsonConverter(typeof(ControlTypeListJsonConverter))]
    public int[] ControlTypes { get; set; } = [];

    [JsonPropertyName("property")] public string? Property { get; set; }

    /// <summary>equals, contains, startsWith, endsWith, hasToken (whitespace token) or exists.</summary>
    [JsonPropertyName("match")] public string Match { get; set; } = "contains";

    [JsonPropertyName("value")] public string Value { get; set; } = "";
    [JsonPropertyName("ignoreCase")] public bool IgnoreCase { get; set; }
    [JsonPropertyName("and")] public SelectorMatcher? And { get; set; }
    [JsonPropertyName("hasDescendant")] public SelectorMatcher? HasDescendant { get; set; }

    /// <summary>Documentation only, never read by code (optional; the shipped file uses "note" instead).</summary>
    [JsonPropertyName("evidence"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Evidence { get; set; }

    [JsonPropertyName("note"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Note { get; set; }

    /// <summary>True when this matcher (or a nested one) inspects text, which pass 1 never reads.</summary>
    [JsonIgnore] public bool UsesText => IsText(Property) || (And?.UsesText ?? false) || (HasDescendant?.UsesText ?? false);

    internal static bool IsText(string? property) => property is not null &&
        (property.Equals("text", StringComparison.OrdinalIgnoreCase) || property.Equals("Name", StringComparison.OrdinalIgnoreCase));

    internal static string Read(TeamsNode node, string property) => property.ToLowerInvariant() switch
    {
        "id" or "automationid" => node.Id,
        "class" or "classname" => node.ClassName,
        "text" or "name" => node.Name,
        "role" or "ariarole" => node.AriaRole,
        _ => "",
    };

    internal static bool Compare(string actual, string match, string expected, bool ignoreCase)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return match.ToLowerInvariant() switch
        {
            "equals" => string.Equals(actual, expected, comparison),
            "startswith" => actual.StartsWith(expected, comparison),
            "endswith" => actual.EndsWith(expected, comparison),
            "hastoken" => actual.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Any(token => string.Equals(token, expected, comparison)),
            "exists" => actual.Length > 0,
            _ => expected.Length > 0 && actual.Contains(expected, comparison),
        };
    }
}

/// <summary>Logical property → UIA property name. Lets a Teams update that moves the HTML id or class into
/// another UIA property be fixed in the override file without a release.</summary>
public sealed class TeamsPropertyMap
{
    [JsonPropertyName("id")] public string Id { get; set; } = "AutomationId";
    [JsonPropertyName("class")] public string Class { get; set; } = "ClassName";
    [JsonPropertyName("text")] public string Text { get; set; } = "Name";
    [JsonPropertyName("role")] public string Role { get; set; } = "AriaRole";

    /// <summary>UIA property IDs (UIA_*PropertyId) that a remap may name.</summary>
    public static readonly IReadOnlyDictionary<string, int> UiaPropertyIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["Name"] = 30005,
        ["AutomationId"] = 30011,
        ["ClassName"] = 30012,
        ["HelpText"] = 30013,
        ["LocalizedControlType"] = 30004,
        ["FrameworkId"] = 30024,
        ["ItemType"] = 30021,
        ["ItemStatus"] = 30026,
        ["AriaRole"] = 30101,
        ["AriaProperties"] = 30102,
        ["FullDescription"] = 30159,
    };

    public static int Resolve(string name, int fallback) => UiaPropertyIds.TryGetValue(name, out var id) ? id : fallback;
}

/// <summary>Read budgets (spec §5.2/§6) and UIA timeouts.</summary>
public sealed class TeamsReadLimits
{
    [JsonPropertyName("budgetMilliseconds")] public int BudgetMilliseconds { get; set; } = 220;
    [JsonPropertyName("diagnosticBudgetMilliseconds")] public int DiagnosticBudgetMilliseconds { get; set; } = 1000;
    [JsonPropertyName("maxNodes")] public int MaxNodes { get; set; } = 2200;
    [JsonPropertyName("maxDepth")] public int MaxDepth { get; set; } = 36;
    [JsonPropertyName("maxWindows")] public int MaxWindows { get; set; } = 8;
    [JsonPropertyName("maxProcesses")] public int MaxProcesses { get; set; } = 4;
    [JsonPropertyName("perElementMilliseconds")] public int PerElementMilliseconds { get; set; } = 50;
    [JsonPropertyName("connectionTimeoutMilliseconds")] public int ConnectionTimeoutMilliseconds { get; set; } = 100;
    [JsonPropertyName("transactionTimeoutMilliseconds")] public int TransactionTimeoutMilliseconds { get; set; } = 220;
    [JsonPropertyName("initializingSeconds")] public double InitializingSeconds { get; set; } = 3;
    [JsonPropertyName("stalledWindowSeconds")] public double StalledWindowSeconds { get; set; } = 30;

    /// <summary><c>subtree</c>: one cached TreeScope_Subtree request per window (≈ 0.2 ms/node);
    /// <c>walk</c>: one raw-view navigation call per node (≈ 1.5 ms/node, budget checked between calls).</summary>
    [JsonPropertyName("traversal")] public string Traversal { get; set; } = "subtree";
}

public sealed class SpeakingTextSelectors
{
    [JsonPropertyName("equals")] public string[] EqualsTexts { get; set; } = [];
    [JsonPropertyName("suffixes")] public string[] Suffixes { get; set; } = [];
}

public sealed class SpeakingGuardSelectors
{
    /// <summary>Never carry secondary speaking signals (classes/ids/texts): tile menu items.</summary>
    [JsonPropertyName("excludedControlTypes"), JsonConverter(typeof(ControlTypeListJsonConverter))]
    public int[] ExcludedControlTypes { get; set; } = [];

    /// <summary>Carry secondary signals only when they are the tile itself (buttons are controls, not tiles).</summary>
    [JsonPropertyName("tileOnlyControlTypes"), JsonConverter(typeof(ControlTypeListJsonConverter))]
    public int[] TileOnlyControlTypes { get; set; } = [];
}

public sealed class SelfSelectors
{
    [JsonPropertyName("labelPrefixes")] public string[] LabelPrefixes { get; set; } = [];
    [JsonPropertyName("idContains")] public string[] IdContains { get; set; } = [];
    [JsonPropertyName("textContains")] public string[] TextContains { get; set; } = [];

    /// <summary>A VDI tile of one of these control types is the local preview (macOS: AXImage).</summary>
    [JsonPropertyName("vdiTileControlTypes"), JsonConverter(typeof(ControlTypeListJsonConverter))]
    public int[] VdiTileControlTypes { get; set; } = [];
}

public sealed class NameStripSelectors
{
    /// <summary>Exact-case prefixes of a tile label that introduce the local user ("Myself video, Name, …").</summary>
    [JsonPropertyName("selfLabelPrefixes")] public string[] SelfLabelPrefixes { get; set; } = [];
    [JsonPropertyName("suffixes")] public string[] Suffixes { get; set; } = [];

    /// <summary>Labels that are control descriptions, never names (compared case-insensitively).</summary>
    [JsonPropertyName("controlOnly")] public string[] ControlOnly { get; set; } = [];
    [JsonPropertyName("maxLength")] public int MaxLength { get; set; } = 160;
}

public sealed class CompactLayoutSelectors
{
    [JsonPropertyName("windowMarkers")] public SelectorMatcher[] WindowMarkers { get; set; } = [];
    [JsonPropertyName("noteMarkers")] public SelectorMatcher[] NoteMarkers { get; set; } = [];

    [JsonPropertyName("textControlTypes"), JsonConverter(typeof(ControlTypeListJsonConverter))]
    public int[] TextControlTypes { get; set; } = [];

    /// <summary>Notes containing one of these control types are skipped (menus, not the speaker note).</summary>
    [JsonPropertyName("skipNotesContaining"), JsonConverter(typeof(ControlTypeListJsonConverter))]
    public int[] SkipNotesContaining { get; set; } = [];

    [JsonPropertyName("selfTexts")] public string[] SelfTexts { get; set; } = [];
}

/// <summary>
/// The Teams UI Automation selectors (<c>teams-selectors.json</c>): shipped default, optionally overlaid by
/// <c>%LOCALAPPDATA%\Wallfly\teams-selectors.json</c>. Every decision about the Teams tree goes through this
/// class so a Teams update can be answered with a JSON file instead of a release.
/// </summary>
public sealed class TeamsSelectors
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    [JsonPropertyName("processNames")] public string[] ProcessNames { get; set; } = ["ms-teams.exe"];
    [JsonPropertyName("webViewProcessNames")] public string[] WebViewProcessNames { get; set; } = ["msedgewebview2.exe"];
    [JsonPropertyName("micProcessNames")] public string[] MicProcessNames { get; set; } = ["ms-teams.exe"];
    [JsonPropertyName("windowClasses")] public string[] WindowClasses { get; set; } = ["TeamsWebView"];

    /// <summary>macOS skips minimized windows (their tree may be stale); then absence cannot be confirmed.</summary>
    [JsonPropertyName("readMinimizedWindows")] public bool ReadMinimizedWindows { get; set; }

    /// <summary>A window that showed a main-window marker is never a meeting window (sticky per HWND).</summary>
    [JsonPropertyName("rejectMainWindows")] public bool RejectMainWindows { get; set; } = true;

    [JsonPropertyName("properties")] public TeamsPropertyMap Properties { get; set; } = new();
    [JsonPropertyName("limits")] public TeamsReadLimits Limits { get; set; } = new();
    [JsonPropertyName("webAreaMarkers")] public SelectorMatcher[] WebAreaMarkers { get; set; } = [];

    [JsonPropertyName("excludedControlTypes"), JsonConverter(typeof(ControlTypeListJsonConverter))]
    public int[] ExcludedControlTypes { get; set; } = [];

    [JsonPropertyName("excludedMarkers")] public SelectorMatcher[] ExcludedMarkers { get; set; } = [];
    [JsonPropertyName("leaveButtonMarkers")] public SelectorMatcher[] LeaveButtonMarkers { get; set; } = [];
    [JsonPropertyName("mainWindowMarkers")] public SelectorMatcher[] MainWindowMarkers { get; set; } = [];
    [JsonPropertyName("meetingMarkers")] public SelectorMatcher[] MeetingMarkers { get; set; } = [];
    [JsonPropertyName("tileMarkers")] public SelectorMatcher[] TileMarkers { get; set; } = [];
    [JsonPropertyName("vdiClasses")] public string[] VdiClasses { get; set; } = [];
    [JsonPropertyName("nameMarkers")] public SelectorMatcher[] NameMarkers { get; set; } = [];

    /// <summary>Classes whose Text descendants carry the participant name.</summary>
    [JsonPropertyName("nameTextClasses")] public string[] NameTextClasses { get; set; } = [];

    [JsonPropertyName("nameTextControlTypes"), JsonConverter(typeof(ControlTypeListJsonConverter))]
    public int[] NameTextControlTypes { get; set; } = [UiaControlTypes.Text];

    /// <summary>Primary speaking signal, valid on any node inside a tile.</summary>
    [JsonPropertyName("speakingMarkers")] public SelectorMatcher[] SpeakingMarkers { get; set; } = [];

    /// <summary>Secondary class tokens (case-insensitive), subject to <see cref="SpeakingGuard"/>.</summary>
    [JsonPropertyName("speakingClasses")] public string[] SpeakingClasses { get; set; } = [];

    [JsonPropertyName("speakingIdSuffix")] public string[] SpeakingIdSuffix { get; set; } = [];
    [JsonPropertyName("speakingTexts")] public SpeakingTextSelectors SpeakingTexts { get; set; } = new();
    [JsonPropertyName("speakingGuard")] public SpeakingGuardSelectors SpeakingGuard { get; set; } = new();
    [JsonPropertyName("selfMarkers")] public SelfSelectors SelfMarkers { get; set; } = new();
    [JsonPropertyName("nameStrip")] public NameStripSelectors NameStrip { get; set; } = new();
    [JsonPropertyName("compactLayout")] public CompactLayoutSelectors CompactLayout { get; set; } = new();

    // ---- evaluation -------------------------------------------------------------------------------------

    public bool Matches(SelectorMatcher matcher, TeamsNode node, bool structureOnly = false)
    {
        if (matcher.ControlTypes.Length > 0 && Array.IndexOf(matcher.ControlTypes, node.ControlType) < 0) return false;
        if (matcher.Property is { Length: > 0 } property && !(structureOnly && SelectorMatcher.IsText(property)))
        {
            if (!SelectorMatcher.Compare(SelectorMatcher.Read(node, property), matcher.Match, matcher.Value, matcher.IgnoreCase)) return false;
        }
        if (matcher.And is { } and && !Matches(and, node, structureOnly)) return false;
        if (matcher.HasDescendant is { } descendant && !SafeNodes(node).Any(candidate => Matches(descendant, candidate, structureOnly))) return false;
        return true;
    }

    public bool MatchesAny(IEnumerable<SelectorMatcher> matchers, TeamsNode node, bool structureOnly = false) =>
        matchers.Any(matcher => Matches(matcher, node, structureOnly));

    /// <summary>Editable text and chat/message containers are never inspected (spec §5.2, §6).</summary>
    public bool IsExcludedControlType(int controlType) => Array.IndexOf(ExcludedControlTypes, controlType) >= 0;

    public bool IsExcluded(TeamsNode node) => IsExcludedControlType(node.ControlType) || MatchesAny(ExcludedMarkers, node, structureOnly: true);

    public bool IsWebArea(TeamsNode node) => MatchesAny(WebAreaMarkers, node, structureOnly: true);

    public bool IsMainWindowMarker(TeamsNode node) => MatchesAny(MainWindowMarkers, node, structureOnly: true);

    public bool IsLeaveButton(TeamsNode node) => MatchesAny(LeaveButtonMarkers, node, structureOnly: true);

    /// <summary>The node and its descendants, skipping excluded subtrees entirely.</summary>
    public IEnumerable<TeamsNode> SafeNodes(TeamsNode root)
    {
        var stack = new Stack<TeamsNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (IsExcluded(node)) continue;
            yield return node;
            for (var i = node.Children.Count - 1; i >= 0; i--) stack.Push(node.Children[i]);
        }
    }

    // ---- loading ----------------------------------------------------------------------------------------

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
    };

    private static readonly string[] NestedSections = ["properties", "limits", "speakingTexts", "speakingGuard", "selfMarkers", "nameStrip", "compactLayout"];

    private static string? defaultJson;

    /// <summary>The shipped selector file (embedded resource).</summary>
    public static string DefaultJson
    {
        get
        {
            if (defaultJson is not null) return defaultJson;
            using var stream = typeof(TeamsSelectors).Assembly.GetManifestResourceStream("Wallfly.Teams.teams-selectors.json")
                ?? throw new InvalidOperationException("teams-selectors.json is not embedded.");
            using var reader = new StreamReader(stream);
            return defaultJson = reader.ReadToEnd();
        }
    }

    public static TeamsSelectors LoadDefault() => Load(DefaultJson, null);

    /// <summary>
    /// Shipped defaults overlaid by an override document. A top-level key replaces the default value; the
    /// sections <c>properties</c>, <c>limits</c>, <c>speakingTexts</c>, <c>speakingGuard</c>, <c>selfMarkers</c>,
    /// <c>nameStrip</c> and <c>compactLayout</c> are merged key by key; a key ending in <c>+</c>
    /// (e.g. <c>"leaveButtonMarkers+"</c>) appends to the default list. Keys starting with <c>$</c> are comments.
    /// </summary>
    public static TeamsSelectors Load(string defaults, string? overrides)
    {
        var options = new JsonNodeOptions { PropertyNameCaseInsensitive = false };
        var documentOptions = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var merged = JsonNode.Parse(defaults, options, documentOptions) as JsonObject ?? throw new JsonException("The selector defaults are not a JSON object.");
        if (!string.IsNullOrWhiteSpace(overrides))
        {
            var overlay = JsonNode.Parse(overrides, options, documentOptions) as JsonObject ?? throw new JsonException("The selector override is not a JSON object.");
            Merge(merged, overlay);
        }
        foreach (var key in merged.Select(pair => pair.Key).Where(key => key.StartsWith('$')).ToList()) merged.Remove(key);
        var selectors = merged.Deserialize<TeamsSelectors>(JsonOptions) ?? throw new JsonException("The selectors could not be read.");
        if (selectors.SchemaVersion != CurrentSchemaVersion)
            throw new JsonException($"teams-selectors.json schemaVersion {selectors.SchemaVersion} is not supported (expected {CurrentSchemaVersion}).");
        return selectors;
    }

    private static void Merge(JsonObject target, JsonObject overlay)
    {
        foreach (var (key, value) in overlay.ToList())
        {
            if (key.StartsWith('$')) continue;
            if (key.EndsWith('+'))
            {
                var name = key[..^1];
                if (target[name] is JsonArray existing && value is JsonArray extra)
                {
                    foreach (var item in extra) existing.Add(item?.DeepClone());
                    continue;
                }
                target[name] = value?.DeepClone();
                continue;
            }
            if (Array.IndexOf(NestedSections, key) >= 0 && target[key] is JsonObject section && value is JsonObject patch)
            {
                foreach (var (inner, innerValue) in patch.ToList()) section[inner] = innerValue?.DeepClone();
                continue;
            }
            target[key] = value?.DeepClone();
        }
    }
}

/// <summary>Control type lists as names or numbers.</summary>
public sealed class ControlTypeListJsonConverter : JsonConverter<int[]>
{
    public override int[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number) return [reader.GetInt32()];
        if (reader.TokenType == JsonTokenType.String)
            return UiaControlTypes.TryParse(reader.GetString() ?? "", out var single) ? [single] : throw new JsonException("Unknown control type.");
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected a control type list.");
        var values = new List<int>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.Number) values.Add(reader.GetInt32());
            else if (reader.TokenType == JsonTokenType.String && UiaControlTypes.TryParse(reader.GetString() ?? "", out var value)) values.Add(value);
            else throw new JsonException("Unknown control type in list.");
        }
        return [.. values];
    }

    public override void Write(Utf8JsonWriter writer, int[] value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value) writer.WriteStringValue(UiaControlTypes.Name(item));
        writer.WriteEndArray();
    }
}

/// <summary>
/// Current selectors with hot reload of the override file: a FileSystemWatcher marks changes and every access
/// re-checks the file's timestamp at most once per second, so missed watcher events are still picked up.
/// An invalid override keeps the last good selectors and reports <see cref="LastError"/>.
/// </summary>
public sealed class TeamsSelectorSource : IDisposable
{
    private readonly object gate = new();
    private readonly string? overridePath;
    private readonly TimeProvider time;
    private readonly FileSystemWatcher? watcher;
    private volatile TeamsSelectors current;
    private volatile bool dirty;
    private DateTimeOffset lastCheck = DateTimeOffset.MinValue;
    private (DateTime Write, long Length)? lastStamp;

    public TeamsSelectorSource(string? overridePath = null, bool watch = true, TimeProvider? time = null, string? defaults = null)
    {
        this.overridePath = overridePath;
        this.time = time ?? TimeProvider.System;
        DefaultsJson = defaults ?? TeamsSelectors.DefaultJson;
        current = TeamsSelectors.Load(DefaultsJson, null);
        Reload();
        var directory = overridePath is null ? null : Path.GetDirectoryName(Path.GetFullPath(overridePath));
        if (watch && directory is not null && Directory.Exists(directory))
        {
            try
            {
                watcher = new FileSystemWatcher(directory, Path.GetFileName(overridePath!))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                };
                watcher.Changed += (_, _) => dirty = true;
                watcher.Created += (_, _) => dirty = true;
                watcher.Deleted += (_, _) => dirty = true;
                watcher.Renamed += (_, _) => dirty = true;
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception error) when (error is IOException or ArgumentException or PlatformNotSupportedException) { watcher = null; }
        }
    }

    /// <summary>The default override location, <c>%LOCALAPPDATA%\Wallfly\teams-selectors.json</c>.</summary>
    public static string DefaultOverridePath => Path.Combine(WallflyPaths.DataRoot, "teams-selectors.json");

    public static TeamsSelectorSource CreateDefault() => new(DefaultOverridePath);

    /// <summary>Fixed selectors without an override file (tests, tools).</summary>
    public static TeamsSelectorSource Fixed(TeamsSelectors selectors)
    {
        var source = new TeamsSelectorSource(null, watch: false);
        source.current = selectors;
        return source;
    }

    public string DefaultsJson { get; }
    public string? OverridePath => overridePath;
    public string? LastError { get; private set; }
    public bool OverrideActive { get; private set; }
    public event EventHandler? Changed;

    public TeamsSelectors Current
    {
        get
        {
            if (overridePath is not null)
            {
                var now = time.GetUtcNow();
                if (dirty || now - lastCheck >= TimeSpan.FromSeconds(1))
                {
                    lastCheck = now;
                    if (dirty || Stamp() != lastStamp) Reload();
                }
            }
            return current;
        }
    }

    /// <summary>Re-reads the override file now.</summary>
    public void Reload()
    {
        if (overridePath is null) return;
        bool changed;
        lock (gate)
        {
            dirty = false;
            var stamp = Stamp();
            lastStamp = stamp;
            string? text = null;
            try
            {
                if (stamp is not null) text = ReadShared(overridePath);
                var loaded = TeamsSelectors.Load(DefaultsJson, text);
                changed = true;
                current = loaded;
                OverrideActive = text is not null;
                LastError = null;
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                LastError = $"{Path.GetFileName(overridePath)}: {error.Message}";
                changed = false;
            }
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private (DateTime, long)? Stamp()
    {
        try
        {
            var info = new FileInfo(overridePath!);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose() => watcher?.Dispose();
}
