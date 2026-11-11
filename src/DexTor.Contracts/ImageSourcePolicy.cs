namespace DexTor.Contracts;

/// <summary>Where the launcher may fetch the DexTor OS image, its .sha256 and its .sig from.</summary>
/// <remarks>
/// The image's address comes from lastversion.link on the server, and the code used to follow
/// any http or https address written there. The privacy policy and the Store listing say the
/// launcher downloads the image from dextoros.com only, so every build - Store, MSI and
/// portable alike - refuses any other address before a byte is requested, and follows no
/// redirect, which could lead elsewhere. The signature check still decides whether a
/// downloaded image is genuine; this decides where it may come from.
/// </remarks>
public static class ImageSourcePolicy
{
    public const string TrustedHost = "dextoros.com";

    /// <summary>https://dextoros.com on its default port, without credentials.</summary>
    public static bool IsTrusted(Uri source) =>
        source.IsAbsoluteUri
        && source.Scheme == Uri.UriSchemeHttps
        && source.IsDefaultPort
        && string.IsNullOrEmpty(source.UserInfo)
        && string.Equals(source.IdnHost, TrustedHost, StringComparison.OrdinalIgnoreCase);
}
