using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MoneyTracker.Data;
using MoneyTracker.Models;

namespace MoneyTracker.Controllers;

[ApiController]
[Authorize]
[Route("api/preferences")]
public class PreferencesController(
    AppDbContext db,
    UserManager<ApplicationUser> userManager) : ControllerBase
{
    private static readonly string[] ValidSortFields = ["date", "payee", "category", "memo", "amount", "status"];
    private static readonly string[] ValidSortDirs = ["asc", "desc"];

    private string? GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        return Ok(new
        {
            defaultRegisterSortBy  = user.DefaultRegisterSortBy,
            defaultRegisterSortDir = user.DefaultRegisterSortDir,
        });
    }

    [HttpPut]
    public async Task<IActionResult> Update(PreferencesDto dto)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        if (dto.DefaultRegisterSortBy is not null && !ValidSortFields.Contains(dto.DefaultRegisterSortBy))
            return BadRequest(new { message = "Invalid sort field." });
        if (dto.DefaultRegisterSortDir is not null && !ValidSortDirs.Contains(dto.DefaultRegisterSortDir))
            return BadRequest(new { message = "Invalid sort direction." });

        user.DefaultRegisterSortBy  = dto.DefaultRegisterSortBy;
        user.DefaultRegisterSortDir = dto.DefaultRegisterSortDir;
        await db.SaveChangesAsync();

        return Ok(new
        {
            defaultRegisterSortBy  = user.DefaultRegisterSortBy,
            defaultRegisterSortDir = user.DefaultRegisterSortDir,
        });
    }
}

// Null fields mean "use the built-in default" — lets the user explicitly reset
// to (date, newest first) instead of just omitting a field they never set.
public record PreferencesDto(string? DefaultRegisterSortBy, string? DefaultRegisterSortDir);
