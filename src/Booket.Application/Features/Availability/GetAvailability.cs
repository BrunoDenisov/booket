using Booket.Application.Abstractions;
using Booket.Application.Common;
using Booket.Application.Features.Businesses;
using Booket.Application.Features.Services;
using Booket.Domain.Common;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Booket.Application.Features.Availability;

/// <param name="From">First business-local date (inclusive).</param>
/// <param name="To">Last business-local date (inclusive). Defaults to <paramref name="From"/>.</param>
/// <param name="StaffId">Only this staff member; omit for "any available staff".</param>
public sealed record GetAvailabilityQuery(Guid BusinessId, Guid ServiceId, DateOnly From, DateOnly? To, Guid? StaffId);

/// <param name="Start">Slot start in the business's local offset (e.g. 2026-06-01T09:00:00+01:00).</param>
/// <param name="StaffIds">Staff members free for this slot.</param>
public sealed record SlotDto(DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<Guid> StaffIds);

public sealed record AvailabilityDayDto(DateOnly Date, IReadOnlyList<SlotDto> Slots);

public sealed record AvailabilityResponse(
    Guid BusinessId,
    Guid ServiceId,
    string TimeZoneId,
    int DurationMinutes,
    IReadOnlyList<AvailabilityDayDto> Days);

public sealed class GetAvailabilityValidator : AbstractValidator<GetAvailabilityQuery>
{
    public const int MaxRangeDays = 31;

    public GetAvailabilityValidator()
    {
        RuleFor(x => x.ServiceId).NotEmpty();
        RuleFor(x => x.To)
            .GreaterThanOrEqualTo(x => x.From).When(x => x.To is not null)
            .WithMessage("'to' must be on or after 'from'.");
        RuleFor(x => x.To)
            .Must((q, to) => to is null || to.Value.DayNumber - q.From.DayNumber < MaxRangeDays)
            .WithMessage($"The date range may span at most {MaxRangeDays} days.");
    }
}

internal sealed class GetAvailabilityHandler(IAppDbContext db, AvailabilityService availability)
    : IHandler<GetAvailabilityQuery, AvailabilityResponse>
{
    public async Task<Result<AvailabilityResponse>> HandleAsync(GetAvailabilityQuery request, CancellationToken cancellationToken)
    {
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

        var to = request.To ?? request.From;
        var slots = await availability.GetSlotsAsync(business, service, request.From, to, request.StaffId, null, cancellationToken);
        var zone = business.TimeZone;

        var byDate = slots
            .GroupBy(s => s.StartUtc)
            .Select(g => new SlotDto(
                TimeZoneInfo.ConvertTime(g.Key, zone),
                TimeZoneInfo.ConvertTime(g.First().EndUtc, zone),
                g.Select(s => s.StaffId).ToList()))
            .ToLookup(s => DateOnly.FromDateTime(s.Start.DateTime));

        var days = new List<AvailabilityDayDto>();
        for (var date = request.From; date <= to; date = date.AddDays(1))
        {
            days.Add(new AvailabilityDayDto(date, byDate[date].ToList()));
        }

        return new AvailabilityResponse(business.Id, service.Id, business.TimeZoneId, (int)service.Duration.TotalMinutes, days);
    }
}
