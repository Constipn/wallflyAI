using System.Text.RegularExpressions;

namespace Wallfly.Core.Text;

/// <summary>
/// Removes transcript timestamp citations such as [03:14] or [03:14–05:20] from enhanced notes.
/// Port of <c>TextSearch.cleanNotes</c>: line count is preserved (search maps hits by line) and
/// Markdown links are left alone. Stored notes are never rewritten; this is a display projection.
/// </summary>
public static partial class TimestampCitations
{
    private const string Token = @"\d{1,3}:\d{2}(?::\d{2})?";
    private const string Citation = @"\[" + Token + @"(?:[ \t]*(?:[-–—,;/]|to|bis)[ \t]*" + Token + @")*\](?!\()";
    private const string Run = "(?:" + Citation + "[ \t]*)*" + Citation;

    [GeneratedRegex(@"(?m)^([ \t]*(?:[-*][ \t]+)?)" + Run + "[ \t]*")] private static partial Regex Leading();
    [GeneratedRegex("[ \t]*" + Run)] private static partial Regex Inline();

    public static string Strip(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('[')) return text ?? "";
        return Inline().Replace(Leading().Replace(text, "$1"), "");
    }
}
