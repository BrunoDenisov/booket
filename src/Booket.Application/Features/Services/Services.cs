using Booket.Application.Abstractions;
using Booket.Application.Common;
using Booket.Application.Features.Businesses;
using Booket.Domain.Catalog;
using Booket.Domain.Common;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Booket.Application.Features.Services;

public sealed record ServiceResponse(
    Guid Id,
    Guid BusinessId,
    string Name,
    string? Description,
    int DurationMinutes,
    int BufferMinutes,
    decimal Price,
    string Currency,
    bool IsActive)
{
    public static ServiceResponse From(Service s) => new(
        s.Id, s.BusinessId, s.Name, s.Description, (int)s.Duration.TotalMinutes, (int)s.BufferAfter.TotalMinutes, s.Price, s.Currency, s.IsActive);
}

public static class ServiceErrors
{
    public static readonly Error NotFound = Error.NotFound("service.not_found", "Service was not found.");
}

public interface IServiceFields
{
    string Name { get; }
    string? Description { get; }
    int DurationMinutes { get; }
    int BufferMinutes { get; }
    decimal Price { get; }
    string Currency { get; }
}

internal sealed class ServiceFieldsValidator : AbstractValidator<IServiceFields>
{
    public ServiceFieldsValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.DurationMinutes).InclusiveBetween(5, 8 * 60);
        RuleFor(x => x.BufferMinutes).InclusiveBetween(0, 120);
        RuleFor(x => x.Price).GreaterThanOrEqualTo(0).PrecisionScale(10, 2, ignoreTrailingZeros: true);
        RuleFor(x => x.Currency).Currency();
    }
}

// ---- List -----------------------------------------------------------------------------------

public sealed record ListServicesQuery(Guid BusinessId, bool IncludeInactive = false);

internal sealed class ListServicesHandler(IAppDbContext db, ICurrentUser currentUser) : IHandler<ListServicesQuery, IReadOnlyList<ServiceResponse>>
{
    public async Task<Result<IReadOnlyList<ServiceResponse>>> HandleAsync(ListServicesQuery request, CancellationToken cancellationToken)
    {
        if (!await db.Businesses.AnyAsync(b => b.Id == request.BusinessId, cancellationToken))
        {
            return BusinessErrors.NotFound;
        }

        var includeInactive = request.IncludeInactive && currentUser.IsMemberOf(request.BusinessId);
        var services = await db.Services.AsNoTracking()
            .Where(s => s.BusinessId == request.BusinessId && (includeInactive || s.IsActive))
            .OrderBy(s => s.Name)
            .ToListAsync(cancellationToken);

        return services.Select(ServiceResponse.From).ToList();
    }
}

// ---- Get --------------------------------------------------------------------------------------

public sealed record GetServiceQuery(Guid BusinessId, Guid ServiceId);

internal sealed class GetServiceHandler(IAppDbContext db) : IHandler<GetServiceQuery, ServiceResponse>
{
    public async Task<Result<ServiceResponse>> HandleAsync(GetServiceQuery request, CancellationToken cancellationToken)
    {
        var service = await db.Services.AsNoTracking()
            .Where(s => s.Id == request.ServiceId && s.BusinessId == request.BusinessId)
            .SingleOrDefaultAsync(cancellationToken);

        return service is null ? ServiceErrors.NotFound : ServiceResponse.From(service);
    }
}

// ---- Create ---------------------------------------------------------------------------------

public sealed record CreateServiceCommand(
    Guid BusinessId,
    string Name,
    string? Description,
    int DurationMinutes,
    int BufferMinutes,
    decimal Price,
    string Currency) : IServiceFields;

public sealed class CreateServiceValidator : AbstractValidator<CreateServiceCommand>
{
    public CreateServiceValidator() => Include(new ServiceFieldsValidator());
}

internal sealed class CreateServiceHandler(IAppDbContext db) : IHandler<CreateServiceCommand, ServiceResponse>
{
    public async Task<Result<ServiceResponse>> HandleAsync(CreateServiceCommand request, CancellationToken cancellationToken)
    {
        if (!await db.Businesses.AnyAsync(b => b.Id == request.BusinessId, cancellationToken))
        {
            return BusinessErrors.NotFound;
        }

        var service = Service.Create(
            request.BusinessId, request.Name, request.Description,
            TimeSpan.FromMinutes(request.DurationMinutes), TimeSpan.FromMinutes(request.BufferMinutes),
            request.Price, request.Currency);

        db.Services.Add(service);
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResponse.From(service);
    }
}

// ---- Update ---------------------------------------------------------------------------------

public sealed record UpdateServiceCommand(
    Guid BusinessId,
    Guid ServiceId,
    string Name,
    string? Description,
    int DurationMinutes,
    int BufferMinutes,
    decimal Price,
    string Currency,
    bool IsActive) : IServiceFields;

public sealed class UpdateServiceValidator : AbstractValidator<UpdateServiceCommand>
{
    public UpdateServiceValidator() => Include(new ServiceFieldsValidator());
}

/// <summary>
/// Updates a service. Existing bookings keep the times they were booked with; only new bookings
/// pick up a changed duration or buffer.
/// </summary>
internal sealed class UpdateServiceHandler(IAppDbContext db) : IHandler<UpdateServiceCommand, ServiceResponse>
{
    public async Task<Result<ServiceResponse>> HandleAsync(UpdateServiceCommand request, CancellationToken cancellationToken)
    {
        var service = await db.Services.SingleOrDefaultAsync(s => s.Id == request.ServiceId && s.BusinessId == request.BusinessId, cancellationToken);
        if (service is null)
        {
            return ServiceErrors.NotFound;
        }

        service.Update(
            request.Name, request.Description,
            TimeSpan.FromMinutes(request.DurationMinutes), TimeSpan.FromMinutes(request.BufferMinutes),
            request.Price, request.Currency, request.IsActive);

        await db.SaveChangesAsync(cancellationToken);
        return ServiceResponse.From(service);
    }
}

// ---- Delete (soft) --------------------------------------------------------------------------

/// <summary>Services are deactivated rather than deleted so booking history stays intact.</summary>
public sealed record DeactivateServiceCommand(Guid BusinessId, Guid ServiceId);

internal sealed class DeactivateServiceHandler(IAppDbContext db) : IHandler<DeactivateServiceCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(DeactivateServiceCommand request, CancellationToken cancellationToken)
    {
        var service = await db.Services.SingleOrDefaultAsync(s => s.Id == request.ServiceId && s.BusinessId == request.BusinessId, cancellationToken);
        if (service is null)
        {
            return ServiceErrors.NotFound;
        }

        service.Deactivate();
        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}
