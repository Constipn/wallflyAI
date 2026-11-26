using System.Runtime.InteropServices;

namespace Wallfly.Core.Diagnostics;

/// <summary>AC or battery for the recording health line (<c>GetSystemPowerStatus</c>).</summary>
public static partial class PowerStatus
{
    /// <summary>"AC", "battery 54%", "AC (battery 98%)" or "unknown".</summary>
    public static string Describe()
    {
        try
        {
            if (!GetSystemPowerStatus(out var status)) return "unknown";
            var percent = status.BatteryLifePercent <= 100 ? $" {status.BatteryLifePercent}%" : "";
            var noBattery = (status.BatteryFlag & 128) != 0 || status.BatteryFlag == 255;
            return status.ACLineStatus switch
            {
                1 => noBattery ? "AC" : $"AC (battery{percent})",
                0 => $"battery{percent}",
                _ => "unknown"
            };
        }
        catch (Exception) { return "unknown"; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);
}
