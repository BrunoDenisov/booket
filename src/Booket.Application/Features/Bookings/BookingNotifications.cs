using Booket.Application.Abstractions;
using Booket.Domain.Bookings;
using Booket.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Booket.Application.Features.Bookings;

public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    private static readonly TimeSpan[] DefaultReminderOffsets = [TimeSpan.FromHours(24), TimeSpan.FromHours(2)];

    /// <summary>How long before the appointment each reminder is sent. Defaults to 24h and 2h.</summary>
    public TimeSpan[]? ReminderOffsets { get; set; }

    public IReadOnlyList<TimeSpan> EffectiveReminderOffsets => ReminderOffsets is { Length: > 0 } offsets ? offsets : DefaultReminderOffsets;
}

/// <summary>
/// Writes notification messages to the outbox as part of the caller's unit of work. Nothing is
/// sent here; the background dispatcher delivers messages once the transaction has committed.
/// </summary>
public sealed class BookingNotifications(IAppDbContext db, IOptions<NotificationOptions> options, TimeProvider clock)
{
    public IReadOnlyList<OutboxMessage> BookingCreated(Booking booking)
    {
        var now = clock.GetUtcNow();
        var messages = new List<OutboxMessage> { OutboxMessage.Schedule(booking.Id, NotificationType.BookingConfirmed, now, now) };
        messages.AddRange(Reminders(booking, now));
        db.OutboxMessages.AddRange(messages);
        return messages;
    }

    public async Task BookingRescheduledAsync(Booking booking, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await CancelPendingRemindersAsync(booking.Id, now, ct);
        db.OutboxMessages.Add(OutboxMessage.Schedule(booking.Id, NotificationType.BookingRescheduled, now, now));
        db.OutboxMessages.AddRange(Reminders(booking, now));
    }

    public async Task BookingCancelledAsync(Booking booking, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await CancelPendingRemindersAsync(booking.Id, now, ct);
        db.OutboxMessages.Add(OutboxMessage.Schedule(booking.Id, NotificationType.BookingCancelled, now, now));
    }

    private IEnumerable<OutboxMessage> Reminders(Booking booking, DateTimeOffset now) =>
        options.Value.EffectiveReminderOffsets
            .Select(offset => booking.StartUtc - offset)
            .Where(due => due > now)
            .Select(due => OutboxMessage.Schedule(booking.Id, NotificationType.Reminder, due, now));

    private async Task CancelPendingRemindersAsync(Guid bookingId, DateTimeOffset now, CancellationToken ct)
    {
        var pending = await db.OutboxMessages
            .Where(m => m.BookingId == bookingId && m.Type == NotificationType.Reminder && m.Status == OutboxStatus.Pending)
            .ToListAsync(ct);

        foreach (var message in pending)
        {
            message.MarkCancelled(now);
        }
    }
}
