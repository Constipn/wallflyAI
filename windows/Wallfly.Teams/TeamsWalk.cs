namespace Wallfly.Teams;

/// <summary>Structure-only properties of one element (pass 1). Never text.</summary>
public readonly record struct TeamsElementInfo(
    int ControlType,
    string Id,
    string ClassName,
    string AriaRole,
    bool Enabled,
    bool Offscreen,
    TeamsBounds? Bounds,
    string? Key,
    int ProcessId);

/// <summary>Result of reading something from an element: a value, or an error key for the diagnostics.</summary>
public readonly record struct ElementRead<T>(T? Value, string? Error)
{
    public static ElementRead<T> Ok(T value) => new(value, null);
    public static ElementRead<T> Fail(string error) => new(default, error);
    public bool Succeeded => Error is null;
}

/// <summary>Transport of the walker: UIA (cached elements) in production, plain trees in the fixture tests.</summary>
public interface ITeamsElementSource<TElement>
{
    ElementRead<TeamsElementInfo> ReadInfo(TElement element);
    ElementRead<IReadOnlyList<TElement>> ReadChildren(TElement element);
}

/// <summary>
/// Budgeted, structure-only walk of one window (port of TeamsAXWalk, macOS 1.13.0). Stops at the first leave
/// control, never inspects editable text or chat/message containers, and reports whether the read was
/// complete: a failed property or children read, an empty window/web area, or a hit budget makes it
/// incomplete, and an incomplete read can never prove that a call ended. Visited elements are tracked by
/// equality (not hash) so colliding element hashes cannot hide the real leave control.
/// </summary>
public sealed class TeamsWalk<TElement> where TElement : notnull
{
    private readonly ITeamsElementSource<TElement> source;
    private readonly TeamsSelectors selectors;
    private readonly Func<double> now;
    private readonly double deadline;
    private readonly int maxDepth;
    private readonly HashSet<TElement> visited;

    public TeamsWalk(ITeamsElementSource<TElement> source, TeamsSelectors selectors, double deadline, Func<double> now,
        bool captureNodes = false, int? maxNodes = null, int? maxDepth = null, IEqualityComparer<TElement>? comparer = null)
    {
        this.source = source;
        this.selectors = selectors;
        this.deadline = deadline;
        this.now = now;
        CaptureNodes = captureNodes;
        Remaining = maxNodes ?? selectors.Limits.MaxNodes;
        this.maxDepth = maxDepth ?? selectors.Limits.MaxDepth;
        visited = new HashSet<TElement>(comparer ?? EqualityComparer<TElement>.Default);
    }

    public int Remaining { get; set; }
    public bool Complete { get; private set; } = true;
    public bool CaptureNodes { get; }
    public bool MainWindowMarkerSeen { get; private set; }
    public UiaReadDiagnostics Diagnostic { get; } = new();

    /// <summary>Elements of tile candidates by node key, for the text pass (pass 2).</summary>
    public Dictionary<string, TElement> Elements { get; } = new(StringComparer.Ordinal);

    public bool KeepElements { get; init; }

    /// <summary>Detection stops at the first leave control; tree reads for speaker names continue.</summary>
    public bool StopAtLeaveControl { get; init; } = true;

    public TeamsNode? Read(TElement element) => Read(element, 0, null);

    private TeamsNode? Read(TElement element, int depth, int? parent)
    {
        if (Remaining <= 0 || depth >= maxDepth || now() >= deadline)
        {
            Complete = false;
            Diagnostic.HitLimit = true;
            return null;
        }
        if (!visited.Add(element)) return null;
        Remaining--;
        Diagnostic.NodesRead++;
        var info = source.ReadInfo(element);
        if (!info.Succeeded)
        {
            Complete = false;
            Diagnostic.AddError(info.Error!);
            return null;
        }
        var value = info.Value;
        // Editable text is never inspected further: no identifiers, no children.
        if (selectors.IsExcludedControlType(value.ControlType)) return null;
        var node = new TeamsNode
        {
            ControlType = value.ControlType,
            Id = value.Id,
            ClassName = value.ClassName,
            AriaRole = value.AriaRole,
            Enabled = value.Enabled,
            Offscreen = value.Offscreen,
            Bounds = value.Bounds,
            Key = value.Key,
            ProcessId = value.ProcessId,
        };
        // Chat and message containers are skipped with their whole subtree.
        if (selectors.IsExcluded(node)) return null;
        if (KeepElements && node.Key is not null) Elements[node.Key] = element;
        if (selectors.IsWebArea(node)) Diagnostic.WebAreasRead++;
        if (selectors.IsMainWindowMarker(node)) MainWindowMarkerSeen = true;
        var index = Diagnostic.Nodes.Count;
        if (CaptureNodes && index < UiaReadDiagnostics.MaxTraceNodes)
        {
            Diagnostic.Nodes.Add(new UiaReadDiagnostics.Node
            {
                Parent = parent,
                Role = node.Role,
                Identifier = UiaReadDiagnostics.TechnicalIdentifier(node.Id),
                Classes = UiaReadDiagnostics.TechnicalClasses(node.ClassName),
                AriaRole = UiaReadDiagnostics.TechnicalRole(node.AriaRole),
                Enabled = node.Enabled,
                Offscreen = node.Offscreen,
                Pid = value.ProcessId == 0 ? null : value.ProcessId,
            });
        }
        else if (CaptureNodes) Diagnostic.TraceTruncated = true;
        if (StopAtLeaveControl && TeamsStructure.HasLeaveControl(node, selectors)) return node;
        var children = source.ReadChildren(element);
        if (!children.Succeeded)
        {
            Complete = false;
            Diagnostic.AddError(children.Error!);
        }
        var list = children.Value ?? [];
        // An empty window or web area is not evidence that a call ended: Chromium may not have exposed the
        // document yet (first UIA contact builds the tree).
        if (list.Count == 0 && (node.ControlType == UiaControlTypes.Window || selectors.IsWebArea(node)))
        {
            Complete = false;
            Diagnostic.EmptyContainers++;
        }
        foreach (var child in list)
        {
            var result = Read(child, depth + 1, index < UiaReadDiagnostics.MaxTraceNodes ? index : null);
            if (result is not null)
            {
                node.Children.Add(result);
                if (StopAtLeaveControl && TeamsStructure.HasLeaveControl(result, selectors)) return node;
            }
            if (Remaining <= 0 || now() >= deadline)
            {
                Complete = false;
                Diagnostic.HitLimit = true;
                break;
            }
        }
        return node;
    }
}

/// <summary>Selector-driven rules about the Teams tree that detection relies on (port of TeamsStructure).</summary>
public static class TeamsStructure
{
    /// <summary>
    /// A stage, tile or pre-join preview alone is no call: only a visible (not offscreen), enabled leave/hang-up
    /// control outside excluded chat/editable regions proves one.
    /// </summary>
    public static bool HasLeaveControl(TeamsNode root, TeamsSelectors selectors)
    {
        if (root.Offscreen || selectors.IsExcluded(root)) return false;
        if (root.Enabled && selectors.IsLeaveButton(root)) return true;
        foreach (var child in root.Children)
            if (HasLeaveControl(child, selectors)) return true;
        return false;
    }

    public static bool ContainsMainWindowMarker(TeamsNode root, TeamsSelectors selectors) =>
        selectors.SafeNodes(root).Any(selectors.IsMainWindowMarker);
}

/// <summary>Walker over plain <see cref="TeamsNode"/> trees (fixtures and synthetic tests), with failure injection.</summary>
public sealed class TeamsNodeElementSource : ITeamsElementSource<TeamsNode>
{
    /// <summary>Elements whose structure read fails with the given error key.</summary>
    public Dictionary<TeamsNode, string> InfoErrors { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>Elements whose children read fails with the given error key.</summary>
    public Dictionary<TeamsNode, string> ChildrenErrors { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>Overrides the children (e.g. to build cycles).</summary>
    public Dictionary<TeamsNode, IReadOnlyList<TeamsNode>> ChildrenOverrides { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>Every property read, for privacy assertions (what was touched).</summary>
    public List<(TeamsNode Node, string What)> Log { get; } = [];

    public ElementRead<TeamsElementInfo> ReadInfo(TeamsNode element)
    {
        Log.Add((element, "info"));
        if (InfoErrors.TryGetValue(element, out var error)) return ElementRead<TeamsElementInfo>.Fail(error);
        return ElementRead<TeamsElementInfo>.Ok(new TeamsElementInfo(element.ControlType, element.Id, element.ClassName, element.AriaRole,
            element.Enabled, element.Offscreen, element.Bounds, element.Key ?? KeyOf(element), element.ProcessId));
    }

    public ElementRead<IReadOnlyList<TeamsNode>> ReadChildren(TeamsNode element)
    {
        Log.Add((element, "children"));
        if (ChildrenErrors.TryGetValue(element, out var error)) return ElementRead<IReadOnlyList<TeamsNode>>.Fail(error);
        return ElementRead<IReadOnlyList<TeamsNode>>.Ok(ChildrenOverrides.TryGetValue(element, out var children) ? children : element.Children);
    }

    private readonly Dictionary<TeamsNode, string> keys = new(ReferenceEqualityComparer.Instance);

    private string KeyOf(TeamsNode node)
    {
        if (!keys.TryGetValue(node, out var key)) keys[node] = key = "n" + keys.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return key;
    }
}
