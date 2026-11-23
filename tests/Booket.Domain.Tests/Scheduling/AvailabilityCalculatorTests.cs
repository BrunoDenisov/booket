using Booket.Domain.Businesses;
using Booket.Domain.Scheduling;

namespace Booket.Domain.Tests.Scheduling;

public sealed class AvailabilityCalculatorTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly Guid StaffA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid StaffB = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    // Monday 2026-06-01. London is on BST (UTC+1) in June.
    private static readonly DateOnly Monday = new(2026, 6, 1);
    private static readonly DateTimeOffset LongBefore = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly BookingPolicy NoLimits = new()
    {
        MinLeadTime = TimeSpan.Zero,
        MaxAdvanceDays = 365,
        SlotGranularityMinutes = 30,
    };

    private static AvailabilityQuery Query(
        DateOnly? from = null,
        DateOnly? to = null,
        int durationMinutes = 30,
        int bufferMinutes = 0,
        BookingPolicy? policy = null,
        DateTimeOffset? now = null,
        TimeZoneInfo? zone = null) =>
        new(from ?? Monday, to ?? from ?? Monday, TimeSpan.FromMinutes(durationMinutes), TimeSpan.FromMinutes(bufferMinutes),
            policy ?? NoLimits, zone ?? London, now ?? LongBefore);

    private static StaffSchedule Staff(Guid id, IEnumerable<TimeRange>? busy = null, params WorkingWindow[] hours) =>
        new(id, hours, busy?.ToList() ?? []);

    private static WorkingWindow Mon(int startHour, int endHour) => new(DayOfWeek.Monday, new TimeOnly(startHour, 0), new TimeOnly(endHour, 0));

    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void Generates_slots_on_the_grid_and_converts_local_time_to_utc()
    {
        var slots = AvailabilityCalculator.Calculate(Query(), [Staff(StaffA, null, Mon(9, 11))]);

        // 09:00-11:00 BST == 08:00-10:00 UTC; 30 min service on a 30 min grid.
        slots.Select(s => s.StartUtc).ShouldBe([Utc(6, 1, 8), Utc(6, 1, 8, 30), Utc(6, 1, 9), Utc(6, 1, 9, 30)]);
        slots.ShouldAllBe(s => s.EndUtc - s.StartUtc == TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Service_must_fit_inside_the_window_but_buffer_may_overrun_it()
    {
        var slots = AvailabilityCalculator.Calculate(Query(durationMinutes: 45, bufferMinutes: 15), [Staff(StaffA, null, Mon(9, 10))]);

        // 09:00 fits (ends 09:45). 09:30 would end 10:15 - outside the window.
        slots.Select(s => s.StartUtc).ShouldBe([Utc(6, 1, 8)]);
    }

    [Fact]
    public void Existing_booking_blocks_overlapping_slots_and_touching_slots_remain()
    {
        // Booking 09:30-10:00 local (08:30-09:00 UTC).
        var busy = new[] { new TimeRange(Utc(6, 1, 8, 30), Utc(6, 1, 9)) };

        var slots = AvailabilityCalculator.Calculate(Query(), [Staff(StaffA, busy, Mon(9, 11))]);

        slots.Select(s => s.StartUtc).ShouldBe([Utc(6, 1, 8), Utc(6, 1, 9), Utc(6, 1, 9, 30)]);
    }

    [Fact]
    public void Buffer_of_the_requested_service_is_checked_against_later_bookings()
    {
        // Booking at 10:00 local (09:00 UTC). A 30 min service + 15 min buffer starting 09:30 would block until 10:15.
        var busy = new[] { new TimeRange(Utc(6, 1, 9), Utc(6, 1, 9, 30)) };

        var slots = AvailabilityCalculator.Calculate(Query(bufferMinutes: 15), [Staff(StaffA, busy, Mon(9, 11))]);

        slots.Select(s => s.StartUtc).ShouldBe([Utc(6, 1, 8), Utc(6, 1, 9, 30)]);
    }

    [Fact]
    public void Split_shift_produces_no_slots_during_the_break()
    {
        var slots = AvailabilityCalculator.Calculate(Query(), [Staff(StaffA, null, Mon(9, 10), Mon(13, 14))]);

        slots.Select(s => s.StartUtc).ShouldBe([Utc(6, 1, 8), Utc(6, 1, 8, 30), Utc(6, 1, 12), Utc(6, 1, 12, 30)]);
    }

    [Fact]
    public void Time_off_blocks_availability()
    {
        var busy = new[] { new TimeRange(Utc(6, 1, 0), Utc(6, 2, 0)) };

        AvailabilityCalculator.Calculate(Query(), [Staff(StaffA, busy, Mon(9, 17))]).ShouldBeEmpty();
    }

    [Fact]
    public void Minimum_lead_time_hides_slots_that_start_too_soon()
    {
        var policy = NoLimits with { MinLeadTime = TimeSpan.FromHours(1) };
        var now = Utc(6, 1, 8, 10); // 09:10 local

        var slots = AvailabilityCalculator.Calculate(Query(policy: policy, now: now), [Staff(StaffA, null, Mon(9, 11))]);

        // Earliest allowed start: 09:10 UTC -> first grid slot 09:30 UTC (10:30 local).
        slots.Select(s => s.StartUtc).ShouldBe([Utc(6, 1, 9, 30)]);
    }

    [Fact]
    public void Booking_horizon_hides_slots_beyond_max_advance_days()
    {
        var policy = NoLimits with { MaxAdvanceDays = 7 };
        var now = Utc(5, 27, 0);

        var slots = AvailabilityCalculator.Calculate(
            Query(from: Monday, to: Monday.AddDays(7), policy: policy, now: now),
            [Staff(StaffA, null, Mon(9, 10))]);

        // Monday 1 June is within 7 days of 27 May; Monday 8 June is not.
        slots.ShouldAllBe(s => s.StartUtc < Utc(6, 2, 0));
        slots.ShouldNotBeEmpty();
    }

    [Fact]
    public void Each_staff_member_gets_their_own_slots_sorted_by_time()
    {
        var bookedA = new[] { new TimeRange(Utc(6, 1, 8), Utc(6, 1, 8, 30)) };

        var slots = AvailabilityCalculator.Calculate(Query(), [Staff(StaffA, bookedA, Mon(9, 10)), Staff(StaffB, null, Mon(9, 10))]);

        slots.ShouldBe(
        [
            new AvailableSlot(StaffB, Utc(6, 1, 8), Utc(6, 1, 8, 30)),
            new AvailableSlot(StaffA, Utc(6, 1, 8, 30), Utc(6, 1, 9)),
            new AvailableSlot(StaffB, Utc(6, 1, 8, 30), Utc(6, 1, 9)),
        ]);
    }

    [Fact]
    public void Spring_forward_gap_is_skipped()
    {
        // 2026-03-29: London clocks jump 01:00 GMT -> 02:00 BST. A Sunday 00:00-04:00 window is only 3 real hours.
        var sunday = new DateOnly(2026, 3, 29);
        var window = new WorkingWindow(DayOfWeek.Sunday, new TimeOnly(0, 0), new TimeOnly(4, 0));
        var policy = NoLimits with { SlotGranularityMinutes = 60 };

        var slots = AvailabilityCalculator.Calculate(Query(from: sunday, durationMinutes: 60, policy: policy, now: Utc(3, 1, 0)), [Staff(StaffA, null, window)]);

        slots.Select(s => s.StartUtc).ShouldBe([Utc(3, 29, 0), Utc(3, 29, 1), Utc(3, 29, 2)]);
    }

    [Fact]
    public void Fall_back_overlap_keeps_the_extra_hour()
    {
        // 2026-10-25: London clocks go 02:00 BST -> 01:00 GMT. A Sunday 00:00-03:00 window is 4 real hours.
        var sunday = new DateOnly(2026, 10, 25);
        var window = new WorkingWindow(DayOfWeek.Sunday, new TimeOnly(0, 0), new TimeOnly(3, 0));
        var policy = NoLimits with { SlotGranularityMinutes = 60 };

        var slots = AvailabilityCalculator.Calculate(Query(from: sunday, durationMinutes: 60, policy: policy, now: Utc(10, 1, 0)), [Staff(StaffA, null, window)]);

        // 00:00 BST == 23:00 UTC (Saturday); 03:00 GMT == 03:00 UTC.
        slots.Select(s => s.StartUtc).ShouldBe([Utc(10, 24, 23), Utc(10, 25, 0), Utc(10, 25, 1), Utc(10, 25, 2)]);
    }

    [Fact]
    public void Day_without_working_hours_has_no_slots()
    {
        var tuesday = Monday.AddDays(1);

        AvailabilityCalculator.Calculate(Query(from: tuesday), [Staff(StaffA, null, Mon(9, 17))]).ShouldBeEmpty();
    }

    [Fact]
    public void IsAvailable_rejects_off_grid_start()
    {
        var staff = Staff(StaffA, null, Mon(9, 11));

        AvailabilityCalculator.IsAvailable(Query(), staff, Utc(6, 1, 8, 30)).ShouldBeTrue();
        AvailabilityCalculator.IsAvailable(Query(), staff, Utc(6, 1, 8, 10)).ShouldBeFalse();
    }

    [Fact]
    public void LocalDate_uses_the_business_time_zone()
    {
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

        AvailabilityCalculator.LocalDate(Utc(6, 1, 20), tokyo).ShouldBe(new DateOnly(2026, 6, 2));
    }
}
