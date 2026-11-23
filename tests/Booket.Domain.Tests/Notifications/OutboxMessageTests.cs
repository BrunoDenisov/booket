using Booket.Domain.Notifications;

namespace Booket.Domain.Tests.Notifications;

public sealed class OutboxMessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static OutboxMessage ClaimedMessage(int attempts)
    {
        var message = OutboxMessage.Schedule(Guid.NewGuid(), NotificationType.Reminder, Now, Now);
        // Attempts are incremented by the dispatcher's claim query; simulate that here.
        typeof(OutboxMessage).GetProperty(nameof(OutboxMessage.Attempts))!.SetValue(message, attempts);
        return message;
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    public void Failure_schedules_retry_with_exponential_backoff(int attempts, int expectedMinutes)
    {
        var message = ClaimedMessage(attempts);

        message.MarkFailed("smtp down", Now, maxAttempts: 5, baseDelay: TimeSpan.FromMinutes(1));

        message.Status.ShouldBe(OutboxStatus.Pending);
        message.NextAttemptAtUtc.ShouldBe(Now.AddMinutes(expectedMinutes));
        message.LastError.ShouldBe("smtp down");
    }

    [Fact]
    public void Failure_after_max_attempts_is_terminal()
    {
        var message = ClaimedMessage(5);

        message.MarkFailed("smtp down", Now, maxAttempts: 5, baseDelay: TimeSpan.FromMinutes(1));

        message.Status.ShouldBe(OutboxStatus.Failed);
        message.ProcessedAtUtc.ShouldBe(Now);
    }
}
