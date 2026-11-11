using DexTor.Contracts;

using FluentAssertions;

using Xunit;

namespace DexTor.Contracts.Tests;

public sealed class ImageSourcePolicyTests
{
    [Theory]
    [InlineData("https://dextoros.com/public/1.1.49/DextorOS_1_1_49.iso")]
    [InlineData("https://DEXTOROS.COM/public/1.1.49/DextorOS_1_1_49.iso.sig")]
    [InlineData("https://dextoros.com:443/public/lastversion.link")]
    public void HttpsOnDextorosIsTrusted(string url) =>
        ImageSourcePolicy.IsTrusted(new Uri(url)).Should().BeTrue();

    [Theory]
    [InlineData("http://dextoros.com/public/1.1.49/DextorOS_1_1_49.iso")]
    [InlineData("https://dextoros.com:8443/public/1.1.49/DextorOS_1_1_49.iso")]
    [InlineData("https://www.dextoros.com/public/1.1.49/DextorOS_1_1_49.iso")]
    [InlineData("https://cdn.dextoros.com/DextorOS_1_1_49.iso")]
    [InlineData("https://dextoros.com.example.com/DextorOS_1_1_49.iso")]
    [InlineData("https://dextoros.com@example.com/DextorOS_1_1_49.iso")]
    [InlineData("https://user@dextoros.com/DextorOS_1_1_49.iso")]
    [InlineData("https://github.com/dextoros/releases/DextorOS_1_1_49.iso")]
    public void AnythingElseIsNot(string url) =>
        ImageSourcePolicy.IsTrusted(new Uri(url)).Should().BeFalse();
}
