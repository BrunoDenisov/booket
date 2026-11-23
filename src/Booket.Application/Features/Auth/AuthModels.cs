using Booket.Application.Abstractions;
using Booket.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Booket.Application.Features.Auth;

public sealed record UserResponse(Guid Id, string Email, string FullName, string? Phone, UserRole Role, Guid? BusinessId)
{
    public static UserResponse From(User user) => new(user.Id, user.Email, user.FullName, user.Phone, user.Role, user.BusinessId);
}

public sealed record AuthResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    UserResponse User);

public static class AuthErrors
{
    public static readonly Domain.Common.Error InvalidCredentials =
        Domain.Common.Error.Unauthorized("auth.invalid_credentials", "Email or password is incorrect.");

    public static readonly Domain.Common.Error InvalidRefreshToken =
        Domain.Common.Error.Unauthorized("auth.invalid_refresh_token", "The refresh token is invalid or expired.");

    public static readonly Domain.Common.Error EmailTaken =
        Domain.Common.Error.Conflict("auth.email_taken", "An account with this email already exists.");
}

/// <summary>Issues an access token and a new refresh token (optionally continuing an existing token family).</summary>
internal sealed class TokenIssuer(IAppDbContext db, ITokenService tokens, TimeProvider clock)
{
    public AuthResponse Issue(User user, Guid? familyId = null)
    {
        var now = clock.GetUtcNow();
        var access = tokens.CreateAccessToken(user);
        var refreshValue = tokens.GenerateRefreshToken();
        var refresh = RefreshToken.Issue(user.Id, tokens.HashRefreshToken(refreshValue), now, tokens.RefreshTokenLifetime, familyId);
        db.RefreshTokens.Add(refresh);

        LastIssued = refresh;
        return new AuthResponse(access.Token, access.ExpiresAtUtc, refreshValue, refresh.ExpiresAtUtc, UserResponse.From(user));
    }

    /// <summary>The refresh token entity created by the last <see cref="Issue"/> call.</summary>
    public RefreshToken? LastIssued { get; private set; }

    public Task<bool> EmailExistsAsync(string email, CancellationToken ct)
    {
        var normalized = User.NormalizeEmail(email);
        return db.Users.AnyAsync(u => u.Email == normalized, ct);
    }
}
