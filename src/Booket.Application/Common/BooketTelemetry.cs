using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Booket.Application.Common;

/// <summary>Custom OpenTelemetry signals. Exported by the API's OpenTelemetry pipeline.</summary>
public static class BooketTelemetry
{
    public const string Name = "Booket";

    public static readonly ActivitySource ActivitySource = new(Name);

    private static readonly Meter Meter = new(Name);

    public static readonly Counter<long> BookingsCreated =
        Meter.CreateCounter<long>("booket.bookings.created", description: "Bookings created");

    public static readonly Counter<long> BookingsCancelled =
        Meter.CreateCounter<long>("booket.bookings.cancelled", description: "Bookings cancelled");

    public static readonly Counter<long> BookingsRescheduled =
        Meter.CreateCounter<long>("booket.bookings.rescheduled", description: "Bookings rescheduled");

    public static readonly Counter<long> BookingConflicts =
        Meter.CreateCounter<long>("booket.bookings.conflicts", description: "Booking attempts rejected because the slot was taken");

    public static readonly Counter<long> NotificationsSent =
        Meter.CreateCounter<long>("booket.notifications.sent", description: "Notifications delivered");

    public static readonly Counter<long> NotificationsFailed =
        Meter.CreateCounter<long>("booket.notifications.failed", description: "Notification delivery attempts that failed");
}
