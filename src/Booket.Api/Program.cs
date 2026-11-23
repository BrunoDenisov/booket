using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Booket.Api;
using Booket.Api.Auth;
using Booket.Api.Endpoints;
using Booket.Api.Http;
using Booket.Api.OpenApi;
using Booket.Application;
using Booket.Infrastructure;
using Booket.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Json;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.AddObservability();

    builder.Services
        .AddApplication()
        .AddInfrastructure(builder.Configuration)
        .AddBooketAuthorization();

    builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
    builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);

    builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
    {
        ctx.ProblemDetails.Instance ??= $"{ctx.HttpContext.Request.Method} {ctx.HttpContext.Request.Path}";
        ctx.ProblemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? ctx.HttpContext.TraceIdentifier;
    });
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

    builder.Services.AddOpenApi(o =>
    {
        o.AddDocumentTransformer<BearerSecurityTransformer>();
        o.AddOperationTransformer<BearerSecurityTransformer>();
    });

    var authPermitLimit = builder.Configuration.GetValue("RateLimiting:AuthPermitsPerMinute", 20);
    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.AddPolicy(AuthEndpoints.RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = authPermitLimit, Window = TimeSpan.FromMinutes(1) }));
        o.OnRejected = async (ctx, ct) =>
        {
            if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            {
                ctx.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            await ctx.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
            {
                HttpContext = ctx.HttpContext,
                ProblemDetails = { Status = StatusCodes.Status429TooManyRequests, Detail = "Too many requests. Try again later." },
            });
        };
    });

    var app = builder.Build();

    app.UseExceptionHandler();
    app.UseStatusCodePages();
    app.UseSerilogRequestLogging(o => o.EnrichDiagnosticContext = (diagnostics, http) =>
    {
        diagnostics.Set("UserId", http.User.FindFirst("sub")?.Value);
        diagnostics.Set("ClientIp", http.Connection.RemoteIpAddress?.ToString());
    });

    app.UseAuthentication();
    app.UseAuthorization();
    app.UseRateLimiter();

    app.MapOpenApi();
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint("/openapi/v1.json", "Booket API v1");
        o.DocumentTitle = "Booket API";
        o.EnablePersistAuthorization();
    });
    app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
    app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

    app.MapAuthEndpoints();
    app.MapBusinessEndpoints();
    app.MapBookingEndpoints();

    await app.Services.InitializeDatabaseAsync();
    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Booket API terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

public partial class Program;
