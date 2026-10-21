using System.Collections.Generic;

namespace DexTor.Release;

public sealed class ReleaseManifest
{
    public string Version { get; init; } = string.Empty;
    public string Channel { get; init; } = "beta";
    public IReadOnlyList<ReleaseSlot> Slots { get; init; } = System.Array.Empty<ReleaseSlot>();
    public IReadOnlyList<ReleaseArtifact> Artifacts { get; init; } = System.Array.Empty<ReleaseArtifact>();
}

public sealed class ReleaseSlot
{
    public string Name { get; init; } = "slot-a";
    public string ImagePath { get; init; } = string.Empty;
    public string Hash { get; init; } = string.Empty;
}

public sealed class ReleaseArtifact
{
    public string Url { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
}
