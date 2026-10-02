using FluentAssertions;
using MoneyTracker.Services;
using Xunit;

namespace MoneyTracker.Tests.Unit;

public class BalanceHistoryTests
{
    private static readonly DateOnly D0 = new(2026, 1, 1);
    private static DateOnly D(int days) => D0.AddDays(days);

    private static BalanceHistory.AccountInput Tx(int id, decimal opening, DateOnly created, params (int Day, decimal Amount)[] tx) =>
        new(id, $"A{id}", "Checking", opening, created, tx.Select(t => (D(t.Day), t.Amount)).ToList(), null);

    private static BalanceHistory.AccountInput Reported(int id, params (int Day, decimal Value)[] v) =>
        new(id, $"R{id}", "Investment", 0, D(0), [], v.Select(x => (D(x.Day), x.Value)).ToList());

    [Fact]
    public void Transaction_account_accumulates_from_opening_balance()
    {
        var r = BalanceHistory.Build([Tx(1, 100, D(10), (2, 50), (4, -30))], D(0), D(5));
        r.Interval.Should().Be(BalanceHistory.Interval.Day);
        r.Accounts[0].Values.Should().Equal(null, null, 150m, 150m, 120m, 120m);
    }

    [Fact]
    public void Account_with_no_transactions_has_its_opening_balance_from_creation()
    {
        var r = BalanceHistory.Build([Tx(1, 500, D(3))], D(0), D(5));
        r.Accounts[0].Values.Should().Equal(null, null, null, 500m, 500m, 500m);
        r.Accounts[0].Since.Should().Be(D(3));
    }

    [Fact]
    public void Reported_account_carries_last_value_forward_and_is_empty_before_first()
    {
        var r = BalanceHistory.Build([Reported(1, (2, 1000), (4, 1100))], D(0), D(5));
        r.Accounts[0].Values.Should().Equal(null, null, 1000m, 1000m, 1100m, 1100m);
    }

    [Fact]
    public void Net_worth_splits_into_assets_and_debts()
    {
        var r = BalanceHistory.Build([Tx(1, 0, D(0), (0, 300)), Tx(2, 0, D(0), (0, -120)), Reported(3, (1, 50))], D(0), D(1));
        r.NetWorth.Should().Equal(180m, 230m);
        r.Assets.Should().Equal(300m, 350m);
        r.Debts.Should().Equal(-120m, -120m);
    }

    [Fact]
    public void Long_spans_sample_weekly_then_monthly_and_always_end_today()
    {
        var (weekly, wd) = BalanceHistory.SampleDates(D(0), D(365));
        weekly.Should().Be(BalanceHistory.Interval.Week);
        wd[^1].Should().Be(D(365));
        (wd[^1].DayNumber - wd[^2].DayNumber).Should().Be(7);

        var to = new DateOnly(2031, 3, 15);
        var (monthly, md) = BalanceHistory.SampleDates(new DateOnly(2026, 1, 10), to);
        monthly.Should().Be(BalanceHistory.Interval.Month);
        md[0].Should().Be(new DateOnly(2026, 1, 31));
        md[^2].Should().Be(new DateOnly(2031, 2, 28));
        md[^1].Should().Be(to);
    }
}
