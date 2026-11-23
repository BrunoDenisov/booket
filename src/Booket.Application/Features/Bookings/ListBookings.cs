using Booket.Application.Abstractions;
using Booket.Application.Common;
using Booket.Application.Features.Businesses;
using Booket.Domain.Bookings;
using Booket.Domain.Common;
using Booket.Domain.Scheduling;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Booket.Application.Features.Bookings;

// ---- Customer: my bookings ------------------------------------------------------------------

/// <param name="Upcoming">true: only appointments that have not started yet; false: only past ones; null: all.</param>
public sealed record ListMyBookingsQuery(bool? Upcoming, BookingStatus? Status);

internal sealed class ListMyBookingsHandler(IAppDbContext db, ICurrentUser currentUser, TimeProvider clock)
    : IHandler<ListMyBookingsQuery, IReadOnlyList<BookingResponse>>
{
    public async Task<Result<IReadOnlyList<BookingResponse>>> HandleAsync(ListMyBookingsQuery request, CancellationToken cancellationToken)
    {
        var userId = currentUser.RequireUserId();
        var now = clock.GetUtcNow();
        var query = db.Bookings.Where(b => b.CustomerId == userId);

        query = request.Upcoming switch
        {
            true => query.Where(b => b.StartUtc >= now),
            false => query.Where(b => b.StartUtc < now),
            null => query,
        };

        if (request.Status is { } status)
        {
            query = query.Where(b => b.Status == status);
        }

        return await query.OrderBy(b => b.StartUtc).Take(200).ToResponsesAsync(db, cancellationToken);
    }
}

// ---- Business: calendar ---------------------------------------------------------------------

/// <param name="From">First business-local date (inclusive).</param>
/// <param name="To">Last business-local date (inclusive).</param>
public sealed record ListBusinessBookingsQuery(Guid BusinessId, DateOnly From, DateOnly To, Guid? StaffId, BookingStatus? Status);

public sealed class ListBusinessBookingsValidator : AbstractValidator<ListBusinessBookingsQuery>
{
    public const int MaxRangeDays = 62;

    public ListBusinessBookingsValidator()
    {
        RuleFor(x => x.To).GreaterThanOrEqualTo(x => x.From).WithMessage("'to' must be on or after 'from'.");
        RuleFor(x => x.To)
            .Must((q, to) => to.DayNumber - q.From.DayNumber < MaxRangeDays)
            .WithMessage($"The date range may span at most {MaxRangeDays} days.");
    }
}

internal sealed class ListBusinessBookingsHandler(IAppDbContext db) : IHandler<ListBusinessBookingsQuery, IReadOnlyList<BookingResponse>>
{
    public async Task<Result<IReadOnlyList<BookingResponse>>> HandleAsync(ListBusinessBookingsQuery request, CancellationToken cancellationToken)
    {
        var business = await db.Businesses.AsNoTracking().SingleOrDefaultAsync(b => b.Id == request.BusinessId, cancellationToken);
        if (business is null)
        {
            return BusinessErrors.NotFound;
        }

        var zone = business.TimeZone;
        var fromUtc = AvailabilityCalculator.ToUtc(request.From.ToDateTime(TimeOnly.MinValue), zone, preferLater: false);
        var toUtc = AvailabilityCalculator.ToUtc(request.To.AddDays(1).ToDateTime(TimeOnly.MinValue), zone, preferLater: false);

        var query = db.Bookings.Where(b => b.BusinessId == business.Id && b.StartUtc >= fromUtc && b.StartUtc < toUtc);

        if (request.StaffId is { } staffId)
        {
            query = query.Where(b => b.StaffId == staffId);
        }

        if (request.Status is { } status)
        {
            query = query.Where(b => b.Status == status);
        }

        return await query.ToResponsesAsync(db, cancellationToken);
    }
}
