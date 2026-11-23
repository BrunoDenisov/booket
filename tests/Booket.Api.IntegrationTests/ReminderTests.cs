using Booket.Api.IntegrationTests.Infrastructure;
using Booket.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Booket.Api.IntegrationTests;

public sealed class ReminderTests(BooketApiFactory factory) : ApiTestBase(factory)
{
    [Fact]
    public async Task Confirmation_is_sent_immediately_and_reminders_when_due()
    {
        var shop = await CreateShopAsync();
        var customer = As((await RegisterCustomerAsync()).AccessToken);
        var thursday = Tuesday.AddDays(2);
        var booking = await BookedAsync(await BookAsync(customer, shop, London(thursday, 15)));

        (await Factory.DispatchOutboxAsync()).ShouldBe(1);
        Factory.Emails.Sent.Single().Subject.ShouldStartWith("Booking confirmed: Haircut");
        Factory.Emails.Sent.Single().To.ShouldBe("casey@example.test");

        // Nothing else is due yet.
        (await Factory.DispatchOutboxAsync()).ShouldBe(0);

        // 24 hours before the appointment, the first reminder goes out.
        Factory.Clock.SetUtcNow(booking.Start.AddHours(-24));
        (await Factory.DispatchOutboxAsync()).ShouldBe(1);
        var reminder = Factory.Emails.Sent.Last();
        reminder.Subject.ShouldBe("Reminder: Haircut in 1 day");
        reminder.TextBody.ShouldContain("Thursday 4 June 2026 at 15:00");

        // 2 hours before, the second.
        Factory.Clock.SetUtcNow(booking.Start.AddHours(-2));
        (await Factory.DispatchOutboxAsync()).ShouldBe(1);
        Factory.Emails.Sent.Last().Subject.ShouldBe("Reminder: Haircut in 2 hours");

        var statuses = await ScopedAsync(db => db.OutboxMessages.Where(m => m.BookingId == booking.Id).Select(m => m.Status).ToListAsync());
        statuses.ShouldAllBe(s => s == OutboxStatus.Sent);
    }

    [Fact]
    public async Task Failed_delivery_is_retried_with_backoff_until_it_succeeds()
    {
        var shop = await CreateShopAsync();
        var customer = As((await RegisterCustomerAsync()).AccessToken);
        await BookedAsync(await BookAsync(customer, shop, London(Tuesday, 15)));

        Factory.Emails.FailAll = true;
        (await Factory.DispatchOutboxAsync()).ShouldBe(1);

        var failed = await ScopedAsync(db => db.OutboxMessages.SingleAsync(m => m.Type == NotificationType.BookingConfirmed));
        failed.Status.ShouldBe(OutboxStatus.Pending);
        failed.Attempts.ShouldBe(1);
        failed.LastError.ShouldNotBeNull().ShouldContain("SMTP unavailable");
        failed.NextAttemptAtUtc.ShouldBe(BooketApiFactory.StartOfTest.AddMinutes(1));

        // Not retried before the backoff elapses.
        Factory.Emails.FailAll = false;
        (await Factory.DispatchOutboxAsync()).ShouldBe(0);

        Factory.Clock.Advance(TimeSpan.FromMinutes(1));
        (await Factory.DispatchOutboxAsync()).ShouldBe(1);
        Factory.Emails.Sent.ShouldHaveSingleItem().Subject.ShouldStartWith("Booking confirmed");
    }

    [Fact]
    public async Task Reminder_for_a_cancelled_booking_is_never_sent()
    {
        var shop = await CreateShopAsync();
        var customer = As((await RegisterCustomerAsync()).AccessToken);
        var booking = await BookedAsync(await BookAsync(customer, shop, London(Tuesday, 16)));

        // Cancel by the business inside the cutoff, then let time pass.
        var cancel = await As(shop.OwnerToken).PostAsync($"/api/v1/bookings/{booking.Id}/cancel", null);
        cancel.EnsureSuccessStatusCode();

        Factory.Clock.SetUtcNow(booking.Start.AddMinutes(-30));
        await Factory.DispatchOutboxAsync();

        Factory.Emails.Sent.Select(e => e.Subject).ShouldBe(
            ["Booking confirmed: Haircut on Tuesday 2 June 2026 at 16:00", "Booking cancelled: Haircut on Tuesday 2 June 2026 at 16:00"],
            ignoreOrder: true);
    }
}
