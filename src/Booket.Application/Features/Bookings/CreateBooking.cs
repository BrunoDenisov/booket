using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Booket.Application.Abstractions;
using Booket.Application.Common;
using Booket.Application.Features.Availability;
using Booket.Application.Features.Businesses;
using Booket.Application.Features.Services;
using Booket.Domain.Bookings;
using Booket.Domain.Common;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Booket.Application.Features.Bookings;

/// <param name="StaffId">A specific staff member, or <c>null</c> for any available staff member.</param>
/// <param name="Start">Desired start; must be one of the slots returned by the availability endpoint.</param>
/// <param name="IdempotencyKey">Optional client-generated key; retries with the same key return the original booking.</param>
public sealed record CreateBookingCommand(
    Guid BusinessId,
    Guid ServiceId,
    Guid? StaffId,
    DateTimeOffset Start,
    string? Notes,
    string? IdempotencyKey);

public sealed record CreateBookingResult(BookingResponse Booking, bool Replayed);

public sealed class CreateBookingValidator : AbstractValidator<CreateBookingCommand>
{
    public CreateBookingValidator()
    {
        RuleFor(x => x.ServiceId).NotEmpty();
        RuleFor(x => x.Start).NotEmpty();
        RuleFor(x => x.Notes).MaximumLength(1000);
        RuleFor(x => x.IdempotencyKey).MaximumLength(100);
    }
}

internal sealed class CreateBookingHandler(
    IAppDbContext db,
    ICurrentUser currentUser,
    AvailabilityService availability,
    BookingNotifications notifications,
    TimeProvider clock) : IHandler<CreateBookingCommand, CreateBookingResult>
{
    /// <summary>When "any staff" is requested and the chosen person is taken concurrently, try the next one.</summary>
    private const int MaxCandidates = 3;

    public async Task<Result<CreateBookingResult>> HandleAsync(CreateBookingCommand request, CancellationToken cancellationToken)
    {
        var customerId = currentUser.RequireUserId();
        var requestHash = Hash(request);

        if (request.IdempotencyKey is { } key)
        {
            var replay = await TryReplayAsync(customerId, key, requestHash, cancellationToken);
            if (replay is not null)
            {
                return replay;
            }
        }

        var business = await db.Businesses.AsNoTracking().SingleOrDefaultAsync(b => b.Id == request.BusinessId, cancellationToken);
        if (business is null)
        {
            return BusinessErrors.NotFound;
        }

        var service = await db.Services.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == request.ServiceId && s.BusinessId == business.Id && s.IsActive, cancellationToken);
        if (service is null)
        {
            return ServiceErrors.NotFound;
        }

        var start = request.Start.ToUniversalTime();
        var candidates = await availability.GetStaffAvailableAtAsync(business, service, start, request.StaffId, null, cancellationToken);

        foreach (var staffId in candidates.Take(MaxCandidates))
        {
            var booking = Booking.Create(business.Id, staffId, service.Id, customerId, start, service.Duration, service.BufferAfter, request.Notes, clock.GetUtcNow());
            db.Bookings.Add(booking);
            var messages = notifications.BookingCreated(booking);
            var record = request.IdempotencyKey is { } k
                ? db.IdempotencyRecords.Add(IdempotencyRecord.Create(customerId, k, requestHash, booking.Id, clock.GetUtcNow())).Entity
                : null;

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                BooketTelemetry.BookingsCreated.Add(1);
                return new CreateBookingResult(await db.GetResponseAsync(booking.Id, cancellationToken), Replayed: false);
            }
            catch (ExclusionConstraintViolationException)
            {
                // Someone else took this staff member's slot between our check and our insert.
                BooketTelemetry.BookingConflicts.Add(1);
                db.Bookings.Remove(booking);
                db.OutboxMessages.RemoveRange(messages);
                if (record is not null)
                {
                    db.IdempotencyRecords.Remove(record);
                }
            }
            catch (UniqueConstraintViolationException) when (request.IdempotencyKey is not null)
            {
                // A concurrent request with the same idempotency key won the race.
                var replay = await TryReplayAsync(customerId, request.IdempotencyKey, requestHash, cancellationToken);
                if (replay is not null)
                {
                    return replay;
                }

                throw;
            }
        }

        return BookingErrors.SlotUnavailable;
    }

    private async Task<Result<CreateBookingResult>?> TryReplayAsync(Guid customerId, string key, string requestHash, CancellationToken ct)
    {
        var existing = await db.IdempotencyRecords.AsNoTracking()
            .SingleOrDefaultAsync(r => r.UserId == customerId && r.Key == key, ct);

        if (existing is null)
        {
            return null;
        }

        if (existing.RequestHash != requestHash)
        {
            return BookingErrors.IdempotencyKeyReused;
        }

        return new CreateBookingResult(await db.GetResponseAsync(existing.BookingId, ct), Replayed: true);
    }

    private static string Hash(CreateBookingCommand r)
    {
        var canonical = string.Join('|',
            r.BusinessId, r.ServiceId, r.StaffId?.ToString() ?? "-",
            r.Start.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), r.Notes ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
