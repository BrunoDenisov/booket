namespace Booket.Application.Common;

/// <summary>A database unique constraint was violated (e.g. a duplicate email under concurrency).</summary>
public sealed class UniqueConstraintViolationException(string? constraintName, Exception innerException)
    : Exception($"Unique constraint '{constraintName}' was violated.", innerException)
{
    public string? ConstraintName { get; } = constraintName;
}

/// <summary>A database exclusion constraint was violated - for bookings, an overlapping appointment.</summary>
public sealed class ExclusionConstraintViolationException(string? constraintName, Exception innerException)
    : Exception($"Exclusion constraint '{constraintName}' was violated.", innerException)
{
    public string? ConstraintName { get; } = constraintName;
}
