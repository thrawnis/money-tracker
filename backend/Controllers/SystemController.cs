using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoneyTracker.Services;

namespace MoneyTracker.Controllers;

/// <summary>
/// Housekeeping for the server itself — used by rebuild.sh to avoid restarting
/// the app in the middle of a bank sync. Answers only requests made directly
/// to the API from the server or its private network (e.g. curl to the
/// published port on the host), never ones that came through the web front
/// end, so it isn't visible from the internet.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/system")]
public class SystemController : ControllerBase
{
    [HttpGet("busy")]
    public IActionResult Busy()
    {
        // UseForwardedHeaders moves a proxy's X-Forwarded-For into X-Original-For,
        // so either header means the request came through nginx.
        var proxied = Request.Headers.ContainsKey("X-Forwarded-For") || Request.Headers.ContainsKey("X-Original-For");
        if (proxied || !IsLocal(HttpContext.Connection.RemoteIpAddress)) return NotFound();

        return Ok(new
        {
            // A SimpleFIN request (sync, connect, auto-import) is running, or a
            // nightly sync run is part-way through its users.
            bankSync = SimpleFinService.Busy || SimpleFinDailyUpdateService.Running,
        });
    }

    private static bool IsLocal(IPAddress? ip)
    {
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }
}
