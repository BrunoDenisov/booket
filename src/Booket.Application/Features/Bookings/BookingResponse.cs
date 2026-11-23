using Booket.Application.Abstractions;
using Booket.Domain.Bookings;
using Microsoft.EntityFrameworkCore;

namespace Booket.Application.Features.Bookings;

/// <param name="Start">Appointment start in the business's local offset.</param>
/// <param name="End">Appointment end in the business's local offset (excludes the internal buffer).</param>
public sealed record BookingResponse(
    Guid Id,
    Guid BusinessId,
    string BusinessName,
    Guid ServiceId,
    string ServiceName,
    Guid StaffId,
    string StaffName,
    Guid CustomerId,
    string CustomerName,
    DateTimeOffset Start,
    DateTimeOffset End,
    string TimeZoneId,
    BookingStatus Status,
    string? Notes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CancelledAtUtc,
    string? CancellationReason,
    int RescheduleCount);

internal static class BookingReads
{
    /// <summary>Projects bookings with the names of their business, service, staff member and customer.</summary>
    public static async Task<List<BookingResponse>> ToResponsesAsync(this IQueryable<Booking> bookings, IAppDbContext db, CancellationToken ct)
    {
        var rows = await (
                from b in bookings
                join biz in db.Businesses on b.BusinessId equals biz.Id
                join s in db.Services on b.ServiceId equals s.Id
                join st in db.StaffMembers on b.StaffId equals st.Id
                join c in db.Users on b.CustomerId equals c.Id
                select new
                {
                    Booking = b,
                    BusinessName = biz.Name,
                    biz.TimeZoneId,
                    ServiceName = s.Name,
                    StaffName = st.DisplayName,
                    CustomerName = c.FullName,
                })
            .AsNoTracking()
            .ToListAsync(ct);

        return rows
            .Select(r =>
            {
                var b = r.Booking;
                var zone = TimeZoneInfo.FindSystemTimeZoneById(r.TimeZoneId);
                return new BookingResponse(
                    b.Id, b.BusinessId, r.BusinessName, b.ServiceId, r.ServiceName, b.StaffId, r.StaffName,
                    b.CustomerId, r.CustomerName,
                    TimeZoneInfo.ConvertTime(b.StartUtc, zone), TimeZoneInfo.ConvertTime(b.EndUtc, zone), r.TimeZoneId,
                    b.Status, b.Notes, b.CreatedAtUtc, b.CancelledAtUtc, b.CancellationReason, b.RescheduleCount);
            })
            .OrderBy(r => r.Start)
            .ToList();
    }

    public static async Task<BookingResponse> GetResponseAsync(this IAppDbContext db, Guid bookingId, CancellationToken ct) =>
        (await db.Bookings.Where(b => b.Id == bookingId).ToResponsesAsync(db, ct)).Single();
}
