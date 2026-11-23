namespace Booket.Domain.Common;

/// <summary>Base type for aggregate roots and entities identified by a time-ordered GUID (v7).</summary>
public abstract class Entity
{
    public Guid Id { get; protected init; } = Guid.CreateVersion7();
}
