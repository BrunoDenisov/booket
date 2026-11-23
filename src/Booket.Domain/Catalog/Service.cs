using Booket.Domain.Common;

namespace Booket.Domain.Catalog;

/// <summary>Something a customer can book, e.g. "Haircut, 30 min".</summary>
public sealed class Service : Entity
{
    private Service() { }

    public Guid BusinessId { get; private set; }
    public string Name { get; private set; } = null!;
    public string? Description { get; private set; }
    public TimeSpan Duration { get; private set; }

    /// <summary>Clean-up/turnaround time blocked after the appointment; not shown to the customer.</summary>
    public TimeSpan BufferAfter { get; private set; }

    public decimal Price { get; private set; }
    public string Currency { get; private set; } = "EUR";
    public bool IsActive { get; private set; } = true;

    /// <summary>Total time the staff member's calendar is blocked by one booking.</summary>
    public TimeSpan BlockedDuration => Duration + BufferAfter;

    public static Service Create(Guid businessId, string name, string? description, TimeSpan duration, TimeSpan bufferAfter, decimal price, string currency) => new()
    {
        BusinessId = businessId,
        Name = name.Trim(),
        Description = description,
        Duration = duration,
        BufferAfter = bufferAfter,
        Price = price,
        Currency = currency.ToUpperInvariant(),
    };

    public void Update(string name, string? description, TimeSpan duration, TimeSpan bufferAfter, decimal price, string currency, bool isActive)
    {
        Name = name.Trim();
        Description = description;
        Duration = duration;
        BufferAfter = bufferAfter;
        Price = price;
        Currency = currency.ToUpperInvariant();
        IsActive = isActive;
    }

    public void Deactivate() => IsActive = false;
}
