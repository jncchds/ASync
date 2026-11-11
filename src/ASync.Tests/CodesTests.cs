using System.Text.RegularExpressions;
using ASync.Core.Security;

namespace ASync.Tests;

public class CodesTests
{
    [Fact]
    public void NewCode_HasGroupsOfFourWithoutLookAlikes()
    {
        var code = Codes.NewCode();
        Assert.Matches(new Regex("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$"), code);
        Assert.DoesNotMatch(new Regex("[01OILU]"), code);
        Assert.Matches(new Regex("^[A-Z2-9]{4}-[A-Z2-9]{4}$"), Codes.NewCode(groups: 2));
    }

    [Theory]
    [InlineData("k7qd-m2xp-9rwa")]
    [InlineData("K7QD M2XP 9RWA")]
    [InlineData(" k7qdm2xp9rwa ")]
    public void HashCode_IgnoresCaseSpacesAndDashes(string typed) =>
        Assert.Equal(Codes.HashCode("K7QD-M2XP-9RWA"), Codes.HashCode(typed));

    [Fact]
    public void HashSecret_IsExact()
    {
        Assert.NotEqual(Codes.HashSecret("abc-DEF"), Codes.HashSecret("ABCDEF"));
        Assert.Equal(Codes.HashSecret("abc-DEF"), Codes.HashSecret("abc-DEF"));
    }

    [Fact]
    public void NewSecret_IsUrlSafeAndUnique()
    {
        var a = Codes.NewSecret();
        Assert.Matches(new Regex("^[A-Za-z0-9_-]{43}$"), a);
        Assert.NotEqual(a, Codes.NewSecret());
    }
}
