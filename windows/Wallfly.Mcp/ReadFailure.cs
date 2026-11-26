namespace Wallfly.Mcp;

/// <summary>The failure classes of the macOS <c>ReadFailure</c>, plus the Windows folder filter.</summary>
public enum ReadFailure
{
    Unavailable,
    InvalidArguments,
    NotFound,
    DamagedMeeting,
    StaleCursor,
    /// <summary>Windows extension: <c>search_meetings</c> named a folder the library does not have.</summary>
    FolderNotFound
}

/// <summary>A tool call that must end as an <c>isError</c> result with the fixed, user-facing message.</summary>
public sealed class ReadException(ReadFailure failure) : Exception(Messages.For(failure))
{
    public ReadFailure Failure { get; } = failure;
}

/// <summary>
/// Error texts of <c>WallflyMCPMain.swift</c>, verbatim. They never contain paths, file contents or keys.
/// </summary>
public static class Messages
{
    public const string Unavailable = "The meeting library is unavailable. Connect the drive or select a library in Wallfly.";
    public const string NotFound = "Meeting not found. It may have been deleted.";
    public const string Damaged = "The requested meeting is damaged or unreadable.";
    public const string StaleCursor = "The library or query changed. Restart without a cursor.";
    public const string InvalidArguments = "Invalid tool or arguments. Check IDs, dates, limits and time range.";
    public const string FolderNotFound = "Folder not found. Use a folder name or ID shown in Wallfly.";
    public const string Usage = "wallfly-mcp [--library <path>] — local read-only MCP over stdio";
    public const string StartFailed = "Wallfly MCP could not start. Use --help to check arguments.";

    public static string For(ReadFailure failure) => failure switch
    {
        ReadFailure.Unavailable => Unavailable,
        ReadFailure.NotFound => NotFound,
        ReadFailure.DamagedMeeting => Damaged,
        ReadFailure.StaleCursor => StaleCursor,
        ReadFailure.FolderNotFound => FolderNotFound,
        _ => InvalidArguments
    };

    public static bool IsKnown(string? text) =>
        text is Unavailable or NotFound or Damaged or StaleCursor or InvalidArguments or FolderNotFound;
}
