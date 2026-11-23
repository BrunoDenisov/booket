using Booket.Domain.Bookings;
using Booket.Domain.Businesses;
using Booket.Domain.Catalog;
using Booket.Domain.Notifications;
using Booket.Domain.Staff;
using Booket.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Booket.Application.Abstractions;

/// <summary>
/// Unit of work over the application's aggregates. Use cases query and change entities through
/// this interface; the infrastructure layer supplies the EF Core/PostgreSQL implementation.
/// </summary>
public interface IAppDbContext
{
    DbSet<Business> Businesses { get; }
    DbSet<Service> Services { get; }
    DbSet<StaffMember> StaffMembers { get; }
    DbSet<WorkingHours> WorkingHours { get; }
    DbSet<StaffService> StaffServices { get; }
    DbSet<TimeOff> TimeOffs { get; }
    DbSet<Booking> Bookings { get; }
    DbSet<IdempotencyRecord> IdempotencyRecords { get; }
    DbSet<User> Users { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<OutboxMessage> OutboxMessages { get; }

    /// <summary>
    /// Persists changes. Throws <see cref="Common.UniqueConstraintViolationException"/> or
    /// <see cref="Common.ExclusionConstraintViolationException"/> for constraint violations.
    /// </summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
