using System.Globalization;

namespace Wallfly.Core.Export;

/// <summary>
/// Markdown export of spec 8: title, date and duration, the user's notes, the enhanced notes with
/// timestamp citations removed, the transcript as <c>[mm:ss] Name: Text</c>, and the transcription
/// gaps when there are any. Port of <c>Meeting.markdown</c>; to-dos are deliberately not exported.
/// </summary>
public static class MarkdownExporter
{
    public static string Export(Meeting meeting)
    {
        var gaps = (meeting.TranscriptionGaps ?? [])
            .Select(gap => $"- {Meeting.Timestamp(gap.Start)} · {gap.Speaker} · {gap.Duration.ToString("F2", CultureInfo.InvariantCulture)} s: {gap.Reason}")
            .ToList();
        var warning = gaps.Count == 0 ? "" : "\n## Transcription gaps / unconfirmed audio\n\n" + string.Join('\n', gaps) + "\n";
        var date = (meeting.StartedAt ?? meeting.CreatedAt).ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture);
        return $"# {meeting.Title}\n\n{date} · {Meeting.Timestamp(meeting.Duration)}\n\n"
            + $"## My notes\n\n{meeting.Notes}\n\n## Enhanced notes\n\n{meeting.DisplayedEnhancedNotes}\n\n"
            + $"## Transcript\n\n{meeting.ConversationText}\n" + warning;
    }

    /// <summary>A file name derived from the title, with every invalid Windows character replaced.</summary>
    public static string FileName(Meeting meeting)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = string.Concat((meeting.Title ?? "").Select(character => invalid.Contains(character) ? '_' : character)).Trim();
        return (name.Length == 0 ? "meeting" : name) + ".md";
    }
}
