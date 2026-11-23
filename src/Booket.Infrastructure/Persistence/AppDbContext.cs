using Booket.Application.Abstractions;
using Booket.Application.Common;
using Booket.Domain.Bookings;
using Booket.Domain.Businesses;
using Booket.Domain.Catalog;
using Booket.Domain.Notifications;
using Booket.Domain.Staff;
using Booket.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Booket.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IAppDbContext
{
    public DbSet<Business> Businesses => Set<Business>();
    public DbSet<Service> Services => Set<Service>();
    public DbSet<StaffMember> StaffMembers => Set<StaffMember>();
    public DbSet<WorkingHours> WorkingHours => Set<WorkingHours>();
    public DbSet<StaffService> StaffServices => Set<StaffService>();
    public DbSet<TimeOff> TimeOffs => Set<TimeOff>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("btree_gist");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Ids are generated client-side (UUID v7), so EF must never treat a set key as "already persisted".
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            var key = entity.FindPrimaryKey();
            if (key is { Properties: [{ Name: "Id", ClrType: var type } id] } && type == typeof(Guid))
            {
                id.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
            }
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Enums are stored as readable strings; the bookings exclusion constraint relies on 'Confirmed'.
        configurationBuilder.Properties<BookingStatus>().HaveConversion<string>().HaveMaxLength(20);
        configurationBuilder.Properties<Actor>().HaveConversion<string>().HaveMaxLength(20);
        configurationBuilder.Properties<UserRole>().HaveConversion<string>().HaveMaxLength(20);
        configurationBuilder.Properties<NotificationType>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<OutboxStatus>().HaveConversion<string>().HaveMaxLength(20);
        configurationBuilder.Properties<DayOfWeek>().HaveConversion<string>().HaveMaxLength(10);
    }

    /// <summary>Translates PostgreSQL constraint violations into application-level exceptions.</summary>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            throw new UniqueConstraintViolationException(pg.ConstraintName, ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation } pg)
        {
            throw new ExclusionConstraintViolationException(pg.ConstraintName, ex);
        }
    }
}
