using System.Net;
using Booket.Api.IntegrationTests.Infrastructure;
using Booket.Application.Features.Services;

namespace Booket.Api.IntegrationTests;

public sealed class ServiceEndpointsTests(BooketApiFactory factory) : ApiTestBase(factory)
{
    private static string ServiceUrl(Guid businessId, Guid serviceId) => $"/api/v1/businesses/{businessId}/services/{serviceId}";

    [Fact]
    public async Task Anonymous_client_can_get_an_active_service()
    {
        var shop = await CreateShopAsync();

        var service = await ReadAsync<ServiceResponse>(await Anonymous().GetAsync(ServiceUrl(shop.BusinessId, shop.ServiceId)));

        service.Id.ShouldBe(shop.ServiceId);
        service.BusinessId.ShouldBe(shop.BusinessId);
        service.IsActive.ShouldBeTrue();
        service.Name.ShouldBe("Haircut");
        service.DurationMinutes.ShouldBe(30);
        service.BufferMinutes.ShouldBe(10);
        service.Price.ShouldBe(25m);
        service.Currency.ShouldBe("GBP");
    }

    [Fact]
    public async Task Inactive_service_is_still_returned_but_marked_inactive()
    {
        var shop = await CreateShopAsync();

        var response = await As(shop.OwnerToken).DeleteAsync(ServiceUrl(shop.BusinessId, shop.ServiceId));
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var service = await ReadAsync<ServiceResponse>(await Anonymous().GetAsync(ServiceUrl(shop.BusinessId, shop.ServiceId)));

        service.IsActive.ShouldBeFalse();
        service.Id.ShouldBe(shop.ServiceId);
    }

    [Fact]
    public async Task Unknown_service_id_returns_not_found()
    {
        var shop = await CreateShopAsync();

        var response = await Anonymous().GetAsync(ServiceUrl(shop.BusinessId, Guid.NewGuid()));
        var problem = await ReadProblemAsync(response, HttpStatusCode.NotFound);

        problem.GetProperty("code").GetString().ShouldBe("service.not_found");
    }

    [Fact]
    public async Task Service_of_another_business_returns_not_found()
    {
        var shop1 = await CreateShopAsync("test-barbers1");
        var shop2 = await CreateShopAsync("test-barbers2");

        var response = await Anonymous().GetAsync(ServiceUrl(shop2.BusinessId, shop1.ServiceId));
        var problem = await ReadProblemAsync(response, HttpStatusCode.NotFound);

        problem.GetProperty("code").GetString().ShouldBe("service.not_found");
    }
}
