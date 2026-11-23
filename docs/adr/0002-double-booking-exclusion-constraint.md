# ADR 0002: Prevent double booking with a PostgreSQL exclusion constraint

- **Status:** Accepted
- **Date:** 2026-11-23

## Context

Two customers can request the same slot at the same moment, possibly through different API
instances. A "check availability, then insert" sequence in application code has a race window
between the check and the insert, so it cannot guarantee anything on its own.

Common alternatives:

| Option | Problem |
|---|---|
| Serializable transactions | Correct, but needs retry logic for every booking write and causes contention under load |
| `SELECT ... FOR UPDATE` on the staff row | Serializes *all* bookings for a staff member, and is easy to forget on new write paths |
| Unique index on `(staff_id, start)` | Only stops identical start times, not overlapping ranges (09:00-09:40 vs 09:15-09:45) |
| Distributed lock (Redis) | Adds infrastructure, and the lock and the data can disagree |

## Decision

Let the database enforce the invariant:

```sql
ALTER TABLE bookings
ADD CONSTRAINT ex_bookings_staff_no_overlap
EXCLUDE USING gist (
    staff_id WITH =,
    tstzrange(start_utc, blocked_until_utc, '[)') WITH &&
)
WHERE (status = 'Confirmed');
```

- `blocked_until_utc` includes the service's buffer time, so clean-up time is protected too.
- The range is half-open (`[)`), so back-to-back appointments are allowed.
- The partial `WHERE` clause means cancelled bookings free their slot immediately.
- `btree_gist` is enabled through EF Core (`HasPostgresExtension`); the constraint itself is raw SQL in the
  initial migration.

The application still validates the slot first, using the same `AvailabilityService` the availability
endpoint uses, so normal failures get a precise error. When the constraint fires (SQLSTATE `23P01`),
`AppDbContext` throws `ExclusionConstraintViolationException`. The booking handler then either tries the
next free staff member (for "any staff" requests) or returns **409 Conflict** (`booking.slot_unavailable`).

## Consequences

- Double booking is impossible regardless of the number of instances or of future code paths
  (admin tools, imports, bugs).
- The integration test `Concurrent_requests_for_the_same_slot_produce_exactly_one_booking` fires 10
  parallel requests and asserts exactly one 201 and nine 409s.
- This ties the system to PostgreSQL, which is an intentional choice.
