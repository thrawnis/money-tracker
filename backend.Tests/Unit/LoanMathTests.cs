using FluentAssertions;
using MoneyTracker.Models;
using MoneyTracker.Services;
using Xunit;

namespace MoneyTracker.Tests.Unit;

public class LoanMathTests
{
    [Theory]
    [InlineData(320_000, 6.25, 360, 1970.30)]   // 30-year mortgage
    [InlineData(200_000, 6.0, 360, 1199.10)]
    [InlineData(26_000, 4.9, 72, 417.52)]       // car loan
    [InlineData(12_000, 0, 24, 500.00)]          // 0% loan
    public void Payment_matches_standard_amortization(double principal, double rate, int months, double expected) =>
        LoanMath.Payment((decimal)principal, (decimal)rate, months).Should().Be((decimal)expected);

    [Fact]
    public void Monthly_interest_is_rate_over_twelve()
    {
        LoanMath.MonthlyInterest(200_000m, 6m).Should().Be(1000m);
        LoanMath.MonthlyInterest(-5m, 6m).Should().Be(0m);
    }

    [Fact]
    public void Projection_pays_off_on_schedule_and_extra_shortens_it()
    {
        var p = LoanMath.Project(200_000m, 6m, 1199.10m);
        p.Covers.Should().BeTrue();
        p.Months.Should().BeInRange(359, 361);
        p.TotalInterest.Should().BeApproximately(231_676m, 50m);
        var faster = LoanMath.Project(200_000m, 6m, 1299.10m);
        faster.Months.Should().BeLessThan(p.Months);
        faster.TotalInterest.Should().BeLessThan(p.TotalInterest);
    }

    [Fact]
    public void Payment_that_doesnt_cover_interest_never_pays_off() =>
        LoanMath.Project(200_000m, 6m, 900m).Covers.Should().BeFalse();

    [Fact]
    public void Payment_comes_from_details_when_not_entered()
    {
        LoanMath.PaymentFor(new LoanDetails { AnnualRate = 6m, OriginalAmount = 200_000m, TermMonths = 360 }).Should().Be(1199.10m);
        LoanMath.PaymentFor(new LoanDetails { AnnualRate = 6m, MonthlyPayment = 1500m }).Should().Be(1500m);
        LoanMath.PaymentFor(new LoanDetails { AnnualRate = 6m }).Should().BeNull();
    }
}
