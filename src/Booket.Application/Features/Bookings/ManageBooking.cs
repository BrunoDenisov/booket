using Booket.Application.Abstractions;
using Booket.Application.Common;
using Booket.Application.Features.Availability;
using Booket.Domain.Bookings;
using Booket.Domain.Businesses;
using Booket.Domain.Common;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Booket.Application.Features.Bookings;

internal static class BookingAccess
{
    public static readonly Error ConcurrentUpdate =
        Error.Conflict("booking.concurrent_update", "The booking was changed by someone else. Reload and try again.");

    /// <summary>
    /// Loads a booking the caller may act on: their own booking, or any booking of a business they
    /// belong to. Anything else is reported as not found so ids from other tenants are not revealed.
    /// </summary>
    public static async Task<(Booking? Booking, Business? Business, Actor Actor)> LoadForCallerAsync(
        this IAppDbContext db, ICurrentUser user, Guid bookingId, CancellationToken ct)
    {
        var booking = await db.Bookings.SingleOrDefaultAsync(b => b.Id == bookingId, ct);
        if (booking is null)
        {
            return (null, null, Actor.Customer);
        }

        var isMember = user.IsMemberOf(booking.BusinessId);
        if (!isMember && booking.CustomerId != user.UserId)
        {
            return (null, null, Actor.Customer);
        }

        var business = await db.Businesses.AsNoTracking().SingleAsync(b => b.Id == booking.BusinessId, ct);
        return (booking, business, isMember ? Actor.Business : Actor.Customer);
    }
}

// ---- Get ------------------------------------------------------------------------------------

public sealed record GetBookingQuery(Guid BookingId);

internal sealed class GetBookingHandler(IAppDbContext db, ICurrentUser currentUser) : IHandler<GetBookingQuery, BookingResponse>
{
    public async Task<Result<BookingResponse>> HandleAsync(GetBookingQuery request, CancellationToken cancellationToken)
    {
        var (booking, _, _) = await db.LoadForCallerAsync(currentUser, request.BookingId, cancellationToken);
        return booking is null ? BookingErrors.NotFound : await db.GetResponseAsync(booking.Id, cancellationToken);
    }
}

// ---- Cancel ---------------------------------------------------------------------------------

public sealed record CancelBookingCommand(Guid BookingId, string? Reason);

public sealed class CancelBookingValidator : AbstractValidator<CancelBookingCommand>
{
    public CancelBookingValidator() => RuleFor(x => x.Reason).MaximumLength(500);
}

internal sealed class CancelBookingHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    BookingNotifications notifications,
    TimeProvider clock) : IHandler<CancelBookingCommand, BookingResponse>
{
    public async Task<Result<BookingResponse>> HandleAsync(CancelBookingCommand request, CancellationToken cancellationToken)
    {
        var (booking, business, actor) = await db.LoadForCallerAsync(currentUser, request.BookingId, cancellationToken);
        if (booking is null)
        {
            return BookingErrors.NotFound;
        }

        var result = booking.Cancel(clock.GetUtcNow(), business!.Policy.CancellationCutoff, actor, request.Reason);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        await notifications.BookingCancelledAsync(booking, cancellationToken);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return BookingAccess.ConcurrentUpdate;
        }

        BooketTelemetry.BookingsCancelled.Add(1, new KeyValuePair<string, object?>("actor", actor.ToString()));
        return await db.GetResponseAsync(booking.Id, cancellationToken);
    }
}

// ---- Reschedule -----------------------------------------------------------------------------

/// <param name="StaffId">Move to this staff member. When omitted, the current staff member is preferred, then anyone available.</param>
public sealed record RescheduleBookingCommand(Guid BookingId, DateTimeOffset NewStart, Guid? StaffId);

public sealed class RescheduleBookingValidator : AbstractValidator<RescheduleBookingCommand>
{
    public RescheduleBookingValidator() => RuleFor(x => x.NewStart).NotEmpty();
}

internal sealed class RescheduleBookingHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    AvailabilityService availability,
    BookingNotifications notifications,
    TimeProvider clock) : IHandler<RescheduleBookingCommand, BookingResponse>
{
    public async Task<Result<BookingResponse>> HandleAsync(RescheduleBookingCommand request, CancellationToken cancellationToken)
    {
        var (booking, business, actor) = await db.LoadForCallerAsync(currentUser, request.BookingId, cancellationToken);
        if (booking is null)
        {
            return BookingErrors.NotFound;
        }

        if (booking.Status != BookingStatus.Confirmed)
        {
            return BookingErrors.NotActive(booking.Status);
        }

        var service = await db.Services.AsNoTracking().SingleAsync(s => s.Id == booking.ServiceId, cancellationToken);
        var newStart = request.NewStart.ToUniversalTime();

        // The booking being moved must not block its own new slot.
        var candidates = await availability.GetStaffAvailableAtAsync(business!, service, newStart, request.StaffId, booking.Id, cancellationToken);
        if (candidates.Count == 0)
        {
            return BookingErrors.SlotUnavailable;
        }

        var staffId = candidates.Contains(booking.StaffId) ? booking.StaffId : candidates[0];
        var result = booking.Reschedule(staffId, newStart, service.Duration, service.BufferAfter, clock.GetUtcNow(), business!.Policy.CancellationCutoff, actor);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        await notifications.BookingRescheduledAsync(booking, cancellationToken);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (ExclusionConstraintViolationException)
        {
            BooketTelemetry.BookingConflicts.Add(1);
            return BookingErrors.SlotUnavailable;
        }
        catch (DbUpdateConcurrencyException)
        {
            return BookingAccess.ConcurrentUpdate;
        }

        BooketTelemetry.BookingsRescheduled.Add(1);
        return await db.GetResponseAsync(booking.Id, cancellationToken);
    }
}

// ---- Complete / no-show (business only) -----------------------------------------------------

public enum BookingOutcome
{
    Completed,
    NoShow,
}

public sealed record RecordBookingOutcomeCommand(Guid BookingId, BookingOutcome Outcome);

internal sealed class RecordBookingOutcomeHandler(IAppDbContext db, ICurrentUser currentUser, TimeProvider clock)
    : IHandler<RecordBookingOutcomeCommand, BookingResponse>
{
    public async Task<Result<BookingResponse>> HandleAsync(RecordBookingOutcomeCommand request, CancellationToken cancellationToken)
    {
        var (booking, _, actor) = await db.LoadForCallerAsync(currentUser, request.BookingId, cancellationToken);
        if (booking is null)
        {
            return BookingErrors.NotFound;
        }

        if (actor != Actor.Business)
        {
            return Error.Forbidden("booking.business_only", "Only the business can record the outcome of a booking.");
        }

        var now = clock.GetUtcNow();
        var result = request.Outcome == BookingOutcome.Completed ? booking.Complete(now) : booking.MarkNoShow(now);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return BookingAccess.ConcurrentUpdate;
        }

        return await db.GetResponseAsync(booking.Id, cancellationToken);
    }
}
