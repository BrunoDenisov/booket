using Booket.Domain.Bookings;
using Booket.Domain.Common;

namespace Booket.Domain.Tests.Bookings;

public sealed class BookingTests
{
    private static readonly DateTimeOffset Start = new(2026, 6, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Cutoff = TimeSpan.FromHours(24);

    private static Booking NewBooking() => Booking.Create(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Start, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(10), null, Start.AddDays(-7));

    [Fact]
    public void Create_computes_end_and_blocked_until()
    {
        var booking = NewBooking();

        booking.Status.ShouldBe(BookingStatus.Confirmed);
        booking.EndUtc.ShouldBe(Start.AddMinutes(30));
        booking.BlockedUntilUtc.ShouldBe(Start.AddMinutes(40));
    }

    [Fact]
    public void Customer_can_cancel_before_cutoff()
    {
        var booking = NewBooking();

        var result = booking.Cancel(Start.AddDays(-2), Cutoff, Actor.Customer, "Plans changed");

        result.IsSuccess.ShouldBeTrue();
        booking.Status.ShouldBe(BookingStatus.Cancelled);
        booking.CancelledBy.ShouldBe(Actor.Customer);
        booking.CancellationReason.ShouldBe("Plans changed");
    }

    [Fact]
    public void Customer_cannot_cancel_inside_cutoff()
    {
        var booking = NewBooking();

        var result = booking.Cancel(Start.AddHours(-2), Cutoff, Actor.Customer, null);

        result.Error!.Code.ShouldBe("booking.cancellation_cutoff");
        result.Error.Type.ShouldBe(ErrorType.BusinessRule);
        booking.Status.ShouldBe(BookingStatus.Confirmed);
    }

    [Fact]
    public void Business_can_cancel_inside_cutoff()
    {
        var booking = NewBooking();

        booking.Cancel(Start.AddHours(-1), Cutoff, Actor.Business, "Staff sick").IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Cancelled_booking_cannot_be_cancelled_or_rescheduled()
    {
        var booking = NewBooking();
        booking.Cancel(Start.AddDays(-2), Cutoff, Actor.Customer, null);

        booking.Cancel(Start.AddDays(-2), Cutoff, Actor.Customer, null).Error!.Code.ShouldBe("booking.not_active");
        booking.Reschedule(booking.StaffId, Start.AddDays(1), TimeSpan.FromMinutes(30), TimeSpan.Zero, Start.AddDays(-2), Cutoff, Actor.Customer)
            .Error!.Code.ShouldBe("booking.not_active");
    }

    [Fact]
    public void Reschedule_moves_the_booking_and_counts()
    {
        var booking = NewBooking();
        var newStart = Start.AddDays(1);
        var newStaff = Guid.NewGuid();

        var result = booking.Reschedule(newStaff, newStart, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(10), Start.AddDays(-2), Cutoff, Actor.Customer);

        result.IsSuccess.ShouldBeTrue();
        booking.StartUtc.ShouldBe(newStart);
        booking.StaffId.ShouldBe(newStaff);
        booking.BlockedUntilUtc.ShouldBe(newStart.AddMinutes(40));
        booking.RescheduleCount.ShouldBe(1);
    }

    [Fact]
    public void Reschedule_to_the_same_slot_is_rejected()
    {
        var booking = NewBooking();

        booking.Reschedule(booking.StaffId, Start, TimeSpan.FromMinutes(30), TimeSpan.Zero, Start.AddDays(-2), Cutoff, Actor.Customer)
            .Error.ShouldBe(BookingErrors.SameSlot);
    }

    [Fact]
    public void Complete_and_no_show_only_after_start()
    {
        var booking = NewBooking();

        booking.Complete(Start.AddMinutes(-5)).Error.ShouldBe(BookingErrors.NotStartedYet);
        booking.MarkNoShow(Start.AddMinutes(15)).IsSuccess.ShouldBeTrue();
        booking.Status.ShouldBe(BookingStatus.NoShow);
    }
}
