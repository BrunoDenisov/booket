using Booket.Application.Abstractions;
using Booket.Application.Common;
using Booket.Domain.Businesses;
using Booket.Domain.Common;
using Booket.Domain.Users;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Booket.Application.Features.Auth;

public sealed record RegisterCustomerCommand(string Email, string Password, string FullName, string? Phone);

public sealed class RegisterCustomerValidator : AbstractValidator<RegisterCustomerCommand>
{
    public RegisterCustomerValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Password).Password();
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Phone).MaximumLength(32);
    }
}

internal sealed class RegisterCustomerHandler(
    IAppDbContext db,
    IPasswordHasher hasher,
    TokenIssuer issuer,
    TimeProvider clock) : IHandler<RegisterCustomerCommand, AuthResponse>
{
    public async Task<Result<AuthResponse>> HandleAsync(RegisterCustomerCommand request, CancellationToken cancellationToken)
    {
        if (await issuer.EmailExistsAsync(request.Email, cancellationToken))
        {
            return AuthErrors.EmailTaken;
        }

        var user = User.Create(request.Email, hasher.Hash(request.Password), request.FullName, request.Phone, UserRole.Customer, null, clock.GetUtcNow());
        db.Users.Add(user);
        var response = issuer.Issue(user);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            return AuthErrors.EmailTaken;
        }

        return response;
    }
}

/// <summary>Self-service onboarding: creates a business and its owner account in one step.</summary>
public sealed record RegisterBusinessCommand(
    string Email,
    string Password,
    string FullName,
    string BusinessName,
    string BusinessSlug,
    string TimeZoneId);

public sealed class RegisterBusinessValidator : AbstractValidator<RegisterBusinessCommand>
{
    public RegisterBusinessValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Password).Password();
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.BusinessName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.BusinessSlug).Slug();
        RuleFor(x => x.TimeZoneId).IanaTimeZone();
    }
}

internal sealed class RegisterBusinessHandler(
    IAppDbContext db,
    IPasswordHasher hasher,
    TokenIssuer issuer,
    TimeProvider clock) : IHandler<RegisterBusinessCommand, AuthResponse>
{
    public static readonly Error SlugTaken = Error.Conflict("business.slug_taken", "A business with this slug already exists.");

    public async Task<Result<AuthResponse>> HandleAsync(RegisterBusinessCommand request, CancellationToken cancellationToken)
    {
        if (await issuer.EmailExistsAsync(request.Email, cancellationToken))
        {
            return AuthErrors.EmailTaken;
        }

        var slug = request.BusinessSlug.ToLowerInvariant();
        if (await db.Businesses.AnyAsync(b => b.Slug == slug, cancellationToken))
        {
            return SlugTaken;
        }

        var now = clock.GetUtcNow();
        var business = Business.Create(request.BusinessName, slug, request.TimeZoneId, now);
        var owner = User.Create(request.Email, hasher.Hash(request.Password), request.FullName, null, UserRole.Owner, business.Id, now);
        db.Businesses.Add(business);
        db.Users.Add(owner);
        var response = issuer.Issue(owner);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException ex)
        {
            return ex.ConstraintName?.Contains("slug", StringComparison.Ordinal) == true ? SlugTaken : AuthErrors.EmailTaken;
        }

        return response;
    }
}
