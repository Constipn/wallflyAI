using System.Diagnostics;

namespace Wallfly.Core.Diagnostics;

/// <summary>
/// The app's log calls. Every line goes through <see cref="Trace"/>, so the <see cref="RollingFileTraceListener"/> the app
/// registers at start (and a test's own listener) receives it next to the existing <c>Trace.Trace*</c> calls.
/// </summary>
/// <remarks>
/// Privacy rule for every caller: ids (GUIDs), device and process names, counts, levels, durations and status strings
/// only; never transcript text, notes, meeting titles, participant names or keys.
/// </remarks>
public static class WallflyLog
{
    public static void Info(string message) => Trace.TraceInformation(message);
    public static void Warning(string message) => Trace.TraceWarning(message);
    public static void Error(string message) => Trace.TraceError(message);
}
