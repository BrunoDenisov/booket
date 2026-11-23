using Booket.Application.Abstractions;
using Booket.Application.Common;
using Booket.Application.Features.Businesses;
using Booket.Domain.Bookings;
using Booket.Domain.Common;
using Booket.Domain.Staff;
using Booket.Domain.Users;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Booket.Application.Features.Staff;

public sealed record WorkingHoursDto(DayOfWeek Day, TimeOnly Start, TimeOnly End);

public sealed record StaffResponse(
    Guid Id,
    Guid BusinessId,
    string DisplayName,
    bool IsActive,
    Guid? UserId,
    IReadOnlyList<Guid> ServiceIds,
    IReadOnlyList<WorkingHoursDto> WorkingHours)
{
    public static StaffResponse From(StaffMember s) => new(
        s.Id, s.BusinessId, s.DisplayName, s.IsActive, s.UserId,
        s.Services.Select(x => x.ServiceId).Order().ToList(),
        s.WorkingHours.OrderBy(w => w.Day).ThenBy(w => w.Start).Select(w => new WorkingHoursDto(w.Day, w.Start, w.End)).ToList());
}

public static class StaffErrors
{
    public static readonly Error NotFound = Error.NotFound("staff.not_found", "Staff member was not found.");

    public static readonly Error UnknownServices =
        Error.Validation("staff.unknown_services", "One or more services do not exist in this business.");
}

internal static class StaffQueries
{
    public static IQueryable<StaffMember> WithSchedule(this IQueryable<StaffMember> query) =>
        query.Include(s => s.WorkingHours).Include(s => s.Services);

    public static async Task<bool> AllServicesBelongToAsync(this IAppDbContext db, Guid businessId, IReadOnlyCollection<Guid> serviceIds, CancellationToken ct)
    {
        if (serviceIds.Count == 0)
        {
            return true;
        }

        var distinct = serviceIds.Distinct().ToList();
        var found = await db.Services.CountAsync(s => s.BusinessId == businessId && distinct.Contains(s.Id), ct);
        return found == distinct.Count;
    }
}

// ---- List / get -----------------------------------------------------------------------------

public sealed record ListStaffQuery(Guid BusinessId, Guid? ServiceId = null);

internal sealed class ListStaffHandler(IAppDbContext db, ICurrentUser currentUser) : IHandler<ListStaffQuery, IReadOnlyList<StaffResponse>>
{
    public async Task<Result<IReadOnlyList<StaffResponse>>> HandleAsync(ListStaffQuery request, CancellationToken cancellationToken)
    {
        if (!await db.Businesses.AnyAsync(b => b.Id == request.BusinessId, cancellationToken))
        {
            return BusinessErrors.NotFound;
        }

        var includeInactive = currentUser.IsMemberOf(request.BusinessId);
        var query = db.StaffMembers.AsNoTracking().WithSchedule()
            .Where(s => s.BusinessId == request.BusinessId && (includeInactive || s.IsActive));

        if (request.ServiceId is { } serviceId)
        {
            query = query.Where(s => s.Services.Any(x => x.ServiceId == serviceId));
        }

        var staff = await query.OrderBy(s => s.DisplayName).ToListAsync(cancellationToken);
        return staff.Select(StaffResponse.From).ToList();
    }
}

public sealed record GetStaffQuery(Guid BusinessId, Guid StaffId);

internal sealed class GetStaffHandler(IAppDbContext db) : IHandler<GetStaffQuery, StaffResponse>
{
    public async Task<Result<StaffResponse>> HandleAsync(GetStaffQuery request, CancellationToken cancellationToken)
    {
        var staff = await db.StaffMembers.AsNoTracking().WithSchedule()
            .SingleOrDefaultAsync(s => s.Id == request.StaffId && s.BusinessId == request.BusinessId, cancellationToken);

        return staff is null ? StaffErrors.NotFound : StaffResponse.From(staff);
    }
}

// ---- Create ---------------------------------------------------------------------------------

/// <summary>Optional login for the new staff member.</summary>
public sealed record StaffAccountDto(string Email, string Password);

public sealed record CreateStaffCommand(Guid BusinessId, string DisplayName, IReadOnlyList<Guid>? ServiceIds, StaffAccountDto? Account);

public sealed class CreateStaffValidator : AbstractValidator<CreateStaffCommand>
{
    public CreateStaffValidator()
    {
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.ServiceIds).Must(ids => ids is null || ids.Count <= 100);
        When(x => x.Account is not null, () =>
        {
            RuleFor(x => x.Account!.Email).NotEmpty().EmailAddress().MaximumLength(256);
            RuleFor(x => x.Account!.Password).Password();
        });
    }
}

internal sealed class CreateStaffHandler(IAppDbContext db, IPasswordHasher hasher, TimeProvider clock) : IHandler<CreateStaffCommand, StaffResponse>
{
    public async Task<Result<StaffResponse>> HandleAsync(CreateStaffCommand request, CancellationToken cancellationToken)
    {
        if (!await db.Businesses.AnyAsync(b => b.Id == request.BusinessId, cancellationToken))
        {
            return BusinessErrors.NotFound;
        }

        var serviceIds = request.ServiceIds ?? [];
        if (!await db.AllServicesBelongToAsync(request.BusinessId, serviceIds, cancellationToken))
        {
            return StaffErrors.UnknownServices;
        }

        User? user = null;
        if (request.Account is { } account)
        {
            var email = User.NormalizeEmail(account.Email);
            if (await db.Users.AnyAsync(u => u.Email == email, cancellationToken))
            {
                return Auth.AuthErrors.EmailTaken;
            }

            user = User.Create(email, hasher.Hash(account.Password), request.DisplayName, null, UserRole.Staff, request.BusinessId, clock.GetUtcNow());
            db.Users.Add(user);
        }

        var staff = StaffMember.Create(request.BusinessId, request.DisplayName, user?.Id);
        staff.SetServices(serviceIds);
        db.StaffMembers.Add(staff);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            return Auth.AuthErrors.EmailTaken;
        }

        return StaffResponse.From(staff);
    }
}

// ---- Update ---------------------------------------------------------------------------------

public sealed record UpdateStaffCommand(Guid BusinessId, Guid StaffId, string DisplayName, bool IsActive);

public sealed class UpdateStaffValidator : AbstractValidator<UpdateStaffCommand>
{
    public UpdateStaffValidator() => RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200);
}

internal sealed class UpdateStaffHandler(IAppDbContext db) : IHandler<UpdateStaffCommand, StaffResponse>
{
    public async Task<Result<StaffResponse>> HandleAsync(UpdateStaffCommand request, CancellationToken cancellationToken)
    {
        var staff = await db.StaffMembers.WithSchedule()
            .SingleOrDefaultAsync(s => s.Id == request.StaffId && s.BusinessId == request.BusinessId, cancellationToken);
        if (staff is null)
        {
            return StaffErrors.NotFound;
        }

        staff.Rename(request.DisplayName);
        staff.SetActive(request.IsActive);
        await db.SaveChangesAsync(cancellationToken);
        return StaffResponse.From(staff);
    }
}

// ---- Working hours --------------------------------------------------------------------------

public sealed record SetWorkingHoursCommand(Guid BusinessId, Guid StaffId, IReadOnlyList<WorkingHoursDto> WorkingHours);

public sealed class SetWorkingHoursValidator : AbstractValidator<SetWorkingHoursCommand>
{
    public SetWorkingHoursValidator()
    {
        RuleFor(x => x.WorkingHours).NotNull().Must(w => w.Count <= 50).WithMessage("At most 50 working windows are allowed.");
        RuleForEach(x => x.WorkingHours).ChildRules(w =>
        {
            w.RuleFor(x => x.Day).IsInEnum();
            w.RuleFor(x => x.End).GreaterThan(x => x.Start).WithMessage("End must be after start.");
        });
    }
}

/// <summary>Replaces a staff member's weekly schedule. Existing bookings are not affected.</summary>
internal sealed class SetWorkingHoursHandler(IAppDbContext db) : IHandler<SetWorkingHoursCommand, StaffResponse>
{
    public async Task<Result<StaffResponse>> HandleAsync(SetWorkingHoursCommand request, CancellationToken cancellationToken)
    {
        var staff = await db.StaffMembers.WithSchedule()
            .SingleOrDefaultAsync(s => s.Id == request.StaffId && s.BusinessId == request.BusinessId, cancellationToken);
        if (staff is null)
        {
            return StaffErrors.NotFound;
        }

        var result = staff.SetWorkingHours(request.WorkingHours.Select(w => (w.Day, w.Start, w.End)));
        if (result.IsFailure)
        {
            return result.Error!;
        }

        await db.SaveChangesAsync(cancellationToken);
        return StaffResponse.From(staff);
    }
}

// ---- Services performed ---------------------------------------------------------------------

public sealed record SetStaffServicesCommand(Guid BusinessId, Guid StaffId, IReadOnlyList<Guid> ServiceIds);

public sealed class SetStaffServicesValidator : AbstractValidator<SetStaffServicesCommand>
{
    public SetStaffServicesValidator() => RuleFor(x => x.ServiceIds).NotNull().Must(ids => ids.Count <= 100);
}

internal sealed class SetStaffServicesHandler(IAppDbContext db) : IHandler<SetStaffServicesCommand, StaffResponse>
{
    public async Task<Result<StaffResponse>> HandleAsync(SetStaffServicesCommand request, CancellationToken cancellationToken)
    {
        var staff = await db.StaffMembers.WithSchedule()
            .SingleOrDefaultAsync(s => s.Id == request.StaffId && s.BusinessId == request.BusinessId, cancellationToken);
        if (staff is null)
        {
            return StaffErrors.NotFound;
        }

        if (!await db.AllServicesBelongToAsync(request.BusinessId, request.ServiceIds, cancellationToken))
        {
            return StaffErrors.UnknownServices;
        }

        staff.SetServices(request.ServiceIds);
        await db.SaveChangesAsync(cancellationToken);
        return StaffResponse.From(staff);
    }
}

// ---- Time off -------------------------------------------------------------------------------

public sealed record TimeOffResponse(Guid Id, Guid StaffId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string? Reason, IReadOnlyList<Guid> ConflictingBookingIds)
{
    public static TimeOffResponse From(TimeOff t, IReadOnlyList<Guid>? conflicts = null) =>
        new(t.Id, t.StaffId, t.StartUtc, t.EndUtc, t.Reason, conflicts ?? []);
}

public sealed record AddTimeOffCommand(Guid BusinessId, Guid StaffId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string? Reason);

public sealed class AddTimeOffValidator : AbstractValidator<AddTimeOffCommand>
{
    public AddTimeOffValidator()
    {
        RuleFor(x => x.EndUtc).GreaterThan(x => x.StartUtc).WithMessage("End must be after start.");
        RuleFor(x => x).Must(x => x.EndUtc - x.StartUtc <= TimeSpan.FromDays(366)).WithName("endUtc").WithMessage("Time off cannot exceed one year.");
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}

/// <summary>
/// Blocks a period in the staff member's calendar. Confirmed bookings that overlap are not
/// cancelled automatically; their ids are returned so the business can contact the customers.
/// </summary>
internal sealed class AddTimeOffHandler(IAppDbContext db) : IHandler<AddTimeOffCommand, TimeOffResponse>
{
    public async Task<Result<TimeOffResponse>> HandleAsync(AddTimeOffCommand request, CancellationToken cancellationToken)
    {
        if (!await db.StaffMembers.AnyAsync(s => s.Id == request.StaffId && s.BusinessId == request.BusinessId, cancellationToken))
        {
            return StaffErrors.NotFound;
        }

        var created = TimeOff.Create(request.StaffId, request.StartUtc, request.EndUtc, request.Reason);
        if (created.IsFailure)
        {
            return created.Error!;
        }

        var timeOff = created.Value;
        db.TimeOffs.Add(timeOff);
        await db.SaveChangesAsync(cancellationToken);

        var conflicts = await db.Bookings.AsNoTracking()
            .Where(b => b.StaffId == request.StaffId
                        && b.Status == BookingStatus.Confirmed
                        && b.StartUtc < timeOff.EndUtc
                        && b.BlockedUntilUtc > timeOff.StartUtc)
            .OrderBy(b => b.StartUtc)
            .Select(b => b.Id)
            .ToListAsync(cancellationToken);

        return TimeOffResponse.From(timeOff, conflicts);
    }
}

public sealed record ListTimeOffQuery(Guid BusinessId, Guid StaffId, DateTimeOffset? FromUtc);

internal sealed class ListTimeOffHandler(IAppDbContext db, TimeProvider clock) : IHandler<ListTimeOffQuery, IReadOnlyList<TimeOffResponse>>
{
    public async Task<Result<IReadOnlyList<TimeOffResponse>>> HandleAsync(ListTimeOffQuery request, CancellationToken cancellationToken)
    {
        if (!await db.StaffMembers.AnyAsync(s => s.Id == request.StaffId && s.BusinessId == request.BusinessId, cancellationToken))
        {
            return StaffErrors.NotFound;
        }

        var from = request.FromUtc ?? clock.GetUtcNow();
        var items = await db.TimeOffs.AsNoTracking()
            .Where(t => t.StaffId == request.StaffId && t.EndUtc > from)
            .OrderBy(t => t.StartUtc)
            .ToListAsync(cancellationToken);

        return items.Select(t => TimeOffResponse.From(t)).ToList();
    }
}

public sealed record DeleteTimeOffCommand(Guid BusinessId, Guid StaffId, Guid TimeOffId);

internal sealed class DeleteTimeOffHandler(IAppDbContext db) : IHandler<DeleteTimeOffCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(DeleteTimeOffCommand request, CancellationToken cancellationToken)
    {
        var timeOff = await (
                from t in db.TimeOffs
                join s in db.StaffMembers on t.StaffId equals s.Id
                where t.Id == request.TimeOffId && t.StaffId == request.StaffId && s.BusinessId == request.BusinessId
                select t)
            .SingleOrDefaultAsync(cancellationToken);

        if (timeOff is null)
        {
            return Error.NotFound("time_off.not_found", "Time off was not found.");
        }

        db.TimeOffs.Remove(timeOff);
        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}
