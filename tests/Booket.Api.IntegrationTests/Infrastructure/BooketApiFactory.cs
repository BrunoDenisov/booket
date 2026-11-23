using System.Collections.Concurrent;
using Booket.Application.Abstractions;
using Booket.Infrastructure.Notifications;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Respawn;
using Testcontainers.PostgreSql;

namespace Booket.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Boots the real API against a throwaway PostgreSQL container. Time and email delivery are
/// replaced with test doubles; the background dispatcher is disabled so tests drive it explicitly.
/// </summary>
public sealed class BooketApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>Monday 1 June 2026, 07:00 in London (BST).</summary>
    public static readonly DateTimeOffset StartOfTest = new(2026, 6, 1, 6, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private Respawner? _respawner;

    public TestClock Clock { get; } = new(StartOfTest);
    public CapturingEmailSender Emails { get; } = new();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        // Starting the server applies migrations (Database:MigrateOnStartup=true).
        _ = Server;

        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        _respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = ["public"],
            TablesToIgnore = ["__EFMigrationsHistory"],
        });
    }

    /// <summary>Empties every table and resets the clock and captured emails.</summary>
    public async Task ResetAsync()
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await _respawner!.ResetAsync(connection);
        Clock.SetUtcNow(StartOfTest);
        Emails.Clear();
    }

    /// <summary>Runs one outbox dispatch cycle, as the background service would.</summary>
    public async Task<int> DispatchOutboxAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OutboxProcessor>().ProcessBatchAsync(CancellationToken.None);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Booket", _postgres.GetConnectionString());
        builder.UseSetting("Jwt:SigningKey", "integration-tests-signing-key-0123456789abcdef");
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Database:SeedDemoData", "false");
        builder.UseSetting("Outbox:DispatcherEnabled", "false");
        builder.UseSetting("RateLimiting:AuthPermitsPerMinute", "10000");
        builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Emails);
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

public sealed class CapturingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();

    public IReadOnlyList<EmailMessage> Sent => _sent.ToArray();

    /// <summary>When set, every send attempt fails (to exercise retries).</summary>
    public bool FailAll { get; set; }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (FailAll)
        {
            throw new InvalidOperationException("SMTP unavailable (simulated).");
        }

        _sent.Enqueue(message);
        return Task.CompletedTask;
    }

    public void Clear()
    {
        _sent.Clear();
        FailAll = false;
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<BooketApiFactory>
{
    public const string Name = "api";
}
