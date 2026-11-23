using Booket.Domain.Common;

namespace Booket.Domain.Staff;

/// <summary>A person who performs services (barber, therapist, instructor) with their own weekly schedule.</summary>
public sealed class StaffMember : Entity
{
    private readonly List<WorkingHours> _workingHours = [];
    private readonly List<StaffService> _services = [];

    private StaffMember() { }

    public Guid BusinessId { get; private set; }

    /// <summary>Optional login for this staff member (role <c>Staff</c>).</summary>
    public Guid? UserId { get; private set; }

    public string DisplayName { get; private set; } = null!;
    public bool IsActive { get; private set; } = true;

    public IReadOnlyCollection<WorkingHours> WorkingHours => _workingHours.AsReadOnly();
    public IReadOnlyCollection<StaffService> Services => _services.AsReadOnly();

    public static StaffMember Create(Guid businessId, string displayName, Guid? userId = null) => new()
    {
        BusinessId = businessId,
        DisplayName = displayName.Trim(),
        UserId = userId,
    };

    public void Rename(string displayName) => DisplayName = displayName.Trim();

    public void SetActive(bool isActive) => IsActive = isActive;

    public void LinkUser(Guid userId) => UserId = userId;

    /// <summary>Replaces the weekly schedule. Windows on the same day must not overlap.</summary>
    public Result SetWorkingHours(IEnumerable<(DayOfWeek Day, TimeOnly Start, TimeOnly End)> windows)
    {
        var list = windows.OrderBy(w => w.Day).ThenBy(w => w.Start).ToList();

        foreach (var w in list)
        {
            if (w.End <= w.Start)
            {
                return Error.Validation("staff.invalid_working_hours", $"{w.Day}: end {w.End} must be after start {w.Start}.");
            }
        }

        for (var i = 1; i < list.Count; i++)
        {
            if (list[i].Day == list[i - 1].Day && list[i].Start < list[i - 1].End)
            {
                return Error.Validation("staff.overlapping_working_hours", $"{list[i].Day}: working windows overlap.");
            }
        }

        _workingHours.Clear();
        _workingHours.AddRange(list.Select(w => new WorkingHours(Id, w.Day, w.Start, w.End)));
        return Result.Success();
    }

    public void SetServices(IEnumerable<Guid> serviceIds)
    {
        _services.Clear();
        _services.AddRange(serviceIds.Distinct().Select(id => new StaffService(Id, id)));
    }

    public bool Performs(Guid serviceId) => _services.Any(s => s.ServiceId == serviceId);
}

/// <summary>A recurring weekly working window, in the business's local time.</summary>
public sealed class WorkingHours : Entity
{
    private WorkingHours() { }

    internal WorkingHours(Guid staffId, DayOfWeek day, TimeOnly start, TimeOnly end)
    {
        StaffId = staffId;
        Day = day;
        Start = start;
        End = end;
    }

    public Guid StaffId { get; private set; }
    public DayOfWeek Day { get; private set; }
    public TimeOnly Start { get; private set; }
    public TimeOnly End { get; private set; }
}

/// <summary>Which services a staff member can perform.</summary>
public sealed class StaffService
{
    private StaffService() { }

    internal StaffService(Guid staffId, Guid serviceId)
    {
        StaffId = staffId;
        ServiceId = serviceId;
    }

    public Guid StaffId { get; private set; }
    public Guid ServiceId { get; private set; }
}
