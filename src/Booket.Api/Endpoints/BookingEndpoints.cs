using Booket.Api.Auth;
using Booket.Api.Http;
using Booket.Application.Common;
using Booket.Application.Features.Availability;
using Booket.Application.Features.Bookings;
using Booket.Domain.Bookings;
using Microsoft.AspNetCore.Mvc;

namespace Booket.Api.Endpoints;

public sealed record CreateBookingRequest(Guid ServiceId, Guid? StaffId, DateTimeOffset Start, string? Notes);

public sealed record CancelBookingRequest(string? Reason);

public sealed record RescheduleBookingRequest(DateTimeOffset NewStart, Guid? StaffId);

internal static class BookingEndpoints
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    public static IEndpointRouteBuilder MapBookingEndpoints(this IEndpointRouteBuilder app)
    {
        var business = app.MapGroup("/api/v1/businesses/{businessId:guid}");

        business.MapGet("/availability", async (
                    Guid businessId,
                    Guid serviceId,
                    DateOnly from,
                    DateOnly? to,
                    Guid? staffId,
                    IHandler<GetAvailabilityQuery, AvailabilityResponse> handler,
                    CancellationToken ct) =>
                (await handler.HandleAsync(new GetAvailabilityQuery(businessId, serviceId, from, to, staffId), ct)).ToOk())
            .WithTags("Availability")
            .WithSummary("Open slots for a service")
            .WithDescription($"Dates are business-local (yyyy-MM-dd). The range may span at most {GetAvailabilityValidator.MaxRangeDays} days. " +
                             "Slots honour working hours, time off, existing bookings, service duration + buffer, lead time and booking horizon.")
            .Produces<AvailabilityResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        business.MapPost("/bookings", async (
                    Guid businessId,
                    CreateBookingRequest body,
                    [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
                    IHandler<CreateBookingCommand, CreateBookingResult> handler,
                    HttpContext http,
                    CancellationToken ct) =>
                {
                    var result = await handler.HandleAsync(
                        new CreateBookingCommand(businessId, body.ServiceId, body.StaffId, body.Start, body.Notes, idempotencyKey), ct);

                    if (result.IsSuccess && result.Value.Replayed)
                    {
                        http.Response.Headers["Idempotent-Replayed"] = "true";
                    }

                    return result.IsSuccess
                        ? TypedResults.Created($"/api/v1/bookings/{result.Value.Booking.Id}", result.Value.Booking)
                        : result.Error!.ToProblem();
                })
            .RequireAuthorization(Policies.Customer)
            .WithTags("Bookings")
            .WithSummary("Book an appointment")
            .WithDescription("Omit staffId to book with any available staff member. Send an Idempotency-Key header to make retries safe.")
            .Produces<BookingResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        business.MapGet("/bookings", async (
                    Guid businessId,
                    DateOnly from,
                    DateOnly to,
                    Guid? staffId,
                    BookingStatus? status,
                    IHandler<ListBusinessBookingsQuery, IReadOnlyList<BookingResponse>> handler,
                    CancellationToken ct) =>
                (await handler.HandleAsync(new ListBusinessBookingsQuery(businessId, from, to, staffId, status), ct)).ToOk())
            .RequireAuthorization(Policies.BusinessMember)
            .WithTags("Bookings")
            .WithSummary("Business calendar: bookings between two business-local dates")
            .Produces<IReadOnlyList<BookingResponse>>();

        var bookings = app.MapGroup("/api/v1/bookings").WithTags("Bookings").RequireAuthorization();

        bookings.MapGet("/me", async (bool? upcoming, BookingStatus? status, IHandler<ListMyBookingsQuery, IReadOnlyList<BookingResponse>> handler, CancellationToken ct) =>
                (await handler.HandleAsync(new ListMyBookingsQuery(upcoming, status), ct)).ToOk())
            .WithSummary("The current customer's bookings")
            .Produces<IReadOnlyList<BookingResponse>>();

        bookings.MapGet("/{bookingId:guid}", async (Guid bookingId, IHandler<GetBookingQuery, BookingResponse> handler, CancellationToken ct) =>
                (await handler.HandleAsync(new GetBookingQuery(bookingId), ct)).ToOk())
            .WithSummary("Get a booking (own bookings, or any booking of your business)")
            .Produces<BookingResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        bookings.MapPost("/{bookingId:guid}/cancel", async (Guid bookingId, CancelBookingRequest? body, IHandler<CancelBookingCommand, BookingResponse> handler, CancellationToken ct) =>
                (await handler.HandleAsync(new CancelBookingCommand(bookingId, body?.Reason), ct)).ToOk())
            .WithSummary("Cancel a booking")
            .WithDescription("Customers must cancel before the business's cancellation cutoff; staff and owners can cancel at any time.")
            .Produces<BookingResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        bookings.MapPost("/{bookingId:guid}/reschedule", async (Guid bookingId, RescheduleBookingRequest body, IHandler<RescheduleBookingCommand, BookingResponse> handler, CancellationToken ct) =>
                (await handler.HandleAsync(new RescheduleBookingCommand(bookingId, body.NewStart, body.StaffId), ct)).ToOk())
            .WithSummary("Move a booking to another slot")
            .WithDescription("The new slot must be available. The current staff member is kept when possible. Reminders are re-scheduled.")
            .Produces<BookingResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        bookings.MapPost("/{bookingId:guid}/complete", async (Guid bookingId, IHandler<RecordBookingOutcomeCommand, BookingResponse> handler, CancellationToken ct) =>
                (await handler.HandleAsync(new RecordBookingOutcomeCommand(bookingId, BookingOutcome.Completed), ct)).ToOk())
            .WithSummary("Mark a booking as completed (business only)")
            .Produces<BookingResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        bookings.MapPost("/{bookingId:guid}/no-show", async (Guid bookingId, IHandler<RecordBookingOutcomeCommand, BookingResponse> handler, CancellationToken ct) =>
                (await handler.HandleAsync(new RecordBookingOutcomeCommand(bookingId, BookingOutcome.NoShow), ct)).ToOk())
            .WithSummary("Mark a booking as a no-show (business only)")
            .Produces<BookingResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return app;
    }
}
