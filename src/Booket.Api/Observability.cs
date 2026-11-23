using Booket.Application.Common;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Sinks.OpenTelemetry;

namespace Booket.Api;

internal static class Observability
{
    public const string ServiceName = "booket-api";

    /// <summary>
    /// Serilog for structured logs (console + OTLP) and OpenTelemetry for traces and metrics.
    /// OTLP export is switched on by the standard <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> setting.
    /// </summary>
    public static WebApplicationBuilder AddObservability(this WebApplicationBuilder builder)
    {
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var exportOtlp = !string.IsNullOrWhiteSpace(otlpEndpoint);

        builder.Services.AddSerilog((services, logger) =>
        {
            logger
                .ReadFrom.Configuration(builder.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("service", ServiceName);

            if (exportOtlp)
            {
                logger.WriteTo.OpenTelemetry(o =>
                {
                    o.Endpoint = otlpEndpoint!;
                    o.Protocol = OtlpProtocol.Grpc;
                    o.ResourceAttributes = new Dictionary<string, object> { ["service.name"] = ServiceName };
                });
            }
        });

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(ServiceName, serviceVersion: typeof(Observability).Assembly.GetName().Version?.ToString()))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddNpgsql()
                .AddSource(BooketTelemetry.Name))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("Npgsql")
                .AddMeter(BooketTelemetry.Name));

        if (exportOtlp)
        {
            otel.UseOtlpExporter();
        }

        return builder;
    }
}
