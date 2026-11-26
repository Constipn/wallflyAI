using System.Net.Sockets;

namespace Wallfly.Ai;

/// <summary>
/// <c>Configuration</c> is fixed in Settings (the banner offers "Open settings"); everything else may pass on a retry
/// ("Try again"). Port of <c>EnhancementServiceError.Kind</c>; the plain <c>MeetingError</c> messages of the macOS app
/// are <see cref="Transient"/>, because the macOS banner offers the retry for them as well.
/// </summary>
public enum AiErrorKind { Configuration, Transient }

/// <summary>The network failure classes the macOS app distinguishes (<c>URLError.Code</c>).</summary>
public enum NetworkFailure { NotConnected, TimedOut, Unreachable, Other }

/// <summary>
/// A failed AI request. Derives from <see cref="InvalidOperationException"/> so existing callers show its message.
/// The message never contains a response body or a key.
/// </summary>
public sealed class AiError : InvalidOperationException
{
    private const string Saved = " Original notes are saved.";

    public AiErrorKind Kind { get; }

    public AiError(AiErrorKind kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;

    public static AiError Configuration(string message) => new(AiErrorKind.Configuration, message);
    public static AiError Transient(string message, Exception? inner = null) => new(AiErrorKind.Transient, message, inner);

    /// <summary>Port of <c>EnhancementServiceError.http</c>.</summary>
    public static AiError Http(int status) => status switch
    {
        401 or 403 => Configuration($"The enhancement service rejected the API key (HTTP {status}). Check the key and permissions in Settings." + Saved),
        404 => Configuration("Model or API address not found (HTTP 404). Check the model and address in Settings." + Saved),
        429 => Transient("The enhancement service usage limit has been reached (HTTP 429). Try again later." + Saved),
        >= 500 and < 600 => Transient($"The enhancement service is currently unavailable (HTTP {status}). Try again later." + Saved),
        _ => Configuration($"The enhancement service reported HTTP {status}. Check the address, model and API key in Settings." + Saved)
    };

    /// <summary>Port of <c>EnhancementServiceError.network</c>.</summary>
    public static AiError Network(NetworkFailure failure, Exception? inner = null) => failure switch
    {
        NetworkFailure.NotConnected => Transient("No internet connection to the enhancement service. Check the connection and try again." + Saved, inner),
        NetworkFailure.TimedOut => Transient("The enhancement service did not respond in time. Try again." + Saved, inner),
        NetworkFailure.Unreachable => Transient("The enhancement service is unreachable. Check the internet connection and the API address in Settings." + Saved, inner),
        _ => Transient("The connection to the enhancement service failed. Try again." + Saved, inner)
    };

    /// <summary>Maps an <see cref="HttpClient"/> failure onto the macOS <c>URLError</c> classes.</summary>
    public static NetworkFailure Classify(Exception failure, bool networkAvailable)
    {
        if (!networkAvailable) return NetworkFailure.NotConnected;
        for (var inner = failure; inner != null; inner = inner.InnerException)
        {
            if (inner is SocketException socket)
                return socket.SocketErrorCode switch
                {
                    SocketError.NetworkDown or SocketError.NetworkUnreachable or SocketError.ConnectionReset
                        or SocketError.ConnectionAborted or SocketError.Disconnecting => NetworkFailure.NotConnected,
                    SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData or SocketError.HostUnreachable
                        or SocketError.ConnectionRefused or SocketError.HostDown => NetworkFailure.Unreachable,
                    SocketError.TimedOut => NetworkFailure.TimedOut,
                    _ => NetworkFailure.Other
                };
        }
        if (failure is HttpRequestException http)
            return http.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError => NetworkFailure.Unreachable,
                HttpRequestError.ResponseEnded => NetworkFailure.NotConnected,
                _ => NetworkFailure.Other
            };
        return NetworkFailure.Other;
    }
}

/// <summary>User-visible wording of the AI layer, verbatim from the macOS 1.13.0 sources. English.</summary>
public static class AiMessages
{
    // Enhancer.swift
    public const string NoTranscript = "There is no transcript to enhance yet. Your original notes are saved.";
    public const string Creating = "Creating notes, tasks and title…";
    public const string CorrectingLanguage = "Correcting the language of the enhanced notes…";
    public const string WrongLanguageAgain = "The model again returned the notes in a different language than the meeting. Existing notes and transcript are saved; try again or choose another model.";
    public const string UnreadableEnhancement = "The model returned no readable enhancement. Your data is saved; try again.";
    public const string EmptyNotes = "The model returned empty notes. Your original notes are saved.";
    public const string InvalidTitle = "The model returned no short, single-line meeting title.";
    public const string EnhancementOff = "Enhancement is turned off. Transcript and notes are kept.";
    public const string InvalidAddress = "The API address is invalid.";
    public const string ChooseModel = "Choose a model for enhancement in Settings.";
    public const string EndpointWithCredentials = "Enter a valid API address without embedded credentials.";
    public const string EndpointNotHttps = "Use HTTPS for external APIs, or HTTP on localhost only.";
    public static string EnterKey(ChatProviderKind kind) => $"Enter your {ChatProviderNames.DisplayName(kind)} API key in Settings. Transcript and notes are kept.";
    public static string ClaudeIncomplete(string? reason) => $"Claude did not finish the response ({reason ?? "unknown"}). Existing notes are kept; try again.";
    public static string ModelIncomplete(string reason) => $"The model did not finish the response ({reason}). Your data is saved; try again.";
    /// <summary>The Foundation decoding message the macOS app shows for a malformed response body.</summary>
    public const string UnreadableData = "The data couldn’t be read because it isn’t in the correct format.";

    // MeetingChat.cs (Windows addition)
    public const string NothingToAskAbout = "This meeting has no notes or transcript to answer from yet.";
    public const string EmptyAnswer = "The model returned an empty answer. Try again.";

    // TaskExtractor.swift
    public static string LookingForTasks(int section, int count) => $"Looking for tasks · section {section} of {count}…";
    public static string MatchingTasks(int index, int count) => $"Matching tasks · {index} of {count}…";
    public const string InvalidMatch = "Task matching returned invalid data. Try again.";
    public const string InvalidMatchReference = "Task matching returned an invalid reference. Try again.";

    // MeetingStore.swift
    public const string LookingForAgreedTasks = "Looking for agreed tasks…";
    public const string NotesEnhanced = "Notes enhanced";
    public const string EnhancementFailedStatus = "Meeting saved · Enhancement failed";
    public const string EnhancementTurnedOffStatus = "Enhancement turned off · Transcript and notes are kept";
    public const string ConfigurationOff = "Enhancement is turned off. Transcript and your own notes remain available.";
    public const string ConfigurationChooseModel = "Choose a model for enhancement.";
    public static string ConfigurationKeyMissing(ChatProviderKind kind) => $"{ChatProviderNames.DisplayName(kind)} API key is missing. Transcript and your own notes are kept.";
    public static string TasksNotDetected(string title, string cause) =>
        $"Tasks for “{title}” could not be detected: {(cause.EndsWith('.') ? cause : cause + ".")} Repeat detection under “My to-dos”. Existing tasks are kept.";
    public const string TaskDecisionNotSavedPrefix = "The task decision could not be saved: ";
}
