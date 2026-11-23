using Booket.Domain.Common;

namespace Booket.Domain.Notifications;

public enum NotificationType
{
    BookingConfirmed,
    BookingRescheduled,
    BookingCancelled,
    Reminder,
}

public enum OutboxStatus
{
    Pending,
    Processing,
    Sent,
    Failed,
    Cancelled,
}

/// <summary>
/// A notification scheduled for delivery. Written in the same transaction as the booking change
/// (transactional outbox) and delivered by a background dispatcher, so a crash can never lose
/// or invent a notification.
/// </summary>
public sealed class OutboxMessage : Entity
{
    private OutboxMessage() { }

    public Guid BookingId { get; private set; }
    public NotificationType Type { get; private set; }
    public DateTimeOffset DueAtUtc { get; private set; }
    public OutboxStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset? NextAttemptAtUtc { get; private set; }
    public DateTimeOffset? LockedUntilUtc { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? ProcessedAtUtc { get; private set; }

    public static OutboxMessage Schedule(Guid bookingId, NotificationType type, DateTimeOffset dueAtUtc, DateTimeOffset now) => new()
    {
        BookingId = bookingId,
        Type = type,
        DueAtUtc = dueAtUtc,
        Status = OutboxStatus.Pending,
        CreatedAtUtc = now,
    };

    public void MarkSent(DateTimeOffset now)
    {
        Status = OutboxStatus.Sent;
        ProcessedAtUtc = now;
        LockedUntilUtc = null;
        LastError = null;
    }

    public void MarkCancelled(DateTimeOffset now)
    {
        Status = OutboxStatus.Cancelled;
        ProcessedAtUtc = now;
        LockedUntilUtc = null;
    }

    /// <summary>Records a failed attempt; retries with exponential backoff until <paramref name="maxAttempts"/>.</summary>
    public void MarkFailed(string error, DateTimeOffset now, int maxAttempts, TimeSpan baseDelay)
    {
        LastError = error.Length > 2000 ? error[..2000] : error;
        LockedUntilUtc = null;

        if (Attempts >= maxAttempts)
        {
            Status = OutboxStatus.Failed;
            ProcessedAtUtc = now;
            return;
        }

        Status = OutboxStatus.Pending;
        NextAttemptAtUtc = now + (baseDelay * Math.Pow(2, Attempts - 1));
    }
}
