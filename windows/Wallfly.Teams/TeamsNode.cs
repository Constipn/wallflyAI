using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wallfly.Teams;

/// <summary>UIA control type IDs used by the Teams selectors (UIA_*ControlTypeId).</summary>
public static class UiaControlTypes
{
    public const int Button = 50000;
    public const int Calendar = 50001;
    public const int CheckBox = 50002;
    public const int ComboBox = 50003;
    public const int Edit = 50004;
    public const int Hyperlink = 50005;
    public const int Image = 50006;
    public const int ListItem = 50007;
    public const int List = 50008;
    public const int Menu = 50009;
    public const int MenuBar = 50010;
    public const int MenuItem = 50011;
    public const int ProgressBar = 50012;
    public const int RadioButton = 50013;
    public const int ScrollBar = 50014;
    public const int Slider = 50015;
    public const int Spinner = 50016;
    public const int StatusBar = 50017;
    public const int Tab = 50018;
    public const int TabItem = 50019;
    public const int Text = 50020;
    public const int ToolBar = 50021;
    public const int ToolTip = 50022;
    public const int Tree = 50023;
    public const int TreeItem = 50024;
    public const int Custom = 50025;
    public const int Group = 50026;
    public const int Thumb = 50027;
    public const int DataGrid = 50028;
    public const int DataItem = 50029;
    public const int Document = 50030;
    public const int SplitButton = 50031;
    public const int Window = 50032;
    public const int Pane = 50033;
    public const int Header = 50034;
    public const int HeaderItem = 50035;
    public const int Table = 50036;
    public const int TitleBar = 50037;
    public const int Separator = 50038;
    public const int SemanticZoom = 50039;
    public const int AppBar = 50040;

    private static readonly Dictionary<int, string> Names = typeof(UiaControlTypes)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(int))
        .ToDictionary(field => (int)field.GetRawConstantValue()!, field => field.Name);

    private static readonly Dictionary<string, int> Ids = Names.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>"Button" for 50000; unknown IDs stay numeric.</summary>
    public static string Name(int controlType) =>
        Names.TryGetValue(controlType, out var name) ? name : controlType.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool TryParse(string value, out int controlType)
    {
        if (Ids.TryGetValue(value, out controlType)) return true;
        return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out controlType);
    }
}

/// <summary>Screen rectangle in physical pixels (UIA BoundingRectangle / GetWindowRect).</summary>
public readonly record struct TeamsBounds(double X, double Y, double Width, double Height)
{
    [JsonIgnore] public double Right => X + Width;
    [JsonIgnore] public double Bottom => Y + Height;
    [JsonIgnore] public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// Reduced, plain UI Automation tree shared by call detection, speaker parsing, diagnostics and the JSON
/// fixtures. Pass 1 of the reader fills only structure (control type, id, classes, ARIA role, state);
/// <see cref="Name"/> is filled by pass 2 and only inside recognised participant tiles.
/// The logical <see cref="Id"/>, <see cref="ClassName"/>, <see cref="Name"/> and <see cref="AriaRole"/> come from the
/// UIA properties named in the selector <c>properties</c> remap (default AutomationId, ClassName, Name, AriaRole).
/// </summary>
public sealed class TeamsNode
{
    [JsonPropertyName("controlType"), JsonConverter(typeof(ControlTypeJsonConverter))]
    public int ControlType { get; set; } = UiaControlTypes.Group;

    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("className")] public string ClassName { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("ariaRole")] public string AriaRole { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("offscreen")] public bool Offscreen { get; set; }

    [JsonPropertyName("bounds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TeamsBounds? Bounds { get; set; }

    /// <summary>Element identity within one read (UIA RuntimeId); fixtures may set it explicitly.</summary>
    [JsonPropertyName("key"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Key { get; set; }

    [JsonPropertyName("processId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ProcessId { get; set; }

    [JsonPropertyName("children")] public List<TeamsNode> Children { get; set; } = [];

    /// <summary>Whitespace-separated HTML classes as exposed by Chromium in ClassName.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Classes => ClassName.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);

    [JsonIgnore] public string Role => UiaControlTypes.Name(ControlType);

    /// <summary>This node and all descendants, pre-order.</summary>
    public IEnumerable<TeamsNode> Nodes()
    {
        var stack = new Stack<TeamsNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            for (var i = node.Children.Count - 1; i >= 0; i--) stack.Push(node.Children[i]);
        }
    }

    /// <summary>A copy of this node without children.</summary>
    public TeamsNode CloneShallow() => new()
    {
        ControlType = ControlType, Id = Id, ClassName = ClassName, Name = Name, AriaRole = AriaRole, Enabled = Enabled,
        Offscreen = Offscreen, Bounds = Bounds, Key = Key, ProcessId = ProcessId,
    };

    public TeamsNode CloneDeep()
    {
        var copy = CloneShallow();
        copy.Children = Children.Select(child => child.CloneDeep()).ToList();
        return copy;
    }

    public override string ToString() => $"{Role} id='{Id}' class='{ClassName}'";

    internal static readonly JsonSerializerOptions FixtureOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        MaxDepth = 512,
    };

    /// <summary>Reads one tree or an array of window trees (fixture files).</summary>
    public static IReadOnlyList<TeamsNode> ParseFixture(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 512 });
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.Deserialize<List<TeamsNode>>(FixtureOptions) ?? []
            : [document.RootElement.Deserialize<TeamsNode>(FixtureOptions) ?? new TeamsNode()];
    }

    public string ToJson() => JsonSerializer.Serialize(this, FixtureOptions);
}

/// <summary>Control types are written as names ("Button") and read as names or numbers.</summary>
public sealed class ControlTypeJsonConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number) return reader.GetInt32();
        var text = reader.GetString() ?? "";
        return UiaControlTypes.TryParse(text, out var value) ? value : throw new JsonException($"Unknown control type '{text}'.");
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
        writer.WriteStringValue(UiaControlTypes.Name(value));
}
