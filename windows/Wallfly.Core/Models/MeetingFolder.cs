using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wallfly.Core.Json;

namespace Wallfly.Core;

/// <summary>The ten folder colors of spec 9.6; stored as the macOS raw values.</summary>
[JsonConverter(typeof(RawValueEnumConverter<FolderColor>))]
public enum FolderColor
{
    [EnumMember(Value = "blue")] Blue,
    [EnumMember(Value = "green")] Green,
    [EnumMember(Value = "purple")] Purple,
    [EnumMember(Value = "orange")] Orange,
    [EnumMember(Value = "pink")] Pink,
    [EnumMember(Value = "teal")] Teal,
    [EnumMember(Value = "red")] Red,
    [EnumMember(Value = "yellow")] Yellow,
    [EnumMember(Value = "brown")] Brown,
    [EnumMember(Value = "gray")] Gray
}

/// <summary>One library folder. <see cref="Icon"/> keeps the macOS SF Symbol name as an opaque key.</summary>
public sealed class MeetingFolder
{
    /// <summary>The ten icon keys written by macOS, in the order the editor shows them.</summary>
    public static readonly string[] Icons =
    [
        "folder.fill", "briefcase.fill", "person.2.fill", "lightbulb.fill", "graduationcap.fill",
        "leaf.fill", "star.fill", "heart.fill", "house.fill", "wrench.and.screwdriver.fill"
    ];

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "folder.fill";
    public string Description { get; set; } = "";
    public FolderColor? Color { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}
