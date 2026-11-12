using SDMate.Core;

namespace SDMate.Core.Tests;

public class UserSearchInputClassifierTests
{
    [Theory]
    [InlineData("Koen van Vliet", "Koen van Vliet")]
    [InlineData(" Anne-Marie O'Neil ", "Anne-Marie O'Neil")]
    [InlineData("Ana María de la Cruz", "Ana María de la Cruz")]
    [InlineData("Çağrı Şahin", "Çağrı Şahin")]
    [InlineData("Jürgen Müller", "Jürgen Müller")]
    [InlineData("Jiří Černý", "Jiří Černý")]
    [InlineData("Łukasz Żółkiewski", "Łukasz Żółkiewski")]
    public void TryClassify_Name_ReturnsTrimmedFullName(string input, string expected)
    {
        var result = UserSearchInputClassifier.TryClassify(input, out var classified);

        Assert.True(result);
        Assert.Equal(UserSearchInputKind.FullName, classified.Kind);
        Assert.Equal(expected, classified.Value);
    }

    [Theory]
    [InlineData("koen.van.vliet@walraven.com")]
    [InlineData("KV-Admin@example.org")]
    public void TryClassify_Email_ReturnsEmailAddress(string input)
    {
        var result = UserSearchInputClassifier.TryClassify(input, out var classified);

        Assert.True(result);
        Assert.Equal(UserSearchInputKind.EmailAddress, classified.Kind);
        Assert.Equal(input, classified.Value);
    }

    [Theory]
    [InlineData("kv-admin")]
    [InlineData("kvanvliet")]
    public void TryClassify_SingleToken_ReturnsSamAccountName(string input)
    {
        var result = UserSearchInputClassifier.TryClassify(input, out var classified);

        Assert.True(result);
        Assert.Equal(UserSearchInputKind.SamAccountName, classified.Kind);
        Assert.Equal(input, classified.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("person@invalid")]
    [InlineData("First 123")]
    public void TryClassify_InvalidInput_ReturnsFalse(string? input)
    {
        var result = UserSearchInputClassifier.TryClassify(input, out _);

        Assert.False(result);
    }
}