using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wallfly.Teams;

/// <summary>
/// Atomic replace that also works while another process has the target open for reading. A plain
/// <c>MoveFileEx(REPLACE_EXISTING)</c> fails with access denied in that case (measured: the helper and the app poll
/// each other's files every 2 s), so the rename uses POSIX semantics (<c>FileRenameInfoEx</c> with
/// <c>FILE_RENAME_FLAG_POSIX_SEMANTICS</c>, Windows 10 1607+ on NTFS): open readers keep the old contents, the name
/// points to the new file at once. Readers must share delete (DetectionStorage does). Falls back to
/// <see cref="File.Move(string, string, bool)"/> with retries where POSIX renames are unavailable.
/// </summary>
internal static unsafe partial class AtomicFile
{
    private const uint Delete = 0x00010000;
    private const uint Synchronize = 0x00100000;
    private const uint ShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const int FileRenameInfoEx = 22;
    private const uint ReplaceIfExists = 0x1;
    private const uint PosixSemantics = 0x2;

    public static void Replace(string source, string destination)
    {
        if (TryPosixRename(source, destination)) return;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception error) when (attempt < 8 && error is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(10 * (attempt + 1));
            }
        }
    }

    private static bool TryPosixRename(string source, string destination)
    {
        using var handle = CreateFileW(source, Delete | Synchronize, ShareAll, 0, OpenExisting, 0, 0);
        if (handle.IsInvalid) return false;
        var target = Path.GetFullPath(destination);
        // FILE_RENAME_INFO: Flags (4) + padding (4) + RootDirectory (8) + FileNameLength (4) + FileName (UTF-16, NUL).
        var nameOffset = 8 + IntPtr.Size + 4;
        var size = nameOffset + (target.Length + 1) * sizeof(char);
        var buffer = (byte*)NativeMemory.AllocZeroed((nuint)size);
        try
        {
            *(uint*)buffer = ReplaceIfExists | PosixSemantics;
            *(nint*)(buffer + 8) = 0;
            *(uint*)(buffer + 8 + IntPtr.Size) = (uint)(target.Length * sizeof(char));
            fixed (char* name = target) Buffer.MemoryCopy(name, buffer + nameOffset, size - nameOffset, target.Length * sizeof(char));
            return SetFileInformationByHandle(handle, FileRenameInfoEx, buffer, (uint)size);
        }
        finally { NativeMemory.Free(buffer); }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, void* information, uint size);
}
