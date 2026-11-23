using Booket.Application.Abstractions;
using Booket.Domain.Businesses;
using Booket.Domain.Catalog;
using Booket.Domain.Staff;
using Booket.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Booket.Infrastructure.Persistence;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Apply EF Core migrations at startup. Convenient for Docker Compose and development.</summary>
    public bool MigrateOnStartup { get; set; }

    /// <summary>Seed a demo business, staff, services and users when the database is empty.</summary>
    public bool SeedDemoData { get; set; }

    public string DemoPassword { get; set; } = "Passw0rd!";
}

public static class DatabaseInitializer
{
    public static async Task InitializeDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseInitializer));

        if (options.MigrateOnStartup)
        {
            logger.LogInformation("Applying database migrations");
            await db.Database.MigrateAsync(ct);
        }

        if (options.SeedDemoData && !await db.Businesses.AnyAsync(ct))
        {
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
            await SeedDemoDataAsync(db, hasher, clock.GetUtcNow(), options.DemoPassword, ct);
            logger.LogInformation("Seeded demo data (password for all demo users: see Database:DemoPassword)");
        }
    }

    private static async Task SeedDemoDataAsync(AppDbContext db, IPasswordHasher hasher, DateTimeOffset now, string password, CancellationToken ct)
    {
        var hash = hasher.Hash(password);

        var shop = Business.Create("Demo Barbershop", "demo-barbershop", "Europe/London", now, new BookingPolicy
        {
            MinLeadTime = TimeSpan.FromHours(1),
            MaxAdvanceDays = 60,
            CancellationCutoff = TimeSpan.FromHours(12),
            SlotGranularityMinutes = 15,
        });

        var haircut = Service.Create(shop.Id, "Haircut", "Classic cut and style", TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(10), 25m, "GBP");
        var beard = Service.Create(shop.Id, "Beard trim", "Shape and line-up", TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(5), 15m, "GBP");
        var combo = Service.Create(shop.Id, "Haircut & beard", "The full works", TimeSpan.FromMinutes(50), TimeSpan.FromMinutes(10), 35m, "GBP");

        var alexUser = User.Create("alex@demo.booket.local", hash, "Alex Barber", null, UserRole.Staff, shop.Id, now);
        var alex = StaffMember.Create(shop.Id, "Alex", alexUser.Id);
        alex.SetServices([haircut.Id, beard.Id, combo.Id]);
        alex.SetWorkingHours(Weekdays(new TimeOnly(9, 0), new TimeOnly(17, 0))
            .Append((DayOfWeek.Saturday, new TimeOnly(10, 0), new TimeOnly(14, 0))));

        var sam = StaffMember.Create(shop.Id, "Sam");
        sam.SetServices([haircut.Id, beard.Id]);
        sam.SetWorkingHours(Weekdays(new TimeOnly(10, 0), new TimeOnly(13, 0))
            .Concat(Weekdays(new TimeOnly(14, 0), new TimeOnly(19, 0))));

        db.Businesses.Add(shop);
        db.Services.AddRange(haircut, beard, combo);
        db.Users.AddRange(
            User.Create("admin@booket.local", hash, "Platform Admin", null, UserRole.Admin, null, now),
            User.Create("owner@demo.booket.local", hash, "Olivia Owner", null, UserRole.Owner, shop.Id, now),
            User.Create("customer@demo.booket.local", hash, "Casey Customer", "+44 7700 900123", UserRole.Customer, null, now),
            alexUser);
        db.StaffMembers.AddRange(alex, sam);

        await db.SaveChangesAsync(ct);
    }

    private static IEnumerable<(DayOfWeek, TimeOnly, TimeOnly)> Weekdays(TimeOnly start, TimeOnly end) =>
        new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }
            .Select(d => (d, start, end));
}
