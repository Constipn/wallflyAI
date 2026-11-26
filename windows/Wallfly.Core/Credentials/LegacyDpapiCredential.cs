using System.Security.Cryptography;
using System.Text;

namespace Wallfly.Core.Credentials;

/// <summary>
/// The DPAPI key file of the first Windows port (<c>&lt;root&gt;\Credentials\openai.dpapi</c>). Read-only:
/// it exists so an existing installation keeps its key after the move to the Credential Manager.
/// </summary>
public static class LegacyDpapiCredential
{
    public static string FilePath(string root) => Path.Combine(root, "Credentials", "openai.dpapi");

    public static string Load(string root)
    {
        var path = FilePath(root);
        if (!File.Exists(path)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser)); }
        catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException) { return ""; }
    }

    /// <summary>Only used by the migration test; the app never writes this file again.</summary>
    public static void Save(string root, string key)
    {
        var path = FilePath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Encoding.UTF8.GetBytes(key.Trim());
        try { File.WriteAllBytes(path, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
