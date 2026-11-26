using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wallfly.Teams;

/// <summary><c>configuration.json</c>: written by the app, read by the helper on every tick.</summary>
public sealed class DetectionConfiguration
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    /// <summary>
    /// Read Teams' UI only while a Teams process holds a microphone capture session (or a detected call is
    /// still open). Off by default until the Teams test call (M6) confirms which process owns the session.
    /// </summary>
    [JsonPropertyName("micPrefilter")] public bool MicPrefilter { get; set; }
}

/// <summary><c>heartbeat.json</c>: the app writes it every 2 s.</summary>
public sealed class DetectionHeartbeat
{
    public const double FreshSeconds = 8;

    [JsonPropertyName("date")] public DateTimeOffset Date { get; set; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("busy")] public bool Busy { get; set; }
    [JsonPropertyName("presenting")] public bool Presenting { get; set; }

    /// <summary>A fresh (&lt; 8 s) heartbeat of a busy or presenting app suppresses the popup; a stale one never does.</summary>
    public bool SuppressesPopup(DateTimeOffset now)
    {
        var age = (now - Date).TotalSeconds;
        return age >= 0 && age < FreshSeconds && (Busy || Presenting);
    }
}

/// <summary><c>status.json</c>: the helper writes it on every tick.</summary>
public sealed class DetectionStatus
{
    [JsonPropertyName("date")] public DateTimeOffset Date { get; set; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("message")] public string Message { get; set; } = "";

    /// <summary>macOS field (Accessibility permission). Windows needs no permission; always true.</summary>
    [JsonPropertyName("accessibilityTrusted")] public bool AccessibilityTrusted { get; set; } = true;

    [JsonPropertyName("helperVersion"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? HelperVersion { get; set; }

    /// <summary>active, absent, unknown, notRunning, unavailable.</summary>
    [JsonPropertyName("state"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? State { get; set; }

    [JsonPropertyName("pid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Pid { get; set; }

    public bool IsFresh(DateTimeOffset now) => (now - Date).TotalSeconds is >= 0 and < DetectionHeartbeat.FreshSeconds;
}

/// <summary><c>request.json</c>: written by the helper when the user clicks "Take notes".</summary>
public sealed class DetectionRequest
{
    public const double ValiditySeconds = 120;
    public const string ActivationHost = "detected-teams";
    public const string UrlScheme = "wallfly";

    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DetectionRequest() { }

    public DetectionRequest(Guid id, DateTimeOffset? createdAt = null)
    {
        Id = id;
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
    }

    /// <summary>Pipe message: <c>detected-teams/&lt;UUID&gt;</c>.</summary>
    public string ActivationMessage => $"{ActivationHost}/{Id.ToString("D").ToUpperInvariant()}";

    /// <summary>Secondary URL form: <c>wallfly://detected-teams/&lt;UUID&gt;</c>.</summary>
    public string Url => $"{UrlScheme}://{ActivationMessage}";

    /// <summary>
    /// Accepts exactly <c>detected-teams/&lt;UUID&gt;</c> or <c>wallfly://detected-teams/&lt;UUID&gt;</c>; anything with
    /// another scheme or host, extra path segments, a query or a fragment is rejected.
    /// </summary>
    public static Guid? ParseActivation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        var prefix = UrlScheme + "://";
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) text = text[prefix.Length..];
        else if (text.Contains("://", StringComparison.Ordinal)) return null;
        if (text.IndexOfAny(['?', '#']) >= 0) return null;
        var parts = text.Split('/');
        if (parts.Length != 2 || !parts[0].Equals(ActivationHost, StringComparison.OrdinalIgnoreCase)) return null;
        return Guid.TryParseExact(parts[1], "D", out var id) ? id : null;
    }
}

/// <summary>
/// The helper's file protocol in <c>%LOCALAPPDATA%\Wallfly\Detection\</c> (a subfolder, so the meeting loader
/// never sees these JSON files). Writes are atomic (temporary file, then a POSIX-semantics rename, see
/// <see cref="AtomicFile"/>); reads share read/write/delete so the other process can replace a file being read.
/// The folder inherits the per-user ACL of %LOCALAPPDATA% (Windows equivalent of the macOS 0600/0700 permissions).
/// </summary>
public sealed class DetectionStorage(string? root = null)
{
    public const string ConfigurationFile = "configuration.json";
    public const string HeartbeatFile = "heartbeat.json";
    public const string StatusFile = "status.json";
    public const string SessionFile = "session.json";
    public const string RequestFile = "request.json";
    public const string ConsumedFile = "consumed.json";
    public const string DiagnosticRequestFile = "diagnostic-request.json";
    public const string DiagnosticReportFile = "diagnostic-report.json";
    public const int ConsumedLimit = 64;

    public string Root { get; } = root ?? WallflyPaths.DetectionRoot;

    public string PathOf(string name) => Path.Combine(Root, name);

    public T? Read<T>(string name) where T : class
    {
        try
        {
            using var stream = new FileStream(PathOf(name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(stream, DetectionJson.Options);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    public void Write<T>(T value, string name)
    {
        Directory.CreateDirectory(Root);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, DetectionJson.Options);
        var path = PathOf(name);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            AtomicFile.Replace(temporary, path);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
        }
    }

    public void Remove(string name)
    {
        var path = PathOf(name);
        if (File.Exists(path)) File.Delete(path);
    }

    public bool Exists(string name) => File.Exists(PathOf(name));

    public DetectionConfiguration ReadConfiguration() => Read<DetectionConfiguration>(ConfigurationFile) ?? new DetectionConfiguration();

    public void WriteConfiguration(DetectionConfiguration configuration) => Write(configuration, ConfigurationFile);

    public void WriteHeartbeat(bool busy, bool presenting, DateTimeOffset? now = null) =>
        Write(new DetectionHeartbeat { Date = now ?? DateTimeOffset.UtcNow, Busy = busy, Presenting = presenting }, HeartbeatFile);

    /// <summary>Takes (and deletes) a pending diagnostic request; stale or future requests are dropped.</summary>
    public DetectionDiagnosticRequest? TakeDiagnosticRequest(DateTimeOffset now)
    {
        var request = Read<DetectionDiagnosticRequest>(DiagnosticRequestFile);
        Remove(DiagnosticRequestFile);
        return request is not null && request.IsFresh(now) ? request : null;
    }

    /// <summary>
    /// Only a recent request written by the local helper can start a recording: the activation must name the
    /// ID in <c>request.json</c>, the request must be 0–120 s old, and each ID is consumed once (the last 64
    /// consumed IDs are kept). Returns the ID when the activation may start a note.
    /// </summary>
    public Guid? Consume(string activation, DateTimeOffset now) =>
        DetectionRequest.ParseActivation(activation) is { } id ? Consume(id, now) : null;

    public Guid? Consume(Guid id, DateTimeOffset now)
    {
        var request = Read<DetectionRequest>(RequestFile);
        if (request is null || request.Id != id) return null;
        var age = (now - request.CreatedAt).TotalSeconds;
        if (age < 0 || age >= DetectionRequest.ValiditySeconds) return null;
        var consumed = Read<List<Guid>>(ConsumedFile) ?? [];
        if (consumed.Contains(id)) return null;
        consumed.Add(id);
        Write(consumed.Skip(Math.Max(0, consumed.Count - ConsumedLimit)).ToList(), ConsumedFile);
        Remove(RequestFile);
        return id;
    }
}
