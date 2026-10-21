using System.Security.Cryptography;
using System.Text;

namespace DexTor.Release;

/// <summary>
/// The key DexTor OS images are signed with, and the check of an image's signature.
/// </summary>
/// <remarks>
/// The image is signed as <c>openssl dgst -sha256 -sign release.key.pem -out image.iso.sig
/// image.iso</c>: ECDSA P-256 over the image's SHA-256, published next to the image. Only the
/// public half is in the launcher. The signature is checked against the SHA-256 the launcher
/// computed itself, so the published <c>.sha256</c> file is a convenience, not the trust anchor:
/// an image whose bytes differ from what the release key signed is never used.
/// </remarks>
public sealed class ImageSigningKey
{
    /// <summary>The DexTor OS release key; its private half stays with the OS release team.</summary>
    public static ImageSigningKey Release { get; } = FromPem("DexTor OS Release", """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE3ziSE8TwoZKJQKiPQGSwOSTmT9cx
        arFo3v2jEuVKL8N//vWVs6MjD+NViwST7/CZAo1On6iWIyPlSxP3YVta1w==
        -----END PUBLIC KEY-----
        """);

    /// <summary>Larger than any P-256 signature in any encoding; a bigger file is not one.</summary>
    public const int MaxSignatureBytes = 512;

    private readonly byte[] _subjectPublicKeyInfo;

    private ImageSigningKey(string name, byte[] subjectPublicKeyInfo)
    {
        Name = name;
        _subjectPublicKeyInfo = subjectPublicKeyInfo;
        Fingerprint = FingerprintOf(subjectPublicKeyInfo);
    }

    /// <summary>What the key is called on screen, "DexTor OS Release".</summary>
    public string Name { get; }

    /// <summary>
    /// The first 20 bytes of the SHA-256 of the public key, in groups of four:
    /// "E246 B0C5 33BA 5EE8 ...". It is what the details on step 3 show.
    /// </summary>
    public string Fingerprint { get; }

    public static ImageSigningKey FromPem(string name, string publicKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        if (key.KeySize != 256)
        {
            throw new ArgumentException("The image signing key must be ECDSA P-256.", nameof(publicKeyPem));
        }

        return new ImageSigningKey(name, key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>
    /// Whether <paramref name="signature"/> is the release key's signature of an image whose
    /// SHA-256 is <paramref name="sha256Digest"/>.
    /// </summary>
    /// <remarks>
    /// openssl writes the DER sequence; the raw 64-byte form and either of them in base64 text
    /// are taken too, since a signature uploaded through a web form can come back as text.
    /// </remarks>
    public bool Verify(ReadOnlySpan<byte> sha256Digest, ReadOnlySpan<byte> signature)
    {
        if (sha256Digest.Length != SHA256.HashSizeInBytes || signature.IsEmpty || signature.Length > MaxSignatureBytes)
        {
            return false;
        }

        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(_subjectPublicKeyInfo, out _);
        foreach (var candidate in Decodings(signature.ToArray()))
        {
            var format = candidate.Length == 64
                ? DSASignatureFormat.IeeeP1363FixedFieldConcatenation
                : DSASignatureFormat.Rfc3279DerSequence;
            try
            {
                if (key.VerifyHash(sha256Digest, candidate, format)) return true;
            }
            catch (CryptographicException)
            {
                // Not a signature in that encoding; the next one may be.
            }
        }

        return false;
    }

    /// <summary>The same as <see cref="Verify(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/> for a hex digest.</summary>
    public bool Verify(string sha256Hex, ReadOnlySpan<byte> signature)
    {
        if (sha256Hex is not { Length: 64 } || !sha256Hex.All(Uri.IsHexDigit)) return false;
        return Verify(Convert.FromHexString(sha256Hex), signature);
    }

    private static IEnumerable<byte[]> Decodings(byte[] raw)
    {
        yield return raw;
        var text = Encoding.ASCII.GetString(raw).Trim();
        if (text.Length > 0 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=' or '\r' or '\n'))
        {
            byte[]? decoded = null;
            try
            {
                decoded = Convert.FromBase64String(text);
            }
            catch (FormatException)
            {
            }

            if (decoded is { Length: > 0 }) yield return decoded;
        }
    }

    private static string FingerprintOf(byte[] subjectPublicKeyInfo)
    {
        var hex = Convert.ToHexString(SHA256.HashData(subjectPublicKeyInfo), 0, 20);
        return string.Join(' ', Enumerable.Range(0, hex.Length / 4).Select(i => hex.Substring(i * 4, 4)));
    }
}
