# ADR 0003: Reminders through a transactional outbox and a hosted dispatcher

- **Status:** Accepted
- **Date:** 2026-11-23

## Context

A booking change must produce emails: a confirmation now, reminders 24 h and 2 h before, and a
cancellation or reschedule notice. Sending the email inside the HTTP request couples the API's latency
and availability to the SMTP server. It also risks two inconsistencies: an email sent for a
transaction that later rolled back, or a committed booking whose email was never sent.

## Decision

- **Transactional outbox.** Handlers write `outbox_messages` rows in the same `SaveChanges` as the
  booking change. Each row has a type and a `due_at_utc`; reminders are simply rows due in the future.
  A cancellation marks pending reminders `Cancelled`, and a reschedule replaces them.
- **Dispatcher.** `OutboxDispatcher` is a `BackgroundService` with a `PeriodicTimer`. On each tick it
  calls `OutboxProcessor`, which claims a batch atomically:

  ```sql
  UPDATE outbox_messages SET status = 'Processing', locked_until_utc = @lease, attempts = attempts + 1
  WHERE id IN (SELECT id FROM outbox_messages
               WHERE (status = 'Pending' AND due_at_utc <= @now AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= @now))
                  OR (status = 'Processing' AND locked_until_utc < @now)
               ORDER BY due_at_utc LIMIT @batch
               FOR UPDATE SKIP LOCKED)
  RETURNING *;
  ```

  `SKIP LOCKED` lets any number of API instances dispatch at once without double-sending. The lease
  means a message claimed by a crashed instance is picked up again once the lease expires.
- **Retries.** A failure goes back to `Pending` with exponential backoff (`base * 2^(attempt-1)`) until
  `MaxAttempts`, after which it becomes `Failed` and the last error is kept.
- **Safety net.** At send time, the composer re-checks the booking. A reminder for a booking that is no
  longer confirmed is skipped, not sent.

Hangfire and Quartz were considered. They are good tools, but they add a dependency, a schema and
a dashboard. Here the job is one table and about 100 lines of code that are easy to read and test.

## Consequences

- At-least-once delivery. A crash between "SMTP accepted" and "row marked Sent" can produce a
  duplicate email, which is acceptable for reminders.
- Delivery latency is bounded by `Outbox:PollInterval` (10 s by default, 5 s in Compose).
- Tests drive time with a controllable `TimeProvider` and run a dispatch cycle directly. They assert
  that the confirmation is sent immediately, the reminders exactly when due, and that backoff and
  cancellation are respected.
- The dispatcher can be disabled per instance (`Outbox:DispatcherEnabled=false`) to run a dedicated
  worker later without code changes.
