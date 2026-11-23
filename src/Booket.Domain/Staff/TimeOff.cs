using Booket.Domain.Common;
using Booket.Domain.Scheduling;

namespace Booket.Domain.Staff;

/// <summary>A one-off absence (holiday, sick day, appointment) that blocks a staff member's availability.</summary>
public sealed class TimeOff : Entity
{
    private TimeOff() { }

    public Guid StaffId { get; private set; }
    public DateTimeOffset StartUtc { get; private set; }
    public DateTimeOffset EndUtc { get; private set; }
    public string? Reason { get; private set; }

    public TimeRange Range => new(StartUtc, EndUtc);

    public static Result<TimeOff> Create(Guid staffId, DateTimeOffset startUtc, DateTimeOffset endUtc, string? reason)
    {
        if (endUtc <= startUtc)
        {
            return Error.Validation("time_off.invalid_range", "Time off must end after it starts.");
        }

        return new TimeOff
        {
            StaffId = staffId,
            StartUtc = startUtc.ToUniversalTime(),
            EndUtc = endUtc.ToUniversalTime(),
            Reason = reason,
        };
    }
}
