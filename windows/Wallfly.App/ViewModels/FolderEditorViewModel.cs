using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wallfly.App.Controls;
using Wallfly.Core;
using Wallfly.Core.Json;
using Wallfly.Core.Services;

namespace Wallfly.App.ViewModels;

/// <summary>One icon choice of the folder editor.</summary>
public sealed class FolderIconChoice(FolderIconOption option) : ObservableObject
{
    private bool selected;
    public FolderIconOption Option { get; } = option;
    public string Symbol => Option.Symbol;
    public string Label => Option.Label;
    public IconKind Kind => Option.Kind;
    public bool IsSelected { get => selected; set => SetProperty(ref selected, value); }
}

/// <summary>One colour choice of the folder editor (spec 9.6 foreground/background pair).</summary>
public sealed class FolderColorChoice(FolderColor color) : ObservableObject
{
    private bool selected;
    public FolderColor Color { get; } = color;
    public string Name => FolderStyles.ColorName(Color);
    public string Foreground => FolderStyles.Colors(Color).Foreground;
    public string Background => FolderStyles.Colors(Color).Background;
    public bool IsSelected { get => selected; set => SetProperty(ref selected, value); }
}

/// <summary>
/// Create or edit a folder (port of <c>FolderEditor</c> and <c>MeetingStore.saveFolder</c>): name, one of ten icons,
/// one of ten colours (a new folder starts with the least used one), description. Deleting moves the meetings to "No folder".
/// </summary>
public sealed class FolderEditorViewModel : ObservableObject
{
    private readonly MeetingLibraryService store;
    private readonly MeetingFolder draft;
    private string name;
    private string description;

    public FolderEditorViewModel(MeetingLibraryService store, MeetingFolder? existing)
    {
        this.store = store;
        Creating = existing == null || store.Catalog.Folders.All(folder => folder.Id != existing.Id);
        draft = existing == null ? new MeetingFolder() : WallflyJson.Copy(existing);
        draft.Color ??= store.Catalog.NextColor;
        if (!MeetingFolder.Icons.Contains(draft.Icon)) draft.Icon = "folder.fill";
        name = draft.Name;
        description = draft.Description;
        Icons = FolderStyles.Icons.Select(option => new FolderIconChoice(option) { IsSelected = option.Symbol == draft.Icon }).ToList();
        Colors = Enum.GetValues<FolderColor>().Select(color => new FolderColorChoice(color) { IsSelected = color == draft.Color }).ToList();
        SelectIconCommand = new RelayCommand<FolderIconChoice>(choice => { if (choice != null) Icon = choice.Symbol; });
        SelectColorCommand = new RelayCommand<FolderColorChoice>(choice => { if (choice != null) Color = choice.Color; });
        SaveCommand = new RelayCommand(() => { if (Save()) CloseRequested?.Invoke(true); }, () => CanSave);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(false));
        DeleteCommand = new RelayCommand(() => DeleteRequested?.Invoke(), () => CanDelete);
    }

    public bool Creating { get; }
    public Guid FolderID => draft.Id;
    public string Title => Creating ? Strings.FolderCreateTitle : Strings.FolderEditTitle;
    public string SaveText => Creating ? Strings.Create : Strings.Save;
    public IReadOnlyList<FolderIconChoice> Icons { get; }
    public IReadOnlyList<FolderColorChoice> Colors { get; }
    public IRelayCommand<FolderIconChoice> SelectIconCommand { get; }
    public IRelayCommand<FolderColorChoice> SelectColorCommand { get; }
    public IRelayCommand SaveCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand DeleteCommand { get; }
    public string? Error => store.Error.Length == 0 ? null : store.Error;

    /// <summary>The dialog closes with true after a successful save.</summary>
    public event Action<bool>? CloseRequested;
    /// <summary>"Delete folder…" asks for confirmation in the shell.</summary>
    public event Action? DeleteRequested;

    public string Name
    {
        get => name;
        set { if (SetProperty(ref name, value ?? "")) SaveCommand.NotifyCanExecuteChanged(); }
    }

    public string Description { get => description; set => SetProperty(ref description, value ?? ""); }

    public string Icon
    {
        get => draft.Icon;
        set
        {
            if (!MeetingFolder.Icons.Contains(value) || draft.Icon == value) return;
            draft.Icon = value;
            foreach (var choice in Icons) choice.IsSelected = choice.Symbol == value;
            OnPropertyChanged(); OnPropertyChanged(nameof(IconKind));
        }
    }

    public IconKind IconKind => FolderStyles.Icon(draft.Icon).Kind;

    public FolderColor Color
    {
        get => draft.Color ?? FolderColor.Blue;
        set
        {
            if (draft.Color == value) return;
            draft.Color = value;
            foreach (var choice in Colors) choice.IsSelected = choice.Color == value;
            OnPropertyChanged(); OnPropertyChanged(nameof(Foreground)); OnPropertyChanged(nameof(Background));
        }
    }

    public string Foreground => FolderStyles.Colors(Color).Foreground;
    public string Background => FolderStyles.Colors(Color).Background;
    public bool CanSave => Name.Trim().Length > 0 && store.FoldersAvailable;
    public bool CanDelete => !Creating && !store.Busy && store.FoldersAvailable;

    /// <summary>Validates and commits the folder in one atomic catalog write.</summary>
    public bool Save()
    {
        if (!CanSave) return false;
        var folder = WallflyJson.Copy(draft);
        folder.Name = Name.Trim();
        folder.Description = Description.Trim();
        var updated = WallflyJson.Copy(store.Catalog);
        var index = updated.Folders.FindIndex(item => item.Id == folder.Id);
        if (index >= 0)
        {
            folder.Color ??= updated.Folders[index].Color ?? updated.NextColor;
            updated.Folders[index] = folder;
        }
        else
        {
            folder.Color ??= updated.NextColor;
            updated.Folders.Add(folder);
        }
        var saved = store.CommitCatalog(updated);
        OnPropertyChanged(nameof(Error));
        return saved;
    }

    /// <summary>Removes a folder; its meetings fall back to "No folder" (their files are untouched).</summary>
    public static bool DeleteFolder(MeetingLibraryService store, Guid folderID)
    {
        if (store.Busy || !store.FoldersAvailable || store.Catalog.Folders.All(folder => folder.Id != folderID)) return false;
        store.Flush();
        var updated = WallflyJson.Copy(store.Catalog);
        updated.Folders.RemoveAll(folder => folder.Id == folderID);
        foreach (var key in updated.Assignments.Where(pair => pair.Value == folderID).Select(pair => pair.Key).ToArray()) updated.Assignments.Remove(key);
        return store.CommitCatalog(updated);
    }
}
