using Booket.Domain.Users;

namespace Booket.Application.Abstractions;

/// <summary>The authenticated caller, resolved from the JWT.</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
    UserRole? Role { get; }
    Guid? BusinessId { get; }

    bool IsAuthenticated => UserId is not null;
    bool IsAdmin => Role == UserRole.Admin;

    /// <summary>Owner of the given business, or a platform admin.</summary>
    bool CanManage(Guid businessId) => IsAdmin || (Role == UserRole.Owner && BusinessId == businessId);

    /// <summary>Owner or staff of the given business, or a platform admin.</summary>
    bool IsMemberOf(Guid businessId) => IsAdmin || (Role is UserRole.Owner or UserRole.Staff && BusinessId == businessId);

    Guid RequireUserId() => UserId ?? throw new InvalidOperationException("An authenticated user is required.");
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string hash, string password);
}

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAtUtc);

public interface ITokenService
{
    TimeSpan RefreshTokenLifetime { get; }
    AccessToken CreateAccessToken(User user);

    /// <summary>Generates a cryptographically random opaque refresh token.</summary>
    string GenerateRefreshToken();

    /// <summary>One-way hash used to store and look up refresh tokens.</summary>
    string HashRefreshToken(string token);
}

public sealed record EmailMessage(string To, string ToName, string Subject, string TextBody);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
