using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Wallfly.Teams;

/// <summary>
/// Structural metadata of a read, safe to write to disk: control types, technical identifiers, classes and
/// counters. Never contains Name/Value/HelpText, and identifiers that could carry user data are redacted with
/// the macOS rule (<see cref="TechnicalIdentifier"/>). Unknown or missing fields read as defaults, so reports
/// from older helper builds stay readable.
/// </summary>
public sealed partial class UiaReadDiagnostics
{
    public const int MaxTraceNodes = 256;

    public sealed class Node
    {
        [JsonPropertyName("parent")] public int? Parent { get; set; }
        [JsonPropertyName("role")] public string Role { get; set; } = "";
        [JsonPropertyName("identifier")] public string Identifier { get; set; } = "";
        [JsonPropertyName("classes")] public string Classes { get; set; } = "";
        [JsonPropertyName("ariaRole")] public string AriaRole { get; set; } = "";
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
        [JsonPropertyName("offscreen")] public bool Offscreen { get; set; }
        [JsonPropertyName("pid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Pid { get; set; }
    }

    public sealed class Process
    {
        [JsonPropertyName("pid")] public int Pid { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("windows")] public int Windows { get; set; }
    }

    [JsonPropertyName("pid")] public int? Pid { get; set; }
    [JsonPropertyName("windows")] public int Windows { get; set; }
    [JsonPropertyName("minimizedWindows")] public int MinimizedWindows { get; set; }
    [JsonPropertyName("nodesRead")] public int NodesRead { get; set; }
    [JsonPropertyName("webAreasRead")] public int WebAreasRead { get; set; }
    [JsonPropertyName("initializing")] public bool Initializing { get; set; }
    [JsonPropertyName("emptyContainers")] public int EmptyContainers { get; set; }
    [JsonPropertyName("hitLimit")] public bool HitLimit { get; set; }
    [JsonPropertyName("traceTruncated")] public bool TraceTruncated { get; set; }

    /// <summary>Main Teams windows recognised by a main-window marker and not used as meeting windows.</summary>
    [JsonPropertyName("mainWindowsRejected")] public int MainWindowsRejected { get; set; }

    /// <summary>Windows skipped because an earlier read of them stalled the UIA thread.</summary>
    [JsonPropertyName("stalledWindows")] public int StalledWindows { get; set; }

    /// <summary>Reads that completed but took longer than the budget (subtree traversal cannot be interrupted).</summary>
    [JsonPropertyName("slowReads")] public int SlowReads { get; set; }

    [JsonPropertyName("readMilliseconds")] public double ReadMilliseconds { get; set; }
    [JsonPropertyName("errors")] public Dictionary<string, int> Errors { get; set; } = [];
    [JsonPropertyName("nodes")] public List<Node> Nodes { get; set; } = [];
    [JsonPropertyName("processes")] public List<Process> Processes { get; set; } = [];

    /// <summary>
    /// Absence needs a complete read, no web view in its first seconds, and positive proof that a Teams web UI
    /// was readable (a web area, or a recognised main window). Native window chrome alone proves nothing.
    /// </summary>
    public TeamsCallSample AbsenceSample(int pid, bool complete) =>
        complete && !Initializing && (WebAreasRead > 0 || MainWindowsRejected > 0) ? TeamsCallSample.Absent(pid) : TeamsCallSample.Unknown;

    public void AddError(string key) => Errors[key] = Errors.GetValueOrDefault(key) + 1;

    public void Merge(UiaReadDiagnostics other)
    {
        NodesRead += other.NodesRead;
        WebAreasRead += other.WebAreasRead;
        EmptyContainers += other.EmptyContainers;
        MainWindowsRejected += other.MainWindowsRejected;
        StalledWindows += other.StalledWindows;
        SlowReads += other.SlowReads;
        ReadMilliseconds += other.ReadMilliseconds;
        Initializing = Initializing || other.Initializing;
        HitLimit = HitLimit || other.HitLimit;
        TraceTruncated = TraceTruncated || other.TraceTruncated || Nodes.Count + other.Nodes.Count > MaxTraceNodes;
        foreach (var (key, count) in other.Errors) Errors[key] = Errors.GetValueOrDefault(key) + count;
        var offset = Nodes.Count;
        foreach (var node in other.Nodes.Take(Math.Max(0, MaxTraceNodes - offset)))
        {
            Nodes.Add(new Node
            {
                Parent = node.Parent + offset, Role = node.Role, Identifier = node.Identifier, Classes = node.Classes,
                AriaRole = node.AriaRole, Enabled = node.Enabled, Offscreen = node.Offscreen, Pid = node.Pid,
            });
        }
    }

    private static readonly string[] UserDataWords = ["participant", "displayname", "display-name", "message", "roster", "contact", "member"];

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]*$")]
    private static partial Regex TechnicalPattern();

    /// <summary>
    /// macOS rule: keep identifiers that look technical (≤ 120 chars, letter first, then letters, digits, '_', '-',
    /// and on Windows '.' for <c>com.microsoft.…</c>), and that contain no word hinting at participant or message
    /// data. Everything else becomes <c>&lt;redacted&gt;</c>.
    /// </summary>
    public static string TechnicalIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var lower = value.ToLowerInvariant();
        return value.Length <= 120 && TechnicalPattern().IsMatch(value) && !UserDataWords.Any(lower.Contains) ? value : "<redacted>";
    }

    /// <summary>HTML classes token by token: technical tokens stay, anything else is redacted.</summary>
    public static string TechnicalClasses(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var tokens = value.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Length <= 80 && TechnicalPattern().IsMatch(token) ? token : "<redacted>");
        return string.Join(' ', tokens);
    }

    public static string TechnicalRole(string value) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= 40 && TechnicalPattern().IsMatch(value) ? value : "<redacted>";
}

/// <summary>A one-shot request by the app for a structure-only diagnostic report (valid 120 s).</summary>
public sealed class DetectionDiagnosticRequest
{
    [JsonPropertyName("id"), JsonConverter(typeof(UppercaseGuidJsonConverter))] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("date"), JsonConverter(typeof(AppleDateJsonConverter))] public DateTimeOffset Date { get; set; } = DateTimeOffset.UtcNow;

    public bool IsFresh(DateTimeOffset now)
    {
        var age = (now - Date).TotalSeconds;
        return age >= 0 && age < 120;
    }
}

/// <summary>What the helper saw for one diagnostic request. Structure only (see <see cref="UiaReadDiagnostics"/>).</summary>
public sealed class DetectionDiagnosticReport
{
    [JsonPropertyName("requestID"), JsonConverter(typeof(UppercaseGuidJsonConverter))] public Guid RequestId { get; set; }
    [JsonPropertyName("date"), JsonConverter(typeof(AppleDateJsonConverter))] public DateTimeOffset Date { get; set; }
    [JsonPropertyName("helperVersion")] public string HelperVersion { get; set; } = "";
    [JsonPropertyName("outcome")] public string Outcome { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("structure")] public UiaReadDiagnostics Structure { get; set; } = new();

    public static DetectionDiagnosticReport Create(DetectionDiagnosticRequest request, TeamsDetectionSnapshot snapshot, string helperVersion, DateTimeOffset now) => new()
    {
        RequestId = request.Id,
        Date = now,
        HelperVersion = helperVersion,
        Status = snapshot.Status,
        Structure = snapshot.Diagnostics,
        Outcome = snapshot.Sample.State switch
        {
            TeamsCallState.Active => "active",
            TeamsCallState.Absent => "absent",
            TeamsCallState.NotRunning => "notRunning",
            TeamsCallState.Unavailable => "unavailable",
            _ => "unknown",
        },
    };

    /// <summary>After a helper update, an unsuccessful report younger than 10 minutes is collected once more.</summary>
    public bool ShouldRefresh(string helperVersion, DateTimeOffset now)
    {
        var age = (now - Date).TotalSeconds;
        return HelperVersion != helperVersion && Outcome != "active" && age >= 0 && age < 600;
    }
}
