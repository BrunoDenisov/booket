using Booket.Application.Abstractions;
using Booket.Application.Common;
using Booket.Application.Features.Notifications;
using Booket.Domain.Notifications;
using Booket.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Booket.Infrastructure.Notifications;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>Run the background dispatcher in this process.</summary>
    public bool DispatcherEnabled { get; set; } = true;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);
    public int BatchSize { get; set; } = 20;
    public int MaxAttempts { get; set; } = 5;
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How long a claimed message is leased to one dispatcher before another may reclaim it.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// Delivers one batch of due outbox messages. Safe to run concurrently on many instances:
/// messages are claimed with <c>FOR UPDATE SKIP LOCKED</c> and leased, so each is processed by
/// exactly one dispatcher at a time and a crashed dispatcher's lease simply expires.
/// </summary>
public sealed class OutboxProcessor(
    AppDbContext db,
    NotificationComposer composer,
    IEmailSender emailSender,
    IOptions<OutboxOptions> options,
    TimeProvider clock,
    ILogger<OutboxProcessor> logger)
{
    /// <returns>The number of messages claimed.</returns>
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        var leaseUntil = now + o.LeaseDuration;

        var messages = await db.OutboxMessages
            .FromSql($"""
                UPDATE outbox_messages
                SET status = 'Processing', locked_until_utc = {leaseUntil}, attempts = attempts + 1
                WHERE id IN (
                    SELECT id FROM outbox_messages
                    WHERE (status = 'Pending' AND due_at_utc <= {now} AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= {now}))
                       OR (status = 'Processing' AND locked_until_utc < {now})
                    ORDER BY due_at_utc
                    LIMIT {o.BatchSize}
                    FOR UPDATE SKIP LOCKED)
                RETURNING *
                """)
            .ToListAsync(cancellationToken);

        foreach (var message in messages.OrderBy(m => m.DueAtUtc))
        {
            await DeliverAsync(message, o, cancellationToken);
        }

        return messages.Count;
    }

    private async Task DeliverAsync(OutboxMessage message, OutboxOptions o, CancellationToken cancellationToken)
    {
        using var activity = BooketTelemetry.ActivitySource.StartActivity("outbox deliver");
        activity?.SetTag("booket.notification.type", message.Type.ToString());
        activity?.SetTag("booket.booking.id", message.BookingId);
        var typeTag = new KeyValuePair<string, object?>("type", message.Type.ToString());

        try
        {
            switch (await composer.ComposeAsync(message, cancellationToken))
            {
                case ComposeResult.Send send:
                    await emailSender.SendAsync(send.Email, cancellationToken);
                    message.MarkSent(clock.GetUtcNow());
                    BooketTelemetry.NotificationsSent.Add(1, typeTag);
                    logger.LogInformation("Sent {NotificationType} for booking {BookingId}", message.Type, message.BookingId);
                    break;

                case ComposeResult.Skip skip:
                    message.MarkCancelled(clock.GetUtcNow());
                    logger.LogInformation("Skipped {NotificationType} for booking {BookingId}: {Reason}", message.Type, message.BookingId, skip.Reason);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            message.MarkFailed(ex.Message, clock.GetUtcNow(), o.MaxAttempts, o.RetryBaseDelay);
            BooketTelemetry.NotificationsFailed.Add(1, typeTag);
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            logger.LogWarning(ex, "Delivering {NotificationType} for booking {BookingId} failed (attempt {Attempt}/{MaxAttempts})",
                message.Type, message.BookingId, message.Attempts, o.MaxAttempts);
        }

        // Persist each outcome on its own so one bad message cannot roll back the rest of the batch.
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
