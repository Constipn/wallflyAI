using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Wallfly.App.ViewModels;

namespace Wallfly.App.Controls;

/// <summary>Eight level bars (macOS <c>RecordingActivityView.meter</c>): 3 px wide, 5/8/11 px high, accent above the level.</summary>
public sealed class AudioMeter : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(double), typeof(AudioMeter),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

    private static readonly Brush Quiet = Frozen(Palette.Line);
    private static readonly Brush Active = Frozen(Palette.Accent);

    public AudioMeter() { Width = 8 * 3 + 7 * 2; Height = 14; VerticalAlignment = VerticalAlignment.Center; }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var level = double.IsFinite(Level) ? Math.Clamp(Level, 0, 1) : 0;
        for (var index = 0; index < 8; index++)
        {
            var height = 5 + index % 3 * 3;
            drawingContext.DrawRoundedRectangle(level > index / 8d ? Active : Quiet, null, new Rect(index * 5, (14 - height) / 2d, 3, height), 1, 1);
        }
    }

    internal static Brush Frozen(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}

/// <summary>A small indeterminate ring (the macOS <c>ProgressView().controlSize(.small)</c>).</summary>
public sealed class Spinner : FrameworkElement
{
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(nameof(Size), typeof(double), typeof(Spinner),
        new FrameworkPropertyMetadata(14d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    private static readonly Brush Track = AudioMeter.Frozen(Palette.Line);
    private static readonly Brush Head = AudioMeter.Frozen(Palette.Muted);
    private readonly RotateTransform rotation = new();

    public Spinner()
    {
        RenderTransform = rotation;
        RenderTransformOrigin = new Point(.5, .5);
        IsVisibleChanged += (_, _) => Animate();
        Loaded += (_, _) => Animate();
    }

    private void Animate()
    {
        if (IsVisible && IsLoaded)
            rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        else rotation.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var radius = Size / 2 - 1;
        var center = new Point(Size / 2, Size / 2);
        drawingContext.DrawEllipse(null, new Pen(Track, 1.6), center, radius, radius);
        var arc = new StreamGeometry();
        using (var context = arc.Open())
        {
            context.BeginFigure(new Point(center.X, center.Y - radius), false, false);
            context.ArcTo(new Point(center.X + radius, center.Y), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
        }
        arc.Freeze();
        drawingContext.DrawGeometry(null, new Pen(Head, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);
    }
}

/// <summary>
/// Search highlighting for a <see cref="TextBlock"/>: every match of the query's terms gets the gold background
/// (#F7F2DD, spec 9.6). The matching itself is <see cref="SearchHighlight.Split"/>, shared with the tests.
/// </summary>
public static class Highlight
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached("Text", typeof(string), typeof(Highlight),
        new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached("Query", typeof(string), typeof(Highlight),
        new PropertyMetadata(null, Changed));

    public static string? GetText(DependencyObject target) => (string?)target.GetValue(TextProperty);
    public static void SetText(DependencyObject target, string? value) => target.SetValue(TextProperty, value);
    public static string? GetQuery(DependencyObject target) => (string?)target.GetValue(QueryProperty);
    public static void SetQuery(DependencyObject target, string? value) => target.SetValue(QueryProperty, value);

    private static readonly Brush Mark = AudioMeter.Frozen(Palette.Gold100);
    private static readonly Brush MarkInk = AudioMeter.Frozen(Palette.Ink);

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not TextBlock block) return;
        block.Inlines.Clear();
        foreach (var run in SearchHighlight.Split(GetText(block) ?? "", GetQuery(block)))
            block.Inlines.Add(run.Match ? new Run(run.Text) { Background = Mark, Foreground = MarkInk } : new Run(run.Text));
    }
}

/// <summary>Binds <see cref="PasswordBox.Password"/>, which is deliberately not a dependency property.</summary>
public static class Secure
{
    // Default is null, not "": a view model whose key is still empty must still trigger Changed once so the
    // PasswordChanged handler gets attached. With "" as default an empty key never changed the value, the handler
    // was never attached, and typed keys never reached the view model (the "key stays missing" bug).
    public static readonly DependencyProperty PasswordProperty = DependencyProperty.RegisterAttached("Password", typeof(string), typeof(Secure),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, Changed));
    private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached("Attached", typeof(bool), typeof(Secure));

    public static string GetPassword(DependencyObject target) => target.GetValue(PasswordProperty) as string ?? "";
    public static void SetPassword(DependencyObject target, string value) => target.SetValue(PasswordProperty, value);

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not PasswordBox box) return;
        if (!(bool)box.GetValue(AttachedProperty))
        {
            box.SetValue(AttachedProperty, true);
            box.PasswordChanged += (_, _) => { if (GetPassword(box) != box.Password) SetPassword(box, box.Password); };
        }
        var value = args.NewValue as string ?? "";
        if (box.Password != value) box.Password = value;
    }
}

/// <summary>
/// The enhanced-notes renderer (port of the macOS <c>markdown(_:)</c> view): # / ## / ### headings in the display font,
/// "- " and "* " bullets, and inline **bold**, *italic*, `code`. Set on a read-only <see cref="RichTextBox"/>
/// so the text stays selectable.
/// </summary>
public static class Markdown
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached("Text", typeof(string), typeof(Markdown),
        new PropertyMetadata(null, Changed));
    public static string? GetText(DependencyObject target) => (string?)target.GetValue(TextProperty);
    public static void SetText(DependencyObject target, string? value) => target.SetValue(TextProperty, value);

    private static readonly Regex InlinePattern = new(@"(\*\*[^*]+\*\*|`[^`]+`|\*[^*\s][^*]*\*)", RegexOptions.Compiled);

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is RichTextBox box) box.Document = Document(args.NewValue as string ?? "");
    }

    public static FlowDocument Document(string markdown)
    {
        var document = new FlowDocument { FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), FontSize = 13, PagePadding = new Thickness(0), Foreground = AudioMeter.Frozen(Palette.Ink) };
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0) continue;
            Paragraph paragraph;
            if (line.StartsWith("### ", StringComparison.Ordinal))
                paragraph = new Paragraph { FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 12) };
            else if (line.StartsWith("## ", StringComparison.Ordinal))
                paragraph = new Paragraph { FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), FontWeight = FontWeights.SemiBold, FontSize = 20, Margin = new Thickness(0, 13, 0, 12) };
            else if (line.StartsWith("# ", StringComparison.Ordinal))
                paragraph = new Paragraph { FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), FontWeight = FontWeights.SemiBold, FontSize = 25, Margin = new Thickness(0, 8, 0, 12) };
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                paragraph = new Paragraph { Margin = new Thickness(18, 0, 0, 12), TextIndent = -18, LineHeight = 20 };
                paragraph.Inlines.Add(new Run("•\t") { });
                AddInlines(paragraph, line[2..]);
                document.Blocks.Add(paragraph);
                continue;
            }
            else paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 12), LineHeight = 20 };
            AddInlines(paragraph, line.TrimStart('#', ' '));
            document.Blocks.Add(paragraph);
        }
        return document;
    }

    private static void AddInlines(Paragraph paragraph, string text)
    {
        foreach (var part in InlinePattern.Split(text))
        {
            if (part.Length == 0) continue;
            if (part.Length > 4 && part.StartsWith("**", StringComparison.Ordinal) && part.EndsWith("**", StringComparison.Ordinal))
                paragraph.Inlines.Add(new Bold(new Run(part[2..^2])));
            else if (part.Length > 2 && part[0] == '`' && part[^1] == '`')
                paragraph.Inlines.Add(new Run(part[1..^1]) { FontFamily = new FontFamily("Cascadia Mono, Consolas"), Background = AudioMeter.Frozen(Palette.Gray100) });
            else if (part.Length > 2 && part[0] == '*' && part[^1] == '*')
                paragraph.Inlines.Add(new Italic(new Run(part[1..^1])));
            else paragraph.Inlines.Add(new Run(part));
        }
    }
}

/// <summary>Marks an element whose clicks never count as "outside" for the live transcript box (its toggle button).</summary>
public static class OutsideClick
{
    public static readonly DependencyProperty ExemptProperty = DependencyProperty.RegisterAttached("Exempt", typeof(bool), typeof(OutsideClick));
    public static bool GetExempt(DependencyObject target) => (bool)target.GetValue(ExemptProperty);
    public static void SetExempt(DependencyObject target, bool value) => target.SetValue(ExemptProperty, value);

    /// <summary>True when <paramref name="source"/> lies in <paramref name="panel"/> or in an exempt element.</summary>
    public static bool IsInside(DependencyObject? source, DependencyObject panel)
    {
        for (var current = source; current != null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, panel) || GetExempt(current)) return true;
        }
        return false;
    }
}

/// <summary>Places one chat bubble at the leading or trailing edge, at most <see cref="Fraction"/> of the row wide (macOS <c>ChatBubbleRow</c>).</summary>
public sealed class BubbleRow : Panel
{
    public static readonly DependencyProperty TrailingProperty = DependencyProperty.Register(nameof(Trailing), typeof(bool), typeof(BubbleRow),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsArrange));
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(nameof(Fraction), typeof(double), typeof(BubbleRow),
        new FrameworkPropertyMetadata(0.75, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public bool Trailing { get => (bool)GetValue(TrailingProperty); set => SetValue(TrailingProperty, value); }
    public double Fraction { get => (double)GetValue(FractionProperty); set => SetValue(FractionProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width * Fraction;
        double height = 0, desired = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(width, double.PositiveInfinity));
            height = Math.Max(height, child.DesiredSize.Height);
            desired = Math.Max(desired, child.DesiredSize.Width);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? desired : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (UIElement child in InternalChildren)
        {
            var width = Math.Min(child.DesiredSize.Width, finalSize.Width * Fraction);
            child.Arrange(new Rect(Trailing ? finalSize.Width - width : 0, 0, width, child.DesiredSize.Height));
        }
        return finalSize;
    }
}

/// <summary>One enhanced-notes line in the preview (macOS <c>MarkdownLine</c>): headings in the display font, bullets, inline bold.</summary>
public static class MarkdownLine
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached("Text", typeof(string), typeof(MarkdownLine),
        new PropertyMetadata(null, Changed));
    public static string? GetText(DependencyObject target) => (string?)target.GetValue(TextProperty);
    public static void SetText(DependencyObject target, string? value) => target.SetValue(TextProperty, value);

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not TextBlock block) return;
        var line = args.NewValue as string ?? "";
        block.Inlines.Clear();
        block.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        block.FontWeight = FontWeights.Normal;
        block.FontSize = 14;
        if (line.StartsWith("### ", StringComparison.Ordinal)) { block.FontSize = 16; block.FontWeight = FontWeights.SemiBold; line = line[4..]; }
        else if (line.StartsWith("## ", StringComparison.Ordinal)) { block.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"); block.FontWeight = FontWeights.SemiBold; block.FontSize = 20; line = line[3..]; }
        else if (line.StartsWith("# ", StringComparison.Ordinal)) { block.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"); block.FontWeight = FontWeights.SemiBold; block.FontSize = 25; line = line[2..]; }
        else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)) line = "•  " + line[2..];
        foreach (var part in Regex.Split(line, @"(\*\*[^*]+\*\*)"))
        {
            if (part.Length == 0) continue;
            if (part.Length > 4 && part.StartsWith("**", StringComparison.Ordinal) && part.EndsWith("**", StringComparison.Ordinal)) block.Inlines.Add(new Bold(new Run(part[2..^2])));
            else block.Inlines.Add(new Run(part));
        }
    }
}
