using AutoDispatch;
using AutoDispatch.Benchmarks.Pipeline;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using MediatR;
using MediatR.Pipeline;
using Microsoft.Extensions.DependencyInjection;

BenchmarkRunner.Run<FullPipelineBenchmarks>();

namespace AutoDispatch.Benchmarks.Pipeline
{
    // This is a separate project (rather than a class in AutoDispatch.Benchmarks) because
    // AutoDispatch's [Behavior]/[PreProcessor]/[PostProcessor] open generics apply to *every*
    // command dispatched within the same compilation — they are not opt-in per command type
    // (this mirrors MediatR's own default: registered IPipelineBehavior<,> implementations also
    // apply globally unless constrained). Keeping the "full pipeline" comparison isolated here
    // means it doesn't change the bare AutoDispatch_SendAsync numbers in the main benchmark
    // project's README.

    // ---- AutoDispatch side: 2 pipeline behaviors + 1 pre-processor + 1 post-processor ----
    public sealed record PingCommand(int Value);

    [Handler]
    public sealed class PingHandler
    {
        public Task<int> HandleAsync(PingCommand command, CancellationToken ct = default) =>
            Task.FromResult(command.Value + 1);
    }

    [Behavior(Order = 0)]
    public sealed class NoOpBehaviorA<TCommand, TResult> : IPipelineBehavior<TCommand, TResult>
    {
        public Task<TResult> HandleAsync(TCommand command, Func<Task<TResult>> next, CancellationToken ct = default) => next();
    }

    [Behavior(Order = 1)]
    public sealed class NoOpBehaviorB<TCommand, TResult> : IPipelineBehavior<TCommand, TResult>
    {
        public Task<TResult> HandleAsync(TCommand command, Func<Task<TResult>> next, CancellationToken ct = default) => next();
    }

    [PreProcessor(Order = 0)]
    public sealed class NoOpPreProcessor<TCommand> : IPreProcessor<TCommand>
    {
        public Task ProcessAsync(TCommand command, CancellationToken ct = default) => Task.CompletedTask;
    }

    [PostProcessor(Order = 0)]
    public sealed class NoOpPostProcessor<TCommand, TResult> : IPostProcessor<TCommand, TResult>
    {
        public Task ProcessAsync(TCommand command, TResult response, CancellationToken ct = default) => Task.CompletedTask;
    }

    // ---- MediatR side: 2 pipeline behaviors + 1 pre-processor + 1 post-processor ----
    public sealed record MediatRPingCommand(int Value) : IRequest<int>;

    public sealed class MediatRPingHandler : IRequestHandler<MediatRPingCommand, int>
    {
        public Task<int> Handle(MediatRPingCommand request, CancellationToken cancellationToken) =>
            Task.FromResult(request.Value + 1);
    }

    public sealed class MediatRNoOpBehaviorA<TRequest, TResponse> : global::MediatR.IPipelineBehavior<TRequest, TResponse>
        where TRequest : notnull
    {
        public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next();
    }

    public sealed class MediatRNoOpBehaviorB<TRequest, TResponse> : global::MediatR.IPipelineBehavior<TRequest, TResponse>
        where TRequest : notnull
    {
        public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next();
    }

    public sealed class MediatRNoOpPreProcessor<TRequest> : IRequestPreProcessor<TRequest>
        where TRequest : notnull
    {
        public Task Process(TRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class MediatRNoOpPostProcessor<TRequest, TResponse> : IRequestPostProcessor<TRequest, TResponse>
        where TRequest : notnull
    {
        public Task Process(TRequest request, TResponse response, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Compares AutoDispatch's compile-time generated <c>SendAsync</c> against MediatR's
    /// reflection-based <c>Send</c> when both go through an equivalent "full" pipeline: two
    /// pipeline behaviors plus a pre-processor and a post-processor wrapping a trivial handler.
    /// This isolates the overhead of the try/async catch-free codegen path (added for exception
    /// handling/actions) plus the pre/post-processor wrapping, versus MediatR's reflection-built
    /// equivalent (<c>RequestPreProcessorBehavior</c>/<c>RequestPostProcessorBehavior</c> plus two
    /// ordinary open-generic behaviors).
    /// </summary>
    [MemoryDiagnoser]
    public class FullPipelineBenchmarks
    {
        private IDispatcher _autoDispatch = null!;
        private IMediator _mediatr = null!;

        [GlobalSetup]
        public void Setup()
        {
            var autoDispatchServices = new ServiceCollection();
            autoDispatchServices.AddAutoDispatch();
            _autoDispatch = autoDispatchServices.BuildServiceProvider().GetRequiredService<IDispatcher>();

            var mediatrServices = new ServiceCollection();
            mediatrServices.AddMediatR(cfg =>
            {
                cfg.RegisterServicesFromAssemblyContaining<MediatRPingHandler>();
                cfg.AddOpenBehavior(typeof(RequestPreProcessorBehavior<,>));
                cfg.AddOpenBehavior(typeof(RequestPostProcessorBehavior<,>));
                cfg.AddOpenBehavior(typeof(MediatRNoOpBehaviorA<,>));
                cfg.AddOpenBehavior(typeof(MediatRNoOpBehaviorB<,>));
            });
            mediatrServices.AddTransient(typeof(IRequestPreProcessor<>), typeof(MediatRNoOpPreProcessor<>));
            mediatrServices.AddTransient(typeof(IRequestPostProcessor<,>), typeof(MediatRNoOpPostProcessor<,>));
            _mediatr = mediatrServices.BuildServiceProvider().GetRequiredService<IMediator>();
        }

        [Benchmark(Baseline = true)]
        public Task<int> AutoDispatch_SendAsync_FullPipeline() => _autoDispatch.SendAsync(new PingCommand(41));

        [Benchmark]
        public Task<int> MediatR_Send_FullPipeline() => _mediatr.Send(new MediatRPingCommand(41));
    }
}
