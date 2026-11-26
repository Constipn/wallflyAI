using System.Globalization;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Wallfly.Core.Json;

namespace Wallfly.Core;

[JsonConverter(typeof(RawValueEnumConverter<EnhancementProvider>))]
public enum EnhancementProvider
{
    [EnumMember(Value = "Disabled")] Disabled,
    [EnumMember(Value = "Anthropic")] Anthropic,
    [EnumMember(Value = "OpenAI")] OpenAi,
    [EnumMember(Value = "Custom API")] Custom
}

[JsonConverter(typeof(RawValueEnumConverter<TranscriptionProvider>))]
public enum TranscriptionProvider
{
    [EnumMember(Value = "Deepgram")] Deepgram,
    [EnumMember(Value = "OpenWhispr · Whisper Turbo (local)")] LocalWhisper,
    [EnumMember(Value = "OpenAI")] OpenAi
}

/// <summary>Where the speaker separation of the "Others" channel runs after a meeting (Windows only).</summary>
[JsonConverter(typeof(RawValueEnumConverter<SpeakerSeparationEngine>))]
public enum SpeakerSeparationEngine
{
    /// <summary>Deepgram's pre-recorded API: the Others track is uploaded once more after the meeting.</summary>
    [EnumMember(Value = "deepgram")] Deepgram,
    /// <summary>The local sherpa-onnx model on this computer.</summary>
    [EnumMember(Value = "onDevice")] OnDevice
}

public static class ModelDefaults
{
    public const string OpenAiTranscription = "gpt-transcribe";
    public const string DeepgramTranscription = "nova-3";
    public const string LocalWhisperTranscription = "large-v3-turbo";
    /// <summary>Default notes/title/task model of the default provider (OpenAI), the first curated model.</summary>
    public const string Notes = "gpt-6-luna";
    /// <summary>Default notes model for Anthropic, the first curated model.</summary>
    public const string AnthropicNotes = "claude-sonnet-5-5";

    /// <summary>Swift <c>SummaryModels.defaultModel(for:)</c>: the first curated model; empty for the custom API and "Off".</summary>
    public static string NotesFor(EnhancementProvider provider) => provider switch
    {
        EnhancementProvider.OpenAi => Notes,
        EnhancementProvider.Anthropic => AnthropicNotes,
        _ => ""
    };

    public static string SupportedModel(TranscriptionProvider provider) => provider switch
    {
        TranscriptionProvider.Deepgram => DeepgramTranscription,
        TranscriptionProvider.LocalWhisper => LocalWhisperTranscription,
        _ => OpenAiTranscription
    };
}

/// <summary>
/// <c>settings.json</c>: every key of spec 8 / the macOS <c>AppSettings</c>, plus the Windows-only
/// render endpoint. Keys written by another version survive through <see cref="AdditionalData"/>.
/// No API keys are ever stored here.
/// </summary>
public sealed class AppSettings
{
    // Transcription
    /// <summary>Deepgram Nova-3 streaming is the default (spec 4.1); OpenAI stays selectable.</summary>
    public TranscriptionProvider TranscriptionProvider { get; set; } = TranscriptionProvider.Deepgram;
    public string TranscriptionModel { get; set; } = ModelDefaults.DeepgramTranscription;
    public int TranscriptionConfigurationVersion { get; set; } = 3;
    public string OpenWhisprAppPath { get; set; } = "";
    public string WhisperModelPath { get; set; } = "";
    /// <summary>"auto", "de-DE" or "en-US".</summary>
    public string Locale { get; set; } = "auto";

    // Speakers and Teams
    public bool TeamsSpeakerNames { get; set; } = true;
    public bool AcousticSpeakerRecognition { get; set; } = true;
    public bool AutoStopWhenMeetingEnds { get; set; } = true;

    // Audio
    public bool KeepAudio { get; set; } = true;
    public bool DeleteAudioAfterProcessing { get; set; }
    /// <summary>
    /// The manual source (with <see cref="AutomaticMeetingAudioSource"/> off): empty = the whole system by per-process
    /// capture; otherwise the meeting app whose process tree is captured.
    /// </summary>
    public string AudioSourceBundleID { get; set; } = "";
    /// <summary>"Automatic (system output, recommended)": device loopback of the output device (Windows; the stored key is unchanged).</summary>
    public bool AutomaticMeetingAudioSource { get; set; } = true;
    /// <summary>Windows endpoint id of the microphone (the macOS field name is kept).</summary>
    public string MicrophoneUID { get; set; } = "";
    /// <summary>Windows only: render endpoint used for the device-loopback fallback.</summary>
    public string OutputDeviceID { get; set; } = "";

    // AI
    public EnhancementProvider Provider { get; set; } = EnhancementProvider.OpenAi;
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = ModelDefaults.Notes;
    public Dictionary<string, string> EnhancementModels { get; set; } = [];
    public string AnthropicWorkspaceID { get; set; } = "";
    public string TaskOwnerName { get; set; } = "";

    // Misc
    public bool OnboardingCompleted { get; set; }
    public bool TranscriptMirrorEnabled { get; set; }
    public TranscriptMirrorInfo? TranscriptMirrorFolder { get; set; }
    public bool PersonSummariesEnabled { get; set; } = true;

    // Windows only (a Mac reading the same library keeps them as unknown keys)
    /// <summary>The Teams call reminder ("Take notes" popup of Wallfly Detector) is on.</summary>
    [JsonPropertyName("windowsDetectTeamsMeetings")] public bool DetectTeamsMeetings { get; set; } = true;
    /// <summary>The recording capsule is hidden from screen sharing and screenshots (<c>WDA_EXCLUDEFROMCAPTURE</c>).</summary>
    [JsonPropertyName("windowsHideIndicatorFromCapture")] public bool HideIndicatorFromScreenCapture { get; set; } = true;
    /// <summary>
    /// "Custom vocabulary": names and terms the live Deepgram transcription should recognise (sent as keyterms after the
    /// user's own name and the Teams participants). One trimmed, non-empty term per entry.
    /// </summary>
    [JsonPropertyName("windowsCustomVocabulary")] public List<string> CustomVocabulary { get; set; } = [];

    /// <summary>
    /// The engine used when the user never chose one (<see cref="SpeakerSeparation"/> null). Making Deepgram the default
    /// after the Phase-B measurement is this one line: users who never picked an engine follow it, explicit choices stay.
    /// </summary>
    public const SpeakerSeparationEngine DefaultSpeakerSeparation = SpeakerSeparationEngine.OnDevice;

    /// <summary>
    /// "Speaker separation": the user's explicit choice; null (not written) follows <see cref="DefaultSpeakerSeparation"/>.
    /// Read <see cref="EffectiveSpeakerSeparation"/>.
    /// </summary>
    [JsonPropertyName("windowsSpeakerSeparation")] public SpeakerSeparationEngine? SpeakerSeparation { get; set; }

    [JsonIgnore] public SpeakerSeparationEngine EffectiveSpeakerSeparation => SpeakerSeparation ?? DefaultSpeakerSeparation;

    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }

    /// <summary>
    /// The decode normalization of the macOS <c>AppSettings.init(from:)</c>: a configuration older than
    /// version 3 selects Deepgram and the Teams speaker names once, the transcription model follows the
    /// provider, the locale collapses to auto/de-DE/en-US, the removed "don't keep audio" choice becomes
    /// automatic deletion, and the configuration version is lifted to 3. Unlike macOS, a file without a
    /// version counts as current: every Windows build writes the version.
    /// </summary>
    public AppSettings Normalize()
    {
        if (TranscriptionConfigurationVersion < 3)
        {
            TranscriptionProvider = TranscriptionProvider.Deepgram;
            TeamsSpeakerNames = true;
        }
        TranscriptionModel = ModelDefaults.SupportedModel(TranscriptionProvider);
        Locale = NormalizeLocale(Locale);
        if (Endpoint == "http://localhost:11434/v1/chat/completions" && Provider != EnhancementProvider.Custom) Endpoint = "";
        if (!KeepAudio) { KeepAudio = true; DeleteAudioAfterProcessing = true; }
        CustomVocabulary ??= [];
        TranscriptionConfigurationVersion = 3;
        return this;
    }

    /// <summary>
    /// Reads <c>settings.json</c> with the decode rules of the macOS <c>AppSettings.init(from:)</c> that a plain
    /// deserialization cannot express: a removed or unknown enhancement provider (Local Ollama, Apple Intelligence, …)
    /// becomes "Off" with an empty model, a missing model is the default of the stored provider, and the removed
    /// "On this Mac" transcription choice uses the current default. Then <see cref="Normalize"/>.
    /// </summary>
    /// <exception cref="JsonException">The file is not a JSON object or another value is invalid.</exception>
    public static AppSettings Decode(string json)
    {
        var node = JsonNode.Parse(json, new JsonNodeOptions { PropertyNameCaseInsensitive = true }) as JsonObject
            ?? throw new JsonException("settings.json is not a JSON object.");
        var removedProvider = false;
        if (RawString(node, "provider") is { } provider && !RawValueEnumConverter<EnhancementProvider>.TryParse(provider, out _))
        {
            node.Remove("provider");
            removedProvider = true;
        }
        if (RawString(node, "transcriptionProvider") == "On this Mac") node.Remove("transcriptionProvider");
        var hasModel = node.ContainsKey("model");
        var hasAutomaticSource = node.ContainsKey("automaticMeetingAudioSource");
        var settings = node.Deserialize<AppSettings>(WallflyJson.Options) ?? new AppSettings();
        // A file from before the automatic source keeps a manual choice of another app; no source or Teams becomes automatic.
        if (!hasAutomaticSource)
            settings.AutomaticMeetingAudioSource = settings.AudioSourceBundleID.Trim().Length == 0 || IsTeamsSource(settings.AudioSourceBundleID);
        if (removedProvider)
        {
            settings.Provider = EnhancementProvider.Disabled;
            settings.Model = "";
        }
        else if (!hasModel) settings.Model = ModelDefaults.NotesFor(settings.Provider);
        return settings.Normalize();
    }

    /// <summary>New Teams as stored by Windows (<c>ms-teams.exe</c>) or by a Mac sharing the library (<c>com.microsoft.teams2</c>).</summary>
    private static bool IsTeamsSource(string source) =>
        string.Equals(source.Trim(), "ms-teams.exe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(source.Trim(), "com.microsoft.teams2", StringComparison.OrdinalIgnoreCase);

    private static string? RawString(JsonObject node, string key) =>
        node.TryGetPropertyValue(key, out var value) && value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;

    public static string NormalizeLocale(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return "auto";
        string language;
        try { language = CultureInfo.GetCultureInfo(locale).TwoLetterISOLanguageName; }
        catch (CultureNotFoundException) { language = locale.Split('-', '_')[0].ToLowerInvariant(); }
        return language switch { "de" => "de-DE", "en" => "en-US", _ => "auto" };
    }

    /// <summary>The two user-facing audio choices, stored in the two macOS flags.</summary>
    [JsonIgnore]
    public bool KeepAudioAfterProcessing
    {
        get => KeepAudio && !DeleteAudioAfterProcessing;
        set { KeepAudio = true; DeleteAudioAfterProcessing = !value; }
    }
}
