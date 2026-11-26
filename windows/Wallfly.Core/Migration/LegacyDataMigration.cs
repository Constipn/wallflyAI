using System.Text.Json;
using System.Text.Json.Serialization;
using Wallfly.Core.Json;

namespace Wallfly.Core.Migration;

/// <summary>What the one-time copy from the first Windows port did.</summary>
public sealed class MigrationReceipt
{
    [JsonPropertyName("migratedAt")] public DateTimeOffset MigratedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("files")] public int Files { get; set; }
    [JsonPropertyName("note")] public string Note { get; set; } = "Copied from the Meeting Notes data folder; the original was left in place.";
}

/// <summary>
/// One-time copy of <c>%LOCALAPPDATA%\MeetingNotes</c> into <c>%LOCALAPPDATA%\Wallfly</c>. The source
/// is never deleted or moved, a non-empty destination is never overwritten, and the receipt
/// <c>migrated.json</c> makes the copy happen exactly once.
/// </summary>
public static class LegacyDataMigration
{
    public const string ReceiptFile = "migrated.json";

    public static string DefaultLegacyRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingNotes");

    public static bool AlreadyMigrated(string destination) => File.Exists(Path.Combine(destination, ReceiptFile));

    /// <summary>Returns true when files were copied. Any failure leaves both folders usable.</summary>
    public static bool Run(string destination, string? legacyRoot = null)
    {
        var source = legacyRoot ?? DefaultLegacyRoot;
        if (AlreadyMigrated(destination) || !Directory.Exists(source)) return false;
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) return false;
        Directory.CreateDirectory(destination);
        if (Directory.EnumerateFileSystemEntries(destination).Any()) return false;

        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
            copied++;
        }
        MeetingLibraryReceipt(destination, source, copied);
        return copied > 0;
    }

    private static void MeetingLibraryReceipt(string destination, string source, int files) =>
        Persistence.MeetingLibrary.AtomicWrite(Path.Combine(destination, ReceiptFile),
            JsonSerializer.SerializeToUtf8Bytes(new MigrationReceipt { Source = source, Files = files }, WallflyJson.Options));
}
