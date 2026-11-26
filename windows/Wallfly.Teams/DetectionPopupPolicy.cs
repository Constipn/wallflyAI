namespace Wallfly.Teams;

public enum PopupCommand
{
    /// <summary>Nothing to change.</summary>
    None,
    /// <summary>Show (or keep showing and re-position) the popup for <see cref="DetectionPopupPolicy.DisplayedEvent"/>.</summary>
    Show,
    /// <summary>Hide for now; the offer stays and may come back on the next active sample.</summary>
    HideTemporarily,
    /// <summary>Hide and forget the offer.</summary>
    Hide,
}

/// <summary>
/// When the "Meeting detected" popup is shown (pure, time injected; seconds). Combines the macOS behaviour
/// (one offer per detection event, hidden while the call is temporarily unreadable, gone when Teams quits
/// or the event resets, suppressed while the app is busy/presenting) with this timing:
/// shown 2 s after the event if the call is still active and nothing suppresses it, not within 120 s of the
/// previous popup (cooldown), and auto-hidden after 120 s. Nothing ever starts without a click.
/// </summary>
public sealed class DetectionPopupPolicy
{
    public double ShowDelaySeconds { get; init; } = 2;
    public double CooldownSeconds { get; init; } = 120;
    public double AutoHideSeconds { get; init; } = 120;

    private Guid? pending;
    private double pendingSince;
    private double? lastShownAt;
    private double shownAt;

    public Guid? DisplayedEvent { get; private set; }
    public bool Visible { get; private set; }
    public Guid? PendingEvent => pending;

    /// <summary>A new detection event (from <see cref="DetectionPolicy.Observe"/>).</summary>
    public void EventDetected(Guid id, double now)
    {
        pending = id;
        pendingSince = now;
    }

    /// <summary>
    /// Evaluates the current state after a sample. <paramref name="currentEvent"/> is the policy's event ID,
    /// <paramref name="state"/> the latest sample, <paramref name="suppressed"/> the heartbeat rule.
    /// </summary>
    public PopupCommand Update(double now, Guid? currentEvent, TeamsCallState state, bool suppressed)
    {
        if (pending is { } candidate)
        {
            if (candidate != currentEvent || suppressed || state == TeamsCallState.NotRunning) pending = null;
            else if (now - pendingSince >= ShowDelaySeconds)
            {
                pending = null;
                if (state == TeamsCallState.Active && (lastShownAt is null || now - lastShownAt.Value >= CooldownSeconds))
                {
                    DisplayedEvent = candidate;
                    Visible = true;
                    shownAt = now;
                    lastShownAt = now;
                    return PopupCommand.Show;
                }
            }
        }
        if (DisplayedEvent is null) return PopupCommand.None;
        if (suppressed || state == TeamsCallState.NotRunning || currentEvent != DisplayedEvent || now - shownAt >= AutoHideSeconds)
            return Hide();
        if (state == TeamsCallState.Active)
        {
            Visible = true;
            return PopupCommand.Show;
        }
        // Temporarily unreadable or minimized views do not dismiss the current offer.
        Visible = false;
        return PopupCommand.HideTemporarily;
    }

    /// <summary>The close button (×): dismissed for this meeting.</summary>
    public PopupCommand Dismiss() => Hide();

    /// <summary>The click was handed over successfully.</summary>
    public PopupCommand Launched() => Hide();

    /// <summary>Sleep or disabled detection.</summary>
    public PopupCommand Reset()
    {
        pending = null;
        return Hide();
    }

    private PopupCommand Hide()
    {
        var wasShown = DisplayedEvent is not null;
        DisplayedEvent = null;
        Visible = false;
        return wasShown ? PopupCommand.Hide : PopupCommand.None;
    }
}
