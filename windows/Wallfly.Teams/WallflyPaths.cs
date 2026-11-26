namespace Wallfly.Teams;

/// <summary>Names shared by Wallfly and Wallfly Detector. Both processes must agree on every one of them.</summary>
public static class WallflyPaths
{
    /// <summary><c>%LOCALAPPDATA%\Wallfly</c>; fixed even when the meeting library moves (see LibraryPointer).</summary>
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wallfly");

    /// <summary><c>%LOCALAPPDATA%\Wallfly\Detection</c>: the helper's file protocol (spec §6).</summary>
    public static string DetectionRoot => Path.Combine(DataRoot, "Detection");

    /// <summary>Single-instance mutex of the main app (Wallfly.App/App.xaml.cs).</summary>
    public const string AppMutexName = @"Local\Wallfly.Windows";

    /// <summary>Single-instance mutex of the helper.</summary>
    public const string DetectorMutexName = @"Local\WallflyDetector";

    /// <summary><c>\\.\pipe\Wallfly.Activation</c>.</summary>
    public const string ActivationPipeName = "Wallfly.Activation";

    public const string AppExecutableName = "Wallfly.exe";
    public const string DetectorExecutableName = "Wallfly Detector.exe";

    /// <summary>Command-line switch of the main app: <c>Wallfly.exe --detected-teams &lt;UUID&gt;</c>.</summary>
    public const string DetectedTeamsSwitch = "--detected-teams";

    /// <summary>Whether the main app currently runs in this session (its single-instance mutex exists).</summary>
    public static bool IsAppRunning(string mutexName = AppMutexName)
    {
        try
        {
            if (!Mutex.TryOpenExisting(mutexName, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; }
    }
}
