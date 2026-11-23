using Booket.Api.Http;
using Booket.Application.Common;
using Booket.Application.Features.Auth;

namespace Booket.Api.Endpoints;

internal static class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Auth");

        group.MapPost("/register", async (RegisterCustomerCommand body, IHandler<RegisterCustomerCommand, AuthResponse> handler, CancellationToken ct) =>
                (await handler.HandleAsync(body, ct)).ToOk())
            .RequireRateLimiting(RateLimitPolicy)
            .WithSummary("Register a customer account")
            .Produces<AuthResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/register-business", async (RegisterBusinessCommand body, IHandler<RegisterBusinessCommand, AuthResponse> handler, CancellationToken ct) =>
                (await handler.HandleAsync(body, ct)).ToOk())
            .RequireRateLimiting(RateLimitPolicy)
            .WithSummary("Onboard a business and its owner account")
            .Produces<AuthResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/login", async (LoginCommand body, IHandler<LoginCommand, AuthResponse> handler, CancellationToken ct) =>
                (await handler.HandleAsync(body, ct)).ToOk())
            .RequireRateLimiting(RateLimitPolicy)
            .WithSummary("Exchange email and password for an access and refresh token")
            .Produces<AuthResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("/refresh", async (RefreshCommand body, IHandler<RefreshCommand, AuthResponse> handler, CancellationToken ct) =>
                (await handler.HandleAsync(body, ct)).ToOk())
            .RequireRateLimiting(RateLimitPolicy)
            .WithSummary("Rotate a refresh token")
            .WithDescription("Each refresh token can be used once. Re-using a rotated token revokes the whole session.")
            .Produces<AuthResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("/logout", async (LogoutCommand body, IHandler<LogoutCommand, Unit> handler, CancellationToken ct) =>
                (await handler.HandleAsync(body, ct)).ToNoContent())
            .RequireAuthorization()
            .WithSummary("Revoke the session that owns the given refresh token")
            .Produces(StatusCodes.Status204NoContent);

        group.MapGet("/me", async (IHandler<GetCurrentUserQuery, UserResponse> handler, CancellationToken ct) =>
                (await handler.HandleAsync(new GetCurrentUserQuery(), ct)).ToOk())
            .RequireAuthorization()
            .WithSummary("The current user")
            .Produces<UserResponse>();

        return app;
    }
}
