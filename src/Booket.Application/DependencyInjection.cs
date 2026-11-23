using Booket.Application.Common;
using Booket.Application.Features.Auth;
using Booket.Application.Features.Availability;
using Booket.Application.Features.Bookings;
using Booket.Application.Features.Notifications;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Booket.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers every <see cref="IHandler{TRequest,TResponse}"/> in this assembly, wrapped in the
    /// <see cref="HandlerPipeline{TRequest,TResponse}"/> (tracing + validation), plus validators and
    /// application services.
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;

        services.AddValidatorsFromAssembly(assembly, ServiceLifetime.Singleton, includeInternalTypes: true);

        var handlers = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false })
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IHandler<,>))
                .Select(i => (Implementation: t, Interface: i)));

        foreach (var (implementation, @interface) in handlers)
        {
            services.AddScoped(implementation);
            var pipeline = typeof(HandlerPipeline<,>).MakeGenericType(@interface.GetGenericArguments());
            services.AddScoped(@interface, sp => ActivatorUtilities.CreateInstance(sp, pipeline, sp.GetRequiredService(implementation)));
        }

        services.AddScoped<TokenIssuer>();
        services.AddScoped<AvailabilityService>();
        services.AddScoped<BookingNotifications>();
        services.AddScoped<NotificationComposer>();
        services.AddOptions<NotificationOptions>(); // bound to configuration by the host

        return services;
    }
}
