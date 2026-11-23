using System.Net;
using System.Net.Http.Json;
using Booket.Api.Endpoints;
using Booket.Api.IntegrationTests.Infrastructure;
using Booket.Application.Features.Bookings;
using Booket.Domain.Bookings;
using Booket.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Booket.Api.IntegrationTests;

public sealed class BookingFlowTests(BooketApiFactory factory) : ApiTestBase(factory)
{
    [Fact]
    public async Task Availability_reflects_working_hours_and_booking_removes_slot_including_buffer()
    {
        var shop = await CreateShopAsync();
        var customer = As((await RegisterCustomerAsync()).AccessToken);

        var before = await GetAvailabilityAsync(shop, Tuesday, shop.StaffId);
        var slots = before.Days.Single().Slots;
        slots.First().Start.ShouldBe(London(Tuesday, 9));
        slots.Last().Start.ShouldBe(London(Tuesday, 16, 30)); // 30 min service must end by 17:00

        var booking = await BookedAsync(await BookAsync(customer, shop, London(Tuesday, 9), shop.StaffId));
        booking.Status.ShouldBe(BookingStatus.Confirmed);
        booking.Start.ShouldBe(London(Tuesday, 9));
        booking.End.ShouldBe(London(Tuesday, 9, 30));
        booking.StaffName.ShouldBe("Alex");

        var after = (await GetAvailabilityAsync(shop, Tuesday, shop.StaffId)).Days.Single().Slots.Select(s => s.Start).ToList();
        after.ShouldNotContain(London(Tuesday, 9));
        after.ShouldNotContain(London(Tuesday, 9, 15)); // would overlap the appointment
        after.ShouldNotContain(London(Tuesday, 9, 30)); // inside the 10 min buffer
        after.ShouldContain(London(Tuesday, 9, 45));
    }

    [Fact]
    public async Task Any_staff_booking_picks_a_free_staff_member()
    {
        var shop = await CreateShopAsync();
        var first = As((await RegisterCustomerAsync("one@example.test")).AccessToken);
        var second = As((await RegisterCustomerAsync("two@example.test")).AccessToken);

        var a = await BookedAsync(await BookAsync(first, shop, London(Tuesday, 10)));
        var b = await BookedAsync(await BookAsync(second, shop, London(Tuesday, 10)));

        new[] { a.StaffId, b.StaffId }.ShouldBe([shop.StaffId, shop.SecondStaffId], ignoreOrder: true);

        var slot = (await GetAvailabilityAsync(shop, Tuesday)).Days.Single().Slots.Select(s => s.Start);
        slot.ShouldNotContain(London(Tuesday, 10));
    }

    [Fact]
    public async Task Concurrent_requests_for_the_same_slot_produce_exactly_one_booking()
    {
        var shop = await CreateShopAsync();
        var customers = new List<HttpClient>();
        for (var i = 0; i < 10; i++)
        {
            customers.Add(As((await RegisterCustomerAsync($"racer{i}@example.test")).AccessToken));
        }

        var responses = await Task.WhenAll(customers.Select(c => BookAsync(c, shop, London(Tuesday, 11), shop.StaffId)));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(9);

        var conflict = await ReadProblemAsync(responses.First(r => r.StatusCode == HttpStatusCode.Conflict), HttpStatusCode.Conflict);
        conflict.GetProperty("code").GetString().ShouldBe("booking.slot_unavailable");

        var stored = await ScopedAsync(db => db.Bookings.CountAsync(b => b.StaffId == shop.StaffId));
        stored.ShouldBe(1);
    }

    [Fact]
    public async Task Booking_an_unavailable_slot_is_rejected()
    {
        var shop = await CreateShopAsync();
        var customer = As((await RegisterCustomerAsync()).AccessToken);

        // Outside working hours, off the 15 min grid, and inside the 1h minimum lead time.
        (await BookAsync(customer, shop, London(Tuesday, 18))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BookAsync(customer, shop, London(Tuesday, 9, 7))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BookAsync(customer, shop, BooketApiFactory.StartOfTest.AddMinutes(30))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Customer_cannot_cancel_inside_cutoff_but_owner_can()
    {
        var shop = await CreateShopAsync();
        var customer = As((await RegisterCustomerAsync()).AccessToken);
        var booking = await BookedAsync(await BookAsync(customer, shop, London(Tuesday, 9)));

        // Default cutoff is 24h; move the clock to 3 hours before the appointment (and log in again,
        // since the original 15 minute access tokens have long expired by then).
        Factory.Clock.SetUtcNow(booking.Start.AddHours(-3));
        customer = As(await LoginAsync("casey@example.test"));
        var owner = As(await LoginAsync("owner@test-barbers.test"));

        var denied = await customer.PostAsJsonAsync($"/api/v1/bookings/{booking.Id}/cancel", new CancelBookingRequest("Can't make it"), Json);
        var problem = await ReadProblemAsync(denied, HttpStatusCode.UnprocessableEntity);
        problem.GetProperty("code").GetString().ShouldBe("booking.cancellation_cutoff");

        var cancelled = await ReadAsync<BookingResponse>(await owner
            .PostAsJsonAsync($"/api/v1/bookings/{booking.Id}/cancel", new CancelBookingRequest("Barber is ill"), Json));
        cancelled.Status.ShouldBe(BookingStatus.Cancelled);
        cancelled.CancellationReason.ShouldBe("Barber is ill");

        // The slot is free again.
        Factory.Clock.SetUtcNow(BooketApiFactory.StartOfTest);
        (await GetAvailabilityAsync(shop, Tuesday, shop.StaffId)).Days.Single().Slots.Select(s => s.Start).ShouldContain(London(Tuesday, 9));
    }

    [Fact]
    public async Task Customer_cancellation_cancels_pending_reminders()
    {
        var shop = await CreateShopAsync();
        var customer = As((await RegisterCustomerAsync()).AccessToken);
        var booking = await BookedAsync(await BookAsync(customer, shop, London(Tuesday.AddDays(2), 14)));

        var cancel = await customer.PostAsJsonAsync($"/api/v1/bookings/{booking.Id}/cancel", new CancelBookingRequest(null), Json);
        cancel.StatusCode.ShouldBe(HttpStatusCode.OK);

        var messages = await ScopedAsync(db => db.OutboxMessages.Where(m => m.BookingId == booking.Id).ToListAsync());
        messages.Where(m => m.Type == NotificationType.Reminder).ShouldAllBe(m => m.Status == OutboxStatus.Cancelled);
        messages.ShouldContain(m => m.Type == NotificationType.BookingCancelled && m.Status == OutboxStatus.Pending);
    }

    [Fact]
    public async Task Reschedule_frees_the_old_slot_takes_the_new_one_and_replans_reminders()
    {
        var shop = await CreateShopAsync();
        var customer = As((await RegisterCustomerAsync()).AccessToken);
        var wednesday = Tuesday.AddDays(1);
        var thursday = Tuesday.AddDays(2);
        var booking = await BookedAsync(await BookAsync(customer, shop, London(wednesday, 10), shop.StaffId));

        var moved = await ReadAsync<BookingResponse>(await customer.PostAsJsonAsync(
            $"/api/v1/bookings/{booking.Id}/reschedule", new RescheduleBookingRequest(London(thursday, 11), null), Json));

        moved.Start.ShouldBe(London(thursday, 11));
        moved.StaffId.ShouldBe(shop.StaffId); // current staff member kept when available
        moved.RescheduleCount.ShouldBe(1);

        (await GetAvailabilityAsync(shop, wednesday, shop.StaffId)).Days.Single().Slots.Select(s => s.Start).ShouldContain(London(wednesday, 10));
        (await GetAvailabilityAsync(shop, thursday, shop.StaffId)).Days.Single().Slots.Select(s => s.Start).ShouldNotContain(London(thursday, 11));

        var reminders = await ScopedAsync(db => db.OutboxMessages
            .Where(m => m.BookingId == booking.Id && m.Type == NotificationType.Reminder)
            .ToListAsync());

        var pending = reminders.Where(m => m.Status == OutboxStatus.Pending).Select(m => m.DueAtUtc).Order().ToList();
        pending.ShouldBe([London(thursday, 11).AddHours(-24).ToUniversalTime(), London(thursday, 11).AddHours(-2).ToUniversalTime()]);
        reminders.Count(m => m.Status == OutboxStatus.Cancelled).ShouldBe(2);
    }

    [Fact]
    public async Task Reschedule_into_a_taken_slot_is_rejected()
    {
        var shop = await CreateShopAsync();
        var casey = As((await RegisterCustomerAsync()).AccessToken);
        var other = As((await RegisterCustomerAsync("other@example.test")).AccessToken);
        var thursday = Tuesday.AddDays(2);

        var mine = await BookedAsync(await BookAsync(casey, shop, London(thursday, 9), shop.StaffId));
        await BookedAsync(await BookAsync(other, shop, London(thursday, 12), shop.StaffId));

        var response = await casey.PostAsJsonAsync(
            $"/api/v1/bookings/{mine.Id}/reschedule", new RescheduleBookingRequest(London(thursday, 12), shop.StaffId), Json);

        (await ReadProblemAsync(response, HttpStatusCode.Conflict)).GetProperty("code").GetString().ShouldBe("booking.slot_unavailable");
    }

    [Fact]
    public async Task Idempotency_key_replays_the_original_booking()
    {
        var shop = await CreateShopAsync();
        var customer = As((await RegisterCustomerAsync()).AccessToken);

        var first = await BookAsync(customer, shop, London(Tuesday, 13), shop.StaffId, idempotencyKey: "abc-123");
        var retry = await BookAsync(customer, shop, London(Tuesday, 13), shop.StaffId, idempotencyKey: "abc-123");

        var original = await BookedAsync(first);
        var replayed = await BookedAsync(retry);
        replayed.Id.ShouldBe(original.Id);
        retry.Headers.GetValues("Idempotent-Replayed").ShouldContain("true");

        var misuse = await BookAsync(customer, shop, London(Tuesday, 14), shop.StaffId, idempotencyKey: "abc-123");
        (await ReadProblemAsync(misuse, HttpStatusCode.UnprocessableEntity)).GetProperty("code").GetString().ShouldBe("booking.idempotency_key_reused");
    }

    [Fact]
    public async Task Business_calendar_lists_bookings_for_members_only()
    {
        var shop = await CreateShopAsync();
        var customerAuth = await RegisterCustomerAsync();
        var customer = As(customerAuth.AccessToken);
        await BookedAsync(await BookAsync(customer, shop, London(Tuesday, 9)));
        var url = $"/api/v1/businesses/{shop.BusinessId}/bookings?from={Tuesday:yyyy-MM-dd}&to={Tuesday:yyyy-MM-dd}";

        var calendar = await ReadAsync<List<BookingResponse>>(await As(shop.OwnerToken).GetAsync(url));
        calendar.Single().CustomerName.ShouldBe("Casey Customer");

        (await customer.GetAsync(url)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var mine = await ReadAsync<List<BookingResponse>>(await customer.GetAsync("/api/v1/bookings/me?upcoming=true"));
        mine.Single().CustomerId.ShouldBe(customerAuth.User.Id);
    }
}
