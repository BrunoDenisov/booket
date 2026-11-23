using Booket.Application.Abstractions;
using Booket.Application.Common;
using Booket.Domain.Businesses;
using Booket.Domain.Common;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Booket.Application.Features.Businesses;

public sealed record BookingPolicyDto(int MinLeadTimeMinutes, int MaxAdvanceDays, int CancellationCutoffHours, int SlotGranularityMinutes)
{
    public static BookingPolicyDto From(BookingPolicy p) =>
        new((int)p.MinLeadTime.TotalMinutes, p.MaxAdvanceDays, (int)p.CancellationCutoff.TotalHours, p.SlotGranularityMinutes);

    public BookingPolicy ToPolicy() => new()
    {
        MinLeadTime = TimeSpan.FromMinutes(MinLeadTimeMinutes),
        MaxAdvanceDays = MaxAdvanceDays,
        CancellationCutoff = TimeSpan.FromHours(CancellationCutoffHours),
        SlotGranularityMinutes = SlotGranularityMinutes,
    };
}

public sealed class BookingPolicyDtoValidator : AbstractValidator<BookingPolicyDto>
{
    public BookingPolicyDtoValidator()
    {
        RuleFor(x => x.MinLeadTimeMinutes).InclusiveBetween(0, 60 * 24 * 14);
        RuleFor(x => x.MaxAdvanceDays).InclusiveBetween(1, 365);
        RuleFor(x => x.CancellationCutoffHours).InclusiveBetween(0, 24 * 14);
        RuleFor(x => x.SlotGranularityMinutes)
            .Must(m => m is 5 or 10 or 15 or 20 or 30 or 60)
            .WithMessage("Slot granularity must be one of 5, 10, 15, 20, 30 or 60 minutes.");
    }
}

public sealed record BusinessResponse(Guid Id, string Name, string Slug, string TimeZoneId, BookingPolicyDto Policy)
{
    public static BusinessResponse From(Business b) => new(b.Id, b.Name, b.Slug, b.TimeZoneId, BookingPolicyDto.From(b.Policy));
}

public static class BusinessErrors
{
    public static readonly Error NotFound = Error.NotFound("business.not_found", "Business was not found.");
}

// ---- Get one --------------------------------------------------------------------------------

/// <summary>Look up a business by id or slug.</summary>
public sealed record GetBusinessQuery(string IdOrSlug);

internal sealed class GetBusinessHandler(IAppDbContext db) : IHandler<GetBusinessQuery, BusinessResponse>
{
    public async Task<Result<BusinessResponse>> HandleAsync(GetBusinessQuery request, CancellationToken cancellationToken)
    {
        var query = db.Businesses.AsNoTracking();
        var business = Guid.TryParse(request.IdOrSlug, out var id)
            ? await query.SingleOrDefaultAsync(b => b.Id == id, cancellationToken)
            : await query.SingleOrDefaultAsync(b => b.Slug == request.IdOrSlug.ToLowerInvariant(), cancellationToken);

        return business is null ? BusinessErrors.NotFound : BusinessResponse.From(business);
    }
}

// ---- List -----------------------------------------------------------------------------------

public sealed record ListBusinessesQuery(string? Search, int Page = 1, int PageSize = 20);

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed class ListBusinessesValidator : AbstractValidator<ListBusinessesQuery>
{
    public ListBusinessesValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.Search).MaximumLength(100);
    }
}

internal sealed class ListBusinessesHandler(IAppDbContext db) : IHandler<ListBusinessesQuery, PagedResponse<BusinessResponse>>
{
    public async Task<Result<PagedResponse<BusinessResponse>>> HandleAsync(ListBusinessesQuery request, CancellationToken cancellationToken)
    {
        var query = db.Businesses.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim().ToLower();
            query = query.Where(b => b.Name.ToLower().Contains(term));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(b => b.Name)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResponse<BusinessResponse>(items.Select(BusinessResponse.From).ToList(), request.Page, request.PageSize, total);
    }
}

// ---- Update ---------------------------------------------------------------------------------

public sealed record UpdateBusinessCommand(Guid BusinessId, string Name, string TimeZoneId, BookingPolicyDto Policy);

public sealed class UpdateBusinessValidator : AbstractValidator<UpdateBusinessCommand>
{
    public UpdateBusinessValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.TimeZoneId).IanaTimeZone();
        RuleFor(x => x.Policy).NotNull().SetValidator(new BookingPolicyDtoValidator());
    }
}

internal sealed class UpdateBusinessHandler(IAppDbContext db) : IHandler<UpdateBusinessCommand, BusinessResponse>
{
    public async Task<Result<BusinessResponse>> HandleAsync(UpdateBusinessCommand request, CancellationToken cancellationToken)
    {
        var business = await db.Businesses.SingleOrDefaultAsync(b => b.Id == request.BusinessId, cancellationToken);
        if (business is null)
        {
            return BusinessErrors.NotFound;
        }

        business.Update(request.Name, request.TimeZoneId, request.Policy.ToPolicy());
        await db.SaveChangesAsync(cancellationToken);
        return BusinessResponse.From(business);
    }
}
