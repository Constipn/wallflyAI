using System.Text.Json.Serialization;

namespace Wallfly.Teams;

/// <summary>
/// Debounces Teams samples into one "meeting detected" event per call (port of TeamsDetectionKit
/// DetectionPolicy, macOS 1.13.0): samples every 2 s, two consecutive <c>active</c> emit one event, ≥ 10 s of
/// <c>absent</c> reset it, a new Teams process resets everything, <c>unknown</c> neither confirms nor ends.
/// Persisted as <c>session.json</c> so a helper restart does not show the popup again; it holds only the
/// announced session, never UI data or participant information.
/// </summary>
public sealed class DetectionPolicy : IEquatable<DetectionPolicy>
{
    public const int RequiredConfirmations = 2;
    public const double RequiredAbsenceSeconds = 10;

    [JsonPropertyName("pid"), JsonInclude] public int? Pid { get; private set; }
    [JsonPropertyName("eventID"), JsonInclude] public Guid? EventId { get; private set; }
    [JsonPropertyName("confirmations"), JsonInclude] public int Confirmations { get; private set; }
    [JsonPropertyName("absentSince"), JsonInclude] public double? AbsentSince { get; private set; }

    /// <summary>Feeds one sample taken at <paramref name="time"/> (seconds); returns a new event ID at most once per call.</summary>
    public Guid? Observe(TeamsCallSample sample, double time, Func<Guid>? newId = null)
    {
        switch (sample.State)
        {
            case TeamsCallState.NotRunning:
                Reset();
                break;
            case TeamsCallState.Unknown:
            case TeamsCallState.Unavailable:
                Confirmations = 0;
                AbsentSince = null;
                break;
            case TeamsCallState.Absent:
                if (Pid != sample.Pid) { Reset(); Pid = sample.Pid; }
                Confirmations = 0;
                AbsentSince ??= time;
                if (time - AbsentSince.Value >= RequiredAbsenceSeconds) EventId = null;
                break;
            case TeamsCallState.Active:
                if (Pid != sample.Pid) { Reset(); Pid = sample.Pid; }
                AbsentSince = null;
                Confirmations = Math.Min(RequiredConfirmations, Confirmations + 1);
                if (Confirmations >= RequiredConfirmations && EventId is null)
                {
                    var id = (newId ?? Guid.NewGuid)();
                    EventId = id;
                    return id;
                }
                break;
        }
        return null;
    }

    /// <summary>A restarted helper (or a wake from sleep) must collect fresh confirmations and fresh end evidence.</summary>
    public void Resume()
    {
        Confirmations = 0;
        AbsentSince = null;
    }

    private void Reset()
    {
        Pid = null;
        EventId = null;
        Confirmations = 0;
        AbsentSince = null;
    }

    public DetectionPolicy Clone() => (DetectionPolicy)MemberwiseClone();

    public bool Equals(DetectionPolicy? other) => other is not null &&
        Pid == other.Pid && EventId == other.EventId && Confirmations == other.Confirmations && Nullable.Equals(AbsentSince, other.AbsentSince);

    public override bool Equals(object? obj) => Equals(obj as DetectionPolicy);

    public override int GetHashCode() => HashCode.Combine(Pid, EventId, Confirmations, AbsentSince);
}

/// <summary>
/// Detects the end of a Teams call during a recording (port of MeetingEndPolicy.swift). Never ends a recording
/// that did not first see two consecutive active samples, so in-person meetings are left alone; then fires
/// exactly once after ≥ 10 s of absent/not-running. Unknown and Unavailable reset the absence timer.
/// </summary>
public sealed class MeetingEndPolicy
{
    public const int RequiredActiveSamples = 2;
    public const double RequiredAbsenceSeconds = 10;

    private int activeSamples;
    private double? absentSince;

    public bool IsArmed { get; private set; }
    public bool HasEnded { get; private set; }

    /// <summary>Returns true exactly once, when the call has been gone long enough.</summary>
    public bool Observe(TeamsCallState sample, double time)
    {
        if (HasEnded) return false;
        switch (sample)
        {
            case TeamsCallState.Active:
                absentSince = null;
                activeSamples = Math.Min(RequiredActiveSamples, activeSamples + 1);
                if (activeSamples >= RequiredActiveSamples) IsArmed = true;
                break;
            case TeamsCallState.Absent:
            case TeamsCallState.NotRunning:
                if (!IsArmed) { activeSamples = 0; return false; }
                absentSince ??= time;
                if (time - absentSince.Value >= RequiredAbsenceSeconds)
                {
                    HasEnded = true;
                    return true;
                }
                break;
            default:
                absentSince = null;
                if (!IsArmed) activeSamples = 0;
                break;
        }
        return false;
    }

    /// <summary>
    /// The caller did not end the recording after <see cref="Observe"/> fired (audio evidence of a running call, Windows):
    /// the absence is kept, so the policy fires again on the next absent/not-running sample and the caller decides again;
    /// an active (or unknown) sample forgets the absence as usual.
    /// </summary>
    public void Veto()
    {
        if (HasEnded && absentSince != null) HasEnded = false;
    }
}
