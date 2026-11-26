using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wallfly.Ai;
using Wallfly.Core;
using Wallfly.Core.Services;

namespace Wallfly.App.ViewModels;

/// <summary>
/// The "Enhancement" and "My to-dos" parts of AI &amp; language (port of <c>SummarySection</c> / <c>TaskOwnerSection</c>,
/// re-homed from the former EnhancementSettingsSection): service, curated model or free model ID, key per provider,
/// "Create API key", the custom API address, the Anthropic workspace ID and "Your name". Keys are only stored, never verified.
/// </summary>
public sealed class EnhancementSettingsViewModel : ObservableObject
{
    private readonly AppSettings draft;
    private readonly Func<string> openAiKey;
    private string modelText;
    private string endpoint;
    private string workspace;
    private string ownerName;
    private string anthropicKey;

    public EnhancementSettingsViewModel(AppSettings draft, string anthropicKey, Func<string> openAiKey, IDialogService dialogs)
    {
        this.draft = draft;
        this.openAiKey = openAiKey;
        this.anthropicKey = anthropicKey;
        modelText = draft.Model;
        endpoint = draft.Endpoint;
        workspace = draft.AnthropicWorkspaceID;
        ownerName = draft.TaskOwnerName;
        Providers =
        [
            new(EnhancementProvider.Disabled, Strings.EnhancementProviderOff), new(EnhancementProvider.Anthropic, Strings.EnhancementProviderAnthropic),
            new(EnhancementProvider.OpenAi, Strings.EnhancementProviderOpenAi), new(EnhancementProvider.Custom, Strings.EnhancementProviderCustom)
        ];
        CreateKeyCommand = new RelayCommand(() => dialogs.OpenUri(CreateKeyUri));
    }

    public IReadOnlyList<Choice<EnhancementProvider>> Providers { get; }
    public IRelayCommand CreateKeyCommand { get; }

    public Choice<EnhancementProvider> SelectedProvider
    {
        get => Providers.First(choice => choice.Value == draft.Provider);
        set
        {
            if (value == null || value.Value == draft.Provider) return;
            draft.Model = modelText.Trim();
            PostProcessingService.SelectProvider(draft, value.Value);
            modelText = draft.Model;
            OnPropertyChanged(string.Empty);
        }
    }

    private ChatProviderKind? Kind => PostProcessingService.Kind(draft.Provider);
    public bool DetailsVisible => Kind != null;
    public IReadOnlyList<ModelChoice> Models => ModelCatalog.Choices(Kind);

    /// <summary>The editable model box: a curated model's ID or any other model ID.</summary>
    public string ModelText
    {
        get => modelText;
        set { if (SetProperty(ref modelText, value ?? "")) Status(); }
    }

    public string Endpoint { get => endpoint; set { if (SetProperty(ref endpoint, value ?? "")) Status(); } }
    public string WorkspaceID { get => workspace; set => SetProperty(ref workspace, value ?? ""); }
    public string OwnerName { get => ownerName; set => SetProperty(ref ownerName, value ?? ""); }
    public string AnthropicKey { get => anthropicKey; set { if (SetProperty(ref anthropicKey, value ?? "")) Status(); } }

    public string PrivacyText => Kind switch
    {
        ChatProviderKind.Anthropic => Strings.PrivacyAnthropic,
        ChatProviderKind.OpenAi => Strings.PrivacyOpenAi,
        _ => Strings.PrivacyCustom
    };

    public bool EndpointVisible => Kind == ChatProviderKind.Custom;
    public bool AnthropicKeyVisible => Kind == ChatProviderKind.Anthropic;
    /// <summary>OpenAI and the custom API use the OpenAI key slot, shared with OpenAI transcription.</summary>
    public bool OpenAiKeyVisible => Kind is ChatProviderKind.OpenAi or ChatProviderKind.Custom;
    public string OpenAiKeyLabel => Kind == ChatProviderKind.Custom ? Strings.ApiKeyOptional : Strings.ApiKey;
    public bool AdvancedVisible => Kind == ChatProviderKind.Anthropic;
    public bool CreateKeyVisible => Kind is ChatProviderKind.Anthropic or ChatProviderKind.OpenAi;
    public string CreateKeyUri => Kind == ChatProviderKind.Anthropic ? ModelCatalog.AnthropicKeyUrl : ModelCatalog.OpenAiKeyUrl;

    private string CurrentKey => Kind == ChatProviderKind.Anthropic ? anthropicKey : openAiKey();
    public bool KeyStatusVisible => Kind is ChatProviderKind.Anthropic or ChatProviderKind.OpenAi;
    public bool KeyMissing => KeyStatusVisible && string.IsNullOrWhiteSpace(CurrentKey);
    public string KeyStatusText => KeyMissing ? Strings.KeyMissing : Strings.KeyEntered;

    /// <summary>Configuration problem (model, address); a missing key is already said by the key status.</summary>
    public string? Issue
    {
        get
        {
            var problem = PostProcessingService.ConfigurationIssue(Current(), CurrentKey);
            return problem == null || KeyMissing || Kind == null ? null : problem;
        }
    }

    /// <summary>The OpenAI key changed in another field of the dialog.</summary>
    internal void OpenAiKeyChanged() => Status();

    private void Status()
    {
        OnPropertyChanged(nameof(KeyMissing)); OnPropertyChanged(nameof(KeyStatusText)); OnPropertyChanged(nameof(Issue));
    }

    private AppSettings Current()
    {
        draft.Model = modelText.Trim();
        draft.Endpoint = endpoint.Trim();
        draft.AnthropicWorkspaceID = workspace.Trim();
        draft.TaskOwnerName = ownerName.Trim();
        return draft;
    }

    /// <summary>Copies the enhancement choices into the settings about to be saved.</summary>
    public void Apply(AppSettings updated)
    {
        Current();
        updated.Provider = draft.Provider;
        updated.Model = draft.Model;
        updated.EnhancementModels = new(draft.EnhancementModels);
        updated.Endpoint = draft.Endpoint;
        updated.AnthropicWorkspaceID = draft.AnthropicWorkspaceID;
        updated.TaskOwnerName = draft.TaskOwnerName;
    }
}
