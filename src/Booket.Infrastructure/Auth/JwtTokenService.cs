using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Booket.Application.Abstractions;
using Booket.Domain.Users;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Booket.Infrastructure.Auth;

public static class BooketClaims
{
    public const string Subject = JwtRegisteredClaimNames.Sub;
    public const string Email = JwtRegisteredClaimNames.Email;
    public const string Name = "name";
    public const string Role = "role";
    public const string BusinessId = "business_id";
}

internal sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider clock) : ITokenService
{
    private readonly JwtOptions _options = options.Value;
    private readonly JsonWebTokenHandler _handler = new();

    public TimeSpan RefreshTokenLifetime => _options.RefreshTokenLifetime;

    public static SymmetricSecurityKey CreateKey(string signingKey) => new(Encoding.UTF8.GetBytes(signingKey));

    public AccessToken CreateAccessToken(User user)
    {
        var now = clock.GetUtcNow();
        var expires = now + _options.AccessTokenLifetime;

        var claims = new List<Claim>
        {
            new(BooketClaims.Subject, user.Id.ToString()),
            new(BooketClaims.Email, user.Email),
            new(BooketClaims.Name, user.FullName),
            new(BooketClaims.Role, user.Role.ToString()),
        };

        if (user.BusinessId is { } businessId)
        {
            claims.Add(new Claim(BooketClaims.BusinessId, businessId.ToString()));
        }

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(CreateKey(_options.SigningKey), SecurityAlgorithms.HmacSha256),
        });

        return new AccessToken(token, expires);
    }

    public string GenerateRefreshToken() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(48));

    public string HashRefreshToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
