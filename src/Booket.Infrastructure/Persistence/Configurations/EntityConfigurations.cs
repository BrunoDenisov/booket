using Booket.Domain.Bookings;
using Booket.Domain.Businesses;
using Booket.Domain.Catalog;
using Booket.Domain.Notifications;
using Booket.Domain.Staff;
using Booket.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Booket.Infrastructure.Persistence.Configurations;

internal sealed class BusinessConfiguration : IEntityTypeConfiguration<Business>
{
    public void Configure(EntityTypeBuilder<Business> builder)
    {
        builder.Property(b => b.Name).HasMaxLength(200);
        builder.Property(b => b.Slug).HasMaxLength(64);
        builder.Property(b => b.TimeZoneId).HasMaxLength(64);
        builder.HasIndex(b => b.Slug).IsUnique();
        builder.Ignore(b => b.TimeZone);

        builder.ComplexProperty(b => b.Policy, policy =>
        {
            policy.Property(p => p.MinLeadTime).HasColumnName("policy_min_lead_time");
            policy.Property(p => p.MaxAdvanceDays).HasColumnName("policy_max_advance_days");
            policy.Property(p => p.CancellationCutoff).HasColumnName("policy_cancellation_cutoff");
            policy.Property(p => p.SlotGranularityMinutes).HasColumnName("policy_slot_granularity_minutes");
        });
    }
}

internal sealed class ServiceConfiguration : IEntityTypeConfiguration<Service>
{
    public void Configure(EntityTypeBuilder<Service> builder)
    {
        builder.Property(s => s.Name).HasMaxLength(200);
        builder.Property(s => s.Description).HasMaxLength(2000);
        builder.Property(s => s.Price).HasPrecision(10, 2);
        builder.Property(s => s.Currency).HasMaxLength(3).IsFixedLength();
        builder.Ignore(s => s.BlockedDuration);
        builder.HasOne<Business>().WithMany().HasForeignKey(s => s.BusinessId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(s => new { s.BusinessId, s.Name });
    }
}

internal sealed class StaffMemberConfiguration : IEntityTypeConfiguration<StaffMember>
{
    public void Configure(EntityTypeBuilder<StaffMember> builder)
    {
        builder.ToTable("staff_members");
        builder.Property(s => s.DisplayName).HasMaxLength(200);
        builder.HasOne<Business>().WithMany().HasForeignKey(s => s.BusinessId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithOne().HasForeignKey<StaffMember>(s => s.UserId).OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(s => s.WorkingHours).WithOne().HasForeignKey(w => w.StaffId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(s => s.WorkingHours).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(s => s.Services).WithOne().HasForeignKey(x => x.StaffId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(s => s.Services).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class WorkingHoursConfiguration : IEntityTypeConfiguration<WorkingHours>
{
    public void Configure(EntityTypeBuilder<WorkingHours> builder)
    {
        builder.ToTable("working_hours", t => t.HasCheckConstraint("ck_working_hours_end_after_start", "end_local > start_local"));
        builder.Property(w => w.Start).HasColumnName("start_local");
        builder.Property(w => w.End).HasColumnName("end_local");
        builder.HasIndex(w => new { w.StaffId, w.Day });
    }
}

internal sealed class StaffServiceConfiguration : IEntityTypeConfiguration<StaffService>
{
    public void Configure(EntityTypeBuilder<StaffService> builder)
    {
        builder.ToTable("staff_services");
        builder.HasKey(x => new { x.StaffId, x.ServiceId });
        builder.HasOne<Service>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class TimeOffConfiguration : IEntityTypeConfiguration<TimeOff>
{
    public void Configure(EntityTypeBuilder<TimeOff> builder)
    {
        builder.ToTable("time_off", t => t.HasCheckConstraint("ck_time_off_end_after_start", "end_utc > start_utc"));
        builder.Property(t => t.Reason).HasMaxLength(500);
        builder.Ignore(t => t.Range);
        builder.HasOne<StaffMember>().WithMany().HasForeignKey(t => t.StaffId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(t => new { t.StaffId, t.StartUtc });
    }
}

internal sealed class BookingConfiguration : IEntityTypeConfiguration<Booking>
{
    /// <summary>
    /// Created by raw SQL in the initial migration:
    /// <c>EXCLUDE USING gist (staff_id WITH =, tstzrange(start_utc, blocked_until_utc) WITH &amp;&amp;) WHERE (status = 'Confirmed')</c>.
    /// </summary>
    public const string NoOverlapConstraint = "ex_bookings_staff_no_overlap";

    public void Configure(EntityTypeBuilder<Booking> builder)
    {
        builder.ToTable("bookings", t => t.HasCheckConstraint("ck_bookings_times", "start_utc < end_utc AND end_utc <= blocked_until_utc"));
        builder.Property(b => b.Notes).HasMaxLength(1000);
        builder.Property(b => b.CancellationReason).HasMaxLength(500);
        builder.Property(b => b.Version).IsRowVersion();
        builder.Ignore(b => b.Blocked);

        builder.HasOne<Business>().WithMany().HasForeignKey(b => b.BusinessId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StaffMember>().WithMany().HasForeignKey(b => b.StaffId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Service>().WithMany().HasForeignKey(b => b.ServiceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(b => b.CustomerId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(b => new { b.BusinessId, b.StartUtc });
        builder.HasIndex(b => new { b.StaffId, b.StartUtc });
        builder.HasIndex(b => new { b.CustomerId, b.StartUtc });
    }
}

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("idempotency_records");
        builder.HasKey(r => new { r.UserId, r.Key });
        builder.Property(r => r.Key).HasMaxLength(100);
        builder.Property(r => r.RequestHash).HasMaxLength(64);
        builder.HasOne<Booking>().WithMany().HasForeignKey(r => r.BookingId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.Property(u => u.Email).HasMaxLength(256);
        builder.Property(u => u.PasswordHash).HasMaxLength(512);
        builder.Property(u => u.FullName).HasMaxLength(200);
        builder.Property(u => u.Phone).HasMaxLength(32);
        builder.HasIndex(u => u.Email).IsUnique();
        builder.HasOne<Business>().WithMany().HasForeignKey(u => u.BusinessId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.Property(t => t.TokenHash).HasMaxLength(64);
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => t.FamilyId);
        builder.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");
        builder.Property(m => m.LastError).HasMaxLength(2000);
        builder.HasOne<Booking>().WithMany().HasForeignKey(m => m.BookingId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(m => m.BookingId);

        // The dispatcher only ever scans undelivered messages; keep that index small.
        builder.HasIndex(m => new { m.Status, m.DueAtUtc })
            .HasFilter("status IN ('Pending', 'Processing')")
            .HasDatabaseName("ix_outbox_messages_undelivered");
    }
}
