using FluentAssertions;
using MoneyTracker.Services;
using Xunit;

namespace MoneyTracker.Tests.Unit;

public class ReceiptMemoTests
{
    [Theory]
    [InlineData("Nopa - Burger and fries", "Nopa", "Burger and fries")]
    [InlineData("NOPA: Pork chop", "Nopa", "Pork chop")]
    [InlineData("Nopa – Pasta", "Nopa", "Pasta")]
    [InlineData("Nopa's burger", "Nopa", "burger")]
    [InlineData("Blue Bottle Coffee latte", "Blue Bottle Coffee", "latte")]
    [InlineData("Burger and fries", "Nopa", "Burger and fries")]       // no prefix: unchanged
    [InlineData("Nopales salad", "Nopa", "Nopales salad")]             // different word: unchanged
    [InlineData("Nopa", "Nopa", "Nopa")]                               // nothing left: unchanged
    public void Strips_the_merchant_name_from_the_front_of_the_memo(string memo, string payee, string expected) =>
        ReceiptExtractionHelper.StripPayeePrefix(memo, payee).Should().Be(expected);

    [Fact]
    public void Applied_when_parsing_the_model_response()
    {
        var dto = ReceiptExtractionHelper.ParseResponse(
            """{"date":"2026-10-20","payee":"Nopa","amount":-48.5,"memo":"Nopa - Burger and fries","suggestedCategory":"Dining"}""");
        dto.Memo.Should().Be("Burger and fries");
        dto.Payee.Should().Be("Nopa");
    }
}
