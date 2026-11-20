using FluentAssertions;
using MoneyTracker.Services;
using Xunit;

namespace MoneyTracker.Tests.Unit;

public class ImportDuplicateMatcherTests
{
    [Theory]
    [InlineData("AMZN MKTP US*2K4LX1", "AMZN Mktp US*9P1QQ", true)]   // same merchant, different reference
    [InlineData("STARBUCKS #1234 SEATTLE", "Starbucks", true)]        // current payee contained in raw text
    [InlineData("Comcast", "COMCAST", true)]
    [InlineData("SQ *BLUE BOTTLE", "SQ *BLUE BOTTLE COFFEE", true)]
    [InlineData("POS DEBIT SHELL OIL 123", "Shell Oil", true)]        // noise words skipped
    [InlineData("Amazon", "AMZN MKTP US", false)]                     // a renamed payee only matches via the original text
    [InlineData("Chevron", "Shell", false)]
    [InlineData("ATM", "ATM", true)]
    [InlineData("SQ *CAFE", "SQ *BAKERY", false)]                     // generic prefix isn't enough
    [InlineData("", "Shell", false)]
    [InlineData(null, "Shell", false)]
    public void Payee_texts_that_name_the_same_merchant_match(string? a, string? b, bool expected) =>
        ImportDuplicateMatcher.PayeeMatches(a, b).Should().Be(expected);
}
