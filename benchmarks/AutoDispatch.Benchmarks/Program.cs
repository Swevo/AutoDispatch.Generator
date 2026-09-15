using AutoDispatch;
using AutoDispatch.Benchmarks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

BenchmarkRunner.Run<DispatchBenchmarks>();

namespace AutoDispatch.Benchmarks
{
    // ---- AutoDispatch side ----
    public sealed record PingCommand(int Value);

    [Handler]
    public sealed class PingHandler
    {
        public Task<int> HandleAsync(PingCommand command, CancellationToken ct = default) =>
            Task.FromResult(command.Value + 1);
    }

    // ---- MediatR side ----
    public sealed record MediatRPingCommand(int Value) : IRequest<int>;

    public sealed class MediatRPingHandler : IRequestHandler<MediatRPingCommand, int>
    {
        public Task<int> Handle(MediatRPingCommand request, CancellationToken cancellationToken) =>
            Task.FromResult(request.Value + 1);
    }

    // ---- AutoDispatch notification side ----
    public sealed record PingNotification(int Value);

    [NotificationHandler]
    public sealed class PingNotificationHandlerA
    {
        public Task HandleAsync(PingNotification notification, CancellationToken ct = default) => Task.CompletedTask;
    }

    [NotificationHandler]
    public sealed class PingNotificationHandlerB
    {
        public Task HandleAsync(PingNotification notification, CancellationToken ct = default) => Task.CompletedTask;
    }

    // ---- MediatR notification side ----
    public sealed record MediatRPingNotification(int Value) : INotification;

    public sealed class MediatRPingNotificationHandlerA : INotificationHandler<MediatRPingNotification>
    {
        public Task Handle(MediatRPingNotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class MediatRPingNotificationHandlerB : INotificationHandler<MediatRPingNotification>
    {
        public Task Handle(MediatRPingNotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Compares per-call dispatch overhead (time + allocations) between AutoDispatch's
    /// compile-time generated <see cref="IDispatcher"/> and MediatR's reflection-based
    /// <see cref="IMediator"/>, both resolving a trivial handler that adds one to an int.
    /// A second benchmark pair compares <c>PublishAsync</c>/<c>Publish</c> fanning a
    /// notification out to two no-op handlers.
    /// </summary>
    [MemoryDiagnoser]
    public class DispatchBenchmarks
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
            mediatrServices.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<MediatRPingHandler>());
            _mediatr = mediatrServices.BuildServiceProvider().GetRequiredService<IMediator>();
        }

        [Benchmark(Baseline = true)]
        public Task<int> AutoDispatch_SendAsync() => _autoDispatch.SendAsync(new PingCommand(41));

        [Benchmark]
        public Task<int> MediatR_Send() => _mediatr.Send(new MediatRPingCommand(41));

        [Benchmark]
        public Task AutoDispatch_PublishAsync() => _autoDispatch.PublishAsync(new PingNotification(41));

        [Benchmark]
        public Task MediatR_Publish() => _mediatr.Publish(new MediatRPingNotification(41));
    }
}
