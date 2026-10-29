using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EduSphere.Safety.Api;

/// <summary>
/// Development-only HS256 token minting for the stub endpoint
/// (POST /v1/auth/dev-token, mapped only when Auth:DevStubEnabled=true).
/// Production sets Auth:Authority to the real provider instead.
/// Tokens carry <c>tid</c> (tenant) + <c>sub</c> (actor) claims.
/// </summary>
public static class DevTokenIssuer
{
    public static string Mint(
        string signingKey,
        string issuer,
        string audience,
        string tenantId,
        string userId,
        TimeSpan? lifetime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signingKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var handler = new JsonWebTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim("tid", tenantId),
                new System.Security.Claims.Claim("sub", userId),
            ]),
            Expires = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromHours(1)),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                SecurityAlgorithms.HmacSha256),
        };
        return handler.CreateToken(descriptor);
    }
}
