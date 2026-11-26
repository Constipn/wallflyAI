namespace Wallfly.Core;

/// <summary>When the floating recording capsule is shown, and where. Pure, so it is fully testable.</summary>
public static class LiveIndicatorPolicy
{
    public static bool Visible(bool recording, bool stopping, bool presenting, bool active, bool visible, bool minimized) =>
        recording && !stopping && !presenting && (!active || !visible || minimized);

    // Inputs and output use physical monitor coordinates; scale converts WPF DIPs.
    public static (int X, int Y, int Width, int Height) Placement(int left, int top, int width, int height, double scale)
    {
        var capsuleWidth = Math.Min(width, (int)Math.Round(34 * scale));
        var capsuleHeight = Math.Min(height, (int)Math.Round(101 * scale));
        return (Math.Max(left, left + width - capsuleWidth - (int)Math.Round(24 * scale)),
            top + (height - capsuleHeight) / 2, capsuleWidth, capsuleHeight);
    }
}
