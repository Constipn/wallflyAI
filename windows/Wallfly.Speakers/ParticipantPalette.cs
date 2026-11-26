using Wallfly.Core;

namespace Wallfly.Speakers;

/// <summary>An sRGB colour of the approved palette.</summary>
public readonly record struct ParticipantColor(byte R, byte G, byte B)
{
    public static ParticipantColor FromHex(uint rgb) => new((byte)((rgb >> 16) & 255), (byte)((rgb >> 8) & 255), (byte)(rgb & 255));
    /// <summary><c>#RRGGBB</c>, uppercase.</summary>
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";
    public override string ToString() => Hex;
}

/// <summary>
/// Speaker colours of the transcript (spec 5.5; Palette.swift <c>speakerColors</c> and SpeakerViews.swift). A participant
/// keeps the colour of its <c>ColorIndex</c>, assigned once in creation order.
/// </summary>
public static class ParticipantPalette
{
    /// <summary>Red-600: the Me channel (Palette.accent).</summary>
    public static ParticipantColor Red600 { get; } = ParticipantColor.FromHex(0xC32B3D);
    /// <summary>Grey-700: unnamed Others (Palette.muted).</summary>
    public static ParticipantColor Gray700 { get; } = ParticipantColor.FromHex(0x565D66);

    /// <summary>#225D88, #356851, #146A59, #6B588A, red-600, grey-700 — in this fixed order.</summary>
    public static IReadOnlyList<ParticipantColor> SpeakerColors { get; } =
    [
        ParticipantColor.FromHex(0x225D88), ParticipantColor.FromHex(0x356851), ParticipantColor.FromHex(0x146A59),
        ParticipantColor.FromHex(0x6B588A), Red600, Gray700
    ];

    /// <summary>Colour of the Me channel.</summary>
    public static ParticipantColor Me => Red600;
    /// <summary>Colour of a remote turn without participant.</summary>
    public static ParticipantColor Unnamed => Gray700;

    /// <summary>The colour of a participant's <c>ColorIndex</c>; negative indices use the first colour, the list repeats.</summary>
    public static ParticipantColor ForIndex(int colorIndex) => SpeakerColors[Math.Max(0, colorIndex) % SpeakerColors.Count];

    /// <summary>The bubble/label colour of a turn: Me, a named participant, or unnamed Others.</summary>
    public static ParticipantColor For(Speaker speaker, MeetingParticipant? participant) =>
        speaker == Speaker.Me ? Me : participant is null ? Unnamed : ForIndex(participant.ColorIndex);

    /// <summary>The colour index for a new participant: the number of participants so far (as on macOS).</summary>
    public static int NextColorIndex(IReadOnlyCollection<MeetingParticipant>? participants) => participants?.Count ?? 0;
}
