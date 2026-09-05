using EcencyApi.Infrastructure;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// The account name grammar behind every desk path: the chain's
/// <c>is_valid_account_name</c>, label by label.
/// </summary>
public class HiveNamesTests
{
    [Theory]
    [InlineData("abc")]
    [InlineData("a-b")]
    [InlineData("a1b")]
    [InlineData("good-karma")]
    [InlineData("user.name")]
    [InlineData("abc1.d-2e.fgh")]
    [InlineData("a--b")]
    [InlineData("abcdefghijklmnop")]
    public void EveryLabelOfAValidNameStartsWithALetterAndEndsWithALetterOrDigit(string name)
    {
        Assert.True(HiveNames.IsAccountName(name));
    }

    [Theory]
    // Length bounds.
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("abcdefghijklmnopq")]
    // Alphabet.
    [InlineData("Abc")]
    [InlineData("a_b")]
    [InlineData("abc\n")]
    [InlineData("ab c")]
    // Label edges.
    [InlineData("-ab")]
    [InlineData("1ab")]
    [InlineData("abc-")]
    [InlineData("abc.-de")]
    [InlineData("abc.def-")]
    [InlineData("abc.1de")]
    // Label length.
    [InlineData("ab.cdef")]
    [InlineData("abcd.ef")]
    [InlineData("a..b")]
    [InlineData("...")]
    // Empty first or last label.
    [InlineData(".abc")]
    [InlineData("abc.")]
    [InlineData("abc..def")]
    public void AnythingElseIsNotAName(string name)
    {
        Assert.False(HiveNames.IsAccountName(name));
    }

    [Fact]
    public void ANullNameIsNotAName()
    {
        Assert.False(HiveNames.IsAccountName(null));
    }
}
