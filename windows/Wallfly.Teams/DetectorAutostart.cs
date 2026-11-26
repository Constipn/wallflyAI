using Microsoft.Win32;

namespace Wallfly.Teams;

/// <summary>Access to the per-user Run key; faked in tests (tests never write the registry).</summary>
public interface IRunKeyStore
{
    string? Get(string name);
    void Set(string name, string value);
    void Delete(string name);

    /// <summary>The Task Manager startup state (<c>Explorer\StartupApproved\Run</c>), if any.</summary>
    byte[]? GetApproval(string name);
}

public enum AutostartState
{
    NotRegistered,
    Enabled,
    /// <summary>Registered, but the user disabled it in Task Manager → Startup apps (like macOS "requires approval").</summary>
    DisabledByUser,
    /// <summary>Registered for another executable path (e.g. an older install location).</summary>
    OtherPath,
}

/// <summary>
/// Launch-at-login of Wallfly Detector via <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run\WallflyDetector</c>
/// (spec §6; an MSIX package would use a StartupTask instead). The app calls this from its settings toggle;
/// on first launch it registers once (autostart is enabled on first start).
/// </summary>
public static class DetectorAutostart
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string ValueName = "WallflyDetector";
    public const string AutostartSwitch = "--autostart";

    public static string CommandLine(string detectorPath) => $"\"{Path.GetFullPath(detectorPath)}\" {AutostartSwitch}";

    /// <summary>The detector next to the running app executable.</summary>
    public static string DefaultDetectorPath => Path.Combine(AppContext.BaseDirectory, WallflyPaths.DetectorExecutableName);

    public static AutostartState GetState(string detectorPath, IRunKeyStore? store = null)
    {
        store ??= new RegistryRunKeyStore();
        var value = store.Get(ValueName);
        if (string.IsNullOrWhiteSpace(value)) return AutostartState.NotRegistered;
        if (!string.Equals(value.Trim(), CommandLine(detectorPath), StringComparison.OrdinalIgnoreCase)) return AutostartState.OtherPath;
        // StartupApproved: first byte even (0x02/0x06) = enabled, odd (0x03/0x07) = disabled by the user.
        var approval = store.GetApproval(ValueName);
        return approval is { Length: > 0 } && (approval[0] & 1) == 1 ? AutostartState.DisabledByUser : AutostartState.Enabled;
    }

    public static bool IsEnabled(string detectorPath, IRunKeyStore? store = null) => GetState(detectorPath, store) == AutostartState.Enabled;

    public static void SetEnabled(bool enabled, string detectorPath, IRunKeyStore? store = null)
    {
        store ??= new RegistryRunKeyStore();
        if (enabled) store.Set(ValueName, CommandLine(detectorPath));
        else store.Delete(ValueName);
    }
}

/// <summary>HKCU Run key.</summary>
public sealed class RegistryRunKeyStore : IRunKeyStore
{
    public string? Get(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(DetectorAutostart.RunKeyPath, writable: false);
        return key?.GetValue(name) as string;
    }

    public void Set(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(DetectorAutostart.RunKeyPath, writable: true);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void Delete(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(DetectorAutostart.RunKeyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public byte[]? GetApproval(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(DetectorAutostart.ApprovedKeyPath, writable: false);
        return key?.GetValue(name) as byte[];
    }
}
