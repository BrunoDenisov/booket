using Booket.Domain.Common;
using Booket.Domain.Scheduling;

namespace Booket.Domain.Bookings;

public enum BookingStatus
{
    Confirmed,
    Cancelled,
    Completed,
    NoShow,
}

/// <summary>Who performed an action on a booking; staff/owners bypass customer self-service limits.</summary>
public enum Actor
{
    Customer,
    Business,
}

/// <summary>An appointment of one customer with one staff member for one service.</summary>
public sealed class Booking : Entity
{
    private Booking() { }

    public Guid BusinessId { get; private set; }
    public Guid StaffId { get; private set; }
    public Guid ServiceId { get; private set; }
    public Guid CustomerId { get; private set; }

    /// <summary>When the appointment starts.</summary>
    public DateTimeOffset StartUtc { get; private set; }

    /// <summary>When the appointment ends, as communicated to the customer.</summary>
    public DateTimeOffset EndUtc { get; private set; }

    /// <summary>End of the blocked period, including the service's buffer. Used for overlap checks.</summary>
    public DateTimeOffset BlockedUntilUtc { get; private set; }

    public BookingStatus Status { get; private set; }
    public string? Notes { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? CancelledAtUtc { get; private set; }
    public string? CancellationReason { get; private set; }
    public Actor? CancelledBy { get; private set; }
    public int RescheduleCount { get; private set; }

    /// <summary>Optimistic concurrency token (mapped to PostgreSQL <c>xmin</c>).</summary>
    public uint Version { get; private set; }

    public TimeRange Blocked => new(StartUtc, BlockedUntilUtc);

    public static Booking Create(
        Guid businessId,
        Guid staffId,
        Guid serviceId,
        Guid customerId,
        DateTimeOffset startUtc,
        TimeSpan duration,
        TimeSpan bufferAfter,
        string? notes,
        DateTimeOffset now)
    {
        var start = startUtc.ToUniversalTime();
        return new Booking
        {
            BusinessId = businessId,
            StaffId = staffId,
            ServiceId = serviceId,
            CustomerId = customerId,
            StartUtc = start,
            EndUtc = start + duration,
            BlockedUntilUtc = start + duration + bufferAfter,
            Status = BookingStatus.Confirmed,
            Notes = notes,
            CreatedAtUtc = now,
        };
    }

    public Result Cancel(DateTimeOffset now, TimeSpan cancellationCutoff, Actor actor, string? reason)
    {
        if (Status != BookingStatus.Confirmed)
        {
            return BookingErrors.NotActive(Status);
        }

        if (actor == Actor.Customer && StartUtc - now < cancellationCutoff)
        {
            return BookingErrors.InsideCancellationCutoff(cancellationCutoff);
        }

        Status = BookingStatus.Cancelled;
        CancelledAtUtc = now;
        CancelledBy = actor;
        CancellationReason = reason;
        return Result.Success();
    }

    /// <summary>
    /// Moves the booking. The caller is responsible for confirming the new slot is available;
    /// the database exclusion constraint is the final guard against overlaps.
    /// </summary>
    public Result Reschedule(Guid staffId, DateTimeOffset newStartUtc, TimeSpan duration, TimeSpan bufferAfter, DateTimeOffset now, TimeSpan cancellationCutoff, Actor actor)
    {
        if (Status != BookingStatus.Confirmed)
        {
            return BookingErrors.NotActive(Status);
        }

        if (actor == Actor.Customer && StartUtc - now < cancellationCutoff)
        {
            return BookingErrors.InsideCancellationCutoff(cancellationCutoff);
        }

        var start = newStartUtc.ToUniversalTime();
        if (start == StartUtc && staffId == StaffId)
        {
            return BookingErrors.SameSlot;
        }

        StaffId = staffId;
        StartUtc = start;
        EndUtc = start + duration;
        BlockedUntilUtc = start + duration + bufferAfter;
        RescheduleCount++;
        return Result.Success();
    }

    public Result Complete(DateTimeOffset now) => CloseOut(BookingStatus.Completed, now);

    public Result MarkNoShow(DateTimeOffset now) => CloseOut(BookingStatus.NoShow, now);

    private Result CloseOut(BookingStatus status, DateTimeOffset now)
    {
        if (Status != BookingStatus.Confirmed)
        {
            return BookingErrors.NotActive(Status);
        }

        if (now < StartUtc)
        {
            return BookingErrors.NotStartedYet;
        }

        Status = status;
        return Result.Success();
    }
}

public static class BookingErrors
{
    public static readonly Error NotFound = Error.NotFound("booking.not_found", "Booking was not found.");

    public static readonly Error SlotUnavailable =
        Error.Conflict("booking.slot_unavailable", "The requested time slot is no longer available.");

    public static readonly Error SameSlot =
        Error.Rule("booking.same_slot", "The booking is already scheduled for that slot.");

    public static readonly Error NotStartedYet =
        Error.Rule("booking.not_started", "The appointment has not started yet.");

    public static readonly Error IdempotencyKeyReused =
        Error.Rule("booking.idempotency_key_reused", "The Idempotency-Key was already used with a different request.");

    public static Error NotActive(BookingStatus status) =>
        Error.Rule("booking.not_active", $"Only confirmed bookings can be changed (current status: {status}).");

    public static Error InsideCancellationCutoff(TimeSpan cutoff) =>
        Error.Rule("booking.cancellation_cutoff", $"Bookings can only be changed up to {cutoff.TotalHours:0.#} hours before the appointment.");
}
