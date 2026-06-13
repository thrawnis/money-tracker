using FluentAssertions;
using MoneyTracker.Models;
using MoneyTracker.Services;
using Xunit;

namespace MoneyTracker.Tests.Unit;

// Tests for the private Advance() date-arithmetic logic in the scheduled-
// transaction posting engine, accessed via reflection. Verifies each
// FrequencyUnit advances the due date correctly, including month/year
// end-of-month clamping (leap years).
public class ScheduledPostingAdvanceTests
{
    private static DateOnly Advance(DateOnly date, FrequencyUnit unit, int interval)
    {
        var method = typeof(ScheduledTransactionPostingService)
            .GetMethod("Advance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (DateOnly)method.Invoke(null, [date, unit, interval])!;
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
}
