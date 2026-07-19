using FluentAssertions;
using MoneyTracker.Models;
using MoneyTracker.Services;
using Xunit;

namespace MoneyTracker.Tests.Unit;

// Tests for the Advance() date-arithmetic logic in the scheduled-transaction
// posting engine (public — also called from ScheduledTransactionsController
// to project multiple upcoming occurrences). Verifies each FrequencyUnit
// advances the due date correctly, including month/year end-of-month
// clamping (leap years) and specific-weekday schedules.
public class ScheduledPostingAdvanceTests
{
    private static DateOnly Advance(DateOnly date, FrequencyUnit unit, int interval, int? daysOfWeekMask = null)
    {
        var s = new ScheduledTransaction
        {
            FrequencyUnit     = unit,
            FrequencyInterval = interval,
            DaysOfWeekMask    = daysOfWeekMask,
        };
        return ScheduledTransactionPostingService.Advance(date, s);
    }

    [Fact]
    public void Days_AddsCorrectDays()
    {
        Advance(new DateOnly(2024, 1, 1), FrequencyUnit.Days, 14)
            .Should().Be(new DateOnly(2024, 1, 15));
    }

    [Fact]
    public void Weeks_Multiplies7()
    {
        Advance(new DateOnly(2024, 1, 1), FrequencyUnit.Weeks, 2)
            .Should().Be(new DateOnly(2024, 1, 15));
    }

    [Fact]
    public void Months_ClampsToEndOfShorterMonth()
    {
        // Jan 31 + 1 month -> Feb 29 in a leap year (clamped)
        Advance(new DateOnly(2024, 1, 31), FrequencyUnit.Months, 1)
            .Should().Be(new DateOnly(2024, 2, 29));
    }

    [Fact]
    public void Years_ClampsLeapDay()
    {
        // Feb 29 + 1 year -> Feb 28 in a non-leap year
        Advance(new DateOnly(2024, 2, 29), FrequencyUnit.Years, 1)
            .Should().Be(new DateOnly(2025, 2, 28));
    }

    [Fact]
    public void Months_MultipleIntervals()
    {
        Advance(new DateOnly(2024, 6, 15), FrequencyUnit.Months, 6)
            .Should().Be(new DateOnly(2024, 12, 15));
    }

    // ── Specific-weekdays schedules ("every Mon/Wed/Fri") ──

    [Fact]
    public void DaysOfWeekMask_AdvancesToNextSetWeekday()
    {
        // 2024-01-01 is a Monday. Mask = Mon|Wed|Fri.
        var mask = (1 << (int)DayOfWeek.Monday) | (1 << (int)DayOfWeek.Wednesday) | (1 << (int)DayOfWeek.Friday);
        Advance(new DateOnly(2024, 1, 1), FrequencyUnit.Weeks, 1, mask)
            .Should().Be(new DateOnly(2024, 1, 3)); // next Wednesday
    }

    [Fact]
    public void DaysOfWeekMask_WrapsToNextWeek()
    {
        // Friday with mask = Mon|Wed|Fri -> following Monday, not later in the same week
        var mask = (1 << (int)DayOfWeek.Monday) | (1 << (int)DayOfWeek.Wednesday) | (1 << (int)DayOfWeek.Friday);
        Advance(new DateOnly(2024, 1, 5), FrequencyUnit.Weeks, 1, mask) // a Friday
            .Should().Be(new DateOnly(2024, 1, 8)); // the following Monday
    }

    [Fact]
    public void DaysOfWeekMask_SingleDayAdvancesExactlyOneWeek()
    {
        var mask = 1 << (int)DayOfWeek.Tuesday;
        Advance(new DateOnly(2024, 1, 2), FrequencyUnit.Weeks, 1, mask) // a Tuesday
            .Should().Be(new DateOnly(2024, 1, 9));
    }

    [Fact]
    public void DaysOfWeekMask_NullOrZero_FallsBackToPlainWeeklyInterval()
    {
        Advance(new DateOnly(2024, 1, 1), FrequencyUnit.Weeks, 2, null)
            .Should().Be(new DateOnly(2024, 1, 15));
        Advance(new DateOnly(2024, 1, 1), FrequencyUnit.Weeks, 2, 0)
            .Should().Be(new DateOnly(2024, 1, 15));
    }

    [Fact]
    public void DaysOfWeekMask_IgnoredForNonWeeksUnits()
    {
        var mask = 1 << (int)DayOfWeek.Monday;
        Advance(new DateOnly(2024, 1, 1), FrequencyUnit.Months, 1, mask)
            .Should().Be(new DateOnly(2024, 2, 1));
    }
}
