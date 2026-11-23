using Booket.Application.Abstractions;
using Booket.Application.Common;
using Booket.Domain.Common;
using Booket.Domain.Users;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Booket.Application.Features.Auth;

public sealed record LoginCommand(string Email, string Password);

public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty();
        RuleFor(x => x.Password).NotEmpty();
    }
}

internal sealed class LoginHandler(IAppDbContext db, IPasswordHasher hasher, TokenIssuer issuer)
    : IHandler<LoginCommand, AuthResponse>
{
    public async Task<Result<AuthResponse>> HandleAsync(LoginCommand request, CancellationToken cancellationToken)
    {
        var email = User.NormalizeEmail(request.Email);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == email, cancellationToken);

        if (user is null)
        {
            // Spend the same time hashing as a real check, so response timing does not reveal which emails exist.
            _ = hasher.Hash(request.Password);
            return AuthErrors.InvalidCredentials;
        }

        if (!hasher.Verify(user.PasswordHash, request.Password))
        {
            return AuthErrors.InvalidCredentials;
        }

        var response = issuer.Issue(user);
        await db.SaveChangesAsync(cancellationToken);
        return response;
    }
}

public sealed record RefreshCommand(string RefreshToken);

public sealed class RefreshValidator : AbstractValidator<RefreshCommand>
{
    public RefreshValidator() => RuleFor(x => x.RefreshToken).NotEmpty();
}

/// <summary>
/// Rotates a refresh token. If a token that was already rotated is presented again, it has
/// probably been stolen, so every token in its family is revoked.
/// </summary>
internal sealed class RefreshHandler(IAppDbContext db, ITokenService tokens, TokenIssuer issuer, TimeProvider clock)
    : IHandler<RefreshCommand, AuthResponse>
{
    public async Task<Result<AuthResponse>> HandleAsync(RefreshCommand request, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var hash = tokens.HashRefreshToken(request.RefreshToken);
        var existing = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (existing is null)
        {
            return AuthErrors.InvalidRefreshToken;
        }

        if (existing.RevokedAtUtc is not null)
        {
            await RevokeFamilyAsync(existing.FamilyId, now, cancellationToken);
            return AuthErrors.InvalidRefreshToken;
        }

        if (!existing.IsActive(now))
        {
            return AuthErrors.InvalidRefreshToken;
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == existing.UserId, cancellationToken);
        if (user is null)
        {
            return AuthErrors.InvalidRefreshToken;
        }

        var response = issuer.Issue(user, existing.FamilyId);
        existing.Revoke(now, issuer.LastIssued!.Id);
        await db.SaveChangesAsync(cancellationToken);
        return response;
    }

    private async Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken ct)
    {
        var family = await db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAtUtc == null).ToListAsync(ct);
        foreach (var token in family)
        {
            token.Revoke(now);
        }

        await db.SaveChangesAsync(ct);
    }
}

public sealed record LogoutCommand(string RefreshToken);

public sealed class LogoutValidator : AbstractValidator<LogoutCommand>
{
    public LogoutValidator() => RuleFor(x => x.RefreshToken).NotEmpty();
}

/// <summary>Revokes the presented refresh token's whole family (i.e. that login session).</summary>
internal sealed class LogoutHandler(IAppDbContext db, ITokenService tokens, ICurrentUser currentUser, TimeProvider clock)
    : IHandler<LogoutCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(LogoutCommand request, CancellationToken cancellationToken)
    {
        var hash = tokens.HashRefreshToken(request.RefreshToken);
        var existing = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash && t.UserId == currentUser.UserId, cancellationToken);

        if (existing is not null)
        {
            var now = clock.GetUtcNow();
            var family = await db.RefreshTokens.Where(t => t.FamilyId == existing.FamilyId && t.RevokedAtUtc == null).ToListAsync(cancellationToken);
            foreach (var token in family)
            {
                token.Revoke(now);
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        // Logout is idempotent: an unknown token is not an error.
        return Unit.Value;
    }
}

public sealed record GetCurrentUserQuery;

internal sealed class GetCurrentUserHandler(IAppDbContext db, ICurrentUser currentUser) : IHandler<GetCurrentUserQuery, UserResponse>
{
    public async Task<Result<UserResponse>> HandleAsync(GetCurrentUserQuery request, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == currentUser.UserId, cancellationToken);
        return user is null
            ? Error.NotFound("user.not_found", "User was not found.")
            : UserResponse.From(user);
    }
}
