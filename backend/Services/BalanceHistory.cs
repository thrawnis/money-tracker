namespace MoneyTracker.Services;

/// <summary>
/// Builds account balances over time for the balance charts. Pure — the
/// caller loads the inputs — so the date and carry-forward rules are unit
/// testable.
///
/// Two kinds of account:
///   • transaction accounts: opening balance plus every non-voided transaction
///     whose effective date (PostDate ?? Date) is on or before the day — the
///     same rule as the current balance everywhere else. The opening
///     balance has no date of its own, so there's no value (null) before the
///     account's first transaction or the day it was created here.
///   • reported accounts (balance-only bank sync): the latest recorded value on
///     or before the day, null before the first one. Market moves aren't
///     transactions, so there's nothing to rebuild earlier history from.
/// </summary>
public static class BalanceHistory
{
    public enum Interval { Day, Week, Month }

    public record AccountInput(
        int Id, string Name, string Type, decimal OpeningBalance, DateOnly CreatedOn,
        // (effective date, total amount that day), any order
        IReadOnlyList<(DateOnly Date, decimal Amount)> DailyAmounts,
        // set for reported accounts: (date, value), any order
        IReadOnlyList<(DateOnly Date, decimal Value)>? ReportedValues);

    public record AccountSeries(int Id, string Name, string Type, bool Reported, decimal?[] Values, DateOnly? Since);

    public record Result(
        Interval Interval, DateOnly[] Dates, List<AccountSeries> Accounts,
        decimal[] NetWorth, decimal[] Assets, decimal[] Debts);

    /// <summary>
    /// Points from <paramref name="from"/> to <paramref name="to"/> (always
    /// including <paramref name="to"/>): daily for spans up to ~4 months, weekly
    /// up to 3 years, otherwise month ends.
    /// </summary>
    public static (Interval Interval, DateOnly[] Dates) SampleDates(DateOnly from, DateOnly to)
    {
        if (from > to) from = to;
        var span = to.DayNumber - from.DayNumber;
        var interval = span <= 120 ? Interval.Day : span <= 3 * 366 ? Interval.Week : Interval.Month;

        var dates = new List<DateOnly>();
        switch (interval)
        {
            case Interval.Day:
                for (var d = from; d <= to; d = d.AddDays(1)) dates.Add(d);
                break;
            case Interval.Week:
                // Step back from `to` so the latest point is today.
                for (var d = to; d >= from; d = d.AddDays(-7)) dates.Add(d);
                dates.Reverse();
                break;
            default:
                for (var m = new DateOnly(from.Year, from.Month, 1); ; m = m.AddMonths(1))
                {
                    var end = m.AddMonths(1).AddDays(-1);
                    if (end >= to) break;
                    if (end >= from) dates.Add(end);
                }
                dates.Add(to);
                break;
        }
        if (dates.Count == 0 || dates[^1] != to) dates.Add(to);
        return (interval, dates.ToArray());
    }

    /// <summary>The earliest day any account has a value — the start of "All time".</summary>
    public static DateOnly? EarliestDate(IEnumerable<AccountInput> accounts) =>
        accounts.Select(Since).Where(d => d.HasValue).Min();

    private static DateOnly? Since(AccountInput a) =>
        a.ReportedValues is { } r
            ? r.Count > 0 ? r.Min(v => v.Date) : null
            : a.DailyAmounts.Count > 0 && a.DailyAmounts.Min(x => x.Date) < a.CreatedOn
                ? a.DailyAmounts.Min(x => x.Date)
                : a.CreatedOn;

    public static Result Build(IReadOnlyList<AccountInput> accounts, DateOnly from, DateOnly to)
    {
        var (interval, dates) = SampleDates(from, to);
        var series = new List<AccountSeries>();
        var netWorth = new decimal[dates.Length];
        var assets = new decimal[dates.Length];
        var debts = new decimal[dates.Length];

        foreach (var a in accounts)
        {
            var values = new decimal?[dates.Length];
            if (a.ReportedValues is { } reported)
            {
                var sorted = reported.OrderBy(v => v.Date).ToList();
                var i = 0;
                decimal? last = null;
                for (var k = 0; k < dates.Length; k++)
                {
                    while (i < sorted.Count && sorted[i].Date <= dates[k]) last = sorted[i++].Value;
                    values[k] = last;
                }
            }
            else
            {
                var sorted = a.DailyAmounts.OrderBy(x => x.Date).ToList();
                var i = 0;
                var running = a.OpeningBalance;
                var started = false;
                for (var k = 0; k < dates.Length; k++)
                {
                    while (i < sorted.Count && sorted[i].Date <= dates[k])
                    {
                        running += sorted[i++].Amount;
                        started = true;
                    }
                    values[k] = started || dates[k] >= a.CreatedOn ? running : null;
                }
            }

            for (var k = 0; k < dates.Length; k++)
            {
                if (values[k] is not decimal v) continue;
                netWorth[k] += v;
                if (v >= 0) assets[k] += v; else debts[k] += v;
            }
            series.Add(new AccountSeries(a.Id, a.Name, a.Type, a.ReportedValues is not null, values, Since(a)));
        }

        return new Result(interval, dates, series, netWorth, assets, debts);
    }
}
