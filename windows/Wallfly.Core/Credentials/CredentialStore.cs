using System.Runtime.InteropServices;
using System.Text;

namespace Wallfly.Core.Credentials;

/// <summary>The three key slots of spec 8, mapped to Credential Manager targets.</summary>
public enum CredentialAccount
{
    /// <summary>OpenAI and the custom API.</summary>
    Enhancement,
    AnthropicEnhancement,
    DeepgramTranscription
}

/// <summary>
/// API keys in the Windows Credential Manager (generic credentials, UTF-16 blob), the counterpart of
/// the macOS keychain service <c>local.meetingnotes.api</c>. Environment variables are read-only
/// fallbacks; a <c>.env</c> file is deliberately not supported on Windows.
/// </summary>
public sealed partial class CredentialStore : ICredentialStore
{
    public const string TargetPrefix = "Wallfly/";
    private const uint TypeGeneric = 1;
    private const uint PersistLocalMachine = 2;
    private const int MaximumBlobBytes = 2560;

    public static string Target(CredentialAccount account) => TargetPrefix + account switch
    {
        CredentialAccount.AnthropicEnhancement => "anthropic-enhancement",
        CredentialAccount.DeepgramTranscription => "deepgram-transcription",
        _ => "enhancement"
    };

    /// <summary>Checked in order; the first non-empty value wins.</summary>
    public static string[] EnvironmentVariables(CredentialAccount account) => account switch
    {
        CredentialAccount.AnthropicEnhancement => ["ANTHROPIC_API_KEY"],
        CredentialAccount.DeepgramTranscription => ["DEEPGRAM_API_KEY", "DEEPGRAM_KEY"],
        _ => ["OPENAI_API_KEY", "OPENAI_KEY"]
    };

    /// <summary>The stored key, else the environment fallback, else an empty string.</summary>
    public string Read(CredentialAccount account)
    {
        var stored = ReadTarget(Target(account));
        if (!string.IsNullOrWhiteSpace(stored)) return stored.Trim();
        foreach (var variable in EnvironmentVariables(account))
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return "";
    }

    /// <summary>Stores or, for an empty value, removes the key. The environment fallback is untouched.</summary>
    public void Write(CredentialAccount account, string? secret) => WriteTarget(Target(account), secret);

    public string? ReadTarget(string target)
    {
        if (!CredRead(target, TypeGeneric, 0, out var handle)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<CredentialW>(handle);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return "";
            var bytes = new byte[Math.Min(credential.CredentialBlobSize, MaximumBlobBytes)];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try { return Encoding.Unicode.GetString(bytes); }
            finally { Array.Clear(bytes); }
        }
        finally { CredFree(handle); }
    }

    public void WriteTarget(string target, string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) { DeleteTarget(target); return; }
        var bytes = Encoding.Unicode.GetBytes(secret.Trim());
        if (bytes.Length > MaximumBlobBytes) throw new InvalidOperationException("The API key is too long for the Windows Credential Manager.");
        var targetName = IntPtr.Zero;
        var userName = IntPtr.Zero;
        var blob = IntPtr.Zero;
        try
        {
            targetName = Marshal.StringToHGlobalUni(target);
            userName = Marshal.StringToHGlobalUni(target);
            blob = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new CredentialW
            {
                Type = TypeGeneric,
                TargetName = targetName,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = userName
            };
            if (!CredWrite(in credential, 0))
                throw new InvalidOperationException($"The Windows Credential Manager rejected the key (error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            Array.Clear(bytes);
            if (blob != IntPtr.Zero) { Marshal.Copy(new byte[Math.Max(1, bytes.Length)], 0, blob, bytes.Length); Marshal.FreeHGlobal(blob); }
            if (targetName != IntPtr.Zero) Marshal.FreeHGlobal(targetName);
            if (userName != IntPtr.Zero) Marshal.FreeHGlobal(userName);
        }
    }

    /// <summary>Removes the credential; a missing entry is not an error.</summary>
    public void DeleteTarget(string target)
    {
        if (CredDelete(target, TypeGeneric, 0)) return;
        var error = Marshal.GetLastWin32Error();
        if (error is 1168 or 0) return; // ERROR_NOT_FOUND
        throw new InvalidOperationException($"The Windows Credential Manager could not remove the key (error {error}).");
    }

    /// <summary>
    /// One-time import of the DPAPI key file of the first Windows port. Runs only when the Credential
    /// Manager has no enhancement key yet; the legacy file is left on disk.
    /// </summary>
    public bool MigrateLegacyDpapi(string legacyRoot)
    {
        if (!string.IsNullOrWhiteSpace(ReadTarget(Target(CredentialAccount.Enhancement)))) return false;
        var legacy = LegacyDpapiCredential.Load(legacyRoot);
        if (string.IsNullOrWhiteSpace(legacy)) return false;
        Write(CredentialAccount.Enhancement, legacy);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CredentialW
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(in CredentialW credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(IntPtr buffer);
}
