using System.Security.Claims;
using Booket.Application.Abstractions;
using Booket.Domain.Users;
using Booket.Infrastructure.Auth;
using Microsoft.AspNetCore.Authorization;

namespace Booket.Api.Auth;

public static class Policies
{
    /// <summary>Owner of the business in the route (<c>{businessId}</c>), or an admin.</summary>
    public const string BusinessOwner = nameof(BusinessOwner);

    /// <summary>Owner or staff of the business in the route, or an admin.</summary>
    public const string BusinessMember = nameof(BusinessMember);

    public const string Customer = nameof(Customer);
    public const string Admin = nameof(Admin);
}

/// <summary>Requires the caller to belong to the business identified by the <c>businessId</c> route value.</summary>
public sealed record BusinessAccessRequirement(bool OwnerOnly) : IAuthorizationRequirement;

internal sealed class BusinessAccessHandler : AuthorizationHandler<BusinessAccessRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, BusinessAccessRequirement requirement)
    {
        if (context.Resource is not HttpContext http
            || !Guid.TryParse(http.GetRouteValue("businessId")?.ToString(), out var routeBusinessId))
        {
            return Task.CompletedTask;
        }

        var role = context.User.FindFirstValue(BooketClaims.Role);
        var businessClaim = context.User.FindFirstValue(BooketClaims.BusinessId);
        var sameBusiness = Guid.TryParse(businessClaim, out var claimBusinessId) && claimBusinessId == routeBusinessId;

        var allowed = role switch
        {
            nameof(UserRole.Admin) => true,
            nameof(UserRole.Owner) => sameBusiness,
            nameof(UserRole.Staff) => sameBusiness && !requirement.OwnerOnly,
            _ => false,
        };

        if (allowed)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary><see cref="ICurrentUser"/> backed by the JWT on the current request.</summary>
internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public Guid? UserId => Guid.TryParse(Principal?.FindFirstValue(BooketClaims.Subject), out var id) ? id : null;

    public UserRole? Role => Enum.TryParse<UserRole>(Principal?.FindFirstValue(BooketClaims.Role), out var role) ? role : null;

    public Guid? BusinessId => Guid.TryParse(Principal?.FindFirstValue(BooketClaims.BusinessId), out var id) ? id : null;
}

internal static class AuthorizationSetup
{
    public static IServiceCollection AddBooketAuthorization(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddSingleton<IAuthorizationHandler, BusinessAccessHandler>();

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.BusinessOwner, p => p.RequireAuthenticatedUser().AddRequirements(new BusinessAccessRequirement(OwnerOnly: true)))
            .AddPolicy(Policies.BusinessMember, p => p.RequireAuthenticatedUser().AddRequirements(new BusinessAccessRequirement(OwnerOnly: false)))
            .AddPolicy(Policies.Customer, p => p.RequireRole(nameof(UserRole.Customer)))
            .AddPolicy(Policies.Admin, p => p.RequireRole(nameof(UserRole.Admin)));

        return services;
    }
}
