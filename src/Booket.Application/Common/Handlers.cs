using System.Diagnostics;
using Booket.Domain.Common;
using FluentValidation;

namespace Booket.Application.Common;

/// <summary>A single use case. Handlers never throw for expected failures; they return a <see cref="Result{T}"/>.</summary>
public interface IHandler<in TRequest, TResponse>
{
    Task<Result<TResponse>> HandleAsync(TRequest request, CancellationToken cancellationToken);
}

/// <summary>Response for use cases that have nothing to return.</summary>
public readonly record struct Unit
{
    public static readonly Unit Value;
}

/// <summary>
/// Wraps every handler: starts a tracing span named after the use case and runs all registered
/// FluentValidation validators before the handler sees the request.
/// </summary>
internal sealed class HandlerPipeline<TRequest, TResponse>(
    IHandler<TRequest, TResponse> inner,
    IEnumerable<IValidator<TRequest>> validators) : IHandler<TRequest, TResponse>
{
    private static readonly string UseCase = typeof(TRequest).Name;

    public async Task<Result<TResponse>> HandleAsync(TRequest request, CancellationToken cancellationToken)
    {
        using var activity = BooketTelemetry.ActivitySource.StartActivity($"usecase {UseCase}");

        var failures = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var validator in validators)
        {
            var validation = await validator.ValidateAsync(request, cancellationToken);
            failures.AddRange(validation.Errors);
        }

        if (failures.Count > 0)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "validation_failed");
            return new ValidationError(failures
                .GroupBy(f => ToCamelCase(f.PropertyName))
                .ToDictionary(g => g.Key, g => g.Select(f => f.ErrorMessage).Distinct().ToArray()));
        }

        var result = await inner.HandleAsync(request, cancellationToken);
        if (result.IsFailure)
        {
            activity?.SetTag("booket.error_code", result.Error!.Code);
        }

        return result;
    }

    private static string ToCamelCase(string propertyPath) =>
        string.Join('.', propertyPath.Split('.').Select(p => p.Length == 0 ? p : char.ToLowerInvariant(p[0]) + p[1..]));
}
