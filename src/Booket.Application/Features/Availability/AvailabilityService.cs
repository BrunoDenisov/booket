using Booket.Application.Abstractions;
using Booket.Domain.Bookings;
using Booket.Domain.Businesses;
using Booket.Domain.Catalog;
using Booket.Domain.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace Booket.Application.Features.Availability;

/// <summary>
/// Loads schedules, bookings and time off from the database and runs the pure
/// <see cref="AvailabilityCalculator"/>. Shared by the availability query and by booking /
/// rescheduling, so a slot is offered if and only if it can be booked.
/// </summary>
public sealed class AvailabilityService(IAppDbContext db, TimeProvider clock)
{
    public async Task<IReadOnlyList<AvailableSlot>> GetSlotsAsync(
        Business business,
        Service service,
        DateOnly from,
        DateOnly to,
        Guid? staffId,
        Guid? excludeBookingId,
        CancellationToken ct)
    {
        var schedules = await LoadSchedulesAsync(business, service, from, to, staffId, excludeBookingId, ct);
        return AvailabilityCalculator.Calculate(BuildQuery(business, service, from, to), schedules);
    }

    /// <summary>Returns the staff members (in deterministic order) who can take a booking starting at <paramref name="startUtc"/>.</summary>
    public async Task<IReadOnlyList<Guid>> GetStaffAvailableAtAsync(
        Business business,
        Service service,
        DateTimeOffset startUtc,
        Guid? staffId,
        Guid? excludeBookingId,
        CancellationToken ct)
    {
        var date = AvailabilityCalculator.LocalDate(startUtc, business.TimeZone);
        var slots = await GetSlotsAsync(business, service, date, date, staffId, excludeBookingId, ct);
        return slots.Where(s => s.StartUtc == startUtc).Select(s => s.StaffId).ToList();
    }

    private AvailabilityQuery BuildQuery(Business business, Service service, DateOnly from, DateOnly to) =>
        new(from, to, service.Duration, service.BufferAfter, business.Policy, business.TimeZone, clock.GetUtcNow());

    private async Task<List<StaffSchedule>> LoadSchedulesAsync(
        Business business,
        Service service,
        DateOnly from,
        DateOnly to,
        Guid? staffId,
        Guid? excludeBookingId,
        CancellationToken ct)
    {
        var staffQuery = db.StaffMembers.AsNoTracking()
            .Include(s => s.WorkingHours)
            .Where(s => s.BusinessId == business.Id && s.IsActive && s.Services.Any(x => x.ServiceId == service.Id));

        if (staffId is { } id)
        {
            staffQuery = staffQuery.Where(s => s.Id == id);
        }

        var staff = await staffQuery.ToListAsync(ct);
        if (staff.Count == 0)
        {
            return [];
        }

        // Search window in UTC, padded by a day on each side to cover any UTC offset.
        var windowStart = new DateTimeOffset(from.AddDays(-1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var windowEnd = new DateTimeOffset(to.AddDays(2).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var staffIds = staff.Select(s => s.Id).ToList();

        var bookings = await db.Bookings.AsNoTracking()
            .Where(b => staffIds.Contains(b.StaffId)
                        && b.Status == BookingStatus.Confirmed
                        && b.StartUtc < windowEnd
                        && b.BlockedUntilUtc > windowStart
                        && (excludeBookingId == null || b.Id != excludeBookingId))
            .Select(b => new { b.StaffId, b.StartUtc, b.BlockedUntilUtc })
            .ToListAsync(ct);

        var timeOff = await db.TimeOffs.AsNoTracking()
            .Where(t => staffIds.Contains(t.StaffId) && t.StartUtc < windowEnd && t.EndUtc > windowStart)
            .Select(t => new { t.StaffId, t.StartUtc, t.EndUtc })
            .ToListAsync(ct);

        return staff
            .OrderBy(s => s.Id)
            .Select(s => new StaffSchedule(
                s.Id,
                s.WorkingHours.Select(w => new WorkingWindow(w.Day, w.Start, w.End)).ToList(),
                bookings.Where(b => b.StaffId == s.Id).Select(b => new TimeRange(b.StartUtc, b.BlockedUntilUtc))
                    .Concat(timeOff.Where(t => t.StaffId == s.Id).Select(t => new TimeRange(t.StartUtc, t.EndUtc)))
                    .ToList()))
            .ToList();
    }
}
