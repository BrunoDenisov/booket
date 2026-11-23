namespace Booket.Domain.Businesses;

/// <summary>Per-business rules that shape availability and customer self-service.</summary>
public sealed record BookingPolicy
{
    public static readonly BookingPolicy Default = new();

    /// <summary>How far in advance a slot must start, relative to now (e.g. no bookings in the next hour).</summary>
    public TimeSpan MinLeadTime { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How many days ahead customers may book.</summary>
    public int MaxAdvanceDays { get; init; } = 60;

    /// <summary>Customers cannot cancel or reschedule within this window before the appointment.</summary>
    public TimeSpan CancellationCutoff { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Slot start times are aligned to this grid, measured from the start of each working window.</summary>
    public int SlotGranularityMinutes { get; init; } = 15;
}
