using Booket.Domain.Common;

namespace Booket.Domain.Businesses;

/// <summary>The tenant. Every service, staff member and booking belongs to exactly one business.</summary>
public sealed class Business : Entity
{
    private Business() { }

    public string Name { get; private set; } = null!;
    public string Slug { get; private set; } = null!;

    /// <summary>IANA time zone id (e.g. <c>Europe/London</c>). Working hours are interpreted in this zone.</summary>
    public string TimeZoneId { get; private set; } = null!;

    public BookingPolicy Policy { get; private set; } = BookingPolicy.Default;
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public TimeZoneInfo TimeZone => TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);

    public static Business Create(string name, string slug, string timeZoneId, DateTimeOffset now, BookingPolicy? policy = null) => new()
    {
        Name = name.Trim(),
        Slug = slug.Trim().ToLowerInvariant(),
        TimeZoneId = timeZoneId,
        Policy = policy ?? BookingPolicy.Default,
        CreatedAtUtc = now,
    };

    public void Update(string name, string timeZoneId, BookingPolicy policy)
    {
        Name = name.Trim();
        TimeZoneId = timeZoneId;
        Policy = policy;
    }
}
