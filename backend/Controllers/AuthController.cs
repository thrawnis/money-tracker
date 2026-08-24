using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoneyTracker.Auth;
using MoneyTracker.Auth.Dtos;
using MoneyTracker.Auth.Services;
using MoneyTracker.Data;
using MoneyTracker.Models;
using MoneyTracker.Services;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;

namespace MoneyTracker.Controllers;

[ApiController]
[Route("api/auth")]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth")]
// Signing out, refreshing, and managing your second factor must keep working
// even while the timezone gate is up — otherwise a user with no timezone set
// couldn't even log out of the screen holding them there.
[AllowWithoutTimeZone]
public class AuthController(
    UserManager<ApplicationUser>    userManager,
    SignInManager<ApplicationUser>  signInManager,
    RoleManager<IdentityRole>       roleManager,
    JwtService                      jwtService,
    IPasskeyService                 passkeyService,
    IEncryptionService              encryption,
    AppDbContext                    db,
    UrlEncoder                      urlEncoder,
    IAuditService                   audit,
    VoidCategoryMigrationService    voidCategoryMigration,
    VoidPayeeMigrationService       voidPayeeMigration,
    IConfiguration                  config) : ControllerBase
{
    private bool IsDemoMode => config["DEMO_MODE"] == "true";

    // How long a just-rotated refresh token stays acceptable, so two tabs
    // refreshing concurrently don't sign each other out. See Refresh().
    private static readonly TimeSpan RefreshReuseGrace = TimeSpan.FromSeconds(60);
    // ── Registration ────────────────────────────────────────────────────────

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        if (await userManager.FindByEmailAsync(request.Email) is not null)
            return Conflict("An account with this email already exists.");

        // Required at sign-up so no account is ever created without one — the
        // whole app's notion of "today" depends on it.
        if (string.IsNullOrWhiteSpace(request.TimeZoneId) || !UserClock.IsValidTimeZone(request.TimeZoneId))
            return BadRequest(new { message = "A valid time zone is required." });

        var user = new ApplicationUser
        {
            UserName         = request.Email,
            Email            = request.Email,
            EncryptedDataKey = encryption.GenerateEncryptedDek(),
            TimeZoneId       = request.TimeZoneId,
        };

        var result = await userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
            return BadRequest(new { message = string.Join(" ", result.Errors.Select(e => e.Description)) });

        await EnsureRolesExistAsync();
        await userManager.AddToRoleAsync(user, Roles.Standard);

        await audit.LogAsync("REGISTER", "User", null, new { email = request.Email });

        // Allow the just-registered user to proceed to MFA setup
        HttpContext.Session.SetString("mfaUserId", user.Id);

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

        // Mark this session as password-verified for this user. All MFA step-2
        // endpoints (TOTP setup/enroll/verify, passkey register/login) require
        // this marker — without it, knowing a userId alone is enough to take
        // over an account via mfa/totp/setup → enroll.
        HttpContext.Session.SetString("mfaUserId", user.Id);

        // Demo user bypasses MFA
        if (IsDemoMode && user.Email == MoneyTracker.Services.DemoSeeder.DemoEmail)
            return await IssueTokensAsync(user);

        if (!user.MfaEnrolled)
            return Ok(new { requiresMfaSetup = true });

        // MFA challenge required — client must call /mfa/totp/verify or /passkey/login/*
        return Ok(new { requiresMfa = true, userId = user.Id });
    }

    // ── TOTP setup ───────────────────────────────────────────────────────────

    [HttpPost("mfa/totp/setup")]
    public async Task<IActionResult> TotpSetup([FromBody] string userId)
    {
        if (!MfaStepAuthorized(userId)) return Unauthorized("Password verification required.");

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return NotFound();

        // An already-enrolled user must pass the TOTP challenge (or the
        // authenticated Settings → Security reset flow) — otherwise a password
        // alone could re-key the authenticator and bypass the second factor.
        if (user.MfaEnrolled)
            return Unauthorized(new { message = "Two-factor authentication is already enrolled. Use the authenticator reset in Settings instead." });

        await userManager.ResetAuthenticatorKeyAsync(user);
        var key = await userManager.GetAuthenticatorKeyAsync(user);
        if (key is null) return StatusCode(500, "Failed to generate authenticator key.");

        var uri = GenerateTotpUri(user.Email!, key);
        return Ok(new TotpSetupResponse(key, uri));
    }

    // Live enrollment state for Settings → Security. The JWT/auth context only
    // knows the state as of login — stale the moment a reset disables MFA.
    [HttpGet("mfa/status")]
    [Authorize]
    public async Task<IActionResult> MfaStatus()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        return Ok(new { mfaEnrolled = user.MfaEnrolled });
    }

    // ── TOTP reset (already-logged-in user invalidating/recreating their
    // authenticator) — re-authenticates via the same short-lived, single-use
    // token as Export/account-deletion instead of the pre-login session flag,
    // since there's no login flow in progress here. ──────────────────────────

    [HttpPost("mfa/totp/reset-setup")]
    [Authorize]
    public async Task<IActionResult> TotpResetSetup([FromBody] TotpResetSetupRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        var stored = await db.ExportTokens
            .FirstOrDefaultAsync(t => t.Token == request.ExportToken && t.UserId == userId
                                   && !t.IsUsed && t.ExpiresAt > DateTime.UtcNow);
        if (stored is null) return Unauthorized(new { message = "Identity verification required or expired." });

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        stored.IsUsed = true;

        // The old code stops working the instant the key is reset. MfaEnrolled
        // drops to false until the new code is verified below, so an abandoned
        // reset degrades to "no MFA" rather than locking the user out of login
        // entirely with a code they have no way to produce.
        await userManager.ResetAuthenticatorKeyAsync(user);
        user.MfaEnrolled = false;
        await userManager.UpdateAsync(user);
        await db.SaveChangesAsync();

        var key = await userManager.GetAuthenticatorKeyAsync(user);
        if (key is null) return StatusCode(500, "Failed to generate authenticator key.");

        await audit.LogAsync("MFA_RESET_STARTED", details: new { type = "TOTP" });

        var uri = GenerateTotpUri(user.Email!, key);
        return Ok(new TotpSetupResponse(key, uri));
    }

    [HttpPost("mfa/totp/reset-enroll")]
    [Authorize]
    public async Task<IActionResult> TotpResetEnroll([FromBody] TotpResetEnrollRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return Unauthorized();

        if (await userManager.IsLockedOutAsync(user))
            return StatusCode(429, "Account locked due to too many failed attempts. Try again later.");

        var valid = await userManager.VerifyTwoFactorTokenAsync(
            user,
            userManager.Options.Tokens.AuthenticatorTokenProvider,
            request.Code);

        if (!valid)
        {
            await userManager.AccessFailedAsync(user);
            return BadRequest("Invalid code.");
        }

        await userManager.ResetAccessFailedCountAsync(user);

        user.MfaEnrolled = true;
        await userManager.UpdateAsync(user);

        await audit.LogAsync("MFA_RESET_COMPLETED", details: new { type = "TOTP" });

        return Ok(new { mfaEnrolled = true });
    }

    [HttpPost("mfa/totp/enroll")]
    public async Task<IActionResult> TotpEnroll([FromBody] TotpEnrollRequest request)
    {
        if (!MfaStepAuthorized(request.UserId)) return Unauthorized("Password verification required.");

        var user = await userManager.FindByIdAsync(request.UserId);
        if (user is null) return NotFound();

        // Enrolled users authenticate via mfa/totp/verify — enroll is only for
        // first-time setup (see TotpSetup for the bypass this closes).
        if (user.MfaEnrolled)
            return Unauthorized(new { message = "Two-factor authentication is already enrolled." });

        if (await userManager.IsLockedOutAsync(user))
            return StatusCode(429, "Account locked due to too many failed attempts. Try again later.");

        var valid = await userManager.VerifyTwoFactorTokenAsync(
            user,
            userManager.Options.Tokens.AuthenticatorTokenProvider,
            request.Code);

        if (!valid)
        {
            await userManager.AccessFailedAsync(user);
            return BadRequest("Invalid code.");
        }

        await userManager.ResetAccessFailedCountAsync(user);

        user.MfaEnrolled = true;
        await userManager.UpdateAsync(user);

        await audit.LogAsync("MFA_ENROLLED", details: new { type = "TOTP" });

        return await IssueTokensAsync(user);
    }

    // ── TOTP login (step 2) ──────────────────────────────────────────────────

    [HttpPost("mfa/totp/verify")]
    public async Task<IActionResult> TotpVerify([FromBody] TotpLoginRequest request)
    {
        if (!MfaStepAuthorized(request.UserId)) return Unauthorized("Password verification required.");

        var user = await userManager.FindByIdAsync(request.UserId);
        if (user is null) return NotFound();

        if (await userManager.IsLockedOutAsync(user))
            return StatusCode(429, "Account locked due to too many failed attempts. Try again later.");

        var valid = await userManager.VerifyTwoFactorTokenAsync(
            user,
            userManager.Options.Tokens.AuthenticatorTokenProvider,
            request.Code);

        if (!valid)
        {
            await userManager.AccessFailedAsync(user);
            return Unauthorized("Invalid or expired code.");
        }

        await userManager.ResetAccessFailedCountAsync(user);

        return await IssueTokensAsync(user);
    }

    // ── Passkey registration ──────────────────────────────────────────────────

    [HttpPost("passkey/register/begin")]
    public async Task<IActionResult> PasskeyRegisterBegin([FromBody] string userId)
    {
        if (!MfaStepAuthorized(userId)) return Unauthorized("Password verification required.");

        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return NotFound();

        // Same rule as TOTP setup: once enrolled, a password alone must never be
        // enough to register a fresh second factor (that would bypass the old one).
        if (user.MfaEnrolled)
            return Unauthorized(new { message = "Two-factor authentication is already enrolled." });

        var optionsJson = await passkeyService.BeginRegistrationAsync(user);
        HttpContext.Session.SetString("passkeyRegOptions", optionsJson);
        return Content(optionsJson, "application/json");
    }

    [HttpPost("passkey/register/complete")]
    public async Task<IActionResult> PasskeyRegisterComplete(
        [FromBody] PasskeyRegisterCompleteRequest request)
    {
        if (!MfaStepAuthorized(request.UserId)) return Unauthorized("Password verification required.");

        var user = await userManager.FindByIdAsync(request.UserId);
        if (user is null) return NotFound();

        if (user.MfaEnrolled)
            return Unauthorized(new { message = "Two-factor authentication is already enrolled." });

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
        if (!MfaStepAuthorized(userId)) return Unauthorized("Password verification required.");

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

        if (!MfaStepAuthorized(credential.UserId)) return Unauthorized("Password verification required.");

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
            .FirstOrDefaultAsync(r => r.Token == tokenValue);

        if (stored is null || stored.ExpiresAt < DateTime.UtcNow)
            return Unauthorized("Refresh token is invalid or expired.");

        if (stored.IsRevoked)
        {
            // Tokens rotate on every use, and the app refreshes whenever a tab
            // becomes visible. Two tabs waking together legitimately present
            // the same cookie, so the loser of that race is not an attacker —
            // honour a just-rotated token briefly instead of signing them out.
            var revokedAgo = DateTime.UtcNow - (stored.RevokedAt ?? DateTime.MinValue);
            if (revokedAgo > RefreshReuseGrace)
            {
                // Outside the grace window this is a replay of a token that was
                // already spent — the classic signal that a refresh token
                // leaked. Drop the entire family so the real user and the
                // holder of the stolen copy both have to sign in again.
                var family = db.RefreshTokens.Where(r => r.UserId == stored.UserId && !r.IsRevoked);
                await family.ForEachAsync(t => { t.IsRevoked = true; t.RevokedAt = DateTime.UtcNow; });
                await db.SaveChangesAsync();

                await audit.LogAsync("REFRESH_TOKEN_REUSE", "User", null,
                    new { userId = stored.UserId, message = "Revoked token replayed; all sessions signed out." });

                Response.Cookies.Delete("refreshToken");
                return Unauthorized("Refresh token is invalid or expired.");
            }
        }
        else
        {
            stored.IsRevoked = true;
            stored.RevokedAt = DateTime.UtcNow;
        }

        // Same one-time cleanup IssueTokensAsync runs. Refresh builds its
        // tokens inline rather than going through that method, so without this
        // the migration only ever fired on a full sign-in — not on the silent
        // refresh that the service's own docs claim covers it.
        await voidCategoryMigration.RunIfNeededAsync(stored.User);
        await voidPayeeMigration.RunIfNeededAsync(stored.User);

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

        // Rotation adds a row every time a tab regains focus, and nothing else
        // ever removed them. Drop this user's long-dead tokens as we go —
        // they're useless past expiry and the table grew without bound.
        var staleCutoff = DateTime.UtcNow.AddDays(-30);
        var stale = db.RefreshTokens.Where(r => r.UserId == stored.UserId && r.ExpiresAt < staleCutoff);
        db.RefreshTokens.RemoveRange(stale);

        await db.SaveChangesAsync();

        SetRefreshCookie(newRefresh, wasRememberMe ? newExpiry : null);
        return Ok(new TokenResponse(accessToken, expiry, role, stored.User.MfaEnrolled,
            RequiresTimeZone: string.IsNullOrWhiteSpace(stored.User.TimeZoneId)));
    }

    // ── Change Password ───────────────────────────────────────────────────────

    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = await userManager.FindByIdAsync(userId!);
        if (user is null) return Unauthorized();

        if (IsDemoMode && user.Email == MoneyTracker.Services.DemoSeeder.DemoEmail)
            return BadRequest(new { message = "Password cannot be changed for the demo account." });

        var result = await userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
            return BadRequest(new { message = string.Join(" ", result.Errors.Select(e => e.Description)) });

        // Revoke every refresh token, including this session's — a password
        // change should not leave any pre-change session alive, and singling
        // out the current one would mean trusting a cookie that was issued
        // under the old credentials.
        var tokens = db.RefreshTokens.Where(r => r.UserId == userId && !r.IsRevoked);
        await tokens.ForEachAsync(t => { t.IsRevoked = true; t.RevokedAt = DateTime.UtcNow; });
        await db.SaveChangesAsync();

        await audit.LogAsync("PASSWORD_CHANGE");

        Response.Cookies.Delete("refreshToken");
        return Ok(new { message = "Password changed successfully. You have been signed out everywhere and will need to sign in again." });
    }

    // ── Logout all sessions ───────────────────────────────────────────────────

    [HttpPost("logout-all")]
    [Authorize]
    public async Task<IActionResult> LogoutAll()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is not null)
        {
            var tokens = db.RefreshTokens.Where(r => r.UserId == userId && !r.IsRevoked);
            await tokens.ForEachAsync(t => { t.IsRevoked = true; t.RevokedAt = DateTime.UtcNow; });
            await db.SaveChangesAsync();
        }

        await audit.LogAsync("LOGOUT_ALL");
        Response.Cookies.Delete("refreshToken");
        return NoContent();
    }

    // ── Logout ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Signs out THIS session only — revokes the refresh token presented by
    /// this browser's cookie and leaves other devices alone. It previously
    /// revoked every token the user had, which made it identical to
    /// logout-all: signing out on a laptop also killed the phone.
    /// </summary>
    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var tokenValue = Request.Cookies["refreshToken"];
        if (userId is not null && !string.IsNullOrEmpty(tokenValue))
        {
            var current = await db.RefreshTokens
                .FirstOrDefaultAsync(r => r.Token == tokenValue && r.UserId == userId && !r.IsRevoked);
            if (current is not null)
            {
                current.IsRevoked = true;
                current.RevokedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
        }

        await audit.LogAsync("LOGOUT");
        Response.Cookies.Delete("refreshToken");
        return NoContent();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// True when the current session has completed password verification
    /// (login or register) for the given user. Required before any MFA
    /// setup/verify or passkey operation may proceed.
    /// </summary>
    private bool MfaStepAuthorized(string userId) =>
        HttpContext.Session.GetString("mfaUserId") == userId;

    private async Task<IActionResult> IssueTokensAsync(ApplicationUser user)
    {
        // Runs once per user (see VoidCategoriesMigrated) — this is the one
        // chokepoint every login path and silent token refresh funnels
        // through, so it fires "next time the app loads" for free.
        await voidCategoryMigration.RunIfNeededAsync(user);
        await voidPayeeMigration.RunIfNeededAsync(user);

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
        HttpContext.Session.Remove("mfaUserId");
        await audit.LogAsync("LOGIN", details: new { email = user.Email, method = "password" });
        return Ok(new TokenResponse(accessToken, expiry, role, user.MfaEnrolled,
            RequiresTimeZone: string.IsNullOrWhiteSpace(user.TimeZoneId)));
    }

    private void SetRefreshCookie(string token, DateTime? expires)
    {
        var options = new CookieOptions
        {
            HttpOnly = true,
            Secure   = Request.IsHttps, // allow plain-HTTP local dev; HTTPS in production
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
public record TotpResetSetupRequest(string ExportToken);
public record TotpResetEnrollRequest(string Code);
public record PasskeyRegisterCompleteRequest(
    string UserId,
    string AttestationResponseJson,
    string? DeviceName);
public record PasskeyLoginCompleteRequest(
    string AssertionResponseJson);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
