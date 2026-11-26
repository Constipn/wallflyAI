namespace Wallfly.Audio;

/// <summary>
/// The parsed <c>WfEvent.detail</c> (wallfly_audio.h "Event detail grammar", 2026-11-23): fields separated by <c>|</c>, each
/// optionally prefixed. <c>mic:&lt;friendly name&gt;</c> / <c>render:&lt;friendly name&gt;</c> name the endpoint of a
/// DEVICE_ACQUIRED / DEVICE_SWITCHED / DEVICE_LOST; <c>fallback:&lt;reason&gt;</c> marks a microphone or pinned output that
/// could not be opened (the Me fallback stays exactly <c>fallback:&lt;reason&gt;</c>, possibly followed by
/// <c>|mic:&lt;name&gt;</c>); <c>keepalive:&lt;what&gt;</c> is the subject of a <see cref="AudioEngineEventKind.Warning"/>
/// (<c>keepalive:start failed 0x88890004|render:Speakers|retry in 0.5 s</c> / <c>…|gave up</c>). Fields without a known
/// prefix (an older DLL's plain detail, "retry in 0.5 s", "gave up") are kept in <see cref="Other"/> in order. Pure.
/// </summary>
/// <param name="Microphone">The <c>mic:</c> name; null when absent or empty.</param>
/// <param name="Render">The <c>render:</c> name; null when absent or empty.</param>
/// <param name="Fallback">The <c>fallback:</c> reason; null without the field, "" when the reason is empty.</param>
/// <param name="KeepAlive">The <c>keepalive:</c> text ("start failed 0x88890004", "stopped 0x…"); null when absent.</param>
/// <param name="Other">The unprefixed fields, trimmed, without empty ones.</param>
public sealed record AudioEventDetail(string? Microphone, string? Render, string? Fallback, string? KeepAlive, IReadOnlyList<string> Other)
{
    public const string MicrophonePrefix = "mic:";
    public const string RenderPrefix = "render:";
    public const string FallbackPrefix = "fallback:";
    public const string KeepAlivePrefix = "keepalive:";
    /// <summary>The last field of a keep-alive warning after the third failed start in a row (no further retries until the endpoint changes).</summary>
    public const string GaveUp = "gave up";

    public static AudioEventDetail Empty { get; } = new(null, null, null, null, []);

    /// <summary>A fallback field is present (possibly with an empty reason).</summary>
    public bool IsFallback => Fallback != null;

    /// <summary>The device the event names: the <c>render:</c> or <c>mic:</c> name, else the first unprefixed field (an older DLL's plain detail); null when none.</summary>
    public string? DeviceName => Render ?? Microphone ?? (Other.Count > 0 && KeepAlive is null ? Other[0] : null);

    /// <summary>A keep-alive warning that stopped retrying (its last field is <see cref="GaveUp"/>).</summary>
    public bool KeepAliveGaveUp => KeepAlive != null && Other.Any(item => string.Equals(item, GaveUp, StringComparison.OrdinalIgnoreCase));

    public static AudioEventDetail Parse(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return Empty;
        string? microphone = null, render = null, fallback = null, keepAlive = null;
        var other = new List<string>();
        foreach (var raw in detail.Split('|'))
        {
            var field = raw.Trim();
            if (field.Length == 0) continue;
            if (TryValue(field, MicrophonePrefix, out var value)) microphone ??= NullIfEmpty(value);
            else if (TryValue(field, RenderPrefix, out value)) render ??= NullIfEmpty(value);
            else if (TryValue(field, FallbackPrefix, out value)) fallback ??= value;
            else if (TryValue(field, KeepAlivePrefix, out value)) keepAlive ??= value;
            else other.Add(field);
        }
        return new(microphone, render, fallback, keepAlive, other);
    }

    private static bool TryValue(string field, string prefix, out string value)
    {
        if (field.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = field[prefix.Length..].Trim();
            return true;
        }
        value = "";
        return false;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
