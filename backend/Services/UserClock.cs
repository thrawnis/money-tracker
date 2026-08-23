using MoneyTracker.Models;

namespace MoneyTracker.Services;

/// <summary>
/// Resolves "today" in the user's own timezone.
///
/// Everything date-sensitive in this app — the as-of-today balance on the
/// Accounts page, Dashboard and register header, the report period shortcuts,
/// and when a scheduled bill posts — used to call
/// DateOnly.FromDateTime(DateTime.UtcNow). For anyone not living on UTC that
/// rolls the date over at the wrong local moment: a user in UTC-8 saw
/// "today's" balance jump at 4pm local, and bills posted up to a day early or
/// late depending on which side of UTC they were on.
///
/// ApplicationUser.TimeZoneId holds an IANA id ("America/Los_Angeles"). Null
/// means the user has never set one, which keeps the historical UTC behavior.
/// An id that doesn't resolve (bad data, or a host without the tz database)
/// also falls back to UTC rather than throwing — a wrong-by-hours date is a
/// far better failure than a 500 on every page that shows a balance.
/// </summary>
public static class UserClock
{
    public static DateOnly Today(string? timeZoneId) =>
        DateOnly.FromDateTime(Now(timeZoneId));

    public static DateTime Now(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) return DateTime.UtcNow;

        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return DateTime.UtcNow;
        }
    }

    public static DateOnly Today(ApplicationUser user) => Today(user.TimeZoneId);

    /// <summary>True when the id resolves on this host — used to validate input.</summary>
    public static bool IsValidTimeZone(string timeZoneId)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }
}
