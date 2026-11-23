using Booket.Domain.Businesses;

namespace Booket.Domain.Scheduling;

/// <summary>A recurring weekly window in business-local time.</summary>
public sealed record WorkingWindow(DayOfWeek Day, TimeOnly Start, TimeOnly End);

/// <summary>Everything the calculator needs to know about one staff member.</summary>
/// <param name="Busy">Existing bookings (including their buffers) and time off, as UTC ranges.</param>
public sealed record StaffSchedule(Guid StaffId, IReadOnlyList<WorkingWindow> WorkingHours, IReadOnlyList<TimeRange> Busy);

/// <param name="From">First business-local date to search (inclusive).</param>
/// <param name="To">Last business-local date to search (inclusive).</param>
public sealed record AvailabilityQuery(
    DateOnly From,
    DateOnly To,
    TimeSpan ServiceDuration,
    TimeSpan BufferAfter,
    BookingPolicy Policy,
    TimeZoneInfo TimeZone,
    DateTimeOffset Now);

/// <summary>A bookable start time for a specific staff member.</summary>
public sealed record AvailableSlot(Guid StaffId, DateTimeOffset StartUtc, DateTimeOffset EndUtc);

/// <summary>
/// Pure, deterministic slot generation. For each staff member and each local date:
/// <list type="number">
///   <item>project the weekly working windows onto that date and convert them to UTC (DST-aware);</item>
///   <item>walk the window on the policy's slot grid, measured from the window start;</item>
///   <item>keep a start if the service fits inside the window, the start honours lead time and the
///   booking horizon, and <c>[start, start + duration + buffer)</c> overlaps nothing busy.</item>
/// </list>
/// The service itself must end by the end of the window; its buffer may run past it.
/// </summary>
public static class AvailabilityCalculator
{
    public static IReadOnlyList<AvailableSlot> Calculate(AvailabilityQuery query, IEnumerable<StaffSchedule> staff)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(query.ServiceDuration, TimeSpan.Zero, nameof(query.ServiceDuration));
        ArgumentOutOfRangeException.ThrowIfLessThan(query.BufferAfter, TimeSpan.Zero, nameof(query.BufferAfter));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(query.Policy.SlotGranularityMinutes, 0, nameof(query.Policy.SlotGranularityMinutes));

        var step = TimeSpan.FromMinutes(query.Policy.SlotGranularityMinutes);
        var blockedLength = query.ServiceDuration + query.BufferAfter;
        var earliestStart = query.Now + query.Policy.MinLeadTime;
        var latestStart = query.Now + TimeSpan.FromDays(query.Policy.MaxAdvanceDays);

        var slots = new List<AvailableSlot>();

        foreach (var schedule in staff)
        {
            var busy = schedule.Busy.OrderBy(b => b.Start).ToArray();
            var seen = new HashSet<DateTimeOffset>();

            for (var date = query.From; date <= query.To; date = date.AddDays(1))
            {
                foreach (var window in schedule.WorkingHours.Where(w => w.Day == date.DayOfWeek).OrderBy(w => w.Start))
                {
                    var windowStart = ToUtc(date.ToDateTime(window.Start), query.TimeZone, preferLater: false);
                    var windowEnd = ToUtc(date.ToDateTime(window.End), query.TimeZone, preferLater: true);

                    for (var start = windowStart; start + query.ServiceDuration <= windowEnd; start += step)
                    {
                        if (start < earliestStart || start > latestStart)
                        {
                            continue;
                        }

                        var candidate = new TimeRange(start, start + blockedLength);
                        if (!OverlapsAny(candidate, busy) && seen.Add(start))
                        {
                            slots.Add(new AvailableSlot(schedule.StaffId, start, start + query.ServiceDuration));
                        }
                    }
                }
            }
        }

        slots.Sort((a, b) => a.StartUtc != b.StartUtc ? a.StartUtc.CompareTo(b.StartUtc) : a.StaffId.CompareTo(b.StaffId));
        return slots;
    }

    /// <summary>Checks a single proposed start; used to validate a booking request against the same rules.</summary>
    public static bool IsAvailable(AvailabilityQuery query, StaffSchedule staff, DateTimeOffset startUtc) =>
        Calculate(query, [staff]).Any(s => s.StartUtc == startUtc);

    /// <summary>
    /// Converts a business-local wall-clock time to UTC.
    /// Times that do not exist (spring-forward gap) move forward to the first valid instant.
    /// Ambiguous times (fall-back overlap) resolve to the earlier instant for window starts and the
    /// later instant for window ends, so a window never shrinks across a transition.
    /// </summary>
    internal static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone, bool preferLater)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(local))
        {
            var probe = local;
            while (zone.IsInvalidTime(probe))
            {
                probe = probe.AddMinutes(1);
            }

            local = probe;
        }

        TimeSpan offset;
        if (zone.IsAmbiguousTime(local))
        {
            var offsets = zone.GetAmbiguousTimeOffsets(local);
            offset = preferLater ? offsets.Min() : offsets.Max();
        }
        else
        {
            offset = zone.GetUtcOffset(local);
        }

        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>Converts a UTC instant to the business-local calendar date.</summary>
    public static DateOnly LocalDate(DateTimeOffset utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, zone).DateTime);

    private static bool OverlapsAny(TimeRange candidate, TimeRange[] sortedBusy)
    {
        foreach (var b in sortedBusy)
        {
            if (b.Start >= candidate.End)
            {
                return false;
            }

            if (b.Overlaps(candidate))
            {
                return true;
            }
        }

        return false;
    }
}
