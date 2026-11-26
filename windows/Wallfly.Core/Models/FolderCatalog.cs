using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wallfly.Core;

/// <summary>
/// Folder definitions and meeting membership in one atomic file. Meeting JSON and audio are
/// independent of this organization catalog.
/// </summary>
public sealed class FolderCatalog
{
    public List<MeetingFolder> Folders { get; set; } = [];
    /// <summary>Uppercase meeting UUID string to folder id.</summary>
    public Dictionary<string, Guid> Assignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }

    /// <summary>The least used color, so a new folder differs from its neighbours.</summary>
    [JsonIgnore]
    public FolderColor NextColor =>
        Enum.GetValues<FolderColor>().OrderBy(color => Folders.Count(folder => folder.Color == color)).ThenBy(color => (int)color).First();

    /// <summary>Fills legacy entries in catalog order and keeps every already assigned color.</summary>
    public void AssignMissingColors()
    {
        foreach (var folder in Folders.Where(folder => folder.Color == null)) folder.Color = NextColor;
    }

    public Guid? FolderID(Guid meetingID) =>
        Assignments.TryGetValue(meetingID.ToString("D").ToUpperInvariant(), out var id) && Folders.Any(folder => folder.Id == id) ? id : null;
}
