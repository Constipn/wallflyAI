using Wallfly.Ai;
using Wallfly.Core.Credentials;
using Wallfly.Core.Text;

namespace Wallfly.Core.Services;

/// <summary>What the caller has to do between the steps of one enhancement run.</summary>
public sealed record PostProcessingCallbacks(Action<string> Progress, Func<Meeting, bool> Persist, Action<string> Error);

/// <summary>The one action the error banner offers next to an AI failure.</summary>
public enum EnhancementRecoveryAction { OpenSettings, Retry }

/// <summary>Port of <c>ErrorRecovery</c>: valid only while the banner still shows <see cref="Message"/>.</summary>
public sealed record EnhancementRecovery(string Message, EnhancementRecoveryAction Action, Guid MeetingID, bool TasksOnly);

/// <summary>
/// Everything that happens to a meeting after the recording, ported from <c>MeetingStore.enhanceInOnePass</c>,
/// <c>extractTasks</c> and the task review of macOS 1.13.0. It runs only on an explicit user action. One request
/// produces enhanced notes, personal to-do suggestions (<c>review = null</c>) and, for unnamed meetings, a title.
/// A failure never touches the user's notes, the previous enhanced notes or existing tasks; it only marks the task
/// recognition as failed so it can be retried.
/// </summary>
public sealed class PostProcessingService
{
    private readonly Func<IChatProvider> providerFactory;
    private readonly Func<AppSettings> settings;
    private readonly ILanguageDetector? languageDetector;

    /// <param name="providerFactory">Called once per run, inside the error handling, so configuration errors mark the run failed.</param>
    /// <param name="settings">The current settings: provider, model, and the name used for personal tasks.</param>
    /// <param name="languageDetector">Null uses the offline Lingua detector.</param>
    public PostProcessingService(Func<IChatProvider> providerFactory, Func<AppSettings>? settings = null, ILanguageDetector? languageDetector = null)
    {
        this.providerFactory = providerFactory;
        this.settings = settings ?? (() => new AppSettings());
        this.languageDetector = languageDetector;
        EngineLabel = () => EngineLabelFor(this.settings());
    }

    /// <summary>Stored as <c>enhancementEngine</c>, for example "OpenAI · gpt-6-luna".</summary>
    public Func<string> EngineLabel { get; init; }

    /// <summary>
    /// <paramref name="tasksOnly"/> is the "Retry to-do recognition" path for meetings whose recognition is pending
    /// or failed. Returns true when new enhanced notes were saved. AI failures are rethrown (an <see cref="AiError"/>
    /// carries the banner action); cancellation leaves the meeting as it is.
    /// </summary>
    public async Task<bool> EnhanceAsync(Meeting meeting, bool tasksOnly, PostProcessingCallbacks callbacks, CancellationToken cancellationToken)
    {
        var configuration = settings();
        if (configuration.Provider == EnhancementProvider.Disabled) return false;
        return tasksOnly
            ? await ExtractTasksAsync(meeting, configuration, callbacks, cancellationToken).ConfigureAwait(true)
            : await EnhanceInOnePassAsync(meeting, configuration, callbacks, cancellationToken).ConfigureAwait(true);
    }

    private async Task<bool> EnhanceInOnePassAsync(Meeting meeting, AppSettings configuration, PostProcessingCallbacks callbacks, CancellationToken cancellationToken)
    {
        meeting.TaskExtraction = TaskExtractionState.Pending;
        callbacks.Persist(meeting);
        var snapshot = Json.WallflyJson.Copy(meeting);
        callbacks.Progress(AiMessages.Creating);
        IChatProvider? provider = null;
        try
        {
            provider = providerFactory();
            var result = await new NotesEnhancer(provider, languageDetector, TimestampCitations.Strip)
                .EnhanceAllAsync(Input(snapshot, configuration.TaskOwnerName), callbacks.Progress, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            meeting.EnhancedNotes = result.Notes;
            meeting.EnhancementEngine = EngineLabel();
            meeting.MergeTasks(result.Tasks.Where(task => PersonalTasks.IsMine(task.Owner, configuration.TaskOwnerName)));
            meeting.TaskExtraction = TaskExtractionState.Complete;
            if (result.Title is { } title && meeting.NeedsSuggestedTitle) meeting.Title = title;
            return callbacks.Persist(meeting);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            meeting.TaskExtraction = TaskExtractionState.Failed;
            callbacks.Persist(meeting);
            throw;
        }
        finally { (provider as IDisposable)?.Dispose(); }
    }

    private async Task<bool> ExtractTasksAsync(Meeting meeting, AppSettings configuration, PostProcessingCallbacks callbacks, CancellationToken cancellationToken)
    {
        // A meeting that never opted in (no recognition state) is not extracted by a retry.
        if (meeting.TaskExtraction is not (TaskExtractionState.Pending or TaskExtractionState.Failed)) return false;
        var snapshot = Json.WallflyJson.Copy(meeting);
        callbacks.Progress(AiMessages.LookingForAgreedTasks);
        IChatProvider? provider = null;
        try
        {
            provider = providerFactory();
            var drafts = await new TaskExtractor(provider, languageDetector)
                .ExtractAsync(TaskInput(snapshot, configuration.TaskOwnerName), callbacks.Progress, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            meeting.MergeTasks(drafts.Where(task => PersonalTasks.IsMine(task.Owner, configuration.TaskOwnerName)));
            meeting.TaskExtraction = TaskExtractionState.Complete;
            callbacks.Persist(meeting);
            return false;
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            meeting.TaskExtraction = TaskExtractionState.Failed;
            callbacks.Persist(meeting);
            throw new AiError((failure as AiError)?.Kind ?? AiErrorKind.Transient, AiMessages.TasksNotDetected(snapshot.Title, failure.Message), failure);
        }
        finally { (provider as IDisposable)?.Dispose(); }
    }

    // Inputs

    /// <summary>The ordered passage texts without timestamps or names; the output language is detected from them.</summary>
    public static string SpokenText(Meeting meeting) => string.Join('\n', meeting.OrderedTranscript.Select(segment => segment.Text));

    private static List<ExistingTaskReference> ExistingTasks(Meeting meeting) =>
        (meeting.Tasks ?? []).Select(task => new ExistingTaskReference(task.Id, task.Text, task.Owner)).ToList();

    public static EnhancementInput Input(Meeting meeting, string? taskOwnerName) =>
        new(meeting.Title, meeting.Notes, meeting.TranscriptText, SpokenText(meeting), ExistingTasks(meeting), taskOwnerName ?? "");

    public static TaskExtractionInput TaskInput(Meeting meeting, string? taskOwnerName) =>
        new(meeting.Notes, meeting.TranscriptText, SpokenText(meeting), ExistingTasks(meeting), taskOwnerName ?? "");

    // Providers and settings

    /// <summary>The raw value stored in settings.json, also the key of <c>enhancementModels</c>.</summary>
    public static string RawName(EnhancementProvider provider) => provider switch
    {
        EnhancementProvider.Anthropic => "Anthropic",
        EnhancementProvider.OpenAi => "OpenAI",
        EnhancementProvider.Custom => "Custom API",
        _ => "Disabled"
    };

    public static ChatProviderKind? Kind(EnhancementProvider provider) => provider switch
    {
        EnhancementProvider.Anthropic => ChatProviderKind.Anthropic,
        EnhancementProvider.OpenAi => ChatProviderKind.OpenAi,
        EnhancementProvider.Custom => ChatProviderKind.Custom,
        _ => null
    };

    /// <summary>Anthropic has its own key; OpenAI and the custom API share the OpenAI key slot, as on the Mac.</summary>
    public static CredentialAccount KeyAccount(EnhancementProvider provider) =>
        provider == EnhancementProvider.Anthropic ? CredentialAccount.AnthropicEnhancement : CredentialAccount.Enhancement;

    /// <summary>"Provider · model", stored with the enhanced notes.</summary>
    public static string EngineLabelFor(AppSettings settings) => $"{RawName(settings.Provider)} · {settings.Model.Trim()}";

    /// <summary>The configured HTTP provider; validation of key, model and address happens before its first request.</summary>
    public static IChatProvider CreateProvider(AppSettings settings, string? apiKey, HttpMessageHandler? handler = null) =>
        Kind(settings.Provider) is { } kind
            ? ChatProviders.Create(new(kind, settings.Model, apiKey ?? "", settings.Endpoint, settings.AnthropicWorkspaceID), handler)
            : throw AiError.Configuration(AiMessages.EnhancementOff);

    /// <summary>Port of <c>enhancementConfigurationIssue</c>: null when enhancement can run.</summary>
    public static string? ConfigurationIssue(AppSettings settings, string? apiKey)
    {
        if (Kind(settings.Provider) is not { } kind) return AiMessages.ConfigurationOff;
        if (string.IsNullOrWhiteSpace(settings.Model)) return AiMessages.ConfigurationChooseModel;
        if (kind != ChatProviderKind.Custom && string.IsNullOrWhiteSpace(apiKey)) return AiMessages.ConfigurationKeyMissing(kind);
        return kind == ChatProviderKind.Custom ? ChatProviders.EndpointIssue(settings.Endpoint) : null;
    }

    /// <summary>Port of <c>selectEnhancementProvider</c>: the model is remembered per provider.</summary>
    public static void SelectProvider(AppSettings settings, EnhancementProvider provider)
    {
        if (provider == settings.Provider) return;
        settings.Model = ModelCatalog.ModelAfterSwitch(settings.EnhancementModels, RawName(settings.Provider), settings.Model, RawName(provider), Kind(provider));
        settings.Provider = provider;
    }

    // Task review

    /// <summary>Personal suggestions still waiting for "Accept" or "Not my task".</summary>
    public static List<MeetingTask> PendingTasks(Meeting meeting, string? taskOwnerName) =>
        (meeting.Tasks ?? []).Where(task => task.Review == null && PersonalTasks.IsMine(task.Owner, taskOwnerName)).ToList();

    /// <summary>Only accepted tasks appear under "My to-dos".</summary>
    public static IEnumerable<MeetingTask> AcceptedTasks(Meeting meeting, bool completed) =>
        (meeting.Tasks ?? []).Where(task => task.Review == TaskReview.Accepted && task.IsCompleted == completed);

    /// <summary>Records the decision once; returns the task, or null when it does not exist or was already reviewed.</summary>
    public static MeetingTask? Review(Meeting meeting, Guid taskID, bool accept)
    {
        var task = meeting.Tasks?.FirstOrDefault(item => item.Id == taskID);
        if (task == null || task.Review != null) return null;
        task.Review = accept ? TaskReview.Accepted : TaskReview.Dismissed;
        return task;
    }

    /// <summary>Settings for configuration failures; otherwise the same retry the meeting's own button offers.</summary>
    public static EnhancementRecovery Recovery(Exception failure, string message, Guid meetingID, bool tasksOnly) =>
        new(message, (failure as AiError)?.Kind == AiErrorKind.Configuration ? EnhancementRecoveryAction.OpenSettings : EnhancementRecoveryAction.Retry,
            meetingID, tasksOnly);
}
