using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Auth;
using MoneyTracker.Auth.Dtos;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Models;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;

namespace MoneyTracker.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(
    UserManager<ApplicationUser>    userManager,
    SignInManager<ApplicationUser>  signInManager,
    RoleManager<IdentityRole>       roleManager,
    JwtService                      jwtService,
    IPasskeyService                 passkeyService,
    IEncryptionService              encryption,
    AppDbContext                    db,
    UrlEncoder                      urlEncoder) : ControllerBase
{
    // ── Registration ────────────────────────────────────────────────────────

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        if (await userManager.FindByEmailAsync(request.Email) is not null)
            return Conflict("An account with this email already exists.");

        var user = new ApplicationUser
        {
            UserName         = request.Email,
            Email            = request.Email,
            EncryptedDataKey = encryption.GenerateEncryptedDek(),
        };

        var result = await userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
            return BadRequest(new { message = string.Join(" ", result.Errors.Select(e => e.Description)) });

        await EnsureRolesExistAsync();
        await userManager.AddToRoleAsync(user, Roles.Standard);

        return Ok(new { userId = user.Id, requiresMfaSetup = true });
    }

    // ── Login (step 1 — password) ────────────────────────────────────────────

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var user = await userManager.FindByEmailAsync(request.Email);
        if (user is null)
            return Unauthorized("Invalid credentials.");

        var result = await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);

        if (result.IsLockedOut)
            return StatusCode(429, "Account locked due to too many failed attempts. Try again later.");

        if (!result.Succeeded)
            return Unauthorized("Invalid credentials.");

        // Stash rememberMe so IssueTokensAsync can read it after MFA completes
        HttpContext.Session.SetString("rememberMe", request.RememberMe ? "1" : "0");

        if (!user.MfaEnrolled)
            return Ok(new { requiresMfaSetup = true });

        // MFA challenge required — client must call /mfa/totp/verify or /passkey/login/*
        return Ok(new { requiresMfa = true, userId = user.Id });
    }

    // ── TOTP setup ───────────────────────────────────────────────────────────

    [HttpPost("mfa/totp/setup")]
    public async Task<IActionResult> TotpSetup([FromBody] string userId)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return NotFound();

        await userManager.ResetAuthenticatorKeyAsync(user);
        var key = await userManager.GetAuthenticatorKeyAsync(user);
        if (key is null) return StatusCode(500, "Failed to generate authenticator key.");

        var uri = GenerateTotpUri(user.Email!, key);
        return Ok(new TotpSetupResponse(key, uri));
    }

    [HttpPost("mfa/totp/enroll")]
    public async Task<IActionResult> TotpEnroll([FromBody] TotpEnrollRequest request)
    {
        var user = await userManager.FindByIdAsync(request.UserId);
        if (user is null) return NotFound();

        var valid = await userManager.VerifyTwoFactorTokenAsync(
            user,
            userManager.Options.Tokens.AuthenticatorTokenProvider,
            request.Code);

        if (!valid) return BadRequest("Invalid code.");

        user.MfaEnrolled = true;
        await userManager.UpdateAsync(user);

        return await IssueTokensAsync(user);
    }

    // ── TOTP login (step 2) ──────────────────────────────────────────────────

    [HttpPost("mfa/totp/verify")]
    public async Task<IActionResult> TotpVerify([FromBody] TotpLoginRequest request)
    {
        var user = await userManager.FindByIdAsync(request.UserId);
        if (user is null) return NotFound();

        var valid = await userManager.VerifyTwoFactorTokenAsync(
            user,
            userManager.Options.Tokens.AuthenticatorTokenProvider,
            request.Code);

        if (!valid) return Unauthorized("Invalid or expired code.");

        return await IssueTokensAsync(user);
    }

    // ── Passkey registration ──────────────────────────────────────────────────

    [HttpPost("passkey/register/begin")]
    public async Task<IActionResult> PasskeyRegisterBegin([FromBody] string userId)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return NotFound();

        var optionsJson = await passkeyService.BeginRegistrationAsync(user);
        HttpContext.Session.SetString("passkeyRegOptions", optionsJson);
        return Content(optionsJson, "application/json");
    }

    [HttpPost("passkey/register/complete")]
    public async Task<IActionResult> PasskeyRegisterComplete(
        [FromBody] PasskeyRegisterCompleteRequest request)
    {
        var user = await userManager.FindByIdAsync(request.UserId);
        if (user is null) return NotFound();

        var optionsJson = HttpContext.Session.GetString("passkeyRegOptions");
        if (optionsJson is null) return BadRequest("Registration session expired.");

        var credential = await passkeyService.CompleteRegistrationAsync(
            request.AttestationResponseJson, optionsJson, request.DeviceName);

        credential.UserId = user.Id;
        db.PasskeyCredentials.Add(credential);

        user.MfaEnrolled = true;
        await userManager.UpdateAsync(user);
        await db.SaveChangesAsync();

        HttpContext.Session.Remove("passkeyRegOptions");
        return await IssueTokensAsync(user);
    }

    // ── Passkey login (step 2) ────────────────────────────────────────────────

    [HttpPost("passkey/login/begin")]
    public async Task<IActionResult> PasskeyLoginBegin([FromBody] string userId)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return NotFound();

        var optionsJson = await passkeyService.BeginAuthenticationAsync(user.Email!);
        HttpContext.Session.SetString("passkeyAuthOptions", optionsJson);
        return Content(optionsJson, "application/json");
    }

    [HttpPost("passkey/login/complete")]
    public async Task<IActionResult> PasskeyLoginComplete(
        [FromBody] PasskeyLoginCompleteRequest request)
    {
        var optionsJson = HttpContext.Session.GetString("passkeyAuthOptions");
        if (optionsJson is null) return BadRequest("Authentication session expired.");

        var credential = await passkeyService.CompleteAuthenticationAsync(
            request.AssertionResponseJson, optionsJson);

        await db.SaveChangesAsync();

        var user = await userManager.FindByIdAsync(credential.UserId);
        if (user is null) return Unauthorized();

        HttpContext.Session.Remove("passkeyAuthOptions");
        return await IssueTokensAsync(user);
    }

    // ── Token refresh ─────────────────────────────────────────────────────────

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh()
    {
        var tokenValue = Request.Cookies["refreshToken"];
        if (string.IsNullOrEmpty(tokenValue))
            return Unauthorized("No refresh token.");

        var stored = await db.RefreshTokens
            .Include(r => r.User)
            .FirstOrDefaultAsync(r => r.Token == tokenValue && !r.IsRevoked);

        if (stored is null || stored.ExpiresAt < DateTime.UtcNow)
            return Unauthorized("Refresh token is invalid or expired.");

        stored.IsRevoked = true;

        var roles = await userManager.GetRolesAsync(stored.User);
        var role  = roles.Contains(Roles.Admin) ? Roles.Admin : Roles.Standard;

        var (accessToken, expiry) = jwtService.GenerateAccessToken(stored.User, role);

        // Preserve remember-me duration: if old token lived > 2 days it was a remember-me token
        bool wasRememberMe   = (stored.ExpiresAt - stored.CreatedAt).TotalDays > 2;
        var newExpiry        = wasRememberMe ? DateTime.UtcNow.AddDays(14) : DateTime.UtcNow.AddDays(1);
        var (newRefresh, _)  = jwtService.GenerateRefreshToken(newExpiry);

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId    = stored.User.Id,
            Token     = newRefresh,
            ExpiresAt = newExpiry,
        });

        await db.SaveChangesAsync();

        SetRefreshCookie(newRefresh, wasRememberMe ? newExpiry : null);
        return Ok(new TokenResponse(accessToken, expiry, role, stored.User.MfaEnrolled));
    }

    // ── Change Password ───────────────────────────────────────────────────────

    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = await userManager.FindByIdAsync(userId!);
        if (user is null) return Unauthorized();

        var result = await userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
            return BadRequest(new { message = string.Join(" ", result.Errors.Select(e => e.Description)) });

        return Ok(new { message = "Password changed successfully." });
    }

    // ── Logout ────────────────────────────────────────────────────────────────

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is not null)
        {
            var tokens = db.RefreshTokens.Where(r => r.UserId == userId && !r.IsRevoked);
            await tokens.ForEachAsync(t => t.IsRevoked = true);
            await db.SaveChangesAsync();
        }

        Response.Cookies.Delete("refreshToken");
        return NoContent();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<IActionResult> IssueTokensAsync(ApplicationUser user)
    {
        var roles      = await userManager.GetRolesAsync(user);
        var role       = roles.Contains(Roles.Admin) ? Roles.Admin : Roles.Standard;
        var rememberMe = HttpContext.Session.GetString("rememberMe") == "1";

        var (accessToken, expiry) = jwtService.GenerateAccessToken(user, role);

        // Remember Me: 14-day persistent cookie; otherwise session cookie (closes with browser)
        DateTime? refreshExpiry = rememberMe ? DateTime.UtcNow.AddDays(14) : DateTime.UtcNow.AddDays(1);
        var refreshToken = jwtService.GenerateRefreshToken(refreshExpiry.Value).token;

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId    = user.Id,
            Token     = refreshToken,
            ExpiresAt = refreshExpiry.Value,
        });
        await db.SaveChangesAsync();

        SetRefreshCookie(refreshToken, rememberMe ? refreshExpiry : null);
        return Ok(new TokenResponse(accessToken, expiry, role, user.MfaEnrolled));
    }

    private void SetRefreshCookie(string token, DateTime? expires)
    {
        var options = new CookieOptions
        {
            HttpOnly = true,
            Secure   = true,
            SameSite = SameSiteMode.Strict,
        };
        // Persistent cookie only when Remember Me was checked
        if (expires.HasValue) options.Expires = expires.Value;
        Response.Cookies.Append("refreshToken", token, options);
    }

    private string GenerateTotpUri(string email, string key)
    {
        const string issuer = "MoneyTracker";
        var encodedIssuer   = urlEncoder.Encode(issuer);
        var encodedEmail    = urlEncoder.Encode(email);
        var encodedKey      = urlEncoder.Encode(key);
        return $"otpauth://totp/{encodedIssuer}:{encodedEmail}?secret={encodedKey}&issuer={encodedIssuer}&digits=6";
    }

    private async Task EnsureRolesExistAsync()
    {
        foreach (var role in new[] { Roles.Admin, Roles.Standard })
        {
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole(role));
        }
    }
}

// ── Supplementary request DTOs used only in this controller ──────────────────

public record TotpEnrollRequest(string UserId, string Code);
public record TotpLoginRequest(string UserId, string Code);
public record PasskeyRegisterCompleteRequest(
    string UserId,
    string AttestationResponseJson,
    string? DeviceName);
public record PasskeyLoginCompleteRequest(
    string AssertionResponseJson);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
