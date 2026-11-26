namespace Wallfly.Core.Credentials;

/// <summary>The key store seam: the real Credential Manager in the app, a memory stub in tests and smoke mode.</summary>
public interface ICredentialStore
{
    string Read(CredentialAccount account);
    void Write(CredentialAccount account, string? secret);
}

/// <summary>Keeps keys for the process only; never touches the user's Windows credentials.</summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<CredentialAccount, string> values = [];
    public string Read(CredentialAccount account) => values.TryGetValue(account, out var value) ? value : "";
    public void Write(CredentialAccount account, string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) values.Remove(account);
        else values[account] = secret.Trim();
    }
}
