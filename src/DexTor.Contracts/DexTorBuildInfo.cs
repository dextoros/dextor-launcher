using System.Reflection;

namespace DexTor.Contracts;

/// <summary>What kind of build this is, as the guest is told.</summary>
public static class DexTorBuildInfo
{
    public const string TestChannel = "test";
    public const string ReleaseChannel = "release";

    /// <summary>
    /// "release" only for a SignedRelease build; every other build is "test", so a build
    /// that forgot to say is never taken for a release.
    /// </summary>
    public static string Channel { get; } = ReadChannel();

    private static string ReadChannel()
    {
        var value = typeof(DexTorBuildInfo).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "DexTorChannel")?.Value;
        return string.Equals(value, ReleaseChannel, StringComparison.Ordinal) ? ReleaseChannel : TestChannel;
    }
}
