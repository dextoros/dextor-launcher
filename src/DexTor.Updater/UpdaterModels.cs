namespace DexTor.Updater;

public enum UpdatePolicy
{
    AutoInstallOnDemand,
    Optional,
    Required,
    Deferred,
}

public sealed record UpdateArtifact(string PackagePath, string Version, string Sha256, string Channel = "beta");

public sealed record UpdatePlan(string SourceVersion, string InstalledVersion, UpdatePolicy Policy);
