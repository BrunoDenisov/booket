namespace Booket.Domain.Scheduling;

/// <summary>A half-open instant range <c>[Start, End)</c>. Two ranges that only touch do not overlap.</summary>
public readonly record struct TimeRange
{
    public TimeRange(DateTimeOffset start, DateTimeOffset end)
    {
        if (end < start)
        {
            throw new ArgumentException("Range end must not be before its start.", nameof(end));
        }

        Start = start;
        End = end;
    }

    public DateTimeOffset Start { get; }
    public DateTimeOffset End { get; }
    public TimeSpan Duration => End - Start;

    public bool Overlaps(TimeRange other) => Start < other.End && other.Start < End;

    public override string ToString() => $"[{Start:O}, {End:O})";
}
