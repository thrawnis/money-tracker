using FluentAssertions;
using MoneyTracker.Services;
using Xunit;

namespace MoneyTracker.Tests.Unit;

public class PayeePatternMatcherTests
{
    [Theory]
    [InlineData("Amazon.com", false, "amazon.com", true)]
    [InlineData("Amazon.com", false, "Amazon.co", false)]
    [InlineData("Amazon*", false, "Amazon F98797", true)]
    [InlineData("Amazon*", false, "Not Amazon", false)]
    [InlineData("*Amazon*", false, "Payment to Amazon Prime", true)]
    [InlineData("*Amazon*", false, "Walmart", false)]
    [InlineData("AMZN*MKTP*", false, "AMZN MKTP US*2K3JF9", true)]
    [InlineData("AMZN*MKTP*", false, "AMZN Prime Video", false)]
    public void GlobAndExactMatching(string pattern, bool isRegex, string raw, bool expected)
    {
        PayeePatternMatcher.IsMatch(pattern, isRegex, raw).Should().Be(expected);
    }

    [Fact]
    public void RegexMode_MatchesArbitraryPattern()
    {
        PayeePatternMatcher.IsMatch(@"^AMZN\s?Mktp", true, "AMZN Mktp US*ABC123").Should().BeTrue();
        PayeePatternMatcher.IsMatch(@"^AMZN\s?Mktp", true, "Not Amazon at all").Should().BeFalse();
    }

    [Fact]
    public void InvalidRegex_DoesNotThrow_JustFailsToMatch()
    {
        PayeePatternMatcher.IsMatch("(unclosed", true, "anything").Should().BeFalse();
    }

    [Fact]
    public void EmptyInputs_NeverMatch()
    {
        PayeePatternMatcher.IsMatch("", false, "text").Should().BeFalse();
        PayeePatternMatcher.IsMatch("text", false, "").Should().BeFalse();
    }

    [Fact]
    public void FindMatch_ReturnsFirstRuleInOrder()
    {
        var rules = new[] { "*amazon*", "amazon f98797" };
        var found = PayeePatternMatcher.FindMatch(rules, "Amazon F98797", p => p, _ => false);
        found.Should().Be("*amazon*");
    }
}
