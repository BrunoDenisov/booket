using System.Net;
using System.Net.Http.Json;
using System.Text;
using Booket.Api.Endpoints;
using Booket.Api.IntegrationTests.Infrastructure;
using Booket.Application.Features.Auth;

namespace Booket.Api.IntegrationTests;

public sealed class AuthAndSecurityTests(BooketApiFactory factory) : ApiTestBase(factory)
{
    [Fact]
    public async Task Login_returns_tokens_and_me_returns_the_user()
    {
        await RegisterCustomerAsync();

        var login = await ReadAsync<AuthResponse>(await Anonymous().PostAsJsonAsync("/api/v1/auth/login",
            new LoginCommand("CASEY@example.test", "Passw0rd!"), Json));

        var me = await ReadAsync<UserResponse>(await As(login.AccessToken).GetAsync("/api/v1/auth/me"));
        me.Email.ShouldBe("casey@example.test");
        me.Role.ShouldBe(Domain.Users.UserRole.Customer);
    }

    [Fact]
    public async Task Wrong_password_is_unauthorized()
    {
        await RegisterCustomerAsync();

        var response = await Anonymous().PostAsJsonAsync("/api/v1/auth/login", new LoginCommand("casey@example.test", "nope"), Json);

        (await ReadProblemAsync(response, HttpStatusCode.Unauthorized)).GetProperty("code").GetString().ShouldBe("auth.invalid_credentials");
    }

    [Fact]
    public async Task Refresh_tokens_rotate_and_reuse_revokes_the_session()
    {
        var auth = await RegisterCustomerAsync();
        var client = Anonymous();

        var rotated = await ReadAsync<AuthResponse>(await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshCommand(auth.RefreshToken), Json));
        rotated.RefreshToken.ShouldNotBe(auth.RefreshToken);

        // Replaying the old token looks like theft: it fails and kills the newer token too.
        (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshCommand(auth.RefreshToken), Json)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshCommand(rotated.RefreshToken), Json)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Expired_access_token_is_rejected()
    {
        var auth = await RegisterCustomerAsync();

        Factory.Clock.Advance(TimeSpan.FromMinutes(16));

        (await As(auth.AccessToken).GetAsync("/api/v1/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Booking_requires_authentication_and_customer_role()
    {
        var shop = await CreateShopAsync();

        var anonymous = await BookAsync(Anonymous(), shop, London(Tuesday, 9));
        await ReadProblemAsync(anonymous, HttpStatusCode.Unauthorized);

        var asOwner = await BookAsync(As(shop.OwnerToken), shop, London(Tuesday, 9));
        await ReadProblemAsync(asOwner, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Owner_cannot_manage_another_business()
    {
        var mine = await CreateShopAsync("mine");
        var theirs = await CreateShopAsync("theirs");

        var response = await As(mine.OwnerToken).PostAsJsonAsync(
            $"/api/v1/businesses/{theirs.BusinessId}/services",
            new CreateServiceRequest("Sneaky", null, 30, 0, 1m, "GBP"), Json);

        await ReadProblemAsync(response, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Customers_cannot_see_or_cancel_each_others_bookings()
    {
        var shop = await CreateShopAsync();
        var alice = As((await RegisterCustomerAsync("alice@example.test")).AccessToken);
        var mallory = As((await RegisterCustomerAsync("mallory@example.test")).AccessToken);
        var booking = await BookedAsync(await BookAsync(alice, shop, London(Tuesday, 9)));

        await ReadProblemAsync(await mallory.GetAsync($"/api/v1/bookings/{booking.Id}"), HttpStatusCode.NotFound);
        await ReadProblemAsync(await mallory.PostAsync($"/api/v1/bookings/{booking.Id}/cancel", null), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Staff_of_another_business_cannot_see_bookings()
    {
        var shop = await CreateShopAsync("shop-a");
        var other = await CreateShopAsync("shop-b");
        var customer = As((await RegisterCustomerAsync()).AccessToken);
        var booking = await BookedAsync(await BookAsync(customer, shop, London(Tuesday, 9)));

        await ReadProblemAsync(await As(other.OwnerToken).GetAsync($"/api/v1/bookings/{booking.Id}"), HttpStatusCode.NotFound);
        (await As(shop.OwnerToken).GetAsync($"/api/v1/bookings/{booking.Id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}

public sealed class ValidationAndErrorTests(BooketApiFactory factory) : ApiTestBase(factory)
{
    [Fact]
    public async Task Invalid_registration_returns_validation_problem_with_field_errors()
    {
        var response = await Anonymous().PostAsJsonAsync("/api/v1/auth/register-business", new
        {
            email = "not-an-email",
            password = "short",
            fullName = "",
            businessName = "X",
            businessSlug = "Not A Slug",
            timeZoneId = "Mars/Olympus_Mons",
        }, Json);

        var problem = await ReadProblemAsync(response, HttpStatusCode.BadRequest);
        var errors = problem.GetProperty("errors");
        foreach (var field in new[] { "email", "password", "fullName", "businessSlug", "timeZoneId" })
        {
            errors.TryGetProperty(field, out _).ShouldBeTrue($"expected a validation error for '{field}'");
        }
    }

    [Fact]
    public async Task Availability_range_is_limited()
    {
        var shop = await CreateShopAsync();

        var response = await Anonymous().GetAsync(
            $"/api/v1/businesses/{shop.BusinessId}/availability?serviceId={shop.ServiceId}&from=2026-06-01&to=2026-08-01");

        (await ReadProblemAsync(response, HttpStatusCode.BadRequest)).GetProperty("errors").TryGetProperty("to", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Malformed_json_returns_problem_details()
    {
        var response = await Anonymous().PostAsync("/api/v1/auth/login", new StringContent("{ not json", Encoding.UTF8, "application/json"));

        await ReadProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Unknown_business_returns_not_found_problem()
    {
        var response = await Anonymous().GetAsync($"/api/v1/businesses/{Guid.NewGuid()}/availability?serviceId={Guid.NewGuid()}&from=2026-06-02");

        (await ReadProblemAsync(response, HttpStatusCode.NotFound)).GetProperty("code").GetString().ShouldBe("business.not_found");
    }

    [Fact]
    public async Task OpenApi_document_is_published_with_bearer_scheme()
    {
        var document = await Anonymous().GetStringAsync("/openapi/v1.json");

        document.ShouldContain("\"Bearer\"");
        document.ShouldContain("/api/v1/businesses/{businessId}/availability");
    }

    [Fact]
    public async Task Health_endpoints_report_healthy()
    {
        (await Anonymous().GetAsync("/health/live")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Anonymous().GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
