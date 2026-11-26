using System.Net.Http.Headers;
using System.Text;

namespace Wallfly.Transcription;

/// <summary>
/// The multipart body exactly as the macOS clients write it: a <c>MeetingNotes-UUID</c> boundary, bare
/// <c>Content-Disposition</c> headers for text fields (no per-part content type), and one
/// <c>file</c> part named <c>meeting.wav</c> with <c>Content-Type: audio/wav</c>.
/// </summary>
internal sealed class SwiftMultipart
{
    private readonly MemoryStream body = new();

    public string Boundary { get; } = "MeetingNotes-" + Guid.NewGuid().ToString("D").ToUpperInvariant();

    public SwiftMultipart Field(string name, string value)
    {
        Write($"--{Boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n");
        return this;
    }

    /// <summary>Appends the WAV part and the closing boundary; returns the request content.</summary>
    public ByteArrayContent Finish(byte[] wav)
    {
        Write($"--{Boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"meeting.wav\"\r\nContent-Type: audio/wav\r\n\r\n");
        body.Write(wav);
        Write($"\r\n--{Boundary}--\r\n");
        var content = new ByteArrayContent(body.ToArray());
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=" + Boundary);
        return content;
    }

    private void Write(string text) => body.Write(Encoding.UTF8.GetBytes(text));
}
