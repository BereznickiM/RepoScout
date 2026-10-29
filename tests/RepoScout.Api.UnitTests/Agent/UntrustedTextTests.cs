using RepoScout.Api.Agent;

namespace RepoScout.Api.UnitTests.Agent;

public class UntrustedTextTests
{
    [Theory]
    [InlineData("a​b")]
    [InlineData("a‌b")]
    [InlineData("a‍b")]
    [InlineData("a⁠b")]
    [InlineData("a﻿b")]
    [InlineData("a\U000E0041b")]
    [InlineData("a\U000E0000b")]
    [InlineData("a\U000E007Fb")]
    public void Clean_InvisibleCharacter_IsRemoved(string input)
    {
        Assert.Equal("ab", UntrustedText.Clean(input));
    }

    [Theory]
    [InlineData("plain text\nwith <b>html</b> and \"quotes\"")]
    [InlineData("zażółć gęślą jaźń \U0001F600 日本語")]
    [InlineData("rtl ‮ mark ‎ and ‏")]
    [InlineData("tab\tand\r\nnewline")]
    [InlineData("")]
    public void Clean_VisibleOrBidiText_IsUnchanged(string input)
    {
        Assert.Equal(input, UntrustedText.Clean(input));
    }

    [Fact]
    public void Clean_CharactersJustOutsideRemovedRanges_AreKept()
    {
        var input = " ‎ ﻾\U000E0080";

        Assert.Equal(input, UntrustedText.Clean(input));
    }
}
