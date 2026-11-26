using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.UIA3;
using UIA = Interop.UIAutomationClient;

namespace Wallfly.Teams;

/// <summary>Result of reading one window.</summary>
public sealed class TeamsWindowRead
{
    public TeamsNode? Tree { get; init; }
    public bool Complete { get; init; }
    public bool LeaveControl { get; init; }
    public bool MainWindowMarker { get; init; }
    public UiaReadDiagnostics Diagnostics { get; init; } = new();
    public TimeSpan Elapsed { get; init; }
}

/// <summary>Reads one window's structure with the walk semantics of <see cref="TeamsWalk{TElement}"/>.</summary>
public interface ITeamsWindowReader
{
    /// <param name="deadline">Monotonic seconds (<see cref="MonotonicClock"/>) after which the walk stops.</param>
    TeamsWindowRead ReadWindow(TeamsWindowInfo window, TeamsSelectors selectors, double deadline, bool captureDiagnostics);
}

/// <summary>Options of a full tree read (speaker names, tools).</summary>
public sealed class UiaTreeOptions
{
    /// <summary>Pass 2: read text (the remapped <c>text</c> property) inside recognised tile candidates only.</summary>
    public bool EnrichTiles { get; init; } = true;
    public double BudgetSeconds { get; init; } = 0.22;
    public int? MaxNodes { get; init; }
    public int? MaxDepth { get; init; }
}

/// <summary>A full structure read plus pass-2 text inside tiles.</summary>
public sealed class TeamsTreeRead
{
    public TeamsNode? Tree { get; init; }
    public bool Complete { get; init; }
    public UiaReadDiagnostics Diagnostics { get; init; } = new();
    public TimeSpan Elapsed { get; init; }
    public int TilesEnriched { get; init; }
}

/// <summary>Monotonic seconds (QueryPerformanceCounter).</summary>
public static class MonotonicClock
{
    public static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}

/// <summary>
/// UI Automation transport over FlaUI.UIA3 (CUIAutomation8). Must be created and used on one MTA thread
/// (<see cref="UiaThread{TSession}"/>). Pass 1 reads the raw view with one cache request per window
/// (<c>TreeScope_Subtree</c>, structure properties only: ControlType, the remapped id/class/role, IsEnabled,
/// IsOffscreen, BoundingRectangle, ProcessId, RuntimeId); measured on Chromium windows this is 5–8× faster than
/// one navigation call per node (≈ 0.2 vs 1.5 ms/node). The budgeted walk then runs on the cached tree.
/// Pass 2 re-caches only tile subtrees with the text property. IUIAutomation2 timeouts: connection 100 ms,
/// transaction 220 ms (UIA rejects values below 50 ms and does not bound a whole subtree request; the
/// UiaThread watchdog does). All COM wrappers created by a read are released when it ends.
/// </summary>
public sealed class UiaReader : ITeamsWindowReader, IDisposable
{
    private const int RuntimeIdProperty = 30000;
    private const int BoundingRectangleProperty = 30001;
    private const int ProcessIdProperty = 30002;
    private const int ControlTypeProperty = 30003;
    private const int IsEnabledProperty = 30010;
    private const int IsOffscreenProperty = 30022;

    private readonly UIA3Automation automation;
    private readonly UIA.IUIAutomation native;
    private readonly Func<double> clock;
    private CacheSet? caches;

    public UiaReader(TeamsReadLimits? limits = null, Func<double>? clock = null)
    {
        limits ??= new TeamsReadLimits();
        this.clock = clock ?? MonotonicClock.Now;
        automation = new UIA3Automation();
        native = automation.NativeAutomation;
        if (native is UIA.IUIAutomation2 timeouts)
        {
            timeouts.ConnectionTimeout = (uint)Math.Max(50, limits.ConnectionTimeoutMilliseconds);
            timeouts.TransactionTimeout = (uint)Math.Max(50, limits.TransactionTimeoutMilliseconds);
            HasTimeouts = true;
        }
    }

    /// <summary>False on systems without IUIAutomation2 (pre Windows 8): reads then have no UIA timeout.</summary>
    public bool HasTimeouts { get; }

    public TeamsWindowRead ReadWindow(TeamsWindowInfo window, TeamsSelectors selectors, double deadline, bool captureDiagnostics)
    {
        var started = Stopwatch.GetTimestamp();
        using var scope = new ComScope();
        var set = Caches(selectors);
        var traversal = selectors.Limits.Traversal.Equals("walk", StringComparison.OrdinalIgnoreCase) ? Traversal.Walk : Traversal.Subtree;
        var root = Root(window.Handle, traversal == Traversal.Subtree ? set.StructureSubtree : set.StructureElement, scope, out var rootError);
        var budgetExceeded = false;
        if (root is null)
        {
            var failed = new UiaReadDiagnostics();
            failed.AddError(rootError!);
            return new TeamsWindowRead { Complete = false, Diagnostics = failed, Elapsed = Stopwatch.GetElapsedTime(started) };
        }
        // The subtree is already cached: its walk only enforces the node/depth limits, not the time budget.
        var walkDeadline = deadline;
        if (traversal == Traversal.Subtree)
        {
            budgetExceeded = clock() >= deadline;
            walkDeadline = double.PositiveInfinity;
        }
        var source = new CachedSource(set, scope, traversal);
        var walk = new TeamsWalk<UiaElement>(source, selectors, walkDeadline, clock, captureDiagnostics);
        var tree = walk.Read(new UiaElement(root, source.KeyOf(root)));
        var elapsed = Stopwatch.GetElapsedTime(started);
        walk.Diagnostic.ReadMilliseconds = elapsed.TotalMilliseconds;
        if (budgetExceeded) walk.Diagnostic.SlowReads++;
        return new TeamsWindowRead
        {
            Tree = tree,
            Complete = walk.Complete,
            LeaveControl = tree is not null && TeamsStructure.HasLeaveControl(tree, selectors),
            MainWindowMarker = walk.MainWindowMarkerSeen,
            Diagnostics = walk.Diagnostic,
            Elapsed = elapsed,
        };
    }

    /// <summary>
    /// Pass 1 over the whole window (no stop at the leave control) and, if requested, pass 2: the text property
    /// for tile candidates only, one cached subtree request per tile. Chat, editable text and everything
    /// outside tiles never has its text read.
    /// </summary>
    public TeamsTreeRead ReadTree(nint window, TeamsSelectors selectors, UiaTreeOptions? options = null)
    {
        options ??= new UiaTreeOptions();
        var started = Stopwatch.GetTimestamp();
        var deadline = clock() + options.BudgetSeconds;
        using var scope = new ComScope();
        var set = Caches(selectors);
        var root = Root(window, set.StructureSubtree, scope, out var rootError);
        if (root is null)
        {
            var failed = new UiaReadDiagnostics();
            failed.AddError(rootError!);
            return new TeamsTreeRead { Diagnostics = failed, Elapsed = Stopwatch.GetElapsedTime(started) };
        }
        var source = new CachedSource(set, scope, Traversal.Subtree);
        var walk = new TeamsWalk<UiaElement>(source, selectors, double.PositiveInfinity, clock, captureNodes: false,
            maxNodes: options.MaxNodes, maxDepth: options.MaxDepth) { KeepElements = true, StopAtLeaveControl = false };
        var tree = walk.Read(new UiaElement(root, source.KeyOf(root)));
        var complete = walk.Complete;
        var enriched = 0;
        if (tree is not null && options.EnrichTiles)
        {
            foreach (var tile in TeamsRosterParser.TileCandidates(tree, selectors))
            {
                if (clock() >= deadline) { complete = false; walk.Diagnostic.HitLimit = true; break; }
                if (tile.Key is null || !walk.Elements.TryGetValue(tile.Key, out var element)) continue;
                var tileStarted = Stopwatch.GetTimestamp();
                if (!EnrichTile(tile, element.Element, set, scope, walk.Diagnostic)) complete = false;
                else enriched++;
                if (Stopwatch.GetElapsedTime(tileStarted).TotalMilliseconds > selectors.Limits.PerElementMilliseconds) walk.Diagnostic.SlowReads++;
            }
        }
        var elapsed = Stopwatch.GetElapsedTime(started);
        walk.Diagnostic.ReadMilliseconds = elapsed.TotalMilliseconds;
        if (elapsed.TotalSeconds > options.BudgetSeconds) walk.Diagnostic.SlowReads++;
        return new TeamsTreeRead { Tree = tree, Complete = complete, Diagnostics = walk.Diagnostic, Elapsed = elapsed, TilesEnriched = enriched };
    }

    private bool EnrichTile(TeamsNode tile, UIA.IUIAutomationElement element, CacheSet set, ComScope scope, UiaReadDiagnostics diagnostics)
    {
        UIA.IUIAutomationElement updated;
        try { updated = scope.Track(element.BuildUpdatedCache(set.TextSubtree)); }
        catch (Exception error) when (IsUiaFailure(error))
        {
            diagnostics.AddError("text:" + ErrorKey(error));
            return false;
        }
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        void Collect(UIA.IUIAutomationElement current)
        {
            var key = RuntimeKey(current);
            if (key is not null) texts[key] = CachedString(current, set.TextProperty);
            UIA.IUIAutomationElementArray? children;
            try { children = scope.Track(current.GetCachedChildren()); }
            catch (Exception error) when (IsUiaFailure(error)) { return; }
            if (children is null) return;
            for (var i = 0; i < children.Length; i++) Collect(scope.Track(children.GetElement(i)));
        }
        Collect(updated);
        foreach (var node in tile.Nodes())
            if (node.Key is not null && texts.TryGetValue(node.Key, out var text)) node.Name = text;
        return true;
    }

    private UIA.IUIAutomationElement? Root(nint window, UIA.IUIAutomationCacheRequest request, ComScope scope, out string? error)
    {
        try
        {
            error = null;
            return scope.Track(native.ElementFromHandleBuildCache(window, request));
        }
        catch (Exception exception) when (IsUiaFailure(exception))
        {
            error = "root:" + ErrorKey(exception);
            return null;
        }
    }

    private CacheSet Caches(TeamsSelectors selectors)
    {
        var id = TeamsPropertyMap.Resolve(selectors.Properties.Id, 30011);
        var cls = TeamsPropertyMap.Resolve(selectors.Properties.Class, 30012);
        var role = TeamsPropertyMap.Resolve(selectors.Properties.Role, 30101);
        var text = TeamsPropertyMap.Resolve(selectors.Properties.Text, 30005);
        if (caches is { } existing && existing.IdProperty == id && existing.ClassProperty == cls && existing.RoleProperty == role && existing.TextProperty == text)
            return existing;
        var structure = native.CreateCacheRequest();
        foreach (var property in new[] { RuntimeIdProperty, ControlTypeProperty, id, cls, role, IsEnabledProperty, IsOffscreenProperty, BoundingRectangleProperty, ProcessIdProperty }.Distinct())
            structure.AddProperty(property);
        structure.TreeFilter = native.RawViewCondition;
        structure.TreeScope = UIA.TreeScope.TreeScope_Element;
        var subtree = structure.Clone();
        subtree.TreeScope = UIA.TreeScope.TreeScope_Subtree;
        var textRequest = native.CreateCacheRequest();
        textRequest.AddProperty(RuntimeIdProperty);
        textRequest.AddProperty(text);
        textRequest.TreeFilter = native.RawViewCondition;
        textRequest.TreeScope = UIA.TreeScope.TreeScope_Subtree;
        return caches = new CacheSet(structure, subtree, textRequest, native.RawViewWalker, id, cls, role, text);
    }

    /// <summary>HRESULT of a failed UIA call (e.g. 0x80131505 UIA_E_TIMEOUT, 0x80040201 UIA_E_ELEMENTNOTAVAILABLE).</summary>
    public static string ErrorKey(Exception error) => $"0x{error.HResult:X8}";

    /// <summary>
    /// Exceptions a UIA call can surface: COM errors, and the HRESULTs .NET maps to managed types
    /// (UIA_E_TIMEOUT → TimeoutException, E_ACCESSDENIED → UnauthorizedAccessException, E_INVALIDARG → ArgumentException,
    /// E_NOTIMPL → NotImplementedException) or a failed cast of a provider value.
    /// </summary>
    public static bool IsUiaFailure(Exception error) => error is COMException or ArgumentException or InvalidCastException
        or TimeoutException or UnauthorizedAccessException or NotImplementedException;

    public static string CachedString(UIA.IUIAutomationElement element, int property)
    {
        try { return element.GetCachedPropertyValue(property) as string ?? ""; }
        catch (Exception error) when (IsUiaFailure(error)) { return ""; }
    }

    public static string? RuntimeKey(UIA.IUIAutomationElement element)
    {
        try
        {
            return element.GetCachedPropertyValue(RuntimeIdProperty) is int[] { Length: > 0 } runtime
                ? string.Join('.', runtime)
                : null;
        }
        catch (Exception error) when (IsUiaFailure(error)) { return null; }
    }

    public void Dispose() => automation.Dispose();

    private enum Traversal { Subtree, Walk }

    private sealed record CacheSet(
        UIA.IUIAutomationCacheRequest StructureElement,
        UIA.IUIAutomationCacheRequest StructureSubtree,
        UIA.IUIAutomationCacheRequest TextSubtree,
        UIA.IUIAutomationTreeWalker RawWalker,
        int IdProperty, int ClassProperty, int RoleProperty, int TextProperty);

    /// <summary>A cached UIA element with a stable key (RuntimeId) for visited-set equality.</summary>
    private readonly record struct UiaElement(UIA.IUIAutomationElement Element, string Key)
    {
        public bool Equals(UiaElement other) => Key == other.Key;
        public override int GetHashCode() => Key.GetHashCode(StringComparison.Ordinal);
    }

    /// <summary>Reads structure from the cache; children from the cache (subtree) or via the raw walker (walk).</summary>
    private sealed class CachedSource(CacheSet set, ComScope scope, Traversal traversal) : ITeamsElementSource<UiaElement>
    {
        private int fallbackKeys;

        public string KeyOf(UIA.IUIAutomationElement element) =>
            RuntimeKey(element) ?? "u" + (++fallbackKeys).ToString(System.Globalization.CultureInfo.InvariantCulture);

        public ElementRead<TeamsElementInfo> ReadInfo(UiaElement element)
        {
            try
            {
                var e = element.Element;
                var rect = e.CachedBoundingRectangle;
                return ElementRead<TeamsElementInfo>.Ok(new TeamsElementInfo(
                    e.CachedControlType,
                    CachedString(e, set.IdProperty),
                    CachedString(e, set.ClassProperty),
                    CachedString(e, set.RoleProperty),
                    e.CachedIsEnabled != 0,
                    e.CachedIsOffscreen != 0,
                    new TeamsBounds(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top),
                    element.Key,
                    e.CachedProcessId));
            }
            catch (Exception error) when (IsUiaFailure(error))
            {
                return ElementRead<TeamsElementInfo>.Fail("info:" + ErrorKey(error));
            }
        }

        public ElementRead<IReadOnlyList<UiaElement>> ReadChildren(UiaElement element)
        {
            var list = new List<UiaElement>();
            try
            {
                if (traversal == Traversal.Subtree)
                {
                    var children = scope.Track(element.Element.GetCachedChildren());
                    if (children is null) return ElementRead<IReadOnlyList<UiaElement>>.Ok(list);
                    for (var i = 0; i < children.Length; i++)
                    {
                        var child = scope.Track(children.GetElement(i));
                        list.Add(new UiaElement(child, KeyOf(child)));
                    }
                }
                else
                {
                    var child = scope.Track(set.RawWalker.GetFirstChildElementBuildCache(element.Element, set.StructureElement));
                    while (child is not null)
                    {
                        list.Add(new UiaElement(child, KeyOf(child)));
                        child = scope.Track(set.RawWalker.GetNextSiblingElementBuildCache(child, set.StructureElement));
                    }
                }
                return ElementRead<IReadOnlyList<UiaElement>>.Ok(list);
            }
            catch (Exception error) when (IsUiaFailure(error))
            {
                return new ElementRead<IReadOnlyList<UiaElement>>(list, "children:" + ErrorKey(error));
            }
        }
    }

    /// <summary>Releases every COM wrapper a read created, so Chromium can drop its provider objects promptly.</summary>
    private sealed class ComScope : IDisposable
    {
        private readonly List<object> objects = [];

        public T Track<T>(T value)
        {
            if (value is not null && Marshal.IsComObject(value)) objects.Add(value);
            return value;
        }

        public void Dispose()
        {
            foreach (var item in objects)
            {
                try { Marshal.ReleaseComObject(item); }
                catch (Exception error) when (error is ArgumentException or InvalidComObjectException) { }
            }
            objects.Clear();
        }
    }
}
