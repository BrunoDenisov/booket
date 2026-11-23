# ADR 0001: Clean Architecture with plain handlers

- **Status:** Accepted
- **Date:** 2026-11-23

## Context

Booket has a small but non-trivial core: slot calculation, booking rules (lead time, cutoff,
rescheduling), and multi-tenant access control. That logic must be testable without a web server
or a database, and it must not depend on infrastructure choices such as PostgreSQL or SMTP.

## Decision

The solution has four projects with a strict inward dependency rule:

| Project | Responsibility | Depends on |
|---|---|---|
| `Booket.Domain` | Entities, invariants, `AvailabilityCalculator`, `Result`/`Error` | nothing (no packages) |
| `Booket.Application` | Use cases (handlers), validators, ports (`IAppDbContext`, `IEmailSender`, `ITokenService`) | Domain |
| `Booket.Infrastructure` | EF Core/PostgreSQL, JWT, password hashing, outbox dispatcher, SMTP | Application |
| `Booket.Api` | Minimal API endpoints, auth policies, ProblemDetails, OpenAPI, telemetry wiring | Application, Infrastructure |

- Use cases are plain `IHandler<TRequest, TResponse>` classes, registered by assembly scanning.
  Each one is wrapped in a small `HandlerPipeline` that starts a tracing span and runs FluentValidation.
  MediatR is not used: it adds indirection (and now a commercial licence) without adding capability here.
- Expected failures (not found, slot taken, cutoff passed) are returned as `Result<T>` with a typed
  `Error`. Exceptions are kept for the unexpected. The API maps `ErrorType` to HTTP status codes in one place.
- The application layer uses EF Core's `DbSet<T>` through `IAppDbContext` instead of a repository per
  aggregate. This is a pragmatic trade-off: LINQ stays expressive, and the database is still swappable at
  the provider level.

## Consequences

- The availability engine and booking rules have fast unit tests with no mocks.
- Adding a use case means adding one request record, one validator and one handler, and the pipeline applies automatically.
- Application code references `Microsoft.EntityFrameworkCore` (for `DbSet`/LINQ operators), but not Npgsql.
  Provider-specific behaviour, such as constraint violation codes, is translated in Infrastructure.
