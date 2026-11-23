using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Booket.Api.Endpoints;
using Booket.Application.Features.Auth;
using Booket.Application.Features.Availability;
using Booket.Application.Features.Bookings;
using Booket.Application.Features.Services;
using Booket.Application.Features.Staff;
using Booket.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Booket.Api.IntegrationTests.Infrastructure;

[Collection(ApiCollection.Name)]
public abstract class ApiTestBase(BooketApiFactory factory) : IAsyncLifetime
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    protected BooketApiFactory Factory { get; } = factory;

    /// <summary>Tuesday 2 June 2026 (London is UTC+1).</summary>
    protected static readonly DateOnly Tuesday = new(2026, 6, 2);

    public async ValueTask InitializeAsync() => await Factory.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected HttpClient Anonymous() => Factory.CreateClient();

    protected HttpClient As(string accessToken)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    protected async Task<T> ScopedAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    protected static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        if (response.StatusCode != expected)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new ShouldAssertException($"Expected {(int)expected} but got {(int)response.StatusCode}: {body}");
        }

        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    protected static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        response.StatusCode.ShouldBe(expected, await response.Content.ReadAsStringAsync());
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        problem.GetProperty("status").GetInt32().ShouldBe((int)expected);
        problem.TryGetProperty("traceId", out _).ShouldBeTrue();
        return problem;
    }

    // ---- Scenario builders -------------------------------------------------------------------

    protected sealed record Shop(Guid BusinessId, string OwnerToken, Guid ServiceId, Guid StaffId, Guid SecondStaffId);

    /// <summary>
    /// A London barbershop with a 30 min haircut (+10 min buffer) performed by two staff members,
    /// both working Monday-Friday 09:00-17:00.
    /// </summary>
    protected async Task<Shop> CreateShopAsync(string slug = "test-barbers")
    {
        var client = Anonymous();
        var owner = await ReadAsync<AuthResponse>(await client.PostAsJsonAsync("/api/v1/auth/register-business", new
        {
            email = $"owner@{slug}.test",
            password = "Passw0rd!",
            fullName = "Test Owner",
            businessName = "Test Barbers",
            businessSlug = slug,
            timeZoneId = "Europe/London",
        }, Json));

        var businessId = owner.User.BusinessId!.Value;
        var ownerClient = As(owner.AccessToken);

        var service = await ReadAsync<ServiceResponse>(
            await ownerClient.PostAsJsonAsync($"/api/v1/businesses/{businessId}/services",
                new CreateServiceRequest("Haircut", null, 30, 10, 25m, "GBP"), Json),
            HttpStatusCode.Created);

        var staffIds = new List<Guid>();
        foreach (var name in new[] { "Alex", "Sam" })
        {
            var staff = await ReadAsync<StaffResponse>(
                await ownerClient.PostAsJsonAsync($"/api/v1/businesses/{businessId}/staff",
                    new CreateStaffRequest(name, [service.Id], null), Json),
                HttpStatusCode.Created);

            var hours = Enum.GetValues<DayOfWeek>()
                .Where(d => d is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
                .Select(d => new WorkingHoursDto(d, new TimeOnly(9, 0), new TimeOnly(17, 0)))
                .ToList();
            await ReadAsync<StaffResponse>(await ownerClient.PutAsJsonAsync(
                $"/api/v1/businesses/{businessId}/staff/{staff.Id}/working-hours", new SetWorkingHoursRequest(hours), Json));

            staffIds.Add(staff.Id);
        }

        return new Shop(businessId, owner.AccessToken, service.Id, staffIds[0], staffIds[1]);
    }

    protected async Task<AuthResponse> RegisterCustomerAsync(string email = "casey@example.test") =>
        await ReadAsync<AuthResponse>(await Anonymous().PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = "Passw0rd!",
            fullName = "Casey Customer",
        }, Json));

    protected async Task<string> LoginAsync(string email, string password = "Passw0rd!") =>
        (await ReadAsync<AuthResponse>(await Anonymous().PostAsJsonAsync("/api/v1/auth/login", new LoginCommand(email, password), Json))).AccessToken;

    protected async Task<AvailabilityResponse> GetAvailabilityAsync(Shop shop, DateOnly date, Guid? staffId = null)
    {
        var url = $"/api/v1/businesses/{shop.BusinessId}/availability?serviceId={shop.ServiceId}&from={date:yyyy-MM-dd}";
        if (staffId is not null)
        {
            url += $"&staffId={staffId}";
        }

        return await ReadAsync<AvailabilityResponse>(await Anonymous().GetAsync(url));
    }

    protected static Task<HttpResponseMessage> BookAsync(HttpClient customer, Shop shop, DateTimeOffset start, Guid? staffId = null, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/businesses/{shop.BusinessId}/bookings")
        {
            Content = JsonContent.Create(new CreateBookingRequest(shop.ServiceId, staffId, start, null), options: Json),
        };

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return customer.SendAsync(request);
    }

    /// <summary>Business-local London time on a June 2026 date (BST, UTC+1).</summary>
    protected static DateTimeOffset London(DateOnly date, int hour, int minute = 0) =>
        new(date.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.FromHours(1));

    protected static async Task<BookingResponse> BookedAsync(HttpResponseMessage response) =>
        await ReadAsync<BookingResponse>(response, HttpStatusCode.Created);
}
