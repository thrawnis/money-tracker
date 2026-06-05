using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using MoneyTracker.Models;

namespace MoneyTracker.Auth.Services;

public class JwtService(IConfiguration config)
{
    private readonly string _key       = config["Jwt:Key"]      ?? throw new InvalidOperationException("Jwt:Key not configured");
    private readonly string _issuer    = config["Jwt:Issuer"]   ?? "MoneyTracker";
    private readonly string _audience  = config["Jwt:Audience"] ?? "MoneyTracker";
    private readonly int    _expiryMin = int.Parse(config["Jwt:AccessTokenExpiryMinutes"] ?? "15");
    private readonly int    _refreshDays = int.Parse(config["Jwt:RefreshTokenExpiryDays"] ?? "7");

    public (string token, DateTime expiry) GenerateAccessToken(ApplicationUser user, string role)
    {
        var expiry = DateTime.UtcNow.AddMinutes(_expiryMin);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub,   user.Id),
            new Claim(JwtRegisteredClaimNames.Email, user.Email!),
            new Claim(JwtRegisteredClaimNames.Jti,   Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, role),
        };

        var key   = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_key));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer:    _issuer,
            audience:  _audience,
            claims:    claims,
            expires:   expiry,
            signingCredentials: creds
        );

        return (new JwtSecurityTokenHandler().WriteToken(token), expiry);
    }

    public (string token, DateTime expiry) GenerateRefreshToken()
    {
        var token  = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var expiry = DateTime.UtcNow.AddDays(_refreshDays);
        return (token, expiry);
    }

    public ClaimsPrincipal? ValidateAccessToken(string token)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_key));

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidIssuer              = _issuer,
            ValidateAudience         = true,
            ValidAudience            = _audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey         = key,
            ValidateLifetime         = false, // allow expired tokens for refresh
        };

        try
        {
            return new JwtSecurityTokenHandler()
                .ValidateToken(token, parameters, out _);
        }
        catch
        {
            return null;
        }
    }
}
