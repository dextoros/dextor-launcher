using System.Security.Cryptography;
using System.Text.Json;
using DexTor.Contracts;

namespace DexTor.Qemu;

public enum SystemImageLocation
{
    TemporaryCache,
    UsbFallback
}

public sealed record ResolvedSystemImage(string Path, SystemImageLocation Location);

public sealed class VmPrelaunchException : InvalidOperationException
{
    public VmPrelaunchException(string message) : base(message)
    {
    }
}

public static class VmPrelaunchGate
{
    public static ResolvedSystemImage ResolveSystemImage(SessionStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateSha256(request.ExpectedSystemImageSha256);

        if (HasExpectedHash(request.PreferredSystemImagePath, request.ExpectedSystemImageSha256))
        {
            return new ResolvedSystemImage(
                Path.GetFullPath(request.PreferredSystemImagePath),
                SystemImageLocation.TemporaryCache);
        }

        if (HasExpectedHash(request.UsbSystemImagePath, request.ExpectedSystemImageSha256))
        {
            return new ResolvedSystemImage(
                Path.GetFullPath(request.UsbSystemImagePath),
                SystemImageLocation.UsbFallback);
        }

        throw new VmPrelaunchException(
            "Neither the temporary system image nor the USB fallback matches the verified SHA-256.");
    }

    public static FileStream AcquireDataImageLease(string dataImagePath)
    {
        if (string.IsNullOrWhiteSpace(dataImagePath) || !File.Exists(dataImagePath))
        {
            throw new VmPrelaunchException("The persistent USB data image is missing.");
        }

        try
        {
            return new FileStream(
                Path.GetFullPath(dataImagePath),
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.RandomAccess);
        }
        catch (IOException exception)
        {
            throw new VmPrelaunchException(
                $"The persistent USB data image cannot be locked exclusively: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new VmPrelaunchException(
                $"The persistent USB data image cannot be opened for writing: {exception.Message}");
        }
    }

    public static string ValidateRuntime(string runtimeDirectory, string manifestPath)
    {
        if (!Directory.Exists(runtimeDirectory) || !File.Exists(manifestPath))
        {
            throw new VmPrelaunchException("The QEMU runtime or its manifest is missing.");
        }

        string[] requiredPaths;
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            requiredPaths = ReadRequiredRuntimePaths(manifest.RootElement);
        }
        catch (JsonException exception)
        {
            throw new VmPrelaunchException($"The QEMU runtime manifest is invalid: {exception.Message}");
        }

        if (requiredPaths.Length == 0)
        {
            throw new VmPrelaunchException("The QEMU runtime manifest does not list required files.");
        }

        foreach (var relativePath in requiredPaths)
        {
            var fullPath = ResolveContainedPath(runtimeDirectory, relativePath);
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                throw new VmPrelaunchException($"The QEMU runtime is incomplete: {relativePath} is missing.");
            }
        }

        var executablePath = Path.Combine(Path.GetFullPath(runtimeDirectory), "qemu-system-x86_64.exe");
        if (!File.Exists(executablePath))
        {
            throw new VmPrelaunchException("The QEMU executable is missing from the runtime bundle.");
        }

        return executablePath;
    }

    private static string[] ReadRequiredRuntimePaths(JsonElement root)
    {
        if (root.TryGetProperty("runtime", out var runtime)
            && runtime.TryGetProperty("files", out var legacyFiles)
            && legacyFiles.ValueKind == JsonValueKind.Array)
        {
            return legacyFiles.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray();
        }

        if (!root.TryGetProperty("files", out var payloadFiles)
            || payloadFiles.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return payloadFiles.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("path", out var path)
                && path.ValueKind == JsonValueKind.String
                    ? path.GetString() ?? string.Empty
                    : string.Empty)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
    }

    private static bool HasExpectedHash(string path, string expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateSha256(string sha256)
    {
        if (sha256.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new VmPrelaunchException("The expected system image SHA-256 is invalid.");
        }
    }

    private static string ResolveContainedPath(string runtimeDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new VmPrelaunchException("The QEMU runtime manifest contains an unsafe path.");
        }

        var root = Path.GetFullPath(runtimeDirectory);
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new VmPrelaunchException("The QEMU runtime manifest contains a path outside the runtime directory.");
        }

        return candidate;
    }

}
