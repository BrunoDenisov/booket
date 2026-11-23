using System.Globalization;
using System.Text;
using Booket.Application.Abstractions;
using Booket.Domain.Bookings;
using Booket.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Booket.Application.Features.Notifications;

/// <summary>What the dispatcher should do with an outbox message.</summary>
public abstract record ComposeResult
{
    public sealed record Send(EmailMessage Email) : ComposeResult;

    /// <summary>The message is obsolete (e.g. a reminder for a cancelled booking) and must not be sent.</summary>
    public sealed record Skip(string Reason) : ComposeResult;
}

/// <summary>Turns an outbox message into an email, rendered in the business's local time.</summary>
public sealed class NotificationComposer(IAppDbContext db)
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-GB");

    public async Task<ComposeResult> ComposeAsync(OutboxMessage message, CancellationToken ct)
    {
        var data = await (
                from b in db.Bookings
                join biz in db.Businesses on b.BusinessId equals biz.Id
                join s in db.Services on b.ServiceId equals s.Id
                join st in db.StaffMembers on b.StaffId equals st.Id
                join c in db.Users on b.CustomerId equals c.Id
                where b.Id == message.BookingId
                select new { Booking = b, BusinessName = biz.Name, biz.TimeZoneId, ServiceName = s.Name, StaffName = st.DisplayName, c.Email, c.FullName })
            .AsNoTracking()
            .SingleOrDefaultAsync(ct);

        if (data is null)
        {
            return new ComposeResult.Skip("Booking no longer exists.");
        }

        var booking = data.Booking;
        if (message.Type == NotificationType.Reminder && booking.Status != BookingStatus.Confirmed)
        {
            return new ComposeResult.Skip($"Booking is {booking.Status}.");
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(data.TimeZoneId);
        var start = TimeZoneInfo.ConvertTime(booking.StartUtc, zone);
        var when = start.ToString("dddd d MMMM yyyy 'at' HH:mm", Culture);

        var (subject, intro) = message.Type switch
        {
            NotificationType.BookingConfirmed => ($"Booking confirmed: {data.ServiceName} on {when}", "Your booking is confirmed."),
            NotificationType.BookingRescheduled => ($"Booking moved: {data.ServiceName} on {when}", "Your booking has been rescheduled."),
            NotificationType.BookingCancelled => ($"Booking cancelled: {data.ServiceName} on {when}", "Your booking has been cancelled."),
            NotificationType.Reminder => ($"Reminder: {data.ServiceName} {Relative(booking.StartUtc - message.DueAtUtc)}", "This is a reminder of your upcoming appointment."),
            _ => throw new ArgumentOutOfRangeException(nameof(message), message.Type, "Unknown notification type."),
        };

        var body = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"Hi {data.FullName},")
            .AppendLine()
            .AppendLine(intro)
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"  What:  {data.ServiceName}")
            .AppendLine(CultureInfo.InvariantCulture, $"  With:  {data.StaffName}")
            .AppendLine(CultureInfo.InvariantCulture, $"  When:  {when} ({data.TimeZoneId})")
            .AppendLine(CultureInfo.InvariantCulture, $"  Where: {data.BusinessName}")
            .AppendLine(CultureInfo.InvariantCulture, $"  Ref:   {booking.Id}")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"- {data.BusinessName}, via Booket")
            .ToString();

        return new ComposeResult.Send(new EmailMessage(data.Email, data.FullName, subject, body));
    }

    private static string Relative(TimeSpan lead) => lead.TotalHours >= 24
        ? $"in {lead.TotalDays:0} day{(lead.TotalDays >= 1.5 ? "s" : string.Empty)}"
        : $"in {Math.Max(1, Math.Round(lead.TotalHours)):0} hour{(lead.TotalHours >= 1.5 ? "s" : string.Empty)}";
}
