using System.Globalization;
using System.IO;
using System.Text.Json;
using Wallfly.App.Controls;
using Wallfly.Audio;
using Wallfly.Core;
using Wallfly.Core.Persistence;
using Wallfly.Core.Search;

namespace Wallfly.App.ViewModels;

/// <summary>The macOS 1.13.0 palette (Palette.swift, spec 9.6) as hex strings, usable from view models and code-built visuals.</summary>
public static class Palette
{
    public const string Paper = "#FFFFFF";
    public const string Sidebar = "#F5F6F7";
    public const string Ink = "#202328";
    public const string Muted = "#565D66";
    public const string Line = "#D7DBE0";
    public const string Gray100 = "#E8EBEF";
    public const string Hairline = "#BFC0C1";
    public const string Accent = "#C32B3D";
    public const string AccentBright = "#F4364C";
    public const string Link = "#225D88";
    public const string Selection = "#E7F0F8";
    public const string Gold100 = "#F7F2DD";
    public const string Gold600 = "#A89433";
    public const string TranscriptMe = "#FDECEF";
    public const string TranscriptOthers = "#EFF1F3";

    /// <summary>Participant colours in fixed order (spec 5.5): #225D88, #356851, #146A59, #6B588A, red-600, gray-700.</summary>
    public static readonly string[] SpeakerColors = ["#225D88", "#356851", "#146A59", "#6B588A", Accent, Muted];

    /// <summary>A participant's colour by its stored index (cycled; a negative index counts as 0).</summary>
    public static string SpeakerColor(int colorIndex) => SpeakerColors[Math.Max(0, colorIndex) % SpeakerColors.Length];
}

/// <summary>Foreground and background of one folder colour.</summary>
public sealed record FolderColors(string Foreground, string Background);

/// <summary>One of the ten folder icons: the macOS SF Symbol name stored in folders.json, its label and the Windows icon.</summary>
public sealed record FolderIconOption(string Symbol, string Label, IconKind Kind);

/// <summary>Folder icons and colours of spec 9.6, keyed by the values macOS writes to folders.json.</summary>
public static class FolderStyles
{
    public static IReadOnlyList<FolderIconOption> Icons { get; } =
    [
        new("folder.fill", Strings.FolderIconFolder, IconKind.Folder),
        new("briefcase.fill", Strings.FolderIconWork, IconKind.Work),
        new("person.2.fill", Strings.FolderIconTeam, IconKind.Team),
        new("lightbulb.fill", Strings.FolderIconIdeas, IconKind.Ideas),
        new("graduationcap.fill", Strings.FolderIconLearning, IconKind.Learning),
        new("leaf.fill", Strings.FolderIconLeaf, IconKind.Leaf),
        new("star.fill", Strings.FolderIconStar, IconKind.Star),
        new("heart.fill", Strings.FolderIconHeart, IconKind.Heart),
        new("house.fill", Strings.FolderIconHome, IconKind.Home),
        new("wrench.and.screwdriver.fill", Strings.FolderIconProjects, IconKind.Projects)
    ];

    /// <summary>Unknown symbols (written by a newer version) show the folder icon.</summary>
    public static FolderIconOption Icon(string? symbol) => Icons.FirstOrDefault(option => option.Symbol == symbol) ?? Icons[0];

    public static FolderColors Colors(FolderColor color) => color switch
    {
        FolderColor.Blue => new("#1D5AA6", "#DAE9FB"),
        FolderColor.Green => new("#356851", "#E0EDDA"),
        FolderColor.Purple => new("#6940A4", "#E9DDFA"),
        FolderColor.Orange => new("#934B12", "#FCE6CF"),
        FolderColor.Pink => new("#9B3563", "#F8DEEA"),
        FolderColor.Teal => new("#146A70", "#D5EEF0"),
        FolderColor.Red => new("#A12C32", "#FADDDD"),
        FolderColor.Yellow => new("#7A610C", "#FAF0C8"),
        FolderColor.Brown => new("#795039", "#EEDFD4"),
        _ => new("#555D68", "#E5E8EC")
    };

    public static FolderColors Colors(MeetingFolder folder) => Colors(folder.Color ?? FolderColor.Blue);

    public static string ColorName(FolderColor color) => color switch
    {
        FolderColor.Blue => Strings.ColorBlue, FolderColor.Green => Strings.ColorGreen, FolderColor.Purple => Strings.ColorPurple,
        FolderColor.Orange => Strings.ColorOrange, FolderColor.Pink => Strings.ColorPink, FolderColor.Teal => Strings.ColorTeal,
        FolderColor.Red => Strings.ColorRed, FolderColor.Yellow => Strings.ColorYellow, FolderColor.Brown => Strings.ColorBrown,
        _ => Strings.ColorGray
    };
}

/// <summary>Dates as the German UI shows them (locale de-DE; the abbreviated month carries its own dot: "Nov.").</summary>
public static class Formats
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Library rows: "1. Nov. · 14:05".</summary>
    public static string ListDate(DateTimeOffset value) => value.ToLocalTime().ToString("d. MMM", German) + " · " + value.ToLocalTime().ToString("HH:mm", German);

    /// <summary>Editor header and preview: "1. November 2026, 14:05".</summary>
    public static string LongDate(DateTimeOffset value) => value.ToLocalTime().ToString("d. MMMM yyyy', 'HH:mm", German);

    /// <summary>To-do links: "1. Nov. 2026".</summary>
    public static string ShortDate(DateTimeOffset value) => value.ToLocalTime().ToString("d. MMM yyyy", German);

    public static string Seconds(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}

/// <summary>Areas of the settings window (spec 9.3).</summary>
public enum SettingsArea { Recording, AiLanguage, Storage }

/// <summary>The two answers of "Save and end meeting?".</summary>
public enum QuitChoice { ReturnToMeeting, SaveAndEnd }

/// <summary>What the quit dialog shows: "Save and end meeting?", or "Library is moving…" with only "Return" (Core refuses to quit then).</summary>
public enum QuitPrompt { SaveAndEnd, LibraryMoving }

/// <summary>A participant offered by "Assign this segment…", with its colour.</summary>
public sealed record SpeakerOption(Guid ParticipantID, string Name, string Color);

/// <summary>The answer of "Assign this segment…": an existing participant, Others (unknown, both null) or a new participant.</summary>
public sealed record SpeakerAssignment(Guid? ParticipantID, string? NewName = null)
{
    public static SpeakerAssignment Unknown { get; } = new((Guid?)null);
    public static SpeakerAssignment Existing(Guid participantID) => new(participantID);
    public static SpeakerAssignment Named(string name) => new(null, name);
}

/// <summary>Callbacks on the UI thread; WPF uses dispatcher timers, the tests advance a manual clock.</summary>
public interface IUiScheduler
{
    /// <summary>Runs <paramref name="action"/> once after <paramref name="delay"/>; disposing the result cancels it.</summary>
    IDisposable Schedule(TimeSpan delay, Action action);

    /// <summary>Runs <paramref name="action"/> every <paramref name="interval"/> until disposed.</summary>
    IDisposable Every(TimeSpan interval, Action action);
}

/// <summary>Everything a view model asks of the user or the shell. The WPF implementation lives in Shell\WpfDialogService.</summary>
public interface IDialogService
{
    bool Confirm(string title, string message, string confirm);
    string? AskMeetingName(string current);
    QuitChoice AskSaveAndEnd(QuitPrompt prompt = QuitPrompt.SaveAndEnd);
    /// <summary>"Rename speaker…": the new name, or null when cancelled.</summary>
    string? AskSpeakerName(string current);
    /// <summary>"Assign this segment…" for the turn <paramref name="excerpt"/>; null when cancelled.</summary>
    SpeakerAssignment? AskSpeakerAssignment(string excerpt, IReadOnlyList<SpeakerOption> participants);
    void ShowSettings(SettingsArea area);
    void ShowOnboarding();
    bool EditFolder(FolderEditorViewModel editor);
    void ShowTaskReview(Guid meetingID);
    string? AskSaveMarkdown(string fileName);
    string? AskFolder(string title, string? initial);
    string? AskFile(string filter, string? initial);
    void Inform(string title, string message);
    /// <summary>Opens Explorer with <paramref name="path"/> selected (<c>explorer /select,</c>).</summary>
    void Reveal(string path);
    void OpenFolder(string path);
    void OpenUri(string uri);
    void CopyText(string text);
}

/// <summary>
/// Per-user UI state (macOS <c>@AppStorage</c>): kept in a folder next to the library pointer, never as a .json file in a
/// library root (those are read as meetings).
/// </summary>
public sealed class UiState
{
    public bool IntroductionDismissed { get; set; }
    /// <summary>0 = automatic height of the live transcript box.</summary>
    public double TranscriptHeight { get; set; }
    /// <summary>
    /// Wallfly Detector's sign-in start was registered once (first launch, macOS <c>meetingDetectionRegistrationAttempted</c>);
    /// later the Run key only changes through the "Detect Teams meetings automatically" toggle.
    /// </summary>
    public bool DetectorRegistrationAttempted { get; set; }
}

public sealed class UiStateStore
{
    private readonly string path;
    public UiState State { get; }

    public UiStateStore(string directory)
    {
        path = Path.Combine(directory, "ui-state.json");
        try { State = File.Exists(path) ? JsonSerializer.Deserialize<UiState>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { State = new(); }
    }

    /// <summary>Best effort: a failure only loses a preference.</summary>
    public void Save()
    {
        try { MeetingLibrary.AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(State)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Windows-only settings that <see cref="AppSettings"/> has no property for. They live in settings.json through
/// <see cref="AppSettings.AdditionalData"/>, so a Mac reading the same library ignores them and every writer keeps them.
/// "Detect Teams meetings automatically" and "Hide from screen sharing" are Core properties
/// (<see cref="AppSettings.DetectTeamsMeetings"/>, <see cref="AppSettings.HideIndicatorFromScreenCapture"/>; the same keys
/// <c>windowsDetectTeamsMeetings</c> / <c>windowsHideIndicatorFromCapture</c> an earlier build kept here).
/// The microphone pre-filter has no Core property (by design: it only concerns Wallfly Detector). Its saved value stays
/// here, so the settings draft, "Done" and the undo of an unsaved toggle work like the other helper toggle; what the helper
/// reads is DetectionStorage's configuration.json (<c>micPrefilter</c>), written by every apply.
/// </summary>
public static class AppSettingsExtras
{
    /// <summary>Wallfly Detector reads Teams only while Teams holds the microphone (configuration.json <c>micPrefilter</c>).</summary>
    public const string DetectorMicPrefilterKey = "windowsDetectorMicPrefilter";

    public static bool DetectorMicPrefilter(this AppSettings settings) => Get(settings, DetectorMicPrefilterKey, false);
    public static void SetDetectorMicPrefilter(this AppSettings settings, bool value) => Set(settings, DetectorMicPrefilterKey, value);

    private static bool Get(AppSettings settings, string key, bool fallback) =>
        settings.AdditionalData != null && settings.AdditionalData.TryGetValue(key, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;

    private static void Set(AppSettings settings, string key, bool value) =>
        (settings.AdditionalData ??= [])[key] = JsonSerializer.SerializeToElement(value);
}

public enum CloseAction { HideToTray, Quit }
public enum QuitAction { QuitNow, AskSaveAndEnd }

/// <summary>Closing and quitting (spec 9.1/9.6, orchestrator decision): closing hides while a recording runs, otherwise it quits.</summary>
public static class QuitPolicy
{
    public static CloseAction OnClose(bool recordingSession) => recordingSession ? CloseAction.HideToTray : CloseAction.Quit;

    /// <summary>Ctrl+Q, the tray "Quit" and closing outside a recording: running work needs "Save and end meeting?".</summary>
    public static QuitAction OnQuit(bool busy) => busy ? QuitAction.AskSaveAndEnd : QuitAction.QuitNow;
}

/// <summary>Height rules of the live transcript box (port of <c>MeetingWorkspace.transcriptHeight</c>).</summary>
public static class TranscriptBoxLayout
{
    /// <summary>Below the editor header a dragged box still leaves room for the note actions, some writing space and the recording bar.</summary>
    public const double EditorChrome = 160;
    public const double Minimum = 120;
    public const double TallestHeader = 240;

    /// <summary>Narrow windows trade width for height: the share grows and the 300 px cap fades out.</summary>
    public static double Automatic(double width, double height)
    {
        var t = Math.Min(Math.Max((800 - width) / 360, 0), 1);
        var share = 0.42 + 0.16 * t;
        var cap = 300 + t * height;
        return Math.Min(cap, height * share);
    }

    /// <summary>A dragged height wins over the automatic one, clamped so the editor header stays unclipped.</summary>
    public static double Height(double width, double height, double? preferred, double header = TallestHeader)
    {
        if (preferred is not double value) return Math.Max(Minimum, Automatic(width, height));
        return Math.Min(Math.Max(value, Minimum), Math.Max(Minimum, height - header - EditorChrome));
    }
}

/// <summary>The drag payload of a meeting row (port of <c>MeetingDrag</c>): the UUID as text, at most 64 characters.</summary>
public static class MeetingDragPayload
{
    public const string Format = "Wallfly.MeetingID";

    public static string Encode(Guid id) => id.ToString("D").ToUpperInvariant();

    public static Guid? Decode(object? data) =>
        data is string text && text.Length <= 64 && Guid.TryParse(text, out var id) && id != Guid.Empty ? id : null;
}

/// <summary>A piece of text with or without search highlight.</summary>
public readonly record struct HighlightRun(string Text, bool Match);

public static class SearchHighlight
{
    /// <summary>Splits <paramref name="text"/> at the matches of the query's terms (same folding as the library search).</summary>
    public static IReadOnlyList<HighlightRun> Split(string text, string? query)
    {
        var terms = MeetingSearch.Terms(query);
        if (terms.Count == 0 || text.Length == 0) return [new(text, false)];
        var runs = new List<HighlightRun>();
        var position = 0;
        foreach (var range in MeetingSearch.Ranges(text, terms))
        {
            if (range.Start > position) runs.Add(new(text[position..range.Start], false));
            runs.Add(new(text.Substring(range.Start, Math.Min(range.Length, text.Length - range.Start)), true));
            position = Math.Min(text.Length, range.End);
        }
        if (position < text.Length) runs.Add(new(text[position..], false));
        return runs;
    }
}

/// <summary>Microphone consent of Windows (Settings → Privacy &amp; security → Microphone).</summary>
public enum MicrophoneAccess { Unknown, Allowed, Denied }

/// <summary>System facts the settings and onboarding need; injectable so tests and the smoke test never touch devices.</summary>
public interface ISystemProbe
{
    IReadOnlyList<AudioDevice> Devices(AudioFlow flow);
    IReadOnlyList<AudioSessionEntry> AudioSessions();
    MicrophoneAccess MicrophoneAccess();
    bool FileExists(string path);
}

/// <summary>A label and the value it stands for (combo boxes).</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}
