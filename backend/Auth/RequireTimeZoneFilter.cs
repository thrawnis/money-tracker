using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MoneyTracker.Auth;

/// <summary>
/// Marks an action that an authenticated user may still call before they have
/// chosen a timezone — the timezone picker itself, and anything it needs to
/// render. Everything else is blocked by <see cref="RequireTimeZoneFilter"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class AllowWithoutTimeZoneAttribute : Attribute;

/// <summary>
/// Blocks every authenticated request from a user who has no timezone set,
/// with 428 Precondition Required.
///
/// Accounts created before the timezone requirement have TimeZoneId = null.
/// The UI puts a mandatory picker in front of them, but the UI is not where
/// this can be enforced — a stale tab, a bookmarked deep link, or anything
/// talking to the API directly would otherwise carry on with UTC silently
/// standing in for their real timezone, quietly shifting every balance and
/// report date. Failing closed here means "before they can do anything else"
/// actually holds.
///
/// The check reads the "tz" claim rather than loading the user: this runs on
/// every single request, and a DB round trip per call to re-read a field that
/// changes once in an account's lifetime is not worth it. Setting a timezone
/// mints a fresh token (see PreferencesController.SetTimeZone), so the claim
/// never lags behind the stored value in a way that matters — a token issued
/// before it was set only survives until the client refreshes, which the
/// picker does immediately.
/// </summary>
public class RequireTimeZoneFilter : IAsyncActionFilter
{
    public const string TimeZoneClaim = "tz";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var user = context.HttpContext.User;

        // Anonymous endpoints police themselves; there's no user to gate yet.
        if (user.Identity?.IsAuthenticated != true)
        {
            await next();
            return;
        }

        var endpoint = context.ActionDescriptor.EndpointMetadata;
        if (endpoint.OfType<AllowWithoutTimeZoneAttribute>().Any())
        {
            await next();
            return;
        }

        if (user.FindFirst(TimeZoneClaim)?.Value is not null and not "")
        {
            await next();
            return;
        }

        context.Result = new ObjectResult(new
        {
            message = "Select your time zone before continuing.",
            requiresTimeZone = true,
        })
        {
            StatusCode = StatusCodes.Status428PreconditionRequired,
        };
    }
}
