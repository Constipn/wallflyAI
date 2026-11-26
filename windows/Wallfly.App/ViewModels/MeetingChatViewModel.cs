using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wallfly.Ai;
using Wallfly.Core;
using Wallfly.Core.Services;

namespace Wallfly.App.ViewModels;

/// <summary>One message of the meeting chat: the user's question (right, grey bubble) or the answer (left, Markdown).</summary>
public sealed class ChatBubble(bool fromUser, string text)
{
    public bool FromUser { get; } = fromUser;
    public bool FromAssistant => !FromUser;
    public string Text { get; } = text;
}

/// <summary>
/// The "Ask anything" chat of a finished meeting (Windows addition): one input bar below the editor and below the preview
/// on every tab, and a panel above it with the questions and answers. Questions go to
/// <see cref="MeetingLibraryService.AskAboutMeetingAsync"/> (the enhancement's provider and model); the stored chat of the
/// meeting is shown when it opens. One question runs at a time; it is cancelled when the editor or the preview closes,
/// another meeting opens or a recording of this meeting starts.
/// </summary>
public sealed class MeetingChatViewModel : ObservableObject, IDisposable
{
    /// <summary>The expanded panel takes at most this share of the editor's height.</summary>
    public const double PanelShare = 0.55;

    private readonly AppViewModel app;
    private readonly Meeting meeting;
    private readonly Action? expanded;
    private string draft = "";
    private bool isExpanded;
    private bool isThinking;
    private string? error;
    private EnhancementRecoveryAction errorAction = EnhancementRecoveryAction.Retry;
    /// <summary>The question shown last whose answer failed; "Try again" asks it once more.</summary>
    private string? unanswered;
    private CancellationTokenSource? request;
    private double areaHeight = 850;
    private bool disposed;

    /// <param name="expanded">Called when the panel opens (the editor closes its transcript box then).</param>
    public MeetingChatViewModel(AppViewModel app, Meeting meeting, Action? expanded = null)
    {
        this.app = app;
        this.meeting = meeting;
        this.expanded = expanded;
        foreach (var message in meeting.Chat ?? []) Messages.Add(new(message.FromUser, message.Text));
        SendCommand = new AsyncRelayCommand(SendAsync, () => CanSend);
        RetryCommand = new AsyncRelayCommand(RetryAsync, () => unanswered != null && !isThinking);
        ErrorActionCommand = new AsyncRelayCommand(ErrorActionAsync);
        CollapseCommand = new RelayCommand(() => IsExpanded = false);
        NewChatCommand = new RelayCommand(NewChat);
    }

    public Guid MeetingID => meeting.Id;
    public ObservableCollection<ChatBubble> Messages { get; } = [];

    /// <summary>Raised after a new message, so the view scrolls to the end.</summary>
    public event Action? ScrollToEndRequested;

    /// <summary>Finished meetings with notes or a transcript; never while this meeting records.</summary>
    public bool IsAvailable => !app.IsLive(meeting.Id) && (meeting.Notes.Trim().Length > 0 || meeting.Transcript.Count > 0);

    public string Draft
    {
        get => draft;
        set { if (SetProperty(ref draft, value ?? "")) SendCommand.NotifyCanExecuteChanged(); }
    }

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            var open = value && IsAvailable;
            if (!SetProperty(ref isExpanded, open)) return;
            OnPropertyChanged(nameof(ShowsPanel));
            if (open) expanded?.Invoke();
        }
    }

    public bool IsThinking
    {
        get => isThinking;
        private set
        {
            if (!SetProperty(ref isThinking, value)) return;
            OnPropertyChanged(nameof(HasContent));
            SendCommand.NotifyCanExecuteChanged(); RetryCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>The inline error under the last question; null without one.</summary>
    public string? Error
    {
        get => error;
        private set
        {
            if (!SetProperty(ref error, value)) return;
            OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(HasContent)); OnPropertyChanged(nameof(ErrorActionText));
        }
    }

    public bool HasError => error != null;
    /// <summary>"Open settings" for configuration problems, otherwise "Try again".</summary>
    public string ErrorActionText => errorAction == EnhancementRecoveryAction.OpenSettings ? Strings.ErrorOpenSettings : Strings.ErrorTryAgain;
    public bool HasContent => Messages.Count > 0 || isThinking || error != null;
    public bool ShowsPanel => isExpanded && IsAvailable;
    public bool CanSend => !isThinking && draft.Trim().Length > 0 && IsAvailable;

    /// <summary>The height the message list may use: about half the editor, less the input and the panel header.</summary>
    public double MessagesMaxHeight => Math.Max(120, areaHeight * PanelShare - 110);

    public IAsyncRelayCommand SendCommand { get; }
    public IAsyncRelayCommand RetryCommand { get; }
    public IAsyncRelayCommand ErrorActionCommand { get; }
    public IRelayCommand CollapseCommand { get; }
    public IRelayCommand NewChatCommand { get; }

    /// <summary>A click into the input opens the panel again when there is something to show.</summary>
    public void InputActivated()
    {
        if (HasContent) IsExpanded = true;
    }

    /// <summary>The editor column's or the preview's height (from <see cref="MeetingViewModel.SetLayout"/> and <see cref="MeetingPreviewViewModel.SetAreaHeight"/>).</summary>
    internal void SetAreaHeight(double height)
    {
        if (Math.Abs(height - areaHeight) < .5 || height <= 0) return;
        areaHeight = height;
        OnPropertyChanged(nameof(MessagesMaxHeight));
    }

    private async Task SendAsync()
    {
        var question = draft.Trim();
        if (question.Length == 0 || isThinking || !IsAvailable) return;
        // A failed question that was not retried is replaced by the new one: only answered questions are stored.
        if (unanswered != null && Messages.Count > 0 && Messages[^1].FromUser) Messages.RemoveAt(Messages.Count - 1);
        Draft = "";
        Messages.Add(new(true, question));
        await AskAsync(question);
    }

    private Task RetryAsync() => unanswered is { } question && !isThinking ? AskAsync(question) : Task.CompletedTask;

    private Task ErrorActionAsync()
    {
        if (errorAction == EnhancementRecoveryAction.OpenSettings) { app.OpenSettings(SettingsArea.AiLanguage); return Task.CompletedTask; }
        return RetryAsync();
    }

    private async Task AskAsync(string question)
    {
        unanswered = question;
        Error = null;
        IsThinking = true;
        IsExpanded = true;
        Notify();
        ScrollToEndRequested?.Invoke();
        var cancellation = request = new CancellationTokenSource();
        try
        {
            var answer = await app.Store.AskAboutMeetingAsync(meeting.Id, question, cancellation.Token);
            if (disposed || cancellation.IsCancellationRequested) return;
            unanswered = null;
            Messages.Add(new(false, answer));
            ScrollToEndRequested?.Invoke();
        }
        catch (OperationCanceledException)
        {
            // Cancelled by "New chat", a recording start or closing the meeting: nothing was stored, so the question goes too.
            if (!disposed && unanswered == question && Messages.Count > 0 && Messages[^1].FromUser && Messages[^1].Text == question) Messages.RemoveAt(Messages.Count - 1);
            if (unanswered == question) unanswered = null;
        }
        catch (Exception failure)
        {
            if (disposed || cancellation.IsCancellationRequested) return;
            errorAction = (failure as AiError)?.Kind == AiErrorKind.Configuration ? EnhancementRecoveryAction.OpenSettings : EnhancementRecoveryAction.Retry;
            Error = failure is InvalidOperationException or InvalidDataException ? failure.Message : Strings.ChatFailed;
            ScrollToEndRequested?.Invoke();
        }
        finally
        {
            if (ReferenceEquals(request, cancellation)) request = null;
            cancellation.Dispose();
            if (!disposed)
            {
                IsThinking = false;
                Notify();
            }
        }
    }

    /// <summary>"New chat": cancels a waiting question, removes the stored chat and closes the panel.</summary>
    private void NewChat()
    {
        Cancel();
        app.Store.ClearChat(meeting.Id);
        Messages.Clear();
        unanswered = null;
        Error = null;
        IsExpanded = false;
        Notify();
    }

    private void Cancel()
    {
        try { request?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Store state changed: a recording of this meeting cancels the question and hides the chat.</summary>
    public void Refresh()
    {
        if (!IsAvailable)
        {
            if (app.IsLive(meeting.Id)) Cancel();
            if (isExpanded) { isExpanded = false; OnPropertyChanged(nameof(IsExpanded)); }
        }
        OnPropertyChanged(nameof(IsAvailable)); OnPropertyChanged(nameof(ShowsPanel));
        SendCommand.NotifyCanExecuteChanged();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(HasContent)); OnPropertyChanged(nameof(ShowsPanel));
        SendCommand.NotifyCanExecuteChanged(); RetryCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Cancel();
    }
}
