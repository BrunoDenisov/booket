using Booket.Application.Common;
using Booket.Domain.Common;
using FluentValidation;

namespace Booket.Application.Tests;

public sealed class HandlerPipelineTests
{
    public sealed record Ping(string Name, int Count);

    private sealed class PingValidator : AbstractValidator<Ping>
    {
        public PingValidator()
        {
            RuleFor(x => x.Name).NotEmpty();
            RuleFor(x => x.Count).GreaterThan(0);
        }
    }

    private sealed class PingHandler : IHandler<Ping, string>
    {
        public int Calls { get; private set; }

        public Task<Result<string>> HandleAsync(Ping request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<Result<string>>($"pong {request.Name}");
        }
    }

    [Fact]
    public async Task Invalid_request_never_reaches_the_handler_and_returns_field_errors()
    {
        var inner = new PingHandler();
        var pipeline = new HandlerPipeline<Ping, string>(inner, [new PingValidator()]);

        var result = await pipeline.HandleAsync(new Ping("", 0), TestContext.Current.CancellationToken);

        inner.Calls.ShouldBe(0);
        var error = result.Error.ShouldBeOfType<ValidationError>();
        error.Errors.Keys.ShouldBe(["name", "count"], ignoreOrder: true);
    }

    [Fact]
    public async Task Valid_request_is_handled()
    {
        var inner = new PingHandler();
        var pipeline = new HandlerPipeline<Ping, string>(inner, [new PingValidator()]);

        var result = await pipeline.HandleAsync(new Ping("booket", 1), TestContext.Current.CancellationToken);

        result.Value.ShouldBe("pong booket");
        inner.Calls.ShouldBe(1);
    }
}
