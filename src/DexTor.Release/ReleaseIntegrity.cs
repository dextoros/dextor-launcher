using System.Security.Cryptography;
using System.Text;
using System.Globalization;

namespace DexTor.Release;

public static class ReleaseIntegrity
{
    public static string ComputeSha256(byte[] data)
    {
        var hash = SHA256.HashData(data);
        var sb = new StringBuilder(hash.Length * 2);

        foreach (var b in hash)
        {
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:x2}", b);
        }

        return sb.ToString();
    }

    public static bool VerifySha256(byte[] data, string expectedHexDigest)
    {
        if (string.IsNullOrWhiteSpace(expectedHexDigest))
        {
            return false;
        }

        var actual = ComputeSha256(data);
        return string.Equals(actual, expectedHexDigest, StringComparison.OrdinalIgnoreCase);
    }
}
