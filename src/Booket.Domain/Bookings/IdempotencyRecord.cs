namespace Booket.Domain.Bookings;

/// <summary>
/// Remembers which booking a client's <c>Idempotency-Key</c> produced, so a retried
/// request returns the original booking instead of creating a duplicate.
/// </summary>
public sealed class IdempotencyRecord
{
    private IdempotencyRecord() { }

    public Guid UserId { get; private set; }
    public string Key { get; private set; } = null!;
    public string RequestHash { get; private set; } = null!;
    public Guid BookingId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static IdempotencyRecord Create(Guid userId, string key, string requestHash, Guid bookingId, DateTimeOffset now) => new()
    {
        UserId = userId,
        Key = key,
        RequestHash = requestHash,
        BookingId = bookingId,
        CreatedAtUtc = now,
    };
}
