using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Wallfly.App.Dialogs;
using Wallfly.App.ViewModels;
using Wallfly.Audio;
using Wallfly.Core.Services;

namespace Wallfly.App.Shell;

/// <summary><see cref="IUiScheduler"/> on dispatcher timers of the UI thread.</summary>
public sealed class DispatcherScheduler(Dispatcher dispatcher) : IUiScheduler
{
    public IDisposable Schedule(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = delay };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
        return new Stopper(timer);
    }

    public IDisposable Every(TimeSpan interval, Action action)
    {
        var timer = new DispatcherTimer(interval, DispatcherPriority.Background, (_, _) => action(), dispatcher);
        timer.Start();
        return new Stopper(timer);
    }

    private sealed class Stopper(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}

/// <summary>Devices, audio sessions and the microphone consent of the running system.</summary>
public sealed class SystemProbe(IAudioDeviceCatalog devices) : ISystemProbe
{
    public IReadOnlyList<AudioDevice> Devices(AudioFlow flow) => devices.List(flow);
    public IReadOnlyList<AudioSessionEntry> AudioSessions() => NativeAudioSessions.List(AudioFlow.Render);
    public bool FileExists(string path) => File.Exists(path);

    /// <summary>Settings → Privacy &amp; security → Microphone: the global switch and the one for desktop apps.</summary>
    public MicrophoneAccess MicrophoneAccess()
    {
        try
        {
            const string store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
            using var global = Registry.CurrentUser.OpenSubKey(store);
            using var desktop = Registry.CurrentUser.OpenSubKey(store + @"\NonPackaged");
            var values = new[] { global?.GetValue("Value") as string, desktop?.GetValue("Value") as string };
            if (values.Any(value => string.Equals(value, "Deny", StringComparison.OrdinalIgnoreCase))) return ViewModels.MicrophoneAccess.Denied;
            return values.Any(value => string.Equals(value, "Allow", StringComparison.OrdinalIgnoreCase)) ? ViewModels.MicrophoneAccess.Allowed : ViewModels.MicrophoneAccess.Unknown;
        }
        catch (Exception error) when (error is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return ViewModels.MicrophoneAccess.Unknown;
        }
    }
}

/// <summary>The WPF side of <see cref="IDialogService"/>: modal windows owned by the main window, file dialogs, Explorer.</summary>
public sealed class WpfDialogService : IDialogService
{
    private readonly Func<Window?> owner;
    private Func<AppViewModel>? app;
    private TaskReviewWindow? taskReview;
    private SettingsWindow? settings;

    public WpfDialogService(Func<Window?> owner) => this.owner = owner;

    /// <summary>Set once the root view model exists (the dialogs need it, the view model needs the dialogs).</summary>
    public void Attach(Func<AppViewModel> root) => app = root;

    private AppViewModel App => app?.Invoke() ?? throw new InvalidOperationException("Dialog service not attached.");

    private T Owned<T>(T window) where T : Window
    {
        var parent = owner();
        if (parent is { IsVisible: true }) window.Owner = parent;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return window;
    }

    public bool Confirm(string title, string message, string confirm) =>
        Owned(new ConfirmDialog(title, message, confirm)).ShowDialog() == true;

    public string? AskMeetingName(string current)
    {
        var dialog = Owned(new RenameDialog(current));
        return dialog.ShowDialog() == true ? dialog.MeetingName : null;
    }

    public QuitChoice AskSaveAndEnd(QuitPrompt prompt = QuitPrompt.SaveAndEnd) =>
        Owned(new QuitDialog(prompt)).ShowDialog() == true ? QuitChoice.SaveAndEnd : QuitChoice.ReturnToMeeting;

    public string? AskSpeakerName(string current)
    {
        var dialog = Owned(new SpeakerNameDialog(current));
        return dialog.ShowDialog() == true ? dialog.SpeakerName : null;
    }

    public SpeakerAssignment? AskSpeakerAssignment(string excerpt, IReadOnlyList<SpeakerOption> participants)
    {
        var dialog = Owned(new AssignSpeakerDialog(excerpt, participants));
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public void ShowSettings(SettingsArea area)
    {
        if (settings?.IsVisible == true) { settings.ViewModel.Area = area; settings.Activate(); return; }
        var model = new SettingsViewModel(App.Store, this, App.System, App.Location, area, App.Detection);
        settings = Owned(new SettingsWindow(model, App.Store));
        model.OpenSetupRequested += () => { settings?.Close(); ShowOnboarding(); };
        settings.ShowDialog();
        settings = null;
        App.Refresh();
    }

    public void ShowOnboarding()
    {
        var root = App;
        var model = new OnboardingViewModel(root.Store, new SettingsViewModel(root.Store, this, root.System, root.Location), root.System, this);
        root.IsSettingUp = true;
        try { Owned(new OnboardingWindow(model, root.Store)).ShowDialog(); }
        finally { root.IsSettingUp = false; root.Refresh(); }
    }

    public bool EditFolder(FolderEditorViewModel editor)
    {
        var window = Owned(new FolderEditorWindow(editor));
        editor.DeleteRequested += () => { if (App.DeleteFolder(editor.FolderID)) window.Close(); };
        return window.ShowModal();
    }

    public void ShowTaskReview(Guid meetingID)
    {
        if (taskReview?.IsVisible == true) { if (taskReview.MeetingID == meetingID) taskReview.Render(); return; }
        var store = App.Store;
        var meeting = store.Meetings.FirstOrDefault(item => item.Id == meetingID);
        if (meeting == null || store.PendingTasks(meeting).Count == 0) return;
        taskReview = Owned(new TaskReviewWindow(store, meetingID));
        taskReview.Closed += (_, _) => { taskReview = null; App.Refresh(); };
        taskReview.ShowDialog();
    }

    public string? AskSaveMarkdown(string fileName)
    {
        var dialog = new SaveFileDialog { FileName = fileName, Filter = Strings.MarkdownFilter, DefaultExt = ".md" };
        return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
    }

    public string? AskFolder(string title, string? initial)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (initial != null && Directory.Exists(initial)) dialog.InitialDirectory = initial;
        return dialog.ShowDialog(owner()) == true ? dialog.FolderName : null;
    }

    public string? AskFile(string filter, string? initial)
    {
        var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        if (!string.IsNullOrWhiteSpace(initial) && Path.GetDirectoryName(initial) is { } directory && Directory.Exists(directory)) dialog.InitialDirectory = directory;
        return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
    }

    public void Inform(string title, string message) => Owned(new ConfirmDialog(title, message, null)).ShowDialog();

    public void Reveal(string path) => Start("explorer.exe", "/select,\"" + path + "\"");
    public void OpenFolder(string path) => Start("explorer.exe", "\"" + path + "\"");
    public void OpenUri(string uri) => Start(uri, null);

    public void CopyText(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { /* the clipboard is held by another process; nothing to report */ }
    }

    private static void Start(string file, string? arguments)
    {
        try { Process.Start(new ProcessStartInfo(file, arguments ?? "") { UseShellExecute = true })?.Dispose(); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { Trace.TraceWarning("Could not start " + file + ": " + error.Message); }
    }
}
