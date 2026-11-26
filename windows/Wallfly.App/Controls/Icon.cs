using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Wallfly.App.Controls;

/// <summary>The app's icons; SF Symbol names of macOS map onto these (see <see cref="ViewModels.FolderStyles"/>).</summary>
public enum IconKind
{
    None, Add, Search, Folder, FolderAdd, Settings, ChevronLeft, ChevronRight, ChevronUp, ChevronDown, More, Microphone,
    Record, Stop, Info, Copy, Check, Close, Delete, Rename, Export, OpenFolder, Home, Work, Team, Ideas, Learning, Leaf,
    Star, Heart, Projects, Tray, Stack, Checklist, Waveform, Sparkles, Clock, FocusMode, ShowLibrary, Presentation,
    Refresh, ArrowDown, ArrowUpRight, Warning, Error, Lock, Person, Minus, Edit
}

/// <summary>
/// One icon: a Segoe Fluent Icons glyph (Segoe MDL2 Assets on Windows 10) when the installed font has it, otherwise a
/// stroked path in a 24×24 box. The glyph test runs once per kind, so a missing font never shows a tofu box.
/// The colour follows <see cref="TextElement.ForegroundProperty"/>, like text.
/// </summary>
public sealed class Icon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(IconKind), typeof(Icon),
        new FrameworkPropertyMetadata(IconKind.None, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(nameof(Size), typeof(double), typeof(Icon),
        new FrameworkPropertyMetadata(14d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(Icon),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeWidthProperty = DependencyProperty.Register(nameof(StrokeWidth), typeof(double), typeof(Icon),
        new FrameworkPropertyMetadata(1.6, FrameworkPropertyMetadataOptions.AffectsRender));

    public IconKind Kind { get => (IconKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double StrokeWidth { get => (double)GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }

    private static readonly Dictionary<IconKind, char> Glyphs = new()
    {
        [IconKind.Add] = '', [IconKind.Search] = '', [IconKind.Folder] = '', [IconKind.FolderAdd] = '',
        [IconKind.Settings] = '', [IconKind.ChevronLeft] = '', [IconKind.ChevronRight] = '', [IconKind.ChevronUp] = '',
        [IconKind.ChevronDown] = '', [IconKind.More] = '', [IconKind.Microphone] = '', [IconKind.Record] = '',
        [IconKind.Stop] = '', [IconKind.Info] = '', [IconKind.Copy] = '', [IconKind.Check] = '',
        [IconKind.Close] = '', [IconKind.Delete] = '', [IconKind.Rename] = '', [IconKind.Export] = '',
        [IconKind.OpenFolder] = '', [IconKind.Home] = '', [IconKind.Work] = '', [IconKind.Team] = '',
        [IconKind.Ideas] = '', [IconKind.Learning] = '', [IconKind.Star] = '', [IconKind.Heart] = '',
        [IconKind.Projects] = '', [IconKind.Stack] = '', [IconKind.Checklist] = '', [IconKind.Clock] = '',
        [IconKind.FocusMode] = '', [IconKind.ShowLibrary] = '', [IconKind.Presentation] = '', [IconKind.Refresh] = '',
        [IconKind.ArrowDown] = '', [IconKind.ArrowUpRight] = '', [IconKind.Warning] = '', [IconKind.Error] = '',
        [IconKind.Lock] = '', [IconKind.Person] = '', [IconKind.Minus] = '', [IconKind.Edit] = ''
    };

    /// <summary>Stroked 24×24 fallbacks; also the only drawing for icons without a Segoe glyph (leaf, tray, waveform, sparkles).</summary>
    private static readonly Dictionary<IconKind, string> Paths = new()
    {
        [IconKind.Add] = "M12,3 L12,21 M3,12 L21,12",
        [IconKind.Search] = "M10,3 A7,7 0 1 0 10,17 A7,7 0 1 0 10,3 M15,15 L21,21",
        [IconKind.Folder] = "M2,6 L2,20 L22,20 L22,8 L11,8 L8,4 L2,4 Z",
        [IconKind.FolderAdd] = "M2,6 L2,20 L22,20 L22,12 M2,6 L2,4 L8,4 L11,8 L13,8 M19,2 L19,10 M15,6 L23,6",
        [IconKind.Settings] = "M12,8 A4,4 0 1 0 12,16 A4,4 0 1 0 12,8 M12,1 L12,4 M12,20 L12,23 M1,12 L4,12 M20,12 L23,12 M4.2,4.2 L6.3,6.3 M17.7,17.7 L19.8,19.8 M4.2,19.8 L6.3,17.7 M17.7,6.3 L19.8,4.2",
        [IconKind.ChevronLeft] = "M15,4 L7,12 L15,20", [IconKind.ChevronRight] = "M9,4 L17,12 L9,20",
        [IconKind.ChevronUp] = "M4,15 L12,7 L20,15", [IconKind.ChevronDown] = "M4,9 L12,17 L20,9",
        [IconKind.More] = "M4,12 L4.5,12 M12,12 L12.5,12 M20,12 L20.5,12",
        [IconKind.Microphone] = "M9,3 L15,3 L15,14 L9,14 Z M5,11 C5,20 19,20 19,11 M12,18 L12,22",
        [IconKind.Record] = "M12,4 A8,8 0 1 0 12,20 A8,8 0 1 0 12,4 M12,9 A3,3 0 1 0 12,15 A3,3 0 1 0 12,9",
        [IconKind.Stop] = "M6,6 L18,6 L18,18 L6,18 Z",
        [IconKind.Info] = "M12,2 A10,10 0 1 0 12,22 A10,10 0 1 0 12,2 M12,10 L12,17 M12,6.5 L12,7",
        [IconKind.Copy] = "M8,8 L20,8 L20,21 L8,21 Z M4,16 L4,3 L16,3",
        [IconKind.Check] = "M3,12 L9,18 L21,5",
        [IconKind.Close] = "M5,5 L19,19 M19,5 L5,19",
        [IconKind.Delete] = "M3,6 L21,6 M8,6 L8,3 L16,3 L16,6 M5,6 L6,21 L18,21 L19,6",
        [IconKind.Rename] = "M3,21 L7,20 L20,7 L17,4 L4,17 Z M14,7 L17,10",
        [IconKind.Edit] = "M3,21 L7,20 L20,7 L17,4 L4,17 Z M14,7 L17,10",
        [IconKind.Export] = "M12,15 L12,2 M7,7 L12,2 L17,7 M5,11 L3,11 L3,22 L21,22 L21,11 L19,11",
        [IconKind.OpenFolder] = "M2,6 L2,20 L19,20 L23,10 L6,10 L2,20 M2,6 L2,4 L8,4 L11,7 L19,7 L19,10",
        [IconKind.Home] = "M2,11 L12,2 L22,11 M5,9 L5,22 L10,22 L10,15 L14,15 L14,22 L19,22 L19,9",
        [IconKind.Work] = "M2,7 L22,7 L22,21 L2,21 Z M8,7 L8,3 L16,3 L16,7 M2,12 L22,12",
        [IconKind.Team] = "M8,3 A3,3 0 1 0 8,9 A3,3 0 1 0 8,3 M17,4 A3,3 0 1 0 17,10 A3,3 0 1 0 17,4 M1,21 C1,11 15,11 15,21 M16,21 L23,21 C23,13 18,12 15,14",
        [IconKind.Ideas] = "M8,17 C8,12 4,12 4,8 A8,8 0 1 1 20,8 C20,12 16,12 16,17 Z M8,20 L16,20 M10,23 L14,23",
        [IconKind.Learning] = "M1,8 L12,2 L23,8 L12,14 Z M5,11 L5,18 Q12,23 19,18 L19,11 M23,8 L23,17",
        [IconKind.Leaf] = "M4,20 C4,9 11,4 21,3 C21,13 15,20 4,20 Z M4,20 L13,11",
        [IconKind.Star] = "M12,2 L15,9 L22,9.5 L16.5,14 L18.5,21.5 L12,17.5 L5.5,21.5 L7.5,14 L2,9.5 L9,9 Z",
        [IconKind.Heart] = "M12,21 C-6,9 5,-3 12,6 C19,-3 30,9 12,21 Z",
        [IconKind.Projects] = "M20,2 L15,7 L17,9 L22,4 C24,10 19,14 15,12 L6,21 C5,22 2,22 2,19 L11,10 C9,6 14,0 20,2 Z",
        [IconKind.Tray] = "M2,13 L6,4 L18,4 L22,13 L22,20 L2,20 Z M2,13 L8,13 L10,16 L14,16 L16,13 L22,13",
        [IconKind.Stack] = "M3,8 L21,8 L21,21 L3,21 Z M5,5 L19,5 M7,2 L17,2",
        [IconKind.Checklist] = "M3,5 L5,7 L8,3 M3,12 L5,14 L8,10 M3,19 L5,21 L8,17 M11,5 L21,5 M11,12 L21,12 M11,19 L21,19",
        [IconKind.Waveform] = "M2,10 L2,14 M6,6 L6,18 M10,2 L10,22 M14,7 L14,17 M18,4 L18,20 M22,10 L22,14",
        [IconKind.Sparkles] = "M10,2 C10,8 13,11 19,11 C13,11 10,14 10,21 C10,14 7,11 1,11 C7,11 10,8 10,2 Z M19,1 L19,6 M16.5,3.5 L21.5,3.5",
        [IconKind.Clock] = "M12,2 A10,10 0 1 0 12,22 A10,10 0 1 0 12,2 M12,6 L12,12 L16,14",
        [IconKind.FocusMode] = "M3,9 L3,3 L9,3 M15,3 L21,3 L21,9 M21,15 L21,21 L15,21 M9,21 L3,21 L3,15",
        [IconKind.ShowLibrary] = "M2,3 L22,3 L22,21 L2,21 Z M9,3 L9,21",
        [IconKind.Presentation] = "M2,4 L22,4 L22,17 L2,17 Z M3,22 L21,2",
        [IconKind.Refresh] = "M20,12 A8,8 0 1 1 17.7,6.3 M20,3 L20,8 L15,8",
        [IconKind.ArrowDown] = "M12,3 L12,21 M5,14 L12,21 L19,14",
        [IconKind.ArrowUpRight] = "M7,17 L17,7 M8,7 L17,7 L17,16",
        [IconKind.Warning] = "M12,2 L23,21 L1,21 Z M12,9 L12,15 M12,18 L12,18.5",
        [IconKind.Error] = "M12,2 A10,10 0 1 0 12,22 A10,10 0 1 0 12,2 M12,7 L12,13 M12,16.5 L12,17",
        [IconKind.Lock] = "M5,11 L19,11 L19,22 L5,22 Z M8,11 L8,7 A4,4 0 0 1 16,7 L16,11",
        [IconKind.Person] = "M12,3 A4.5,4.5 0 1 0 12,12 A4.5,4.5 0 1 0 12,3 M3,22 C3,13 21,13 21,22",
        [IconKind.Minus] = "M4,12 L20,12"
    };

    private static readonly IconKind[] PathOnly = [IconKind.Leaf, IconKind.Tray, IconKind.Waveform, IconKind.Sparkles];
    private static readonly Dictionary<IconKind, Geometry> Geometries = [];
    private static readonly Dictionary<IconKind, GlyphTypeface?> Fonts = [];
    private static readonly object Gate = new();
    private static GlyphTypeface?[]? candidates;

    static Icon()
    {
        foreach (var (kind, data) in Paths)
        {
            var geometry = Geometry.Parse(data);
            geometry.Freeze();
            Geometries[kind] = geometry;
        }
    }

    /// <summary>The font that draws <paramref name="kind"/>, or null when the path fallback is used.</summary>
    public static GlyphTypeface? GlyphFont(IconKind kind)
    {
        lock (Gate)
        {
            if (Fonts.TryGetValue(kind, out var cached)) return cached;
            GlyphTypeface? match = null;
            if (!PathOnly.Contains(kind) && Glyphs.TryGetValue(kind, out var glyph))
            {
                candidates ??= new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" }
                    .Select(name => new Typeface(new FontFamily(name), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).TryGetGlyphTypeface(out var face) ? face : null)
                    .ToArray();
                match = candidates.FirstOrDefault(face => face != null && face.CharacterToGlyphMap.ContainsKey(glyph));
            }
            Fonts[kind] = match;
            return match;
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Kind == IconKind.None) return;
        var size = Size;
        var brush = Foreground;
        if (GlyphFont(Kind) is { } face && Glyphs.TryGetValue(Kind, out var glyph))
        {
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var text = new FormattedText(glyph.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(face.FamilyNames.Values.First()), size, brush, dpi);
            drawingContext.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
            return;
        }
        if (!Geometries.TryGetValue(Kind, out var geometry)) return;
        var scale = size / 24d;
        var pen = new Pen(brush, StrokeWidth / scale) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        drawingContext.DrawGeometry(null, pen, geometry);
        drawingContext.Pop();
    }
}
