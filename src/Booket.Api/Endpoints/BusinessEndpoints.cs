using Booket.Api.Auth;
using Booket.Api.Http;
using Booket.Application.Common;
using Booket.Application.Features.Businesses;
using Booket.Application.Features.Services;
using Booket.Application.Features.Staff;

namespace Booket.Api.Endpoints;

public sealed record UpdateBusinessRequest(string Name, string TimeZoneId, BookingPolicyDto Policy);

public sealed record CreateServiceRequest(string Name, string? Description, int DurationMinutes, int BufferMinutes, decimal Price, string Currency);

public sealed record UpdateServiceRequest(string Name, string? Description, int DurationMinutes, int BufferMinutes, decimal Price, string Currency, bool IsActive);

public sealed record CreateStaffRequest(string DisplayName, IReadOnlyList<Guid>? ServiceIds, StaffAccountDto? Account);

public sealed record UpdateStaffRequest(string DisplayName, bool IsActive);

public sealed record SetWorkingHoursRequest(IReadOnlyList<WorkingHoursDto> WorkingHours);

public sealed record SetStaffServicesRequest(IReadOnlyList<Guid> ServiceIds);

public sealed record AddTimeOffRequest(DateTimeOffset StartUtc, DateTimeOffset EndUtc, string? Reason);

internal static class BusinessEndpoints
{
    public static IEndpointRouteBuilder MapBusinessEndpoints(this IEndpointRouteBuilder app)
    {
        MapBusinesses(app.MapGroup("/api/v1/businesses").WithTags("Businesses"));
        MapServices(app.MapGroup("/api/v1/businesses/{businessId:guid}/services").WithTags("Services"));
        MapStaff(app.MapGroup("/api/v1/businesses/{businessId:guid}/staff").WithTags("Staff"));
        return app;
    }

    private static void MapBusinesses(RouteGroupBuilder group)
    {
        group.MapGet("/", async (string? search, int? page, int? pageSize, IHandler<ListBusinessesQuery, PagedResponse<BusinessResponse>> handler, CancellationToken ct) =>
                (await handler.HandleAsync(new ListBusinessesQuery(search, page ?? 1, pageSize ?? 20), ct)).ToOk())
            .WithSummary("Search businesses")
            .Produces<PagedResponse<BusinessResponse>>();

        group.MapGet("/{idOrSlug}", async (string idOrSlug, IHandler<GetBusinessQuery, BusinessResponse> handler, CancellationToken ct) =>
                (await handler.HandleAsync(new GetBusinessQuery(idOrSlug), ct)).ToOk())
            .WithSummary("Get a business by id or slug")
            .Produces<BusinessResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{businessId:guid}", async (Guid businessId, UpdateBusinessRequest body, IHandler<UpdateBusinessCommand, BusinessResponse> handler, CancellationToken ct) =>
                (await handler.HandleAsync(new UpdateBusinessCommand(businessId, body.Name, body.TimeZoneId, body.Policy), ct)).ToOk())
            .RequireAuthorization(Policies.BusinessOwner)
            .WithSummary("Update business details and booking policy")
            .Produces<BusinessResponse>()
            .ProducesValidationProblem();
    }

    private static void MapServices(RouteGroupBuilder group)
    {
        group.MapGet("/", async (Guid businessId, bool? includeInactive, IHandler<ListServicesQuery, IReadOnlyList<ServiceResponse>> handler, CancellationToken ct) =>
                (await handler.HandleAsync(new ListServicesQuery(businessId, includeInactive ?? false), ct)).ToOk())
            .WithSummary("List the services a business offers")
            .Produces<IReadOnlyList<ServiceResponse>>();

        group.MapGet("/{serviceId:guid}", async (Guid businessId, Guid serviceId, IHandler<GetServiceQuery, ServiceResponse> handler, CancellationToken ct) =>
                    (await handler.HandleAsync(new GetServiceQuery(businessId, serviceId), ct)).ToOk())
            .WithSummary("Get a service")
            .Produces<ServiceResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", async (Guid businessId, CreateServiceRequest body, IHandler<CreateServiceCommand, ServiceResponse> handler, CancellationToken ct) =>
                (await handler.HandleAsync(new CreateServiceCommand(businessId, body.Name, body.Description, body.DurationMinutes, body.BufferMinutes, body.Price, body.Currency), ct))
                .ToCreated(s => $"/api/v1/businesses/{businessId}/services/{s.Id}"))
            .RequireAuthorization(Policies.BusinessOwner)
            .WithSummary("Create a service")
            .Produces<ServiceResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        group.MapPut("/{serviceId:guid}", async (Guid businessId, Guid serviceId, UpdateServiceRequest body, IHandler<UpdateServiceCommand, ServiceResponse> handler, CancellationToken ct) =>
                (await handler.HandleAsync(new UpdateServiceCommand(businessId, serviceId, body.Name, body.Description, body.DurationMinutes, body.BufferMinutes, body.Price, body.Currency, body.IsActive), ct)).ToOk())
            .RequireAuthorization(Policies.BusinessOwner)
            .WithSummary("Update a service")
            .Produces<ServiceResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{serviceId:guid}", async (Guid businessId, Guid serviceId, IHandler<DeactivateServiceCommand, Unit> handler, CancellationToken ct) =>
                (await handler.HandleAsync(new DeactivateServiceCommand(businessId, serviceId), ct)).ToNoContent())
            .RequireAuthorization(Policies.BusinessOwner)
            .WithSummary("Deactivate a service (soft delete; booking history is kept)")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static void MapStaff(RouteGroupBuilder group)
    {
        group.MapGet("/", async (Guid businessId, Guid? serviceId, IHandler<ListStaffQuery, IReadOnlyList<StaffResponse>> handler, CancellationToken ct) =>
                (await handler.HandleAsync(new ListStaffQuery(businessId, serviceId), ct)).ToOk())
            .WithSummary("List staff, optionally only those who perform a service")
            .Produces<IReadOnlyList<StaffResponse>>();

        group.MapGet("/{staffId:guid}", async (Guid businessId, Guid staffId, IHandler<GetStaffQuery, StaffResponse> handler, CancellationToken ct) =>
                (await handler.HandleAsync(new GetStaffQuery(businessId, staffId), ct)).ToOk())
            .WithSummary("Get a staff member with their weekly schedule")
            .Produces<StaffResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", async (Guid businessId, CreateStaffRequest body, IHandler<CreateStaffCommand, StaffResponse> handler, CancellationToken ct) =>
                (await handler.HandleAsync(new CreateStaffCommand(businessId, body.DisplayName, body.ServiceIds, body.Account), ct))
                .ToCreated(s => $"/api/v1/businesses/{businessId}/staff/{s.Id}"))
            .RequireAuthorization(Policies.BusinessOwner)
            .WithSummary("Add a staff member, optionally with a login")
            .Produces<StaffResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        group.MapPut("/{staffId:guid}", async (Guid businessId, Guid staffId, UpdateStaffRequest body, IHandler<UpdateStaffCommand, StaffResponse> handler, CancellationToken ct) =>
                (await handler.HandleAsync(new UpdateStaffCommand(businessId, staffId, body.DisplayName, body.IsActive), ct)).ToOk())
            .RequireAuthorization(Policies.BusinessOwner)
            .WithSummary("Rename or (de)activate a staff member")
            .Produces<StaffResponse>();

        group.MapPut("/{staffId:guid}/working-hours", async (Guid businessId, Guid staffId, SetWorkingHoursRequest body, IHandler<SetWorkingHoursCommand, StaffResponse> handler, CancellationToken ct) =>
                (await handler.HandleAsync(new SetWorkingHoursCommand(businessId, staffId, body.WorkingHours), ct)).ToOk())
            .RequireAuthorization(Policies.BusinessOwner)
            .WithSummary("Replace the weekly working hours (business-local times; split shifts allowed)")
            .Produces<StaffResponse>()
            .ProducesValidationProblem();

        group.MapPut("/{staffId:guid}/services", async (Guid businessId, Guid staffId, SetStaffServicesRequest body, IHandler<SetStaffServicesCommand, StaffResponse> handler, CancellationToken ct) =>
                (await handler.HandleAsync(new SetStaffServicesCommand(businessId, staffId, body.ServiceIds), ct)).ToOk())
            .RequireAuthorization(Policies.BusinessOwner)
            .WithSummary("Set which services a staff member performs")
            .Produces<StaffResponse>()
            .ProducesValidationProblem();

        group.MapGet("/{staffId:guid}/time-off", async (Guid businessId, Guid staffId, DateTimeOffset? fromUtc, IHandler<ListTimeOffQuery, IReadOnlyList<TimeOffResponse>> handler, CancellationToken ct) =>
                (await handler.HandleAsync(new ListTimeOffQuery(businessId, staffId, fromUtc), ct)).ToOk())
            .RequireAuthorization(Policies.BusinessMember)
            .WithSummary("List current and upcoming time off")
            .Produces<IReadOnlyList<TimeOffResponse>>();

        group.MapPost("/{staffId:guid}/time-off", async (Guid businessId, Guid staffId, AddTimeOffRequest body, IHandler<AddTimeOffCommand, TimeOffResponse> handler, CancellationToken ct) =>
                (await handler.HandleAsync(new AddTimeOffCommand(businessId, staffId, body.StartUtc, body.EndUtc, body.Reason), ct))
                .ToCreated(t => $"/api/v1/businesses/{businessId}/staff/{staffId}/time-off/{t.Id}"))
            .RequireAuthorization(Policies.BusinessOwner)
            .WithSummary("Block time off")
            .WithDescription("Existing bookings in the period are not cancelled; their ids are returned in conflictingBookingIds.")
            .Produces<TimeOffResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        group.MapDelete("/{staffId:guid}/time-off/{timeOffId:guid}", async (Guid businessId, Guid staffId, Guid timeOffId, IHandler<DeleteTimeOffCommand, Unit> handler, CancellationToken ct) =>
                (await handler.HandleAsync(new DeleteTimeOffCommand(businessId, staffId, timeOffId), ct)).ToNoContent())
            .RequireAuthorization(Policies.BusinessOwner)
            .WithSummary("Remove time off")
            .Produces(StatusCodes.Status204NoContent);
    }
}
