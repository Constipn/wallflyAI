using System.Collections.Concurrent;
using System.Security.Cryptography;
using Wallfly.Speakers;

namespace Wallfly.Diarization;

/// <summary>One model file below the diarization model directory, pinned like in <c>tools/models.lock.json</c>.</summary>
public sealed record DiarizationModelFile(string RelativePath, long Size, string Sha256, string License, string Source);

/// <summary>Absolute paths of a verified model set.</summary>
public sealed record DiarizationModels(string Directory, string SegmentationModel, string EmbeddingModel);

/// <summary>
/// Finds the diarization models (<c>windows\models\diarization\</c> in a checkout, <c>models\diarization\</c> next to an
/// installed app) and verifies size and SHA-256 before sherpa-onnx loads them: a corrupt model must not reach the
/// native code. Hashes are checked once per file version and process.
/// </summary>
public static class ModelLocator
{
    /// <summary>Overrides the models root (the directory that contains <c>diarization\</c>).</summary>
    public const string EnvironmentVariable = "WALLFLY_MODELS_DIR";
    /// <summary>Section name in <c>tools/models.lock.json</c> and sub-directory of the models root.</summary>
    public const string Section = "diarization";

    /// <summary>pyannote segmentation-3.0 as ONNX (sherpa-onnx release <c>speaker-segmentation-models</c>), MIT.</summary>
    public static DiarizationModelFile Segmentation { get; } = new("sherpa-onnx-pyannote-segmentation-3-0/model.onnx", 5_992_913,
        "220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079", "MIT", "pyannote/segmentation-3.0 (CNRS), ONNX export by k2-fsa/sherpa-onnx");

    /// <summary>
    /// 3D-Speaker ERes2Net trained on VoxCeleb (sherpa-onnx release <c>speaker-recongition-models</c>), Apache-2.0. Chosen over
    /// WeSpeaker ResNet34-LM: same speed, but it separated 3 of 3 synthetic voice pairs at threshold 0.6 (ResNet34-LM: 1 of 3).
    /// </summary>
    public static DiarizationModelFile Embedding { get; } = new("3dspeaker_speech_eres2net_sv_en_voxceleb_16k.onnx", 26_485_263,
        "c59158379255ad66e161679cca6af8d52d51e389e3224ab7d7a7baae295c2db5", "Apache-2.0",
        "3D-Speaker ERes2Net (iic/speech_eres2net_sv_en_voxceleb_16k), ONNX by k2-fsa/sherpa-onnx");

    private static readonly ConcurrentDictionary<(string Path, long Length, DateTime Written), bool> Verified = new();

    /// <summary>Directories searched in this order (each one is a <c>…\diarization</c> directory).</summary>
    public static List<string> CandidateDirectories(string? baseDirectory = null)
    {
        var result = new List<string>();
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } root) result.Add(Path.Combine(root, Section));
        var start = baseDirectory ?? AppContext.BaseDirectory;
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            result.Add(Path.Combine(directory.FullName, "models", Section));
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (local.Length > 0) result.Add(Path.Combine(local, "Wallfly", "models", Section));
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The first candidate directory containing both models with the pinned sizes, or null with the reason.</summary>
    public static DiarizationModels? TryLocate(out string reason, string? directory = null, bool verifyHashes = true)
    {
        var candidates = directory is null ? CandidateDirectories() : [directory];
        foreach (var candidate in candidates)
        {
            var segmentation = Path.Combine(candidate, Segmentation.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var embedding = Path.Combine(candidate, Embedding.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(segmentation) || !File.Exists(embedding)) continue;
            if (Check(segmentation, Segmentation, verifyHashes) is { } segmentationError) { reason = segmentationError; return null; }
            if (Check(embedding, Embedding, verifyHashes) is { } embeddingError) { reason = embeddingError; return null; }
            reason = "";
            return new DiarizationModels(candidate, segmentation, embedding);
        }
        reason = directory is null
            ? $"models not found (run tools\\fetch-models.ps1 -Section {Section}; searched {string.Join("; ", candidates)})"
            : $"models not found in {directory}";
        return null;
    }

    /// <summary>Like <see cref="TryLocate"/> but throws <see cref="DiarizationException"/> (ModelsUnavailable).</summary>
    public static DiarizationModels Locate(string? directory = null, bool verifyHashes = true) =>
        TryLocate(out var reason, directory, verifyHashes) ?? throw DiarizationException.ModelsUnavailable(reason);

    private static string? Check(string path, DiarizationModelFile expected, bool verifyHash)
    {
        var info = new FileInfo(path);
        if (info.Length != expected.Size) return $"{expected.RelativePath} has {info.Length} bytes instead of {expected.Size}";
        if (!verifyHash) return null;
        var key = (info.FullName, info.Length, info.LastWriteTimeUtc);
        if (Verified.ContainsKey(key)) return null;
        string actual;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (IOException error) { return $"{expected.RelativePath} unreadable: {error.Message}"; }
        catch (UnauthorizedAccessException error) { return $"{expected.RelativePath} unreadable: {error.Message}"; }
        if (!string.Equals(actual, expected.Sha256, StringComparison.OrdinalIgnoreCase)) return $"{expected.RelativePath} has an unexpected SHA-256 ({actual})";
        Verified[key] = true;
        return null;
    }
}
